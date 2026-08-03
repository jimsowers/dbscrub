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

        Assert.Equal("Xxxxxx42", result);
        Assert.Equal("Lovelace".Length, result!.Length);
    }

    [Fact]
    public void AValueShorterThanTheKeyBecomesTheKey()
    {
        // Nothing to overwrite into. The planner refuses this strategy on a
        // column too narrow to hold the widest key, so the result always fits.
        Assert.Equal("12345", Scrambler.ScrambleUnique("Li", "12345"));
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
        // The dangerous direction. Peeling a trailing digit run must not turn a
        // real Social Security number into something the gate excuses.
        Assert.False(PlaceholderRules.IsMaskedOutput("123-45-6789"));
        Assert.False(Scrambler.LooksScrambledWithKey("123-45-6789"));

        // Digits alone are not scrambler output with a key on the end.
        Assert.False(Scrambler.LooksScrambledWithKey("4172"));
        Assert.False(Scrambler.LooksScrambledWithKey("212-555-0100"));
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
