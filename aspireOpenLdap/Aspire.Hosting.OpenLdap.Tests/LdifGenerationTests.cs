using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class AdminBindDnTests
{
    private static OpenLdapResource CreateResource(string adminUsername, string baseDn)
        => new("ldap", baseDn, adminUsername, new ParameterResource("pw", _ => "secret", secret: true));

    [Theory]
    [InlineData("admin", "cn=admin,dc=example,dc=org")]
    [InlineData("Admin User", "cn=Admin User,dc=example,dc=org")] // interior spaces preserved
    public void AdminBindDn_Composes_Username_And_Base_Dn(string username, string expected)
    {
        var resource = CreateResource(username, "dc=example,dc=org");
        Assert.Equal(expected, resource.AdminBindDn);
    }

    [Fact]
    public void Username_Needing_Dn_Escaping_Is_Rejected_At_Construction()
    {
        // Previously $"cn={AdminUsername},{BaseDn}" produced a silently broken DN whose
        // cn RDN ended at the first comma. The container init composes the same DN
        // verbatim, so such usernames can never bind consistently — they are rejected
        // at model construction rather than escaped into a host/container mismatch.
        var ex = Assert.Throws<ArgumentException>(() => CreateResource("Doe, John", "dc=example,dc=org"));

        // The whole message is the contract, not just the fact that something threw: it has to
        // name the offending value, enumerate the rejected characters, and say what to do
        // instead. This is the only place a user learns why an ordinary-looking name is refused.
        Assert.Contains("Admin username 'Doe, John'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DN escaping", ex.Message, StringComparison.Ordinal);
        Assert.Contains(", + \" \\ < > ;", ex.Message, StringComparison.Ordinal);
        Assert.Contains("use a username without DN special characters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_Username_With_Control_Characters_Is_Rejected()
    {
        // The value is interpolated into shell-generated cn=config LDIF, so a control character
        // is an injection vector, not a cosmetic problem — and it is rejected by a different
        // rule than DN escaping, which the message must make clear.
        var ex = Assert.Throws<ArgumentException>(() => CreateResource("ad\nmin", "dc=example,dc=org"));

        Assert.Contains("Admin username", ex.Message, StringComparison.Ordinal);
        Assert.Contains("control character", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        // Every hostile value must have been base64-encoded or hashed; none may appear raw.
        Assert.DoesNotContain("word: injected", ldif);
        Assert.DoesNotContain("Seán", ldif);

        // Parse the LDIF back the way a consumer (slapd/ldapadd) sees it and assert the
        // EXACT decoded shape: the hostile newline must not have created an extra record,
        // and must not have smuggled an extra attribute into the user entry.
        var records = LdifReader.Parse(ldif);
        var user = Assert.IsType<LdifContentRecord>(
            Assert.Single(records, r => r.Dn == "uid=user01,dc=example,dc=org"));

        Assert.Equal(
            ["cn", "mail", "objectClass", "sn", "uid", "userPassword"],
            user.Attributes.Select(a => a.Name).Order().ToArray());

        // Exact decoded values — dropping or altering any of them fails, not just the
        // presence of a "cn:: " marker.
        Assert.Equal("user01", Assert.Single(user["uid"]!.Values).AsString());
        Assert.Equal(" Seán Ó Briain ", Assert.Single(user["cn"]!.Values).AsString());
        Assert.Equal("Ó Briain", Assert.Single(user["sn"]!.Values).AsString());
        Assert.Equal("seán@example.org", Assert.Single(user["mail"]!.Values).AsString());

        // The password is hashed: the injected text exists nowhere in the decoded entry,
        // and the hash value itself is newline-free (a raw "\n" here is what would have
        // become a second attribute line).
        var password = Assert.Single(user["userPassword"]!.Values).AsString();
        Assert.StartsWith("{SSHA}", password);
        Assert.DoesNotContain("injected", password);
        Assert.DoesNotContain('\n', password);
    }

    [Fact]
    public void Fluent_Seed_Api_Generates_The_Exact_Expected_Records()
    {
        // Built through the PUBLIC fluent API consumers actually call — not by populating
        // internal LdapSeedModel collections — then parsed back and asserted record-exactly.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOrganizationalUnit("people")
            .WithUser("user01", "password1", ou: "people", cn: "User One", sn: "One", mail: "user01@example.org")
            .WithGroup("admins", ["user01"]);

        var model = ldap.Resource.SeedModel;
        Assert.NotNull(model);
        var ldif = LdapSeedLdifGenerator.Generate(ldap.Resource, model!);

        var records = LdifReader.Parse(ldif);
        Assert.Equal(
            [
                "dc=example,dc=org",
                "ou=people,dc=example,dc=org",
                "uid=user01,ou=people,dc=example,dc=org",
                "cn=admins,dc=example,dc=org",
            ],
            records.Select(r => r.Dn).ToArray());

        var user = Assert.IsType<LdifContentRecord>(records[2]);
        Assert.Equal("User One", Assert.Single(user["cn"]!.Values).AsString());
        Assert.Equal("One", Assert.Single(user["sn"]!.Values).AsString());
        Assert.Equal("user01@example.org", Assert.Single(user["mail"]!.Values).AsString());

        var group = Assert.IsType<LdifContentRecord>(records[3]);
        Assert.Equal(
            "uid=user01,ou=people,dc=example,dc=org",
            Assert.Single(group["member"]!.Values).AsString());

        // The password is stored hashed, never cleartext (F05).
        Assert.StartsWith("{SSHA}", Assert.Single(user["userPassword"]!.Values).AsString());
        Assert.DoesNotContain("password1", ldif);
    }

    [Fact]
    public void Records_Are_Emitted_In_Ordinal_Order_Within_Each_Entry_Kind()
    {
        // Declaration order must not leak into the file: the generated LDIF is mounted into the
        // container and diffed by humans, and ldapadd applies it top-down, so the OU/user/group
        // blocks are each sorted ordinal. Two runs of the SAME declarations being identical is
        // not enough to pin this — that holds under any stable order.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOrganizationalUnit("zeta")
            .WithOrganizationalUnit("alpha")
            .WithUser("zoe", "pw", ou: "alpha")
            .WithUser("amy", "pw", ou: "alpha")
            .WithGroup("zookeepers", ["amy"], ou: "alpha")
            .WithGroup("admins", ["amy"], ou: "alpha");

        var records = LdifReader.Parse(LdapSeedLdifGenerator.Generate(ldap.Resource, ldap.Resource.SeedModel!));

        Assert.Equal(
            [
                "dc=example,dc=org",
                "ou=alpha,dc=example,dc=org",
                "ou=zeta,dc=example,dc=org",
                "uid=amy,ou=alpha,dc=example,dc=org",
                "uid=zoe,ou=alpha,dc=example,dc=org",
                "cn=admins,ou=alpha,dc=example,dc=org",
                "cn=zookeepers,ou=alpha,dc=example,dc=org",
            ],
            records.Select(r => r.Dn).ToArray());
    }

    [Fact]
    public void Emitted_Object_Classes_Are_The_Schema_Contract_For_Each_Entry_Kind()
    {
        // slapd rejects an entry whose objectClass set does not carry its naming/required
        // attributes, and that rejection only shows up as a failed container load. These are the
        // exact classes each generated entry kind must declare.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOrganizationalUnit("people")
            .WithUser("user01", "pw", ou: "people")
            .WithGroup("admins", ["user01"]);

        var records = LdifReader.Parse(LdapSeedLdifGenerator.Generate(ldap.Resource, ldap.Resource.SeedModel!))
            .Cast<LdifContentRecord>()
            .ToArray();

        static string[] ObjectClasses(LdifContentRecord record)
            => record["objectClass"]!.Values.Select(v => v.AsString()).ToArray();

        Assert.Equal(["dcObject", "organization"], ObjectClasses(records[0]));
        Assert.Equal(["organizationalUnit"], ObjectClasses(records[1]));
        Assert.Equal(["inetOrgPerson", "organizationalPerson", "person", "top"], ObjectClasses(records[2]));
        Assert.Equal(["groupOfNames", "top"], ObjectClasses(records[3]));
    }

    [Fact]
    public void Group_Members_Are_Uid_Resolved_Unless_They_Are_Literal_Dns()
    {
        // The '=' carve-out lets a group reference an entry outside the seed model (the admin
        // DN here). A literal DN must be emitted verbatim; a bare uid must be expanded to the
        // DN the user entry was written under, OU included.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOrganizationalUnit("people")
            .WithUser("user01", "pw", ou: "people")
            .WithGroup("admins", ["user01", "cn=admin,dc=example,dc=org"]);

        var records = LdifReader.Parse(LdapSeedLdifGenerator.Generate(ldap.Resource, ldap.Resource.SeedModel!));
        var group = Assert.IsType<LdifContentRecord>(records[^1]);

        Assert.Equal(
            ["uid=user01,ou=people,dc=example,dc=org", "cn=admin,dc=example,dc=org"],
            group["member"]!.Values.Select(v => v.AsString()).ToArray());
    }

    [Fact]
    public void Generated_File_Carries_The_Do_Not_Edit_Header()
    {
        // The file lands in the user's AppHost output; without the header a reader has no way
        // to tell it is generated and edits get silently overwritten on the next run.
        var ldif = LdapSeedLdifGenerator.Generate(CreateResource(), new LdapSeedModel());

        Assert.StartsWith("# Generated by Aspire.Hosting.OpenLdap. Do not edit;", ldif, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_Root_Naming_Attribute_Is_Rejected_At_Model_Construction()
    {
        // dc=/o=/c= are the supported roots; anything else has no correct root objectClass.
        // The fast witness for that rule is here, at resource construction — the generator's
        // own matching throw is unreachable defence-in-depth because no resource carrying an
        // unsupported root can be built (the container-side guard is covered by
        // InitializationIntegrityTests, which needs Docker).
        var ex = Assert.Throws<ArgumentException>(() => CreateResource("ou=nope,dc=example,dc=org"));

        Assert.Contains("starts with 'ou='", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'dc=', 'o=', and 'c='", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seeded_Password_Is_Stored_As_Verifiable_Ssha_Hash()
    {
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("user01", "s3cret!", null, "User One", "One", null));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        // Extract the {SSHA} value and verify it the way slapd does: base64 decodes to
        // 20 digest bytes + salt, and SHA1(password + salt) must equal the digest.
        var line = ldif.Split('\n').Single(l => l.StartsWith("userPassword: ", StringComparison.Ordinal));
        var value = line["userPassword: ".Length..];
        Assert.StartsWith("{SSHA}", value);

        var decoded = Convert.FromBase64String(value["{SSHA}".Length..]);
        var digest = decoded[..System.Security.Cryptography.SHA1.HashSizeInBytes];
        var salt = decoded[System.Security.Cryptography.SHA1.HashSizeInBytes..];
        Assert.NotEmpty(salt);

        var passwordBytes = System.Text.Encoding.UTF8.GetBytes("s3cret!");
        var recomputed = System.Security.Cryptography.SHA1.HashData([.. passwordBytes, .. salt]);
        Assert.Equal(digest, recomputed);
    }

    [Fact]
    public void Equal_Passwords_Hash_With_Distinct_Salts()
    {
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("user01", "same-password", null, "User One", "One", null));
        model.Users.Add(new SeedUserEntry("user02", "same-password", null, "User Two", "Two", null));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        var hashes = ldif.Split('\n')
            .Where(l => l.StartsWith("userPassword: ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, hashes.Count);
        Assert.NotEqual(hashes[0], hashes[1]);
    }

    [Fact]
    public void Prehashed_Password_Passes_Through_Verbatim()
    {
        // A caller migrating existing data may supply an already-hashed value; re-hashing
        // it would break the user's bind.
        const string prehashed = "{SSHA}AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var resource = CreateResource();
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("user01", prehashed, null, "User One", "One", null));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        Assert.Contains($"userPassword: {prehashed}\n", ldif);
    }

    [Fact]
    public void NonAscii_Base_Dn_Is_Base64_Encoded_In_Dn_Lines()
    {
        var resource = CreateResource("dc=büro,dc=example");
        var model = new LdapSeedModel();
        model.OrganizationalUnits.Add(new OrganizationalUnitEntry("people"));

        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        // Encoded on the wire (a raw non-ASCII dn: line is invalid LDIF) ...
        Assert.Contains("dn:: ", ldif);
        Assert.DoesNotContain("dn: ou=people,dc=büro", ldif);

        // ... and decodes back to the EXACT intended DN, byte-for-byte.
        var records = LdifReader.Parse(ldif);
        Assert.Contains(records, r => r.Dn == "ou=people,dc=büro,dc=example");
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

}

public class ConfigLdifGenerationTests
{
    // These parse the generated cn=config LDIF and assert record/change semantics — what
    // the privileged ldapmodify apply actually consumes. Runtime application of overlays
    // and ACLs is covered by OverlayAccessControlIntegrationTests.

    [Fact]
    public void Overlay_Ldif_Contains_Module_List_And_Overlay_Entry()
    {
        var overlay = OpenLdapOverlay.MemberOf("groupOfNames", "member");

        var ldif = OpenLdapOverlayConfiguration.GenerateOverlayLdif([overlay]);

        Assert.DoesNotContain("version:", ldif);

        var records = LdifReader.Parse(ldif);
        Assert.Equal(2, records.Count);

        // The module list must precede the overlay entry — slapd rejects an overlay whose
        // module is not loaded yet, so record ORDER is part of the contract.
        var module = Assert.IsType<LdifContentRecord>(records[0]);
        Assert.Equal("cn=module{1},cn=config", module.Dn);
        Assert.Equal("memberof.so", Assert.Single(module["olcModuleLoad"]!.Values).AsString());

        var overlayRecord = Assert.IsType<LdifContentRecord>(records[1]);
        Assert.Equal("olcOverlay=memberof,olcDatabase={2}mdb,cn=config", overlayRecord.Dn);
        Assert.Equal(
            ["olcOverlayConfig", "olcMemberOf"],
            overlayRecord["objectClass"]!.Values.Select(v => v.AsString()).ToArray());
        Assert.Equal("memberof", Assert.Single(overlayRecord["olcOverlay"]!.Values).AsString());
        Assert.Equal("groupOfNames", Assert.Single(overlayRecord["olcMemberOfGroupOC"]!.Values).AsString());
    }

    [Fact]
    public void Access_Ldif_Is_A_Single_Modify_With_Ordered_Rules()
    {
        const string rule0 = "to dn.subtree=\"ou=entity,dc=example,dc=org\" by dn.exact=\"uid=svc,ou=entity,dc=example,dc=org\" write by * break";
        const string rule1 = "to attrs=userPassword by self write by * break";
        var ldif = OpenLdapOverlayConfiguration.GenerateDatabaseConfigLdif([rule0, rule1], limitRules: null);

        Assert.DoesNotContain("version:", ldif);

        // Exactly one modify record against the mdb database: an extra record, a different
        // target DN, a changed modification type, or reordered rules all fail.
        var records = LdifReader.Parse(ldif);
        var modify = Assert.IsType<LdifModifyRecord>(Assert.Single(records));
        Assert.Equal("olcDatabase={2}mdb,cn=config", modify.Dn);

        var modification = Assert.Single(modify.Modifications);
        Assert.Equal(LdifModificationType.Add, modification.Type);
        Assert.Equal("olcAccess", modification.AttributeName);
        Assert.Equal(
            [$"{{0}}{rule0}", $"{{1}}{rule1}"],
            modification.Values.Select(v => v.AsString()).ToArray());
    }

    [Fact]
    public void Limits_Ldif_Alone_Is_A_Single_Modify_With_Ordered_Rules()
    {
        // aspireldap#118: per-principal olcLimits ride the same database-config LDIF.
        const string rule0 = "dn.exact=\"uid=svc,ou=users,dc=example,dc=org\" size=10";
        const string rule1 = "users time=30";
        var ldif = OpenLdapOverlayConfiguration.GenerateDatabaseConfigLdif(accessRules: null, [rule0, rule1]);

        var records = LdifReader.Parse(ldif);
        var modify = Assert.IsType<LdifModifyRecord>(Assert.Single(records));
        Assert.Equal("olcDatabase={2}mdb,cn=config", modify.Dn);

        var modification = Assert.Single(modify.Modifications);
        Assert.Equal(LdifModificationType.Add, modification.Type);
        Assert.Equal("olcLimits", modification.AttributeName);
        Assert.Equal(
            [$"{{0}}{rule0}", $"{{1}}{rule1}"],
            modification.Values.Select(v => v.AsString()).ToArray());
    }

    [Fact]
    public void Access_And_Limits_Compose_Into_One_Modify_Record_Access_First()
    {
        // One record, two modifications: ldapmodify applies them as one atomic change to
        // the database entry, and the access policy lands before the limits that assume it.
        const string access = "to * by users read by * none";
        const string limit = "dn.exact=\"uid=svc,ou=users,dc=example,dc=org\" size=10";
        var ldif = OpenLdapOverlayConfiguration.GenerateDatabaseConfigLdif([access], [limit]);

        var records = LdifReader.Parse(ldif);
        var modify = Assert.IsType<LdifModifyRecord>(Assert.Single(records));
        Assert.Equal("olcDatabase={2}mdb,cn=config", modify.Dn);

        Assert.Equal(2, modify.Modifications.Count);
        Assert.Equal("olcAccess", modify.Modifications[0].AttributeName);
        Assert.Equal($"{{0}}{access}", Assert.Single(modify.Modifications[0].Values).AsString());
        Assert.Equal("olcLimits", modify.Modifications[1].AttributeName);
        Assert.Equal($"{{0}}{limit}", Assert.Single(modify.Modifications[1].Values).AsString());
    }
}
