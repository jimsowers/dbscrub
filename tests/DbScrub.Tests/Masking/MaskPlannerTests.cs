using DbScrub.Core.Configuration;
using DbScrub.Core.Masking;
using DbScrub.Core.Schema;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Masking;

/// <summary>
/// How each table gets masked, and what the planner refuses. Everything here is
/// decided before `clean` modifies anything — a refusal at plan time stops a
/// run, while the same refusal at execution time leaves a half-masked database.
/// </summary>
public class MaskPlannerTests
{
    // ---- choosing a mode ---------------------------------------------------

    [Fact]
    public void AScrambledTableIsWalkedRowByRow()
    {
        // Scramble's replacement is a function of the value being replaced, so
        // each row needs its own UPDATE, so each row needs a key to address it.
        var plan = Plan(
            Keyed("dbo.Person", "PersonId", "Email"),
            """{ "name": "dbo.Person", "columns": [ { "name": "Email", "strategy": "scramble" } ] }""");

        Assert.Equal(MaskMode.RowByRow, Assert.Single(plan.Tables).Mode);
    }

    [Fact]
    public void ATableWithOnlyConstantStrategiesIsSetBased()
    {
        // Nothing depends on the old value, so no row has to be read at all. The
        // key walk survives only to keep each transaction bounded.
        var plan = Plan(
            Keyed("dbo.Person", "PersonId", "FirstName", "Notes"),
            """
            { "name": "dbo.Person", "columns": [
                { "name": "FirstName", "strategy": "static", "value": "Dev" },
                { "name": "Notes", "strategy": "null" } ] }
            """);

        Assert.Equal(MaskMode.BatchedConstant, Assert.Single(plan.Tables).Mode);
    }

    [Fact]
    public void AKeylessTableWithOnlyConstantStrategiesIsOneStatement()
    {
        // SPEC 5.3's fallback. Correct, and unbounded — which is why the report
        // names these tables separately.
        var plan = Plan(
            SchemaBuilder.Database().Table("dbo.ContactImport", "Email").Build(),
            """{ "name": "dbo.ContactImport", "columns": [ { "name": "Email", "strategy": "null" } ] }""");

        Assert.True(plan.CanRun);
        Assert.Equal(MaskMode.WholeTable, Assert.Single(plan.Tables).Mode);
    }

    [Fact]
    public void OneScrambleColumnIsEnoughToForceRowByRow()
    {
        var plan = Plan(
            Keyed("dbo.Person", "PersonId", "Email", "FirstName"),
            """
            { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" },
                { "name": "FirstName", "strategy": "static", "value": "Dev" } ] }
            """);

        var table = Assert.Single(plan.Tables);

        Assert.Equal(MaskMode.RowByRow, table.Mode);
        Assert.Equal(["Email"], table.ComputedColumns.Select(c => c.Name));
        Assert.Equal(["FirstName"], table.ConstantColumns.Select(c => c.Name));
    }

    // ---- refusals ----------------------------------------------------------

