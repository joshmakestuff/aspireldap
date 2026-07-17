using Aspire.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class ConnectionStringTests
{
    private static string Build(string password, string baseDn = "dc=example,dc=org")
    {
        var bindDn = $"cn=admin,{baseDn}";
        return $"Endpoint=ldap://localhost:1389" +
               $";BaseDN={Aspire.OpenLdap.ConnectionStringQuoting.Quote(baseDn)}" +
               $";BindDN={Aspire.OpenLdap.ConnectionStringQuoting.Quote(bindDn)}" +
               $";BindPassword={Aspire.OpenLdap.ConnectionStringQuoting.Quote(password)}";
    }

    [Theory]
    [InlineData("simplepassword")]
    [InlineData("abc;def")]
    [InlineData("a\"b\"\"c")]
    [InlineData(" leading and trailing ")]
    [InlineData("ünïcode-påsswörd")]
    [InlineData("with=equals;and;semis")]
    [InlineData("\"fully quoted\"")]
    [InlineData("")]
    public void Password_Round_Trips(string password)
    {
        var parsed = OpenLdapConnectionStringBuilder.Parse(Build(password));
        Assert.Equal(password, parsed.BindPassword);
        Assert.Equal("dc=example,dc=org", parsed.BaseDn);
        Assert.Equal("cn=admin,dc=example,dc=org", parsed.BindDn);
        Assert.Equal("localhost", parsed.Endpoint.Host);
        Assert.Equal(1389, parsed.Endpoint.Port);
    }

    [Fact]
    public void Quoting_Matches_Between_Hosting_And_Client_Copies()
    {
        // The hosting emitter and client parser compile the same shared source file;
        // this guards against the two link entries drifting apart.
        foreach (var value in new[] { "plain", "a;b", "a\"b", " pad ", "" })
        {
            Assert.Equal(
                Aspire.Hosting.OpenLdap.ConnectionStringQuoting.Quote(value),
                Aspire.OpenLdap.ConnectionStringQuoting.Quote(value));
        }
    }

    [Fact]
    public async Task QuotedParameterValue_Quotes_At_Resolution_Time()
    {
        var parameter = new Aspire.Hosting.ApplicationModel.ParameterResource("pw", _ => "se;cret\"x", secret: true);
        var quoted = new Aspire.Hosting.ApplicationModel.QuotedParameterValue(parameter);

        var value = await quoted.GetValueAsync();

        Assert.Equal("\"se;cret\"\"x\"", value);
    }

    [Fact]
    public void Duplicate_Keys_Are_Rejected()
    {
        Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldap://h:1;BaseDN=a;BindDN=b;BindPassword=c;BaseDN=again"));
    }

    [Theory]
    [InlineData("Endpoint=http://h:1389;BaseDN=a;BindDN=b;BindPassword=c")] // wrong scheme
    [InlineData("Endpoint=ldap://h:1389/path;BaseDN=a;BindDN=b;BindPassword=c")] // path
    [InlineData("Endpoint=ldap://h:1389?q=1;BaseDN=a;BindDN=b;BindPassword=c")] // query
    [InlineData("BaseDN=a;BindDN=b;BindPassword=c")] // missing endpoint
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\"unterminated")] // bad quote
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\"x\"tail")] // trailing junk
    [InlineData("Endpoint=ldap://h:1389;justakeywithnovalue;BindDN=b;BindPassword=c")] // no '='
    public void Malformed_Connection_Strings_Throw(string connectionString)
    {
        Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(connectionString));
    }

    [Fact]
    public void Portless_Endpoint_Uses_The_Scheme_Default_Port()
    {
        // System.Uri registers ldap/ldaps default ports (389/636), so a portless endpoint
        // is well-defined rather than an error.
        var parsed = OpenLdapConnectionStringBuilder.Parse("Endpoint=ldap://h;BaseDN=a;BindDN=b;BindPassword=c");
        Assert.Equal(389, parsed.Endpoint.Port);
    }

    [Fact]
    public void CaCertFile_Is_Optional_And_Parsed_When_Present()
    {
        var without = OpenLdapConnectionStringBuilder.Parse(Build("p"));
        Assert.Null(without.CaCertFile);

        var withCa = OpenLdapConnectionStringBuilder.Parse(Build("p") + ";CaCertFile=C:\\certs\\ca.crt");
        Assert.Equal("C:\\certs\\ca.crt", withCa.CaCertFile);
    }
}
