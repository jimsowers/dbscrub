using DbScrub.Core.Configuration;
using DbScrub.Core.Masking;
using DbScrub.Core.Schema;
using Xunit;

namespace DbScrub.Tests.Masking;

/// <summary>
/// The generated SQL, asserted verbatim. Every string here is something the tool
/// will send to a live database, so these tests are the review surface for the
/// destructive path — a change to the SQL that nobody meant shows up as a failing
/// string comparison rather than as a surprise at 2am.
/// </summary>
public class MaskSqlTests
{
    // ---- reading a batch ---------------------------------------------------

    [Fact]
    public void TheFirstBatchHasNoWhereClauseBecauseThereIsNothingToBeGreaterThan()
    {
        Assert.Equal(
            "SELECT TOP (@batchSize) [PersonId], [Email] FROM [dbo].[Person] ORDER BY [PersonId];",
            MaskSql.SelectBatch(Plan(Scramble("Email")), isFirstBatch: true));
    }

    [Fact]
    public void LaterBatchesSeekPastTheLastKeySeen()
    {
        // A keyset seek, never OFFSET (SPEC 5.3). OFFSET re-reads and discards
        // every row already processed, so walking a table costs O(rows squared).
        Assert.Equal(
            "SELECT TOP (@batchSize) [PersonId], [Email] FROM [dbo].[Person] "
            + "WHERE [PersonId] > @lo0 ORDER BY [PersonId];",
            MaskSql.SelectBatch(Plan(Scramble("Email")), isFirstBatch: false));
    }

    [Fact]
    public void OnlyTheColumnsWhoseNewValueDependsOnTheOldOneAreRead()
    {
        // A static replacement is the same on every row, so reading the current
        // value would be a pointless round trip for it. Reading less also means
        // an nvarchar(max) column nobody is scrambling never crosses the wire.
        var plan = Plan(Scramble("Email"), Static("FirstName", "Dev"), Null("Notes"));

        var sql = MaskSql.SelectBatch(plan, isFirstBatch: true);

        Assert.Contains("[Email]", sql);
        Assert.DoesNotContain("[FirstName]", sql);
        Assert.DoesNotContain("[Notes]", sql);
    }

    // ---- composite keys ----------------------------------------------------

    [Fact]
    public void ACompositeKeyComparesLexicographically()
    {
        // "Greater in the first column that differs." Getting this wrong does
        // not throw — it silently skips rows, and a skipped row here is a row
        // that keeps its real values while the run reports success.
        var plan = CompositePlan();

        Assert.Equal(
            "SELECT TOP (@batchSize) [TenantId], [PersonId], [Email] FROM [dbo].[Person] "
            + "WHERE [TenantId] > @lo0 OR ([TenantId] = @lo0 AND [PersonId] > @lo1) "
            + "ORDER BY [TenantId], [PersonId];",
            MaskSql.SelectBatch(plan, isFirstBatch: false));
    }

    [Fact]
    public void AThreeColumnKeyChainsTheEqualityTerms()
    {
        var table = new SchemaTable("dbo", "Reading", TemporalType.None, false, null, null,
            [Key("A", "int"), Key("B", "int"), Key("C", "int"), Text("Note")])
        {
            PrimaryKey = ["A", "B", "C"],
        };

        var plan = new TableMaskPlan(table, [Scramble("Note")], MaskMode.RowByRow);

        Assert.Contains(
            "WHERE [A] > @lo0 OR ([A] = @lo0 AND [B] > @lo1) "
            + "OR ([A] = @lo0 AND [B] = @lo1 AND [C] > @lo2)",
            MaskSql.SelectBatch(plan, isFirstBatch: false));
    }

    [Fact]
    public void EveryKeyColumnIsOrderedByInKeyOrder()
    {
        // The ORDER BY and the comparison have to agree, or the walk skips rows.
        var sql = MaskSql.SelectKeyBatch(CompositePlan(), isFirstBatch: true);

        Assert.EndsWith("ORDER BY [TenantId], [PersonId];", sql);
    }

    // ---- writing constants -------------------------------------------------

