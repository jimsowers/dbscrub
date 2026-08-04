/*
    DbScrubTest — a local fixture database for exercising dbscrub against a
    real SQL Server.

    THE DATA IN HERE IS FAKE. Every value is invented, and the domains and
    number ranges are ones reserved by standards bodies precisely so they can
    never belong to a real person:
      - example.invalid  (RFC 2606 / RFC 6761 — .invalid can never be registered)
      - 555-01xx phone numbers (reserved for fiction)
      - 123-45-6789 style SSNs (never issued)

    It is nonetheless shaped EXACTLY like real PII, on purpose: step 5's
    verify gate sweeps for email, SSN, and 10-digit phone patterns, and it can
    only be trusted if it finds these before masking and finds nothing after.
    Obviously-fake values that still match the detector is the combination we
    want (DECISIONS.md D2).

    Never point this script at a database you care about. It DROPS DbScrubTest.

    Run (PowerShell). NOTE the named instance — this box has no default
    instance, so a bare "localhost" will not connect:
      sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql
    or open it in SSMS and execute.
*/

SET NOCOUNT ON;
GO

USE master;
GO

IF DB_ID('DbScrubTest') IS NOT NULL
BEGIN
    -- SINGLE_USER kicks out any pooled connection still holding the database,
    -- which is the usual reason a DROP hangs. Same mechanic the rename ritual
    -- uses in SPEC 5.5.
    ALTER DATABASE DbScrubTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE DbScrubTest;
END
GO

CREATE DATABASE DbScrubTest;
GO

ALTER DATABASE DbScrubTest SET RECOVERY SIMPLE;
GO

USE DbScrubTest;
GO

/*  A non-dbo schema, so we prove schema qualification is real and not an
    assumption that everything lives in dbo.  */
CREATE SCHEMA app;
GO

/* ---------------------------------------------------------------------------
   dbo.Person — the kitchen-sink table.

   Deliberately carries one of each thing SchemaInventory has never seen:
     - IDENTITY column        -> is_identity, and must be refused for masking
     - computed column        -> is_computed, likewise
     - NOT NULL column        -> is_nullable = 0, so "null" strategy is refused
     - nvarchar(max)          -> max_length = -1, the value most likely to break
     - SYSTEM_VERSIONING      -> temporal_type = 2 plus the history_table_id join
--------------------------------------------------------------------------- */
CREATE TABLE dbo.Person
(
    PersonId   int IDENTITY(1,1)   NOT NULL CONSTRAINT PK_Person PRIMARY KEY,
    FirstName  nvarchar(100)           NULL,
    LastName   nvarchar(100)           NULL,
    Email      nvarchar(256)       NOT NULL,   -- NOT NULL on purpose
    Ssn        char(11)                NULL,
    Phone      varchar(20)             NULL,
    Notes      nvarchar(max)           NULL,   -- max_length = -1
    FullName   AS (FirstName + N' ' + LastName),  -- computed
    CreatedUtc datetime2(3)        NOT NULL
        CONSTRAINT DF_Person_CreatedUtc DEFAULT SYSUTCDATETIME(),
    ValidFrom  datetime2(7) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo    datetime2(7) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PersonHistory));
GO

/* ---------------------------------------------------------------------------
   app.Enrollment — CDC-tracked, in a non-dbo schema.
   Proves is_tracked_by_cdc and that the cdc.* capture tables are correctly
   EXCLUDED from the inventory (SchemaInventory filters schema 'cdc').
--------------------------------------------------------------------------- */
CREATE TABLE app.Enrollment
(
    EnrollmentId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Enrollment PRIMARY KEY,
    PersonId     int               NOT NULL,
    Notes        nvarchar(1000)        NULL,
    CONSTRAINT FK_Enrollment_Person FOREIGN KEY (PersonId) REFERENCES dbo.Person (PersonId)
);
GO

/* ---------------------------------------------------------------------------
   app.Membership — the uniqueness case and the composite-key case.

   Two things live here that no other table in this fixture has:

     - A COMPOSITE primary key. The mask engine walks a table in key order
       using a lexicographic predicate — "greater in the first column that
       differs" — and with a single key column that predicate collapses to
       `k > @lo`. The interesting form only exists with two, and the seed data
       below is chosen so a naive one-column comparison SKIPS rows rather than
       erroring: MemberNumber 100 appears under both organizations, so a walk
       comparing MemberNumber alone steps straight past (2, 100).

     - UNIQUE rules, in both spellings. A UNIQUE constraint and a
       CREATE UNIQUE INDEX are the same object to SQL Server — both are rows in
       sys.indexes with is_unique = 1 — and both are here so the inventory
       proves it reads the pair rather than only the one it was written for.

   Why the uniqueness matters: SQL Server enforces a unique index DURING an
   UPDATE. `static` writes the same value to every row, so on Username it would
   raise error 2601 on the second row, partway through the run, leaving a
   database that is neither raw nor clean. dbscrub refuses that at plan time
   (DECISIONS.md D27), and the complete config masks these columns with the two
   strategies that DO give every row a different value.
--------------------------------------------------------------------------- */
CREATE TABLE app.Membership
(
    OrganizationId int           NOT NULL,
    MemberNumber   int           NOT NULL,
    Username       nvarchar(100) NOT NULL,
    Email          nvarchar(256) NOT NULL,
    Nickname       nvarchar(50)      NULL,   -- NOT unique: `static` is fine here
    CONSTRAINT PK_Membership PRIMARY KEY (OrganizationId, MemberNumber),
    CONSTRAINT UQ_Membership_Username UNIQUE (Username)
);
GO

