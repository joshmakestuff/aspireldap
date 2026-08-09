using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Fast witnesses for the seed model's REJECTION contracts. Before #64 the fast suite only ever
/// called <see cref="LdapSeedValidator.Validate"/> on models that pass, so every rule below —
/// and the "did you mean" hint that makes a rejection actionable — was unwitnessed outside the
/// Docker-backed suite. Each test asserts the message the rule owns, so a rejection produced by
/// a different rule (or a message that decays to nothing) is a failure.
/// </summary>
public class LdapSeedValidatorTests
{
    private static IResourceBuilder<OpenLdapResource> Ldap() =>
        DistributedApplication.CreateBuilder().AddOpenLdap("ldap");

    private static string ValidateAndCaptureMessage(IResourceBuilder<OpenLdapResource> ldap) =>
        Assert.Throws<DistributedApplicationException>(
            () => LdapSeedValidator.Validate(ldap.Resource, ldap.Resource.SeedModel!)).Message;

    [Fact]
    public void Duplicate_Organizational_Unit_Is_Rejected_Case_Insensitively()
    {
        var ldap = Ldap().WithOrganizationalUnit("people").WithOrganizationalUnit("People");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("declares the organizational unit 'People' more than once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_User_Uid_Is_Rejected_Case_Insensitively()
    {
        var ldap = Ldap().WithUser("alice", "pw").WithUser("ALICE", "pw");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("declares the user uid 'ALICE' more than once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_Group_Cn_Is_Rejected_Case_Insensitively()
    {
        var ldap = Ldap()
            .WithUser("alice", "pw")
            .WithGroup("admins", ["alice"])
            .WithGroup("Admins", ["alice"]);

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("declares the group cn 'Admins' more than once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_User_Password_Is_Rejected()
    {
        // WithUser() guards this at the fluent call, so the validator's own rule is only
        // reachable through the model — which is what the start-time pipeline validates, and
        // what LDIF generation would otherwise hash into an unbindable entry.
        var ldap = Ldap();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("alice", "", null, "alice", "alice", null));

        var ex = Assert.Throws<DistributedApplicationException>(
            () => LdapSeedValidator.Validate(ldap.Resource, model));

        Assert.Contains("User 'alice' on OpenLDAP resource 'ldap' has an empty password", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("comma,name")]
    [InlineData("plus+name")]
    [InlineData("")]
    public void Unsafe_Names_Are_Rejected_With_The_Allowed_Character_Set(string name)
    {
        var ex = Assert.Throws<DistributedApplicationException>(
            () => LdapSeedValidator.RequireSafeName(name, "organizational unit"));

        Assert.Contains($"Invalid organizational unit '{name}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[A-Za-z0-9._-]+", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Each kind must run the safe-name check with ITS OWN label — the fluent API only rejects
    // null/whitespace, so an OU/uid/cn carrying DN-special characters reaches the validator and
    // would otherwise be escaped straight into the generated LDIF.
    [InlineData("ou", "bad name", "Invalid organizational unit 'bad name'")]
    [InlineData("uid", "bad,uid", "Invalid user uid 'bad,uid'")]
    [InlineData("cn", "bad+cn", "Invalid group cn 'bad+cn'")]
    public void Unsafe_Names_Are_Rejected_By_Validate_For_Every_Entry_Kind(
        string kind, string name, string expectedMessageFragment)
    {
        var ldap = Ldap();
        ldap = kind switch
        {
            "ou" => ldap.WithOrganizationalUnit(name),
            "uid" => ldap.WithUser(name, "pw"),
            _ => ldap.WithUser("alice", "pw").WithGroup(name, ["alice"]),
        };

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains(expectedMessageFragment, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_Without_Members_Is_Rejected()
    {
        // WithGroup() accepts an empty member list, so this is reachable from the public API too;
        // the model is built directly here only to keep the failing rule unambiguous.
        var ldap = Ldap();
        var model = new LdapSeedModel();
        model.Groups.Add(new SeedGroupEntry("admins", [], null));

        var ex = Assert.Throws<DistributedApplicationException>(
            () => LdapSeedValidator.Validate(ldap.Resource, model));

        Assert.Contains("must declare at least one member", ex.Message, StringComparison.Ordinal);
        Assert.Contains("groupOfNames", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_Member_Containing_Equals_Is_Accepted_As_A_Literal_Dn()
    {
        // The '=' carve-out is what lets a group reference an entry this model never declared
        // (e.g. the admin DN). Without a witness, dropping it would only surface as a failed
        // container start.
        var ldap = Ldap().WithGroup("admins", ["cn=admin,dc=example,dc=org"]);

        LdapSeedValidator.Validate(ldap.Resource, ldap.Resource.SeedModel!);
    }

    [Fact]
    public void Undeclared_Reference_With_A_Near_Match_Suggests_It()
    {
        var ldap = Ldap().WithOrganizationalUnit("people").WithUser("alice", "pw", ou: "peple");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("references undeclared organizational unit 'peple'", message, StringComparison.Ordinal);
        Assert.Contains("Did you mean \"people\"?", message, StringComparison.Ordinal);
        Assert.Contains(".WithOrganizationalUnit(\"peple\")", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_Reference_With_No_Near_Match_Lists_What_Was_Declared()
    {
        var ldap = Ldap().WithOrganizationalUnit("people").WithUser("alice", "pw", ou: "warehouses");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.DoesNotContain("Did you mean", message, StringComparison.Ordinal);
        Assert.Contains("Declared: [people]", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_Reference_With_Nothing_Declared_Only_Gives_The_Declare_Hint()
    {
        var ldap = Ldap().WithUser("alice", "pw", ou: "people");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.DoesNotContain("Did you mean", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Declared:", message, StringComparison.Ordinal);
        Assert.Contains("Declare it with .WithOrganizationalUnit(\"people\").", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_Referencing_An_Undeclared_Ou_Is_Rejected()
    {
        var ldap = Ldap().WithUser("alice", "pw").WithGroup("admins", ["alice"], ou: "groups");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("Group 'admins' on OpenLDAP resource 'ldap' references undeclared organizational unit 'groups'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_Referencing_An_Undeclared_Uid_Is_Rejected_With_A_Suggestion()
    {
        var ldap = Ldap().WithUser("alice", "pw").WithGroup("admins", ["alic"]);

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("references undeclared user uid 'alic'", message, StringComparison.Ordinal);
        Assert.Contains("Did you mean \"alice\"?", message, StringComparison.Ordinal);
        Assert.Contains(".WithUser(\"alic\", ...)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestion_Picks_The_Closest_Declared_Candidate()
    {
        // Two candidates, one clearly closer: pins that the hint ranks by edit distance rather
        // than returning whichever candidate the set happens to enumerate first.
        var ldap = Ldap()
            .WithOrganizationalUnit("warehouses")
            .WithOrganizationalUnit("people")
            .WithUser("alice", "pw", ou: "peopl");

        var message = ValidateAndCaptureMessage(ldap);

        Assert.Contains("Did you mean \"people\"?", message, StringComparison.Ordinal);
    }
}
