using DbScrub.Core.Configuration;
using DbScrub.Core.Verdicts;
using DbScrub.Tests.Schema;
using Xunit;

namespace DbScrub.Tests.Verdicts;

public class VerdictResolverTests
{
    // ---- the core promise: every live column gets a verdict ----------------

    [Fact]
    public void EveryLiveColumnResolvesToExactlyOneVerdict()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "Email", "Nickname")
            .Table("dbo.LoginAudit", "Id", "UserName")
            .Build();

        var plan = Resolve(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                  { "name": "Email",    "strategy": "scramble" }
                ]},
                { "name": "dbo.LoginAudit", "strategy": "truncate" }
              ]
            }
            """);

        var verdicts = plan.Tables.SelectMany(t => t.Columns).ToList();

        Assert.Equal(5, verdicts.Count);
        Assert.Equal(VerdictKind.Kept, Find(verdicts, "dbo.Person.PersonId").Kind);
        Assert.Equal(VerdictKind.Masked, Find(verdicts, "dbo.Person.Email").Kind);
        Assert.Equal(VerdictKind.Unclassified, Find(verdicts, "dbo.Person.Nickname").Kind);
        Assert.Equal(VerdictKind.Truncated, Find(verdicts, "dbo.LoginAudit.Id").Kind);
        Assert.Equal(VerdictKind.Truncated, Find(verdicts, "dbo.LoginAudit.UserName").Kind);
    }

    [Fact]
    public void UnclassifiedListMatchesTheUnclassifiedVerdicts()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", "PersonId", "Nickname", "Nudge")
            .Build();

        var plan = Resolve(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" }
                ]}
              ]
            }
            """);

        Assert.Equal(["dbo.Person.Nickname", "dbo.Person.Nudge"],
            plan.Unclassified.Select(u => u.QualifiedColumn));
        Assert.False(plan.IsFullyClassified);
    }

    [Fact]
    public void ATableWithNoConfigEntryIsEntirelyUnclassified()
    {
        var schema = SchemaBuilder.Database().Table("dbo.Secrets", "Id", "Value").Build();

        var plan = Resolve(schema, """{ "tables": [] }""");

        Assert.Equal(2, plan.Unclassified.Count);
        Assert.Equal(TableAction.Untouched, plan.Tables.Single().Action);
    }

    [Fact]
    public void AFullyClassifiedSchemaReportsClean()
    {
        var schema = SchemaBuilder.Database().Table("dbo.Person", "PersonId", "Email").Build();

        var plan = Resolve(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "columns": [
                  { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                  { "name": "Email",    "strategy": "static", "value": "dev@example.invalid" }
                ]}
              ]
            }
            """);

        Assert.True(plan.IsFullyClassified);
        Assert.Empty(plan.Problems);
        Assert.Equal(1, plan.ColumnsToMask);
        Assert.Equal(TableAction.Mask, plan.Tables.Single().Action);
    }

    // ---- exemptions --------------------------------------------------------

    [Fact]
    public void TheToolsOwnLogTableIsExemptNotUnclassified()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.__SanitizationLog", "RunUtc", "ConfigHash")
            .Build();

        var plan = Resolve(schema, """{ "tables": [] }""");

        Assert.Empty(plan.Unclassified);
        Assert.Equal(TableAction.ToolOwned, plan.Tables.Single().Action);
        Assert.All(plan.Tables.Single().Columns, c => Assert.Equal(VerdictKind.Exempt, c.Kind));
    }

    [Fact]
    public void TemporalHistoryTablesAreCoveredByTheirParentNotUnclassified()
    {
        // Masking a temporal table without the SYSTEM_VERSIONING dance copies
        // unmasked rows into history (SPEC 5.2). The history table is real and
        // must appear in the plan — but it is not something the user configures,
        // so listing its columns as UNCLASSIFIED would be noise that trains
        // people to ignore the list.
        var schema = SchemaBuilder.Database()
            .TemporalTable("dbo.Person", "dbo.PersonHistory", "PersonId", "Email")
            .Build();

        var plan = Resolve(schema, """
            {
              "tables": [
                { "name": "dbo.Person", "history": "truncate", "columns": [
                  { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" },
                  { "name": "Email",    "strategy": "scramble" }
                ]}
              ]
            }
            """);

        Assert.Empty(plan.Unclassified);

        var history = plan.Tables.Single(t => t.QualifiedName == "dbo.PersonHistory");
        Assert.Equal(TableAction.HistoryOfTemporalTable, history.Action);
        Assert.All(history.Columns, c => Assert.Equal(VerdictKind.CoveredByParent, c.Kind));

        Assert.Equal("dbo.Person", plan.Temporal.Single().QualifiedName);
    }

    [Fact]
    public void CdcTrackedTablesAreSurfacedInThePlan()
    {
        var schema = SchemaBuilder.Database()
            .WithCdcEnabled()
            .CdcTable("dbo.Person", "PersonId")
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" } ]} ] }
            """);

        Assert.True(plan.Schema.IsCdcEnabled);
        Assert.Equal("dbo.Person", plan.CdcTracked.Single().QualifiedName);
    }

    // ---- schema-dependent validation ---------------------------------------

    [Fact]
    public void NullStrategyOnANotNullColumnIsAProblem()
    {
        // The config file alone cannot catch this — it has never seen the
        // database (DECISIONS.md D12).
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.NotNull("Email"))
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "Email", "strategy": "null" } ]} ] }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("NOT NULL", problem.Message);
        Assert.Contains("scramble", problem.Suggestion);
    }

    [Fact]
    public void MaskingAnIdentityColumnIsAProblem()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"))
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "scramble" } ]} ] }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("identity column", problem.Message);
    }

    [Fact]
    public void MaskingAComputedColumnIsAProblem()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Computed("FullName"))
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "FullName", "strategy": "scramble" } ]} ] }
            """);

        Assert.Contains("computed column", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void KeepingAnIdentityColumnIsFine()
    {
        var schema = SchemaBuilder.Database()
            .Table("dbo.Person", SchemaBuilder.Identity("PersonId"))
            .Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" } ]} ] }
            """);

        Assert.Empty(plan.Problems);
    }

    // ---- stale config ------------------------------------------------------

    [Fact]
    public void ConfigForATableThatNoLongerExistsIsAProblem()
    {
        var schema = SchemaBuilder.Database().Table("dbo.Person", "PersonId").Build();

        var plan = Resolve(schema, """
            {
              "tables": [
                { "name": "dbo.Person",  "columns": [
                    { "name": "PersonId", "strategy": "keep", "reason": "surrogate key" } ]},
                { "name": "dbo.Retired", "strategy": "truncate" }
              ]
            }
            """);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("dbo.Retired", problem.Message);
        Assert.Contains("does not exist", problem.Message);
    }

    [Fact]
    public void ConfigForAColumnThatNoLongerExistsIsAProblem()
    {
        // The dangerous case: the entry reads as protection and delivers none.
        var schema = SchemaBuilder.Database().Table("dbo.Person", "PersonId").Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Person", "columns": [
                { "name": "PersonId",  "strategy": "keep", "reason": "surrogate key" },
                { "name": "OldEmail",  "strategy": "scramble" } ]} ] }
            """);

        Assert.Contains("dbo.Person.OldEmail", Assert.Single(plan.Problems).Message);
    }

    // ---- matching rules ----------------------------------------------------

    [Fact]
    public void TableAndColumnMatchingIsCaseInsensitive()
    {
        // SQL Server's default collation is case-insensitive; a config that
        // says "email" for a column named "Email" means the same column.
        var schema = SchemaBuilder.Database().Table("dbo.Person", "Email").Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "DBO.PERSON", "columns": [
                { "name": "email", "strategy": "scramble" } ]} ] }
            """);

        Assert.Empty(plan.Unclassified);
        Assert.Empty(plan.Problems);
        Assert.Equal(VerdictKind.Masked, plan.Tables.Single().Columns.Single().Kind);
    }

    [Fact]
    public void ATableWhereEveryColumnIsKeptIsUntouched()
    {
        var schema = SchemaBuilder.Database().Table("dbo.Lookup", "Code", "Label").Build();

        var plan = Resolve(schema, """
            { "tables": [ { "name": "dbo.Lookup", "columns": [
                { "name": "Code",  "strategy": "keep", "reason": "reference data" },
                { "name": "Label", "strategy": "keep", "reason": "reference data" } ]} ] }
            """);

        Assert.Equal(TableAction.Untouched, plan.Tables.Single().Action);
        Assert.Equal(0, plan.ColumnsToMask);
        Assert.True(plan.IsFullyClassified);
    }

    // ---- helpers -----------------------------------------------------------

    private static ScrubPlan Resolve(Core.Schema.DatabaseSchema schema, string configJson) =>
        VerdictResolver.Resolve(schema, MaskingConfigLoader.Parse(configJson, "test"));

    private static ColumnVerdict Find(IEnumerable<ColumnVerdict> verdicts, string qualifiedColumn) =>
        verdicts.Single(v => v.QualifiedColumn == qualifiedColumn);
}