/*  The other spelling of the same thing. sys.indexes reports this with
    is_unique = 1 and is_unique_constraint = 0; the constraint above reports
    both as 1. One query has to find them both.  */
CREATE UNIQUE INDEX UX_Membership_Email ON app.Membership (Email);
GO

/* ---------------------------------------------------------------------------
   dbo.LoginAudit — the truncate case (DECISIONS.md D5). Carries PII buried in
   a JSON payload, which is exactly why audit tables are truncated and never
   masked: no column strategy can reach inside that string.
--------------------------------------------------------------------------- */
CREATE TABLE dbo.LoginAudit
(
    Id        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoginAudit PRIMARY KEY,
    UserName  nvarchar(256)            NULL,
    IpAddress varchar(45)              NULL,
    Payload   nvarchar(max)            NULL,
    OccurUtc  datetime2(3)         NOT NULL
        CONSTRAINT DF_LoginAudit_OccurUtc DEFAULT SYSUTCDATETIME()
);
GO

/* ---------------------------------------------------------------------------
   dbo.ContactImport — a HEAP. No primary key, no identity, no unique index.

   This is the table that exercises SPEC 5.3's keyless fallback, and it exists
   because every other table here has a key, which made that path unfixtured.

   Without a key there is no way to address one row, so the mask engine cannot
   batch and cannot compute a replacement from a row's current value. What it
   CAN do is rewrite the whole table in one set-based UPDATE, which is correct
   for any strategy whose replacement is the same on every row — `static` and
   `null`. `scramble` on a table like this is refused at plan time rather than
   approximated (DECISIONS.md D19), and the config below therefore uses static
   and null on purpose.

   Real-world shape: a staging table for a spreadsheet import, which is exactly
   where a heap full of contact details tends to turn up.
--------------------------------------------------------------------------- */
CREATE TABLE dbo.ContactImport
(
    SourceFile  nvarchar(260)     NULL,
    Email       nvarchar(256)     NULL,
    Phone       varchar(20)       NULL,
    Notes       nvarchar(max)     NULL,
    ImportedUtc datetime2(3)  NOT NULL
        CONSTRAINT DF_ContactImport_ImportedUtc DEFAULT SYSUTCDATETIME()
);
GO

/* ---------------------------------------------------------------------------
   Seed data — fake, but shaped like the real thing.
--------------------------------------------------------------------------- */
INSERT INTO dbo.Person (FirstName, LastName, Email, Ssn, Phone, Notes)
VALUES
    (N'Ada',     N'Lovelace', N'ada.lovelace@example.invalid',  '123-45-6789', '212-555-0100',
     N'Prefers email. Secondary contact: ada.alt@example.invalid'),
    (N'Grace',   N'Hopper',   N'grace.hopper@example.invalid',  '234-56-7890', '212-555-0101',
     N'Called from 212-555-0142 about enrollment.'),
    (N'Alan',    N'Turing',   N'alan.turing@example.invalid',   '345-67-8901', '212-555-0102',
     NULL),
    (N'Katherine', N'Johnson', N'k.johnson@example.invalid',    NULL,          NULL,
     N'No phone on file.');
GO

/*  Update every row so the temporal history table actually has rows. Without
    this PersonHistory is empty, and an empty history table would let a broken
    "mask the history too" path look like it worked.  */
UPDATE dbo.Person SET Notes = ISNULL(Notes, N'') + N' [updated]';
GO

INSERT INTO app.Enrollment (PersonId, Notes)
SELECT PersonId, N'Enrolled; contact ' + Email FROM dbo.Person;
GO

/*  Five rows across two organizations, and MemberNumber 100 deliberately
    appears in both. Ordered by (OrganizationId, MemberNumber) the rows are
    (1,100) (1,101) (2,100) (2,101) (2,102) — so a batch that ends at (1,101)
    must resume at "OrganizationId > 1 OR (OrganizationId = 1 AND MemberNumber
    > 101)". A predicate comparing MemberNumber alone would resume at
    "MemberNumber > 101" and silently skip (2,100), which would keep its real
    values while the run reported success. That is the shape of bug this table
    exists to catch, and the row-count reconciliation (DECISIONS.md D21) turns
    it into a failed run.  */
