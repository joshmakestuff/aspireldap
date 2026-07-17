using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class LdifEncoderTests
{
    private static string Encode(string attribute, string value)
    {
        var sb = new StringBuilder();
        LdifEncoder.AppendAttribute(sb, attribute, value, "\n");
        return sb.ToString().TrimEnd('\n');
    }

    [Theory]
    [InlineData("Alice Smith")]
    [InlineData("O'Brien-D.")]
    [InlineData("with = equals")]
    [InlineData("trailing:colon ok if not leading")]
    public void Safe_Values_Stay_Plain(string value)
    {
        Assert.Equal($"cn: {value}", Encode("cn", value));
    }

    [Theory]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData(":leading colon")]
    [InlineData("<leading angle")]
    [InlineData("embedded\nnewline")]
    [InlineData("embedded\rcarriage")]
    [InlineData("nul\0char")]
    [InlineData("Seán Ó Briain")]
    [InlineData("日本語")]
    public void Unsafe_Values_Are_Base64_Encoded(string value)
    {
        var line = Encode("cn", value);
        Assert.StartsWith("cn:: ", line);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(line["cn:: ".Length..]));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Newline_Value_Cannot_Inject_Extra_Attributes()
    {
        var line = Encode("cn", "user\nuserPassword: injected");
        Assert.DoesNotContain("\nuserPassword", line);
        Assert.StartsWith("cn:: ", line);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "a\\,b")]
    [InlineData("a+b;c", "a\\+b\\;c")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("\"quoted\"", "\\\"quoted\\\"")]
    [InlineData("<angles>", "\\<angles\\>")]
    [InlineData("#leading", "\\#leading")]
    [InlineData(" pad ", "\\ pad\\ ")]
    [InlineData("mid#hash ok", "mid#hash ok")]
    public void EscapeDnValue_Follows_Rfc4514(string input, string expected)
    {
        Assert.Equal(expected, LdifEncoder.EscapeDnValue(input));
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
}
