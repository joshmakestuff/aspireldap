using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The DN equivalence contract behind the bind-identity password guard (#94): two spellings
/// of the same name must compare equal however they differ in case, whitespace, escaping or
/// multi-valued component order — and anything unparsable is equivalent to nothing.
/// </summary>
public class DnEqualityTests
{
    [Fact]
    public void Identical_dns_are_equivalent()
    {
        Assert.True(DnEquality.AreEquivalent(
            "cn=admin,dc=example,dc=org",
            "cn=admin,dc=example,dc=org"));
    }

    [Fact]
    public void Case_differences_in_type_and_value_are_equivalent()
    {
        Assert.True(DnEquality.AreEquivalent(
            "CN=Admin,DC=Example,DC=Org",
            "cn=admin,dc=example,dc=org"));
    }

    [Fact]
    public void Whitespace_after_rdn_separators_is_equivalent()
    {
        Assert.True(DnEquality.AreEquivalent(
            "cn=admin, dc=example, dc=org",
            "cn=admin,dc=example,dc=org"));
    }

    [Fact]
    public void Multi_valued_rdn_component_order_does_not_matter()
    {
        Assert.True(DnEquality.AreEquivalent(
            "cn=a+sn=b,dc=example,dc=org",
            "sn=b+cn=a,dc=example,dc=org"));
    }

    [Fact]
    public void An_escaped_value_matches_its_equivalent_spelling()
    {
        var composed = Dn.Combine(Dn.Rdn("cn", "Smith, John"), "dc=example,dc=org");

        Assert.True(DnEquality.AreEquivalent(@"cn=Smith\, John,dc=example,dc=org", composed));
    }

    [Fact]
    public void Different_rdn_counts_are_not_equivalent()
    {
        Assert.False(DnEquality.AreEquivalent(
            "cn=admin,dc=example,dc=org",
            "cn=admin,ou=people,dc=example,dc=org"));
    }

    [Fact]
    public void Different_values_are_not_equivalent()
    {
        Assert.False(DnEquality.AreEquivalent(
            "cn=admin,dc=example,dc=org",
            "cn=alice,dc=example,dc=org"));
    }

    [Fact]
    public void A_descendant_is_under_its_ancestor_however_spelled()
    {
        Assert.True(DnEquality.IsUnder("cn=admin,dc=example,dc=org", "DC=Example, DC=Org"));
        Assert.True(DnEquality.IsUnder("uid=x,ou=people,dc=example,dc=org", "dc=example,dc=org"));
        Assert.True(DnEquality.IsUnder("uid=x,ou=people,dc=example,dc=org", "ou=people,dc=example,dc=org"));
    }

    [Fact]
    public void Equal_sibling_reversed_or_mismatched_dns_are_not_under()
    {
        Assert.False(DnEquality.IsUnder("cn=admin,dc=example,dc=org", "cn=admin,dc=example,dc=org")); // equal, not under
        Assert.False(DnEquality.IsUnder("cn=a,dc=example,dc=org", "cn=b,dc=example,dc=org"));
        Assert.False(DnEquality.IsUnder("dc=example,dc=org", "cn=admin,dc=example,dc=org")); // reversed
        Assert.False(DnEquality.IsUnder("cn=admin,dc=example,dc=net", "dc=example,dc=org"));
        Assert.False(DnEquality.IsUnder("cn=admin,dc=example,dc=org", ""));
        Assert.False(DnEquality.IsUnder("not a dn", "dc=example,dc=org"));
    }

    [Fact]
    public void An_unparsable_null_or_empty_dn_is_never_equivalent()
    {
        // An unescaped '<' is an RFC 4514 parse failure.
        Assert.False(DnEquality.AreEquivalent("cn=<admin>,dc=org", "cn=<admin>,dc=org"));
        Assert.False(DnEquality.AreEquivalent("cn=admin,dc=org", "not a dn"));
        Assert.False(DnEquality.AreEquivalent(null, "cn=admin,dc=org"));
        Assert.False(DnEquality.AreEquivalent("cn=admin,dc=org", ""));
        Assert.False(DnEquality.AreEquivalent("", ""));
    }
}