    [Fact]
    public void ScramblingAKeylessTableIsRefused()
    {
        // There is no safe fallback. SPEC 5.3 says the keyless case degrades to
        // a set-based UPDATE and that all the strategies are expressible in
        // T-SQL — true of null and static, not of scramble. See DECISIONS.md D19.
        var plan = Plan(
            SchemaBuilder.Database().Table("dbo.ContactImport", "Email").Build(),
            """{ "name": "dbo.ContactImport", "columns": [ { "name": "Email", "strategy": "scramble" } ] }""");

        Assert.False(plan.CanRun);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("no primary key", problem.Message);
        Assert.Contains("Email", problem.Message);
        Assert.Contains("add a primary key", problem.Suggestion!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScramblingANonTextColumnIsRefused()
    {
        // Scramble replaces letters and digits character by character. A date
        // whose digits became 9s is not a date, and an int has no letters.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Column("PersonId"),
                new SchemaColumn("Balance", "decimal", IsNullable: true, MaxLength: 9,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("PersonId")
            .Build();

        var plan = Plan(schema,
            """{ "name": "dbo.Person", "columns": [ { "name": "Balance", "strategy": "scramble" } ] }""");

        Assert.False(plan.CanRun);
        Assert.Contains("decimal", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void AStaticValueTheColumnCannotHoldIsRefusedBeforeTheRunStarts()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Column("PersonId"),
                new SchemaColumn("Ssn", "char", IsNullable: true, MaxLength: 11,
                    IsComputed: false, IsIdentity: false))
            .WithPrimaryKey("PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "columns": [
                { "name": "Ssn", "strategy": "static", "value": "not-a-social-security-number" } ] }
            """);

        Assert.False(plan.CanRun);
        Assert.Contains("28 characters long", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void AProblemNamesTheTableAndColumnSoItCanBeFound()
    {
        var plan = Plan(
            SchemaBuilder.Database().Table("app.Enrollment", "Notes").Build(),
            """{ "name": "app.Enrollment", "columns": [ { "name": "Notes", "strategy": "scramble" } ] }""");

        var problem = Assert.Single(plan.Problems);

        Assert.Contains("app.Enrollment", problem.Message);
        Assert.Equal("tables[app.Enrollment]", problem.Path);
    }

    // ---- the primary key is not maskable -----------------------------------

    [Fact]
    public void MaskingAKeyColumnIsRefusedByTheVerdictPass()
    {
        // Three independent reasons, any one of them fatal: it breaks the walk
        // the mask engine batches with, it collapses distinct keys onto one
        // value, and it orphans every row referencing it.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Column("Ssn"), SchemaBuilder.Column("Email"))
            .WithPrimaryKey("Ssn")
            .Build();

        var scrub = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Ssn", "strategy": "scramble" },
                { "name": "Email", "strategy": "scramble" } ] } ] }
            """);

        var problem = Assert.Single(scrub.Problems);

        Assert.Contains("part of the primary key", problem.Message);
        Assert.Contains("Ssn", problem.Message);
    }

    [Fact]
    public void AnIdentityKeyProducesOneComplaintNotTwo()
    {
        // It is both an identity column and a key column, and two errors about
        // one line reads as two problems.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"), SchemaBuilder.Column("Email"))
            .WithPrimaryKey("PersonId")
            .Build();

        var scrub = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "scramble" } ] } ] }
            """);

        Assert.Single(scrub.Problems);
    }

    [Fact]
    public void KeepingAKeyColumnIsFine()
    {
        // `keep` is the right verdict for a key, and it is what the sample
        // configs use.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"), SchemaBuilder.Column("Email"))
            .WithPrimaryKey("PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                { "name": "Email", "strategy": "scramble" } ] }
            """);

        Assert.True(plan.CanRun);
        Assert.Equal(["Email"], Assert.Single(plan.Tables).Columns.Select(c => c.Name));
    }

    // ---- temporal history --------------------------------------------------

    [Fact]
    public void HistoryIsNotMaskedByDefault()
    {
        // The default is truncate (DECISIONS.md D5), and the hygiene pass empties
        // it — so the mask engine has nothing to do with it.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "FirstName")
            .WithPrimaryKeyOn("dbo.Person", "PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "columns": [
                { "name": "FirstName", "strategy": "static", "value": "Dev" } ] }
            """);

        Assert.Equal(["dbo.Person"], plan.Tables.Select(t => t.QualifiedName));
    }

    [Fact]
    public void HistoryModeMaskAppliesTheParentsStrategiesToTheHistoryTable()
    {
        // A history table masked by a different rule set would be the same data
        // with different holes in it, so the columns have to match exactly.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "FirstName")
            .WithPrimaryKeyOn("dbo.Person", "PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "history": "mask", "columns": [
                { "name": "FirstName", "strategy": "static", "value": "Dev" } ] }
            """);

        Assert.Equal(["dbo.Person", "dbo.PersonHistory"], plan.Tables.Select(t => t.QualifiedName));

        // A history table has a clustered index, never a primary key, so it
        // cannot be walked — which is fine for a constant strategy.
        Assert.Equal(MaskMode.WholeTable, plan.Tables[1].Mode);
    }

    [Fact]
    public void HistoryModeMaskIsRefusedWhenTheParentScrambles()
    {
        // SQL Server gives a history table a clustered index, never a primary
        // key, so there is no way to address one of its rows. The workaround is
        // the default: truncate it.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "LastName")
            .WithPrimaryKeyOn("dbo.Person", "PersonId")
            .Build();

        var plan = Plan(schema, """
            { "name": "dbo.Person", "history": "mask", "columns": [
                { "name": "LastName", "strategy": "scramble" } ] }
            """);

        Assert.False(plan.CanRun);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("dbo.PersonHistory", problem.Message);
        Assert.Contains("\"history\": \"truncate\"", problem.Suggestion!);
    }

    // ---- helpers -----------------------------------------------------------

    private static DatabaseSchema Keyed(string qualifiedName, string keyColumn, params string[] columns) =>
        SchemaBuilder.Database()
            .Table(qualifiedName, [keyColumn, .. columns])
            .WithPrimaryKey(keyColumn)
            .Build();

    private static ScrubPlan Resolve(DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));

    private static MaskPlan Plan(DatabaseSchema schema, string tableJson) =>
        MaskPlanner.Build(Resolve(schema, $$"""{ "tables": [ {{tableJson}} ] }"""));
}
