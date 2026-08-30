using Aspire.LdapAdmin.Web.Components.Directory;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The schema filter's match contract (aspireldap#116). OID matching is pinned here so a
/// query that is a partial — not only a whole — OID returning rows can never regress
/// without a fast-tier failure; the whole component path is pinned by the integration-tier
/// <see cref="SchemaPanelComponentTests"/> against the live subschema.
/// </summary>
public sealed class SchemaFilterTests
{
    private sealed record Def(IReadOnlyList<string> Names, string Oid, string? Description);

    /// <summary>The <c>cn</c> attribute type: names, OID 2.5.4.3, one description.</summary>
    private static readonly Def Cn = new(["cn", "commonName"], "2.5.4.3", "Common Name");

    /// <summary>Directory String, the syntax <c>cn</c> resolves to.</summary>
    private static readonly Def DirectoryString = new([], "1.3.6.1.4.1.1466.115.121.1.15", "Directory String");

    [Theory]
    [InlineData("2.5.4.3")] // the whole OID
    [InlineData("2.5.4")]   // a prefix of the OID
    [InlineData("5.4.3")]   // an infix of the OID
    [InlineData("2.5")]     // a fragment shared with other definitions in the same subtree
    [InlineData(" 2.5.4.3 ")] // an OID pasted with surrounding whitespace
    public void Attribute_Type_Matches_A_Partial_Or_Pasted_Oid_Query(string query) =>
        Assert.True(SchemaFilter.Matches(query, Cn.Names, Cn.Oid, Cn.Description));

    [Theory]
    [InlineData("1466.115.121")]                  // a fragment shared by the syntax family
    [InlineData("1.3.6.1.4.1.1466.115.121.1.15")] // the whole syntax OID
    public void Syntax_Matches_By_Oid_Fragment(string query) =>
        Assert.True(SchemaFilter.Matches(query, DirectoryString.Names, DirectoryString.Oid, DirectoryString.Description));

    [Theory]
    [InlineData("2.5.4.4")] // a neighbouring OID that is a different attribute
    [InlineData("1.3.6")]   // an LDAP-wide prefix that is not part of cn's own OID
    public void A_Query_That_Matches_No_Field_Is_Not_A_Match(string query) =>
        Assert.False(SchemaFilter.Matches(query, Cn.Names, Cn.Oid, Cn.Description));

    [Fact]
    public void Names_Match_Case_Insensitively_And_Across_Aliases() =>
        Assert.True(SchemaFilter.Matches("COMMON", Cn.Names, Cn.Oid, Cn.Description));

    [Fact]
    public void Description_Matches() =>
        Assert.True(SchemaFilter.Matches("Common Name", Cn.Names, Cn.Oid, Cn.Description));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Blank_Query_Is_Not_A_Filter_Everything_Matches(string query) =>
        Assert.True(SchemaFilter.Matches(query, Cn.Names, Cn.Oid, Cn.Description));
}
