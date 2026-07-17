using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class AdminBindDnTests
{
    private static OpenLdapResource CreateResource(string adminUsername, string baseDn)
        => new("ldap", baseDn, adminUsername, new ParameterResource("pw", _ => "secret", secret: true));

    [Fact]
    public void AdminBindDn_Composes_Plain_Username_And_Base_Dn()
    {
        var resource = CreateResource("admin", "dc=example,dc=org");
        Assert.Equal("cn=admin,dc=example,dc=org", resource.AdminBindDn);
    }

    [Fact]
    public void Username_Needing_Dn_Escaping_Is_Rejected_At_Construction()
    {
        // Previously $"cn={AdminUsername},{BaseDn}" produced a silently broken DN whose
        // cn RDN ended at the first comma. The container init composes the same DN
        // verbatim, so such usernames can never bind consistently — they are rejected
        // at model construction rather than escaped into a host/container mismatch.
        var ex = Assert.Throws<ArgumentException>(() => CreateResource("Doe, John", "dc=example,dc=org"));
        Assert.Contains("DN escaping", ex.Message);
    }

    [Fact]
    public void AdminBindDn_Preserves_Interior_Spaces()
    {
        var resource = CreateResource("Admin User", "dc=example,dc=org");
        Assert.Equal("cn=Admin User,dc=example,dc=org", resource.AdminBindDn);
    }
}

public class LdapSeedLdifGeneratorTests
{
    private static OpenLdapResource CreateResource(string baseDn = "dc=example,dc=org")
        => new("ldap", baseDn, "admin", new ParameterResource("pw", _ => "secret", secret: true));

    [Fact]
    public void Hostile_Attribute_Values_Are_Encoded_Not_Injected()
    {
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry(
            Uid: "user01",
            Password: "p;ass\nword: injected",
            OrganizationalUnit: null,
            Cn: " Seán Ó Briain ",
            Sn: "Ó Briain",
            Mail: "seán@example.org"));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        // Every hostile value must have been base64-encoded; none may appear raw.
        Assert.DoesNotContain("word: injected", ldif);
        Assert.DoesNotContain("Seán", ldif);
        Assert.Contains("userPassword:: ", ldif);
        Assert.Contains("cn:: ", ldif);
        Assert.Contains("sn:: ", ldif);
        Assert.Contains("mail:: ", ldif);
        Assert.Contains("dn: uid=user01,dc=example,dc=org\n", ldif);
    }

    [Fact]
    public void Plain_Ascii_Model_Generates_Readable_Ldif()
    {
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.OrganizationalUnits.Add(new OrganizationalUnitEntry("people"));
        model.Users.Add(new SeedUserEntry("user01", "password1", "people", "User One", "One", "user01@example.org"));
        model.Groups.Add(new SeedGroupEntry("admins", ["user01"], null));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains("dn: ou=people,dc=example,dc=org\n", ldif);
        Assert.Contains("dn: uid=user01,ou=people,dc=example,dc=org\n", ldif);
        Assert.Contains("dn: cn=admins,dc=example,dc=org\n", ldif);
        Assert.Contains("member: uid=user01,ou=people,dc=example,dc=org\n", ldif);
        Assert.Contains("userPassword: password1\n", ldif);
    }

    [Fact]
    public void NonAscii_Base_Dn_Is_Base64_Encoded_In_Dn_Lines()
    {
        var resource = CreateResource("dc=büro,dc=example");
        var model = new LdapSeedModel();
        model.OrganizationalUnits.Add(new OrganizationalUnitEntry("people"));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains("dn:: ", ldif);
        Assert.DoesNotContain("dn: ou=people,dc=büro", ldif);
    }

    [Fact]
    public void Root_Entry_For_Country_Base_Dn_Uses_Country_Object_Class()
    {
        // c=US is a valid suffix; previously the root entry assumed dcObject/organization
        // and never emitted the naming c attribute, so container init failed mid-bootstrap.
        var resource = CreateResource("c=US");
        var model = new LdapSeedModel();

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains("dn: c=US\n", ldif);
        Assert.Contains("objectClass: country\n", ldif);
        Assert.Contains("c: US\n", ldif);
        Assert.DoesNotContain("dcObject", ldif);
    }

    [Fact]
    public void Root_Entry_For_Dc_Base_Dn_Takes_O_Value_From_Later_Rdn()
    {
        var resource = CreateResource("dc=example,o=Acme Corp,c=US");
        var model = new LdapSeedModel();

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains("objectClass: dcObject\n", ldif);
        Assert.Contains("dc: example\n", ldif);
        Assert.Contains("o: Acme Corp\n", ldif);
    }

    [Fact]
    public void Root_Entry_Handles_Escaped_Comma_In_Base_Dn_Value()
    {
        // A base DN whose o value contains an escaped comma used to split mid-value:
        // the old splitter yielded o="Acme\" instead of the real value. Dn.Parse unescapes it.
        var resource = CreateResource("o=Acme\\, Inc.,c=US");
        var model = new LdapSeedModel();

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains("dn: o=Acme\\, Inc.,c=US\n", ldif);
        Assert.Contains("objectClass: organization\n", ldif);
        Assert.Contains("o: Acme, Inc.\n", ldif);
    }

    [Fact]
    public void Generated_Seed_Has_No_Version_Line_And_Uses_Lf_Only()
    {
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("user01", "password1", null, "User One", "One", null));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.DoesNotContain("version:", ldif);
        Assert.DoesNotContain('\r', ldif);
    }
}

public class ConfigLdifGenerationTests
{
    [Fact]
    public void Overlay_Ldif_Contains_Module_List_And_Overlay_Entry()
    {
        var overlay = OpenLdapOverlay.MemberOf("groupOfNames", "member");

        var ldif = OpenLdapResourceBuilderExtensions.GenerateOverlayLdif([overlay]);

        Assert.DoesNotContain("version:", ldif);
        Assert.Contains("dn: cn=module{1},cn=config\n", ldif);
        Assert.Contains("olcModuleLoad: memberof.so\n", ldif);
        Assert.Contains("\n\ndn: olcOverlay=memberof,olcDatabase={2}mdb,cn=config\n", ldif);
        Assert.Contains("objectClass: olcOverlayConfig\n", ldif);
        Assert.Contains("objectClass: olcMemberOf\n", ldif);
        Assert.Contains("olcOverlay: memberof\n", ldif);
        Assert.Contains("olcMemberOfGroupOC: groupOfNames\n", ldif);
        Assert.EndsWith("\n", ldif);
    }

    [Fact]
    public void Access_Ldif_Is_A_Single_Modify_With_Ordered_Rules()
    {
        var ldif = OpenLdapResourceBuilderExtensions.GenerateAccessLdif(
        [
            "to dn.subtree=\"ou=entity,dc=example,dc=org\" by dn.exact=\"uid=svc,ou=entity,dc=example,dc=org\" write by * break",
            "to attrs=userPassword by self write by * break",
        ]);

        Assert.DoesNotContain("version:", ldif);
        Assert.Contains("dn: olcDatabase={2}mdb,cn=config\n", ldif);
        Assert.Contains("changetype: modify\n", ldif);
        Assert.Contains("add: olcAccess\n", ldif);
        Assert.Contains("olcAccess: {0}to dn.subtree=\"ou=entity,dc=example,dc=org\" by dn.exact=\"uid=svc,ou=entity,dc=example,dc=org\" write by * break\n", ldif);
        Assert.Contains("olcAccess: {1}to attrs=userPassword by self write by * break\n", ldif);
    }
}
