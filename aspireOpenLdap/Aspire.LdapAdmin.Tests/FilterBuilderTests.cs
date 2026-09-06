using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Aspire.OpenLdap;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The search query builder: the flat filter model's compose/parse contract, and the
/// two-way raw ⇄ builder sync in the rendered panel — the raw field wins once hand-edited,
/// until a builder control is touched.
/// </summary>
public sealed class FilterBuilderTests
{
    // ---- Compose ------------------------------------------------------------------------

    private static LdapFilterCondition Cond(string attr, LdapFilterOperator op = LdapFilterOperator.Equals, string value = "") =>
        new(attr, op, value);

    [Fact]
    public void Compose_Of_Nothing_Matches_Everything()
    {
        Assert.Equal("(objectClass=*)", LdapFilterConditions.Compose([], LdapFilterJoin.All));
        Assert.Equal("(objectClass=*)", LdapFilterConditions.Compose([Cond("")], LdapFilterJoin.All));
    }

    [Fact]
    public void Compose_Renders_Each_Operator_And_Wraps_Only_When_Plural()
    {
        Assert.Equal("(uid=alice)", LdapFilterConditions.Compose([Cond("uid", value: "alice")], LdapFilterJoin.All));
        Assert.Equal("(mail=*)", LdapFilterConditions.Compose([Cond("mail", LdapFilterOperator.Present)], LdapFilterJoin.Any));
        Assert.Equal("(uidNumber>=1000)", LdapFilterConditions.Compose([Cond("uidNumber", LdapFilterOperator.GreaterOrEqual, "1000")], LdapFilterJoin.All));
        Assert.Equal("(&(uid=alice)(uidNumber<=2000))", LdapFilterConditions.Compose(
            [Cond("uid", value: "alice"), Cond("uidNumber", LdapFilterOperator.LessOrEqual, "2000")], LdapFilterJoin.All));
        Assert.Equal("(|(cn=a*)(sn=b))", LdapFilterConditions.Compose(
            [Cond("cn", value: "a*"), Cond("sn", value: "b")], LdapFilterJoin.Any));
    }

    [Fact]
    public void Compose_Treats_An_Empty_Value_As_Presence()
    {
        Assert.Equal("(cn=*)", LdapFilterConditions.Compose([Cond("cn")], LdapFilterJoin.All));
    }

    // ---- Parse --------------------------------------------------------------------------

    [Fact]
    public void Parse_Round_Trips_What_Compose_Writes()
    {
        var conditions = new[]
        {
            Cond("uid", value: "ali*"),
            Cond("mail", LdapFilterOperator.Present),
            Cond("uidNumber", LdapFilterOperator.GreaterOrEqual, "1000"),
        };
        foreach (var join in new[] { LdapFilterJoin.All, LdapFilterJoin.Any })
        {
            var parse = LdapFilterConditions.Parse(LdapFilterConditions.Compose(conditions, join));
            Assert.False(parse.Lossy);
            Assert.Equal(join, parse.Join);
            Assert.Equal(conditions.Select(static c => (c.Attribute, c.Operator, c.Value)),
                parse.Conditions.Select(static c => (c.Attribute, c.Operator, c.Value)));
        }
    }

    [Fact]
    public void Parse_Reads_A_Bare_Condition_As_All_Join()
    {
        var parse = LdapFilterConditions.Parse("(uid=alice)");
        Assert.False(parse.Lossy);
        Assert.Equal(LdapFilterJoin.All, parse.Join);
        var condition = Assert.Single(parse.Conditions);
        Assert.Equal(("uid", LdapFilterOperator.Equals, "alice"), (condition.Attribute, condition.Operator, condition.Value));
    }

    [Theory]
    [InlineData("(!(uid=alice))")]                        // negation has no builder row
    [InlineData("(&(uid=a)(|(cn=b)(sn=c)))")]             // nesting beyond one level
    [InlineData("(cn~=alice)")]                           // approx match
    [InlineData("(cn:caseExactMatch:=Alice)")]            // extensible match
    [InlineData("not a filter")]                          // no condition at all
    public void Parse_Reports_What_The_Flat_Shape_Loses(string raw) =>
        Assert.True(LdapFilterConditions.Parse(raw).Lossy);

    [Fact]
    public void Parse_Is_Not_Lossy_For_Flat_Filters()
    {
        Assert.False(LdapFilterConditions.Parse("(&(uid=a)(cn=b))").Lossy);
        Assert.False(LdapFilterConditions.Parse("(|(uid=a)(mail=*))").Lossy);
    }

    // ---- Two-way sync in the rendered panel ---------------------------------------------