    [Fact]
    public void ANullStrategyWritesTheKeywordNotAParameter()
    {
        // A parameter bound to null and the `null` strategy would look identical
        // in the SQL. Using the keyword means a binding bug can never be
        // mistaken for the strategy working.
        Assert.Equal(
            "UPDATE [dbo].[Person] SET [Notes] = NULL;",
            MaskSql.UpdateWholeTable(Keyless(Null("Notes"))));
    }

    [Fact]
    public void AStaticStrategyWritesAParameterNeverTheValue()
    {
        var sql = MaskSql.UpdateWholeTable(Keyless(Static("FirstName", "Dev")));

        Assert.Equal("UPDATE [dbo].[Person] SET [FirstName] = @c0;", sql);
        Assert.DoesNotContain("Dev", sql);
    }

    [Fact]
    public void ConstantParametersAreNumberedAcrossTheStaticColumnsOnly()
    {
        // The `null` column takes no parameter, so the second static column is
        // @c1 and not @c2. The executor binds them from the same sequence.
        Assert.Equal(
            "UPDATE [dbo].[Person] SET [FirstName] = @c0, [Notes] = NULL, [Email] = @c1;",
            MaskSql.UpdateWholeTable(Keyless(
                Static("FirstName", "Dev"), Null("Notes"), Static("Email", "dev@example.invalid"))));
    }

    [Fact]
    public void ABatchedConstantUpdateRewritesExactlyTheRangeTheSelectReturned()
    {
        Assert.Equal(
            "UPDATE [dbo].[Person] SET [FirstName] = @c0 "
            + "WHERE ([PersonId] > @lo0) AND NOT ([PersonId] > @hi0);",
            MaskSql.UpdateConstantRange(Plan(Static("FirstName", "Dev")), isFirstBatch: false));
    }

    [Fact]
    public void TheFirstBatchedConstantUpdateHasAnUpperBoundOnly()
    {
        Assert.Equal(
            "UPDATE [dbo].[Person] SET [FirstName] = @c0 WHERE NOT ([PersonId] > @hi0);",
            MaskSql.UpdateConstantRange(Plan(Static("FirstName", "Dev")), isFirstBatch: true));
    }

    // ---- writing rows ------------------------------------------------------

    [Fact]
    public void EachRowIsAddressedByItsFullPrimaryKey()
    {
        var sql = MaskSql.UpdateRows(CompositePlan(), rowCount: 2);

        Assert.Equal(
            """
            UPDATE [dbo].[Person] SET [Email] = @v0_0 WHERE [TenantId] = @k0_0 AND [PersonId] = @k0_1;
            UPDATE [dbo].[Person] SET [Email] = @v1_0 WHERE [TenantId] = @k1_0 AND [PersonId] = @k1_1;
            """.ReplaceLineEndings(),
            sql.ReplaceLineEndings());
    }

    [Fact]
    public void AConstantColumnRidesAlongInTheSameStatement()
    {
        // A table mixing scramble and static is still one pass over the rows,
        // and the static parameter is bound once for the whole command.
        var sql = MaskSql.UpdateRows(Plan(Scramble("Email"), Static("FirstName", "Dev")), rowCount: 2);

        Assert.Contains("SET [Email] = @v0_0, [FirstName] = @c0 WHERE [PersonId] = @k0_0;", sql);
        Assert.Contains("SET [Email] = @v1_0, [FirstName] = @c0 WHERE [PersonId] = @k1_0;", sql);
    }

    [Fact]
    public void RowsPerCommandStaysUnderTheParameterCeiling()
    {
        // SQL Server refuses any command carrying more than 2100 parameters. A
        // configured batchSize of 5000 over a three-parameter row would exceed
        // it, and the failure arrives only when the command is sent.
        var plan = CompositePlan();

        var rows = MaskSql.RowsPerCommand(plan, batchSize: 5000);

        // Two key columns plus one computed column per row.
        Assert.Equal(MaskSql.MaxParametersPerCommand / 3, rows);
        Assert.True(rows * 3 <= 2100, "The command must stay under SQL Server's parameter limit.");
    }

    [Fact]
    public void ASmallBatchSizeIsHonoredRatherThanRaisedToTheCeiling()
    {
        Assert.Equal(10, MaskSql.RowsPerCommand(CompositePlan(), batchSize: 10));
    }

