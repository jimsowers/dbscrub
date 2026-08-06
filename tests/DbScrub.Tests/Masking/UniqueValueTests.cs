using DbScrub.Core.Configuration;
using DbScrub.Core.Masking;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;
using DbScrub.Core.Verify;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Masking;

/// <summary>
/// Per-row distinct values (DECISIONS.md D23, D26): `scramble` with
/// `"unique": "key"`, and the generated `email` strategy.
///
/// The through-line in every test here is that a value the tool WRITES must
/// still be recognisable to the tool that CHECKS. Masking that a correct verify
/// pass would reject is not masking; it is a database that can never be stamped.
/// </summary>
public class UniqueValueTests
{
    // ---- the discriminator -------------------------------------------------

    [Fact]
    public void TheDiscriminatorIsTheRowsKey()
    {
        Assert.Equal("42", RowDiscriminator.For([42]));
        Assert.Equal("42-7", RowDiscriminator.For([42, 7]));
    }

    [Fact]
    public void TheSameKeyAlwaysGivesTheSameValue()
    {
        // Stability is the point. A developer who bookmarks person 4172 gets the
        // same fake name after every refresh; a random scheme breaks that daily.
        Assert.Equal(
            Scrambler.ScrambleUnique("Lovelace", RowDiscriminator.For([4172])),
            Scrambler.ScrambleUnique("Lovelace", RowDiscriminator.For([4172])));
    }

    [Fact]
    public void DifferentRowsGetDifferentValuesEvenFromIdenticalInput()
    {
        // The whole requirement: two people called Smith must not both become
        // Xxxxx, or the dev database is useless for telling records apart.
        var first = Scrambler.ScrambleUnique("Smith", RowDiscriminator.For([1]));
        var second = Scrambler.ScrambleUnique("Smith", RowDiscriminator.For([2]));

        Assert.NotEqual(first, second);
    }

    // ---- scramble + unique -------------------------------------------------

    [Fact]
    public void TheKeyOverwritesTheTailSoLengthIsPreserved()
    {
        // Appending would overflow a fixed-width column and break the one
        // guarantee scramble exists for.
        var result = Scrambler.ScrambleUnique("Lovelace", "42");

        Assert.Equal("Xxxxx#42", result);
        Assert.Equal("Lovelace".Length, result!.Length);
    }

    [Fact]
    public void AValueShorterThanTheKeyBecomesTheKey()
    {
        // Nothing to overwrite into. The planner refuses this strategy on a
        // column too narrow to hold the widest key, so the result always fits.
        Assert.Equal("#12345", Scrambler.ScrambleUnique("Li", "12345"));
    }

    // ---- the delimiter, and the collision it closes (D28) --------------------

    [Fact]
    public void TwoDifferentKeysCannotProduceTheSameValue()
    {
        // The regression that named D28. Without the delimiter both of these
        // masked to `xxx995`: one splits as "xxx99" + key "5", the other as
        // "xxx9" + key "95", and nothing in the text says which. A scrambled
        // digit is a 9 and a key digit is a digit, so the boundary was invisible.
        var five = Scrambler.ScrambleUnique("emp005", "5");
        var ninetyFive = Scrambler.ScrambleUnique("emp095", "95");

        Assert.NotEqual(five, ninetyFive);
    }

    [Fact]
    public void ValuesFullOfDigitsStayDistinctAcrossAWholeTable()
    {
        // The measured case: 5,000 zero-padded account codes, each masked with
        // its own row key. Before the delimiter this produced 99 duplicates —
        // on a column whose whole reason for using "unique" is that duplicates
        // are not allowed.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var key = 1; key <= 5000; key++)
        {
            var masked = Scrambler.ScrambleUnique($"acct{key:D5}", key.ToString())!;

            Assert.True(seen.Add(masked), $"Row {key} reused a value another row already has.");
        }
    }

    [Fact]
    public void TheDelimiterIsPaidForInTheWidthCheck()
    {
        // An int key is 11 characters at its widest, so a spliced value needs 12.
        // The email strategy does NOT pay this, because its key sits between a
        // fixed prefix and a fixed domain and needs no delimiter.
        var key = new[] { SchemaBuilder.Identity("PersonId") };

        Assert.Equal(11, RowDiscriminator.MaxWidth(key));
        Assert.Equal(12, RowDiscriminator.MaxSplicedWidth(key));
    }

