using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Witnesses for #61: invalid overlay declarations fail at the factory or fluent call
/// (AppHost model construction), not later inside the container bootstrap.
/// </summary>
public class OverlayValidationTests
{
    [Theory]
    [InlineData("", "member")]
    [InlineData("   ", "member")]
    [InlineData("group Of Names", "member")] // interior whitespace corrupts the olc attribute line
    [InlineData("groupOfNames", "")]
    [InlineData("groupOfNames", "mem\nber")] // control chars would inject LDIF lines
    public void MemberOf_Rejects_Empty_Or_Unclean_Descriptors(string groupObjectClass, string memberAttribute)
    {
        Assert.ThrowsAny<ArgumentException>(() => OpenLdapOverlay.MemberOf(groupObjectClass, memberAttribute));
    }

    [Fact]
    public void MemberOf_Rejects_A_Whitespace_MemberOf_Attribute()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => OpenLdapOverlay.MemberOf("groupOfNames", "member", memberOfAttribute: " "));
    }

    [Fact]
    public void MemberOf_Rejects_Undefined_Dangling_Policy_Casts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OpenLdapOverlay.MemberOf("groupOfNames", "member", dangling: (OpenLdapMemberOfDanglingPolicy)7));
    }

    [Theory]
    [InlineData(OpenLdapMemberOfDanglingPolicy.Ignore, "ignore")]
    [InlineData(OpenLdapMemberOfDanglingPolicy.Drop, "drop")]
    [InlineData(OpenLdapMemberOfDanglingPolicy.Error, "error")]
    public void MemberOf_Maps_Each_Dangling_Policy_To_Its_Slapd_Keyword(
        OpenLdapMemberOfDanglingPolicy policy, string expected)
    {
        var overlay = OpenLdapOverlay.MemberOf("groupOfNames", "member", dangling: policy);

        var dangling = Assert.Single(overlay.Attributes, a => a.Key == "olcMemberOfDangling");
        Assert.Equal(expected, dangling.Value);
    }

    [Theory]
    [InlineData("", "olcCustom")]        // empty name
    [InlineData("my overlay", "olcCustom")] // whitespace in name (spliced into a DN)
    [InlineData("custom", "")]           // empty objectClass
    [InlineData("custom", "olc\tCustom")]
    public void WithOverlay_Rejects_Unclean_Custom_Declarations(string name, string objectClass)
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        var overlay = new OpenLdapOverlay { Name = name, OverlayObjectClass = objectClass };

        Assert.Throws<DistributedApplicationException>(() => ldap.WithOverlay(overlay));
    }

    [Fact]
    public void WithOverlay_Rejects_Unclean_Modules_And_Attribute_Names()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        var badModule = new OpenLdapOverlay
        {
            Name = "custom",
            OverlayObjectClass = "olcCustom",
            ModuleLoads = ["bad module.so"],
        };
        Assert.Throws<DistributedApplicationException>(() => ldap.WithOverlay(badModule));

        var badAttributeName = new OpenLdapOverlay
        {
            Name = "custom",
            OverlayObjectClass = "olcCustom",
            Attributes = [new(" ", "value")],
        };
        Assert.Throws<DistributedApplicationException>(() => ldap.WithOverlay(badAttributeName));

        var nullAttributeValue = new OpenLdapOverlay
        {
            Name = "custom",
            OverlayObjectClass = "olcCustom",
            Attributes = [new("olcSetting", null!)],
        };
        Assert.Throws<DistributedApplicationException>(() => ldap.WithOverlay(nullAttributeValue));
    }

    [Fact]
    public void WithOverlay_Rejects_A_Duplicate_Overlay_Name_Case_Insensitively()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOverlay(OpenLdapOverlay.MemberOf("groupOfNames", "member"));

        var duplicate = new OpenLdapOverlay { Name = "MemberOf", OverlayObjectClass = "olcMemberOf" };

        var ex = Assert.Throws<DistributedApplicationException>(() => ldap.WithOverlay(duplicate));
        Assert.Contains("already declared", ex.Message);

        // The rejected declaration must not have been half-registered.
        Assert.Single(ldap.Resource.Overlays!);
    }

    [Fact]
    public void Distinct_Overlays_Can_Still_Be_Stacked()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOverlay(OpenLdapOverlay.MemberOf("groupOfNames", "member"))
            .WithOverlay(new OpenLdapOverlay
            {
                Name = "refint",
                OverlayObjectClass = "olcRefintConfig",
                ModuleLoads = ["refint.so"],
                Attributes = [new("olcRefintAttribute", "member")],
            });

        Assert.Equal(2, ldap.Resource.Overlays!.Count);
    }
}