    /// <summary>
    /// A real service layer pointed at a port nothing listens on: the sync under test is
    /// pure UI state, and the panel's background OU lookup fails into its documented
    /// empty-datalist fallback.
    /// </summary>
    private static TestContext PanelContext()
    {
        var ctx = new TestContext();
        var connectionString = new OpenLdapConnectionStringBuilder
        {
            Endpoint = new Uri("ldap://127.0.0.1:1"),
            BaseDn = "dc=example,dc=org",
            BindDn = "cn=admin,dc=example,dc=org",
            BindPassword = "unused",
        }.Build();
        var factory = new OpenLdapClientFactory(
            OpenLdapConnectionStringBuilder.Parse(connectionString),
            new OpenLdapClientSettings { ConnectionString = connectionString });
        var schema = new LdapSchemaService(factory, NullLogger<LdapSchemaService>.Instance);
        ctx.Services.AddSingleton(schema);
        ctx.Services.AddSingleton(new LdapDirectoryService(factory, schema));
        ctx.Services.AddSingleton(new LdapAdminSettings());
        ctx.Services.AddSingleton(new ConsoleToastService());
        ctx.Services.AddSingleton(new ConsoleClipboard(ctx.JSInterop.JSRuntime));
        return ctx;
    }

    private static string Raw(IRenderedFragment cut) =>
        cut.Find("input[aria-label='LDAP filter']").GetAttribute("value")!;

    [Fact]
    public void Builder_Starts_As_Match_Everything_And_Composes_Into_The_Raw_Field()
    {
        using var ctx = PanelContext();
        var cut = ctx.RenderComponent<SearchPanel>();

        Assert.Equal("(objectClass=*)", Raw(cut));
        var row = cut.Find(".frow");
        Assert.Equal("objectClass", row.QuerySelector("input[aria-label='Condition attribute']")!.GetAttribute("value"));

        // Touching the builder rewrites the raw filter from the rows.
        row.QuerySelector("input[aria-label='Condition attribute']")!.Input("mail");
        Assert.Equal("(mail=*)", Raw(cut));
    }

    [Fact]
    public void Editing_The_Raw_Filter_Projects_Into_The_Builder_Rows()
    {
        using var ctx = PanelContext();
        var cut = ctx.RenderComponent<SearchPanel>();

        cut.Find("input[aria-label='LDAP filter']").Input("(|(uid=alice)(uidNumber>=1000))");

        var rows = cut.FindAll(".frow");
        Assert.Equal(2, rows.Count);
        Assert.Equal("uid", rows.First().QuerySelector("input[aria-label='Condition attribute']")!.GetAttribute("value"));
        Assert.Equal("alice", rows.First().QuerySelector("input[aria-label='Condition value']")!.GetAttribute("value"));
        Assert.Equal("ge", rows.ElementAt(1).QuerySelector("select")!.GetAttribute("value"));
        // The | join is reflected in the segmented control.
        Assert.True(cut.FindAll(".seg-opt input").ElementAt(1).HasAttribute("checked"));
    }

    [Fact]
    public void The_Raw_Field_Wins_Until_A_Builder_Control_Is_Touched()
    {
        using var ctx = PanelContext();
        var cut = ctx.RenderComponent<SearchPanel>();

        // Hand-edit the raw filter to something beyond the builder: it stays verbatim,
        // and the panel says the projection is lossy.
        cut.Find("input[aria-label='LDAP filter']").Input("(!(uid=alice))");
        Assert.Equal("(!(uid=alice))", Raw(cut));
        Assert.Contains("cannot show", cut.Find("p.note").TextContent, StringComparison.Ordinal);

        // Touching a builder control adopts the flat projection and rewrites the raw field.
        cut.Find("button.btn-secondary.btn-sm").Click(); // Add condition
        Assert.Equal(2, cut.FindAll(".frow").Count);     // uid=alice + the new empty row
        Assert.Equal("(uid=alice)", Raw(cut));           // negation is gone — and said so beforehand
    }

    [Fact]
    public void Switching_The_Join_Rewrites_The_Raw_Filter()
    {
        using var ctx = PanelContext();
        var cut = ctx.RenderComponent<SearchPanel>();

        cut.Find("input[aria-label='LDAP filter']").Input("(&(uid=a)(cn=b))");
        cut.FindAll(".seg-opt input").ElementAt(1).Change(true);

        Assert.Equal("(|(uid=a)(cn=b))", Raw(cut));
    }

    [Fact]
    public void Removing_A_Condition_Rewrites_The_Raw_Filter()
    {
        using var ctx = PanelContext();
        var cut = ctx.RenderComponent<SearchPanel>();

        cut.Find("input[aria-label='LDAP filter']").Input("(&(uid=a)(cn=b))");
        cut.FindAll("button[aria-label='Remove condition']").First().Click();

        Assert.Equal("(cn=b)", Raw(cut));
    }
}