    [Fact]
    public void AColumnTooNarrowForTheDelimitedKeyIsRefusedBeforeTheRun()
    {
        // 11 characters holds the widest int key but not the delimiter too.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"),
                new SchemaColumn("Code", "char", IsNullable: true, MaxLength: 11,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "columns": [
                { "name": "Code", "strategy": "scramble", "unique": "key" } ] }
            """);

        Assert.False(plan.CanRun);
        Assert.Contains("at least 12 characters", Assert.Single(plan.Problems).Message);
    }

    [Theory]
    [InlineData("varchar")]           // can hold '#' itself: the split lands in two places
    [InlineData("uniqueidentifier")]  // renders as hex, whose a-f the gate reads as letters
    [InlineData("datetime2")]         // brings ':' and 'T' along
    public void AKeyThatCannotBeSplicedIsRefused(string keyType)
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.NotNull("Code", keyType), SchemaBuilder.Column("LastName"))
            .WithPrimaryKey("Code")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "columns": [
                { "name": "LastName", "strategy": "scramble", "unique": "key" } ] }
            """);

        Assert.False(plan.CanRun);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains($"Code ({keyType})", problem.Message);
        Assert.Contains("\"email\"", problem.Suggestion!);
    }

    [Fact]
    public void EveryKeyTypeThePlannerAcceptsSurvivesTheVerifyGate()
    {
        // The invariant behind the allowlist, asserted rather than argued. A key
        // type the planner accepts but the gate does not recognise would be a
        // config that passes planning and then can never be stamped — accepted,
        // then explodes, which is what plan-time refusals exist to prevent.
        foreach (var keyType in new[] { "tinyint", "smallint", "int", "bigint" })
        {
            var key = new[] { SchemaBuilder.NotNull("Id", keyType) };

            Assert.True(RowDiscriminator.CanSplice(key), keyType);

            // A negative key brings a '-' along, and a composite brings another.
            foreach (var discriminator in new[] { "1", "4172", "-5", "1-100" })
            {
                var masked = Scrambler.ScrambleUnique("Lovelace", discriminator);

                Assert.True(PlaceholderRules.IsMaskedOutput(masked),
                    $"{keyType} key '{discriminator}' produced a value the gate calls a leak: {masked}");
            }
        }
    }

    [Theory]
    [InlineData("varbinary")]
    [InlineData("binary")]
    [InlineData("rowversion")]
    public void AKeyThatReadsAsBytesIsRefusedForEmail(string keyType)
    {
        // Convert.ToString on a byte array returns the TYPE NAME, "System.Byte[]",
        // identical for every row — so every address would be the same, and the
        // verify gate would not catch it, because the resulting value does not
        // match the email pattern and is therefore never inspected. It would
        // pass, and be stamped.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.NotNull("Id", keyType),
                new SchemaColumn("Email", "nvarchar", IsNullable: true, MaxLength: 512,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("Id")
            .Build();

        var plan = Plan(schema,
            """{ "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "email" } ] }""");

        Assert.False(plan.CanRun);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains($"Id ({keyType})", problem.Message);
        Assert.Contains("bytes rather than as a value", problem.Message);
    }

    [Fact]
    public void EveryKeyThatCanBeSplicedCanAlsoBeRendered()
    {
        // The two checks guard different things — one the text's alphabet, the
        // other whether there is a value at all — and `email` uses only the
        // second, so it accepts keys `unique` refuses. What must never happen is
        // the reverse: a key good enough to splice but not good enough to read.
        foreach (var keyType in new[] { "tinyint", "smallint", "int", "bigint" })
        {
            var key = new[] { SchemaBuilder.NotNull("Id", keyType) };

            Assert.True(RowDiscriminator.CanSplice(key), keyType);
            Assert.True(RowDiscriminator.CanRender(key), keyType);
        }

        // And the keys email keeps that unique gives up.
        var guid = new[] { SchemaBuilder.NotNull("Id", "uniqueidentifier") };

        Assert.False(RowDiscriminator.CanSplice(guid));
        Assert.True(RowDiscriminator.CanRender(guid));
    }

    [Fact]
    public void ACompositeOfIntegersCanBeSpliced()
    {
        Assert.True(RowDiscriminator.CanSplice(
            [SchemaBuilder.Identity("OrgId"), SchemaBuilder.Identity("MemberNumber")]));

        // One bad column is enough to fail the whole key.
        Assert.False(RowDiscriminator.CanSplice(
            [SchemaBuilder.Identity("OrgId"), SchemaBuilder.NotNull("Code", "varchar")]));
    }

    [Fact]
    public void AnEmailColumnDoesNotPayForADelimiterItDoesNotUse()
    {
        // The key sits between "fakeemail" and "@notreal.invalid", so both
        // boundaries are fixed and the value is unambiguous without one. A shared
        // width number would have made email columns need a character too many.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.NotNull("Code", "varchar"),
                new SchemaColumn("Email", "varchar", IsNullable: true, MaxLength: 256,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("Code")
            .Build();

        // Same varchar key the test above refuses for scramble.
        var plan = Plan(schema,
            """{ "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "email" } ] }""");

        Assert.True(plan.CanRun);
    }

    [Fact]
    public void NullStaysNull()
    {
        // A null row is not a duplicate of anything, so uniqueness has nothing
        // to say about it.
        Assert.Null(Scrambler.ScrambleUnique(null, "42"));
    }

    [Fact]
    public void AUniquelyScrambledValueIsRecognisedAsMaskedOutput()
    {
        // Without this the verify gate flags every uniquely-masked column and no
        // correctly scrubbed database can be stamped — the D17 trap again.
        var scrambled = Scrambler.ScrambleUnique("ada.lovelace@example.invalid", "42");

        Assert.True(PlaceholderRules.IsMaskedOutput(scrambled),
            $"The gate would report this as a leak: {scrambled}");
    }

    [Fact]
    public void ARealValueIsNotMistakenForAUniquelyScrambledOne()
    {
        // The dangerous direction. A real Social Security number carries no
        // delimiter, so there is nothing for the rule to split on.
        Assert.False(PlaceholderRules.IsMaskedOutput("123-45-6789"));
        Assert.False(Scrambler.LooksScrambledWithKey("123-45-6789"));

        // Digits alone are not scrambler output with a key on the end.
        Assert.False(Scrambler.LooksScrambledWithKey("4172"));
        Assert.False(Scrambler.LooksScrambledWithKey("212-555-0100"));

        // A delimiter is not enough on its own: what precedes it still has to be
        // scrambler output, and a real name is not.
        Assert.False(Scrambler.LooksScrambledWithKey("Lovelace#42"));

        // Nor is a delimiter with something that is not a key after it.
        Assert.False(Scrambler.LooksScrambledWithKey("xxxx#alan"));
        Assert.False(Scrambler.LooksScrambledWithKey("xxxx#"));
    }

    [Fact]
    public void ThePrefixIsReadFromTheLastDelimiterNotTheFirst()
    {
        // Scramble PRESERVES punctuation, so a value that already contained a
        // '#' keeps it — and the splice always adds the rightmost one. Splitting
        // at the first would leave "9#xxx" as the supposed key and flag a
        // correctly masked value as a leak.
        var masked = Scrambler.ScrambleUnique("ref#99/abcd", "42")!;

        Assert.Contains(Scrambler.KeyDelimiter, masked);
        Assert.True(Scrambler.LooksScrambledWithKey(masked), $"Not recognised: {masked}");
    }

    [Fact]
    public void ACompositeKeyIsStillRecognisedAsMaskedOutput()
    {
        // The composite separator is a hyphen, which the key half of the rule
        // has to allow without allowing letters.
        var masked = Scrambler.ScrambleUnique("alovelace", RowDiscriminator.For([1, 100]))!;

        Assert.Equal("xxx#1-100", masked);
        Assert.True(PlaceholderRules.IsMaskedOutput(masked));
    }

    // ---- the email strategy ------------------------------------------------

    [Fact]
    public void GeneratedAddressesAreDistinctAndNonDeliverable()
    {
        Assert.Equal("fakeemail15@notreal.invalid", FakeEmail.For("15"));
        Assert.NotEqual(FakeEmail.For("15"), FakeEmail.For("16"));

        // RFC 2606 reserves .invalid permanently, so this can never resolve and
        // dev-environment mail can never leave the building.
        Assert.EndsWith(".invalid", FakeEmail.For("15"));
    }

    [Fact]
    public void AGeneratedAddressStillLooksLikeAnEmailToTheApplication()
    {
        // Shape has to survive or forms stop validating — the same reason
        // scramble preserves length.
        Assert.Contains(VerifyPattern.All, p => p.Name == "email" && p.Matches(FakeEmail.For("15")));
    }

    [Fact]
    public void AndIsRecognisedByTheVerifyGateWithoutBeingDeclared()
    {
        // THE advantage over a `static` address. The tool defined this shape, so
        // it can recognise a million distinct values with no list to maintain —
        // which is exactly what a per-row value makes impossible otherwise.
        Assert.True(PlaceholderRules.IsMaskedOutput(FakeEmail.For("15")));
        Assert.True(PlaceholderRules.IsMaskedOutput(FakeEmail.For("999999")));
    }

    [Fact]
    public void ARealAddressIsNeverExcused()
    {
        Assert.False(PlaceholderRules.IsMaskedOutput("ada.lovelace@example.invalid"));
        Assert.False(PlaceholderRules.IsMaskedOutput("fakeemail15@real.example.com"));

        // Anchored at both ends: an address buried in a note is still a leak.
        Assert.False(PlaceholderRules.IsMaskedOutput("write to fakeemail15@notreal.invalid please"));
    }

    // ---- what the planner refuses ------------------------------------------

    [Fact]
    public void EmailNeedsAPrimaryKeyToVaryBy()
    {
        var plan = Plan(
            SchemaBuilder.Database().Table("dbo.Import", "Email").Build(),
            """{ "name": "dbo.Import", "columns": [ { "name": "Email", "strategy": "email" } ] }""");

        Assert.False(plan.CanRun);
        Assert.Contains("needs a primary key", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void AColumnTooNarrowForAGeneratedAddressIsRefusedBeforeTheRun()
    {
        // SQL Server error 8152 partway through a run, caught at plan time.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"),
                new SchemaColumn("Email", "varchar", IsNullable: true, MaxLength: 20,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("PersonId")
            .Build();

        var plan = Plan(schema,
            """{ "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "email" } ] }""");

        Assert.False(plan.CanRun);
        Assert.Contains("too narrow", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void UniqueOnStaticIsRefusedBecauseThereIsNowhereToPutTheKey()
    {
        // "dev@example.invalid" with a key on either end is a BROKEN address.
        // Getting it right needs a template, which D23 deferred deliberately.
        var errors = Load("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "static", "value": "dev@example.invalid", "unique": "key" }
            ]} ] }
            """);

        var error = Assert.Single(errors);
        Assert.Equal(ConfigErrorCodes.Contradictory, error.Code);
        Assert.Contains("\"email\"", error.Suggestion!);
    }

    [Fact]
    public void UniqueOnEmailIsRefusedAsRedundant()
    {
        var error = Assert.Single(Load("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "email", "unique": "key" } ]} ] }
            """));

        Assert.Contains("already gives every row a different address", error.Message);
    }

    [Fact]
    public void UniqueOnNullIsRefusedBecauseNothingCanVary()
    {
        Assert.Single(Load("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Notes", "strategy": "null", "unique": "key" } ]} ] }
            """));
    }

    [Fact]
    public void AnUnknownUniqueModeIsRejected()
    {
        var error = Assert.Single(Load("""
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "LastName", "strategy": "scramble", "unique": "random" } ]} ] }
            """));

        Assert.Contains("unknown unique mode", error.Message);
    }

    // ---- how it is masked --------------------------------------------------

    [Fact]
    public void AUniqueColumnForcesRowByRowEvenWithoutScramble()
    {
        // The value differs per row, so it cannot be one set-based statement.
        var plan = Plan(Keyed(), """
            { "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "email" } ] }
            """);

        var table = Assert.Single(plan.Tables);

        Assert.Equal(MaskMode.RowByRow, table.Mode);
        Assert.Equal([ColumnStrategy.Email], table.ComputedColumns.Select(c => c.Strategy));
    }

    [Fact]
    public void AnEmailColumnIsNeverREADFromTheDatabase()
    {
        // Its new value comes from the key alone, so reading the old one would
        // drag real addresses across the wire for nothing.
        var plan = Plan(Keyed(), """
            { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "email" },
                { "name": "LastName", "strategy": "scramble" } ] }
            """);

        var table = Assert.Single(plan.Tables);

        Assert.Equal(["LastName"], table.ReadColumns.Select(c => c.Name));
        Assert.Equal(["Email", "LastName"], table.ComputedColumns.Select(c => c.Name));
        Assert.DoesNotContain("[Email]", MaskSql.SelectBatch(table, isFirstBatch: true));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// An int identity key, as a real table has. The key's TYPE drives the width
    /// check — a wide textual key genuinely does require a wider email column,
    /// which is correct and worth knowing when reading a refusal.
    /// </summary>
    private static DatabaseSchema Keyed() =>
        SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"),
                SchemaBuilder.Column("Email"), SchemaBuilder.Column("LastName"))
            .WithPrimaryKey("PersonId")
            .Build();

    private static MaskPlan Plan(DatabaseSchema schema, string tableJson) =>
        MaskPlanner.Build(VerdictResolver.Resolve(
            schema, MaskingConfigLoader.Parse($$"""{ "tables": [ {{tableJson}} ] }""", "test")));

    private static IReadOnlyList<ConfigError> Load(string json)
    {
        MaskingConfigLoader.TryParse(json, "test", out _, out var errors);
        return errors;
    }
}