    [Fact]
    public void AVeryWideRowStillGetsAtLeastOneRowPerCommand()
    {
        // Otherwise the walk would make no progress and loop forever.
        var columns = Enumerable.Range(0, 400).Select(i => Text($"C{i}")).ToList();
        var table = new SchemaTable("dbo", "Wide", TemporalType.None, false, null, null,
            [Key("Id", "int"), .. columns])
        {
            PrimaryKey = ["Id"],
        };

        var plan = new TableMaskPlan(table,
            columns.Select(c => new MaskColumn(c, ColumnStrategy.Scramble, null)).ToList(),
            MaskMode.RowByRow);

        Assert.True(MaskSql.RowsPerCommand(plan, batchSize: 5000) >= 1);
    }

    // ---- safety ------------------------------------------------------------

    [Fact]
    public void AKeyedStatementRefusesToBuildWithoutAKey()
    {
        // Built anyway, the WHERE clause would be empty and every batch would
        // rewrite the entire table. Better to be a bug that throws.
        var plan = Keyless(Scramble("Email"));

        Assert.Throws<InvalidOperationException>(() => MaskSql.SelectBatch(plan, isFirstBatch: true));
        Assert.Throws<InvalidOperationException>(() => MaskSql.UpdateRows(plan, rowCount: 1));
        Assert.Throws<InvalidOperationException>(() => MaskSql.UpdateConstantRange(plan, isFirstBatch: true));
    }

    [Fact]
    public void IdentifiersAreQuotedSoAHostileNameCannotEndTheStatement()
    {
        var table = new SchemaTable("dbo", "Person]; DROP DATABASE Foo --", TemporalType.None,
            false, null, null, [Key("Id", "int"), Text("Email")])
        {
            PrimaryKey = ["Id"],
        };

        var sql = MaskSql.UpdateWholeTable(new TableMaskPlan(table, [Null("Email")], MaskMode.WholeTable));

        Assert.Equal("UPDATE [dbo].[Person]]; DROP DATABASE Foo --] SET [Email] = NULL;", sql);
    }

    [Fact]
    public void TheRowCountQueryCannotOverflowOnAHugeTable()
    {
        // COUNT returns int and overflows past 2.1 billion rows. That failure
        // would land in the check that proves the run was complete.
        Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[Person];", MaskSql.CountRows(PersonTable()));
    }

    // ---- helpers -----------------------------------------------------------

    private static SchemaColumn Key(string name, string type) =>
        new(name, type, IsNullable: false, MaxLength: 4, IsComputed: false, IsIdentity: true);

    private static SchemaColumn Text(string name) =>
        new(name, "nvarchar", IsNullable: true, MaxLength: 200, IsComputed: false, IsIdentity: false);

    private static MaskColumn Scramble(string name) => new(Text(name), ColumnStrategy.Scramble, null);

    private static MaskColumn Null(string name) => new(Text(name), ColumnStrategy.Null, null);

    private static MaskColumn Static(string name, string value) =>
        new(Text(name), ColumnStrategy.Static, value);

    private static SchemaTable PersonTable() =>
        new("dbo", "Person", TemporalType.None, false, null, null,
            [Key("PersonId", "int"), Text("Email"), Text("FirstName"), Text("Notes")])
        {
            PrimaryKey = ["PersonId"],
        };

    private static TableMaskPlan Plan(params MaskColumn[] columns) =>
        new(PersonTable(), columns,
            columns.Any(c => !c.IsConstant) ? MaskMode.RowByRow : MaskMode.BatchedConstant);

    private static TableMaskPlan Keyless(params MaskColumn[] columns) =>
        new(PersonTable() with { PrimaryKey = [] }, columns, MaskMode.WholeTable);

    private static TableMaskPlan CompositePlan()
    {
        var table = new SchemaTable("dbo", "Person", TemporalType.None, false, null, null,
            [Key("TenantId", "int"), Key("PersonId", "int"), Text("Email")])
        {
            PrimaryKey = ["TenantId", "PersonId"],
        };

        return new TableMaskPlan(table, [Scramble("Email")], MaskMode.RowByRow);
    }
}