INSERT INTO app.Membership (OrganizationId, MemberNumber, Username, Email, Nickname)
VALUES
    (1, 100, N'alovelace', N'ada.lovelace@example.invalid',   N'Ada'),
    (1, 101, N'ghopper',   N'grace.hopper@example.invalid',   N'Grace'),
    (2, 100, N'aturing',   N'alan.turing@example.invalid',    NULL),
    (2, 101, N'kjohnson',  N'k.johnson@example.invalid',      N'Katherine'),
    (2, 102, N'mhamilton', N'm.hamilton@example.invalid',     N'Margaret');
GO

INSERT INTO dbo.LoginAudit (UserName, IpAddress, Payload)
VALUES
    (N'ada.lovelace@example.invalid', '10.0.0.7',
     N'{"event":"login","email":"ada.lovelace@example.invalid","ssn":"123-45-6789"}'),
    (N'grace.hopper@example.invalid', '10.0.0.8',
     N'{"event":"login","email":"grace.hopper@example.invalid","phone":"212-555-0101"}');
GO

/*  The heap. Deliberately more than one row with the SAME values in every
    column: with no key there is nothing to tell those rows apart, which is the
    situation the keyless fallback has to handle without losing or double-
    counting anything.  */
INSERT INTO dbo.ContactImport (SourceFile, Email, Phone, Notes)
VALUES
    (N'contacts-2024-01.csv', N'ada.lovelace@example.invalid', '212-555-0100', N'row 1'),
    (N'contacts-2024-01.csv', N'grace.hopper@example.invalid', '212-555-0101', N'row 2'),
    (N'contacts-2024-01.csv', N'grace.hopper@example.invalid', '212-555-0101', N'row 2'),
    (N'contacts-2024-02.csv', N'alan.turing@example.invalid',  '212-555-0102', NULL);
GO

/* ---------------------------------------------------------------------------
   CDC. Enable at the database level, then on one table.

   If sp_cdc_enable_table reports that SQL Server Agent is not running, that is
   FINE for our purposes — the capture jobs never start, but the metadata
   (is_cdc_enabled, is_tracked_by_cdc) is what SchemaInventory reads.

   If sp_cdc_enable_db fails with a metadata/owner error, the database owner
   does not map to a valid login. Fix with:
       ALTER AUTHORIZATION ON DATABASE::DbScrubTest TO sa;
   (A freshly CREATEd database normally has a valid owner, so this is unlikely
   here; it bites on restored backups, which is a step-2 concern.)
--------------------------------------------------------------------------- */
EXEC sys.sp_cdc_enable_db;
GO

EXEC sys.sp_cdc_enable_table
    @source_schema = N'app',
    @source_name   = N'Enrollment',
    @role_name     = NULL;
GO

/* ---------------------------------------------------------------------------
   What the inventory should find. Compare this against `dbscrub report`.
--------------------------------------------------------------------------- */
SELECT 'Database' AS Fact,
       DB_NAME() AS [Value],
       CAST(is_cdc_enabled AS varchar(1)) AS IsCdcEnabled
FROM sys.databases WHERE database_id = DB_ID();

SELECT s.name AS SchemaName,
       t.name AS TableName,
       t.temporal_type,
       t.temporal_type_desc,
       t.is_tracked_by_cdc,
       h.name AS HistoryTable
FROM sys.tables AS t
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
LEFT  JOIN sys.tables  AS h ON h.object_id = t.history_table_id
WHERE t.is_ms_shipped = 0
  AND s.name NOT IN ('sys', 'cdc', 'INFORMATION_SCHEMA')
ORDER BY s.name, t.name;

/*  Primary keys in KEY ORDER — what the mask engine batches on. dbo.PersonHistory
    and dbo.ContactImport should be absent: SQL Server gives a history table a
    clustered index rather than a primary key, and the heap has neither.  */
SELECT s.name AS SchemaName,
       t.name AS TableName,
       ic.key_ordinal,
       c.name AS KeyColumn
FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c        ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
INNER JOIN sys.tables  AS t        ON t.object_id  = i.object_id
INNER JOIN sys.schemas AS s        ON s.schema_id  = t.schema_id
WHERE i.is_primary_key = 1
  AND t.is_ms_shipped = 0
ORDER BY s.name, t.name, ic.key_ordinal;

/*  Uniqueness rules OTHER than the primary key — what the planner refuses a
    constant strategy on. Expect two, both on app.Membership: the constraint
    reports is_unique_constraint = 1, the index reports 0, and dbscrub treats
    them identically because SQL Server enforces them identically.  */
SELECT s.name AS SchemaName,
       t.name AS TableName,
       i.name AS IndexName,
       i.is_unique_constraint,
       ic.key_ordinal,
       c.name AS ColumnName
FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c        ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
INNER JOIN sys.tables  AS t        ON t.object_id  = i.object_id
INNER JOIN sys.schemas AS s        ON s.schema_id  = t.schema_id
WHERE i.is_unique = 1
  AND i.is_primary_key = 0
  AND t.is_ms_shipped = 0
ORDER BY s.name, t.name, i.name, ic.key_ordinal;
GO

PRINT 'DbScrubTest created. Expect 6 user tables: app.Enrollment, app.Membership, dbo.ContactImport, dbo.LoginAudit, dbo.Person, dbo.PersonHistory.';
GO
