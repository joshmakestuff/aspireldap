using AngleSharp.Dom;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The Schema panel against the live subschema (aspireldap#116). The fetched fixture's
/// <see cref="LdapSchemaService"/> is injected into bUnit, so the full path the issue
/// describes — typing in the filter, the 200 ms debounce, ApplyFilter, the rows the
/// tables render — is what the assertions watch. The per-kind toggles (Part 1) are pinned
/// here too, against the same loaded schema.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaPanelComponentTests(LdapAdminAppHostFixture fixture) : TestContext
{
    /// <summary>Directory String — the syntax <c>cn</c> resolves to.</summary>
    private const string DirectoryStringOid = "1.3.6.1.4.1.1466.115.121.1.15";

    /// <summary>A fragment of the syntax OID family (1.3.6.1.4.1.1466.115.121.1.x).</summary>
    private const string SyntaxFamilyFragment = "1466.115.121";

    /// <summary>The <c>cn</c>/<c>commonName</c> attribute's OID.</summary>
    private const string CnOid = "2.5.4.3";

    private TestContext Context()
    {
        var ctx = new TestContext();
        ctx.Services.AddSingleton(fixture.Schema);
        return ctx;
    }

    /// <summary>Parses "<c>Syntaxes — 27</c>"-style section headings.</summary>
    private static int SectionCount(IRenderedComponent<SchemaPanel> cut, string sectionName)
    {
        var text = cut.FindAll(".sec")
            .Select(static s => s.TextContent)
            .SingleOrDefault(t => t.StartsWith(sectionName, StringComparison.Ordinal));
        Assert.NotNull(text);
        var count = text![(text.IndexOf('—') + 1)..].Trim();
        return int.Parse(count, CultureInfo.InvariantCulture);
    }

    /// <summary>The checkbox that toggles one kind, found by its visible label.</summary>
    private static IElement KindToggle(IRenderedComponent<SchemaPanel> cut, string label) =>
        cut.FindAll("label.seg-opt").Single(l => l.TextContent.Trim() == label).QuerySelector("input")!;

    [Fact]
    public void Filtering_By_A_Partial_Oid_Returns_The_Matching_Rows()
    {
        using var ctx = Context();
        var cut = ctx.RenderComponent<SchemaPanel>();
        var filter = cut.WaitForElement("input[aria-label='Filter the schema']", TimeSpan.FromSeconds(10));

        filter.Input(SyntaxFamilyFragment);

        cut.WaitForAssertion(
            () =>
            {
                // The syntax family fragment is a substring of most syntax OIDs but of no
                // attribute-type or object-class OID here, so exactly the syntaxes section
                // keeps rows — the reported "OID returns nothing" case, pinned.
                Assert.Equal(0, SectionCount(cut, "Object classes"));
                Assert.Equal(0, SectionCount(cut, "Attribute types"));
                Assert.True(SectionCount(cut, "Syntaxes") > 0);

                // And the row for Directory String is actually rendered — matching by OID
                // is not a count-only illusion.
                var syntaxOids = cut.FindAll("table.table").Last()
                    .QuerySelectorAll("tbody tr td:first-child")
                    .Select(td => td.TextContent.Trim());
                Assert.Contains(DirectoryStringOid, syntaxOids);
            },
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Attribute_Types_Are_Matchable_By_A_Bare_Oid()
    {
        using var ctx = Context();
        var cut = ctx.RenderComponent<SchemaPanel>();
        var filter = cut.WaitForElement("input[aria-label='Filter the schema']", TimeSpan.FromSeconds(10));

        filter.Input(CnOid);

        cut.WaitForAssertion(
            () =>
            {
                Assert.True(SectionCount(cut, "Attribute types") > 0);
                var names = cut.FindAll("table.table tbody tr td:first-child")
                    .Select(td => td.TextContent.Trim());
                Assert.Contains("cn", names);
            },
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_Kind_Toggle_Hides_Only_Its_Own_Section_And_Rechecking_Restores_It()
    {
        using var ctx = Context();
        var cut = ctx.RenderComponent<SchemaPanel>();
        cut.WaitForAssertion(
            () => Assert.True(cut.FindAll("label.seg-opt").Count >= 3),
            TimeSpan.FromSeconds(10));

        KindToggle(cut, "Classes").Change(false);

        Assert.DoesNotContain(
            cut.FindAll(".sec"),
            s => s.TextContent.StartsWith("Object classes", StringComparison.Ordinal));
        Assert.Contains(
            cut.FindAll(".sec"),
            s => s.TextContent.StartsWith("Attribute types", StringComparison.Ordinal));
        Assert.Contains(
            cut.FindAll(".sec"),
            s => s.TextContent.StartsWith("Syntaxes", StringComparison.Ordinal));

        KindToggle(cut, "Classes").Change(true);

        Assert.Contains(
            cut.FindAll(".sec"),
            s => s.TextContent.StartsWith("Object classes", StringComparison.Ordinal));
    }

    [Fact]
    public void Hiding_Every_Kind_States_That_Nothing_Is_Shown()
    {
        using var ctx = Context();
        var cut = ctx.RenderComponent<SchemaPanel>();
        cut.WaitForAssertion(
            () => Assert.True(cut.FindAll("label.seg-opt").Count >= 3),
            TimeSpan.FromSeconds(10));

        // Each toggle is re-queried fresh: bUnit's element collections go stale the moment
        // the first Change re-renders and detaches them.
        KindToggle(cut, "Classes").Change(false);
        KindToggle(cut, "Attribute types").Change(false);
        KindToggle(cut, "Syntaxes").Change(false);

        Assert.Empty(cut.FindAll(".sec"));
        Assert.Contains(
            cut.FindAll("p"),
            p => p.TextContent.Contains("enable at least one kind", StringComparison.OrdinalIgnoreCase));
    }
}