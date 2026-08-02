using DbScrub.Core.Configuration;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Verdicts;

/// <summary>
/// Regression tests for temporal period columns.
///
/// Found by running `report` against the DbScrubTest fixture on a real SQL
/// Server 2025 instance. `sys.columns` reports ValidFrom/ValidTo with
/// is_computed = 0 AND is_identity = 0, so the original IsWritable
/// (!IsComputed &amp;&amp; !IsIdentity) said they were ordinary writable
/// datetime2 columns. A config could have asked to scramble ValidFrom, passed
/// every validation, and failed inside the mask engine mid-run.
/// </summary>
public class SystemGeneratedColumnTests
{
    [Fact]
    public void PeriodColumnsAreNotWritable()
    {
        // The bug in one assertion: neither flag the old check looked at is set.
        var validFrom = SchemaBuilder.PeriodStart();

        Assert.False(validFrom.IsComputed);
        Assert.False(validFrom.IsIdentity);
        Assert.True(validFrom.IsSystemGenerated);
        Assert.False(validFrom.IsWritable);
    }

    [Fact]
    public void AnOrdinaryColumnIsNotSystemGenerated()
    {
        var column = SchemaBuilder.Column("Email");

        Assert.False(column.IsSystemGenerated);
        Assert.True(column.IsWritable);
        Assert.Equal("NOT_APPLICABLE", column.GeneratedAlwaysDescription);
    }

    [Fact]
    public void PeriodColumnsAreExemptNotUnclassified()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person",
                SchemaBuilder.Column("Email"),
                SchemaBuilder.PeriodStart(),
                SchemaBuilder.PeriodEnd())
            .Build();

        var plan = Resolve(schema, """{ "tables": [] }""");

        // Email still needs a human. ValidFrom/ValidTo never did.
        Assert.Equal(["dbo.Person.Email"], plan.Unclassified.Select(u => u.QualifiedColumn));

        var verdicts = plan.Tables.Single().Columns;
        Assert.Equal(VerdictKind.Exempt, Find(verdicts, "ValidFrom").Kind);
        Assert.Equal(VerdictKind.Exempt, Find(verdicts, "ValidTo").Kind);
    }

    [Fact]
    public void TheExemptionSaysWhy()
    {
        // A column vanishing from the list with no explanation is worse than
        // one listed unnecessarily.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.PeriodStart())
            .Build();

        var verdict = plan(schema).Tables.Single().Columns.Single();

        Assert.Equal("maintained by SQL Server (AS_ROW_START)", verdict.Reason);

        static ScrubPlan plan(Core.Schema.DatabaseSchema s) =>
            VerdictResolver.Resolve(s, MaskingConfigLoader.Parse("""{ "tables": [] }""", "test"));
    }

    [Fact]
    public void PeriodColumnsAreExemptInAConfiguredTableToo()
    {
        // The table is configured but does not mention ValidFrom/ValidTo, which
        // is the realistic case — a human would never think to list them.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person",
                SchemaBuilder.Column("Email"),
                SchemaBuilder.PeriodStart(),
                SchemaBuilder.PeriodEnd())
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Empty(plan.Unclassified);
        Assert.True(plan.IsFullyClassified);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void MaskingAPeriodColumnIsAProblem()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.PeriodStart())
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "ValidFrom", "strategy": "scramble" } ]} ] }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("maintained by SQL Server (AS_ROW_START)", problem.Message);
        Assert.Contains("GENERATED ALWAYS", problem.Suggestion);
    }

    [Fact]
    public void KeepingAPeriodColumnIsAllowed()
    {
        // Someone who already pasted "keep" entries from an older report should
        // not be punished for it.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.PeriodStart())
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "ValidFrom", "strategy": "keep", "reason": "period column" } ]} ] }
            """);

        Assert.Empty(plan.Problems);
        Assert.Equal(VerdictKind.Kept, plan.Tables.Single().Columns.Single().Kind);
    }

    [Fact]
    public void InsideATruncatedTablePeriodColumnsAreStillTruncated()
    {
        // The exemption is specifically about "unclassified". If the whole table
        // is being emptied, Truncated is the honest verdict for every column.
        var schema = SchemaBuilder.Database()
            .Table("dbo.Audit", SchemaBuilder.Column("UserName"), SchemaBuilder.PeriodStart())
            .Build();

        var plan = Resolve(schema, """{ "tables": [ { "name": "dbo.Audit", "strategy": "truncate" } ] }""");

        Assert.All(plan.Tables.Single().Columns, c => Assert.Equal(VerdictKind.Truncated, c.Kind));
    }

    // ---- helpers -----------------------------------------------------------

    private static ScrubPlan Resolve(Core.Schema.DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));

    private static ColumnVerdict Find(IEnumerable<ColumnVerdict> verdicts, string column) =>
        verdicts.Single(v => v.Column == column);
}
