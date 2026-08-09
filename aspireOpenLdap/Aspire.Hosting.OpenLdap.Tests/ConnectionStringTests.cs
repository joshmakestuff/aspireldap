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

    // One row per parser/quoting equivalence class: unquoted plain, quoted separator chars
    // (';' plus '=' inside a value), doubled embedded quotes, edge whitespace, a value that
    // itself looks fully quoted, and empty. (A non-ASCII row was prosecuted and removed:
    // Quote/Parse have no charset-sensitive branch, so it duplicated the plain row.)
    [Theory]
    [InlineData("simplepassword")]
    [InlineData("with=equals;and;semis")]
    [InlineData("a\"b\"\"c")]
    [InlineData(" leading and trailing ")]
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
    public async Task QuotedParameterValue_Quotes_At_Resolution_Time()
    {
        var parameter = new Aspire.Hosting.ApplicationModel.ParameterResource("pw", _ => "se;cret\"x", secret: true);
        var quoted = new Aspire.Hosting.ApplicationModel.QuotedParameterValue(parameter);

        var value = await quoted.GetValueAsync();

        Assert.Equal("\"se;cret\"\"x\"", value);
    }

    [Fact]
    public void QuotedParameterValue_Manifest_Expression_Carries_The_Quotes()
    {
        // At deployment time the manifest substitutes the parameter's raw value with no code
        // of ours running, so the expression itself must carry the connection-string quotes —
        // otherwise a deployed password containing ';' or edge whitespace corrupts the
        // connection string even though the same secret works during local AppHost resolution.
        var parameter = new Aspire.Hosting.ApplicationModel.ParameterResource("pw", _ => "unused", secret: true);
        var quoted = new Aspire.Hosting.ApplicationModel.QuotedParameterValue(parameter);

        Assert.Equal(
            $"\"{parameter.ValueExpression}\"",
            ((Aspire.Hosting.ApplicationModel.IManifestExpressionProvider)quoted).ValueExpression);

        // The deployed shape — literal quotes around a substituted raw value — parses back
        // losslessly for any value without embedded double quotes.
        var parsed = OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\" se;cret \"");
        Assert.Equal(" se;cret ", parsed.BindPassword);
    }

    /// <summary>
    /// Pins the published-manifest contract for embedded double quotes (#62): deployment
    /// substitutes the RAW secret between the literal quotes the manifest expression carries,
    /// with no code of ours running, so a password containing '"' is unsupported. This test
    /// documents BOTH failure shapes — fail-loud parse, and the crafted case that parses to a
    /// DIFFERENT value (which is why the XML docs say "unsupported", not "fails loudly").
    /// </summary>
    [Fact]
    public void Published_Substitution_Of_A_Password_With_Embedded_Quotes_Never_Round_Trips()
    {
        static string Substitute(string rawSecret) =>
            $"Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\"{rawSecret}\"";

        // Semicolons and edge whitespace DO round-trip through the published shape.
        Assert.Equal(" se;cret ", OpenLdapConnectionStringBuilder.Parse(Substitute(" se;cret ")).BindPassword);

        // A lone embedded quote fails loudly (trailing junk after the closing quote).
        Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(Substitute("pa\"ss")));

        // Adjacent quotes read as valid doubling: the parse SUCCEEDS but yields a different
        // value than the deployed secret — the bind then fails with wrong credentials.
        Assert.Equal("x\"y", OpenLdapConnectionStringBuilder.Parse(Substitute("x\"\"y")).BindPassword);
    }

    [Fact]
    public void Duplicate_Keys_Are_Rejected()
    {
        var ex = Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldap://h:1;BaseDN=a;BindDN=b;BindPassword=c;BaseDN=again"));
        Assert.Contains("duplicate key 'BaseDN'", ex.Message, StringComparison.Ordinal);
    }

    // Each row asserts WHICH rule rejected the input, not merely that something threw.
    // Type-only assertions let a rejection be produced by an unrelated downstream rule — a
    // mutation pass (#64) showed several parser mutants surviving exactly that way, because
    // the mangled input still ended up throwing FormatException somewhere else.
    [Theory]
    [InlineData("Endpoint=http://h:1389;BaseDN=a;BindDN=b;BindPassword=c", "scheme must be 'ldap' or 'ldaps'")]
    [InlineData("Endpoint=ldap://h:1389/path;BaseDN=a;BindDN=b;BindPassword=c", "must not contain a path or query")]
    [InlineData("Endpoint=ldap://h:1389?q=1;BaseDN=a;BindDN=b;BindPassword=c", "must not contain a path or query")]
    // user info is ignored by LdapDirectoryIdentifier, so it must be rejected rather than dropped
    [InlineData("Endpoint=ldap://user:pw@h:1389;BaseDN=a;BindDN=b;BindPassword=c", "must not contain user info or a fragment")]
    [InlineData("Endpoint=ldap://h:1389#frag;BaseDN=a;BindDN=b;BindPassword=c", "must not contain user info or a fragment")]
    [InlineData("Endpoint=ldap://h:0;BaseDN=a;BindDN=b;BindPassword=c", "port 0 is not valid")]
    [InlineData("Endpoint=notauri;BaseDN=a;BindDN=b;BindPassword=c", "is not a valid absolute URI")]
    [InlineData("Endpoint=ldap:///;BaseDN=a;BindDN=b;BindPassword=c", "must include a host")]
    [InlineData("BaseDN=a;BindDN=b;BindPassword=c", "missing the required 'Endpoint' key")]
    [InlineData("Endpoint=ldap://h:1389;BindDN=b;BindPassword=c", "missing the required 'BaseDN' key")]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindPassword=c", "missing the required 'BindDN' key")]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b", "missing the required 'BindPassword' key")]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\"unterminated", "unterminated quoted value")]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=\"x\"tail", "unexpected characters after a quoted value")]
    [InlineData("Endpoint=ldap://h:1389;justakeywithnovalue;BindDN=b;BindPassword=c", "Malformed connection-string segment: 'justakeywithnovalue'")]
    [InlineData("Endpoint=ldap://h:1389;=orphan;BaseDN=a;BindDN=b;BindPassword=c", "pair with an empty key")]
    public void Malformed_Connection_Strings_Throw(string connectionString, string expectedMessageFragment)
    {
        var ex = Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(connectionString));
        Assert.Contains(expectedMessageFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=c;")]        // trailing separator
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=c;   ")]     // trailing separator + whitespace
    [InlineData(" ;; Endpoint=ldap://h:1389;;BaseDN=a; BindDN=b ;BindPassword=c")]  // stray separators/whitespace between pairs
    public void Separator_And_Whitespace_Noise_Between_Pairs_Is_Tolerated(string connectionString)
    {
        var parsed = OpenLdapConnectionStringBuilder.Parse(connectionString);
        Assert.Equal("a", parsed.BaseDn);
        Assert.Equal("b", parsed.BindDn);
        Assert.Equal("c", parsed.BindPassword);
        Assert.Equal(1389, parsed.Endpoint.Port);
    }

    [Fact]
    public void Portless_Endpoint_Uses_The_Scheme_Default_Port()
    {
        // Deliberate contract (#41): portless endpoints are supported and resolve to the
        // scheme default. System.Uri supplies 389 for ldap; ldaps is not a registered scheme,
        // so the parser fills in 636 itself.
        var ldap = OpenLdapConnectionStringBuilder.Parse("Endpoint=ldap://h;BaseDN=a;BindDN=b;BindPassword=c");
        Assert.Equal(389, ldap.Endpoint.Port);

        var ldaps = OpenLdapConnectionStringBuilder.Parse("Endpoint=ldaps://h;BaseDN=a;BindDN=b;BindPassword=c");
        Assert.Equal(636, ldaps.Endpoint.Port);
        Assert.True(ldaps.UsesLdaps);
        Assert.Equal("h", ldaps.Endpoint.Host);
    }

    [Fact]
    public void CaCertFile_Is_Optional_And_Parsed_When_Present()
    {
        var without = OpenLdapConnectionStringBuilder.Parse(Build("p"));
        Assert.Null(without.CaCertFile);

        var withCa = OpenLdapConnectionStringBuilder.Parse(Build("p") + ";CaCertFile=C:\\certs\\ca.crt");
        Assert.Equal("C:\\certs\\ca.crt", withCa.CaCertFile);

        // A present-but-blank CaCertFile means "no custom CA", not a path of whitespace: the
        // client factory keys the custom-trust path off CaCertFile being non-null, so leaking
        // "" or " " through here would send it looking for a certificate file named "".
        Assert.Null(OpenLdapConnectionStringBuilder.Parse(Build("p") + ";CaCertFile=").CaCertFile);
        Assert.Null(OpenLdapConnectionStringBuilder.Parse(Build("p") + ";CaCertFile=\"   \"").CaCertFile);
    }

    // ---- Build(): the public write path (#72) ----

    private static OpenLdapConnectionStringBuilder Sample(
        string password = "pw", string baseDn = "dc=example,dc=org", string? caCertFile = null) =>
        new()
        {
            Endpoint = new Uri("ldap://localhost:1389"),
            BaseDn = baseDn,
            BindDn = $"cn=admin,{baseDn}",
            BindPassword = password,
            CaCertFile = caCertFile,
        };

    /// <summary>
    /// The reason #72 exists: a consumer synthesizing a connection string from its own inputs
    /// must be able to call the quoting rules instead of copying them. Same equivalence classes
    /// as <see cref="Password_Round_Trips"/>, driven through the write path.
    /// </summary>
    [Theory]
    [InlineData("simplepassword")]
    [InlineData("with=equals;and;semis")]
    [InlineData("a\"b\"\"c")]
    [InlineData(" leading and trailing ")]
    [InlineData("\"fully quoted\"")]
    [InlineData("")]
    public void Build_Round_Trips_Through_Parse(string password)
    {
        var original = Sample(password, baseDn: "dc=ex;ample,dc=org");

        var parsed = OpenLdapConnectionStringBuilder.Parse(original.Build());

        Assert.Equal(password, parsed.BindPassword);
        Assert.Equal("dc=ex;ample,dc=org", parsed.BaseDn);
        Assert.Equal("cn=admin,dc=ex;ample,dc=org", parsed.BindDn);
        Assert.Equal(original.Endpoint.Host, parsed.Endpoint.Host);
        Assert.Equal(original.Endpoint.Port, parsed.Endpoint.Port);
        Assert.Null(parsed.CaCertFile);
    }

    [Fact]
    public void Build_Emits_The_Documented_Shape()
    {
        // Pins key names, order, and that values needing no quoting stay bare — this is the
        // format OpenLdapResource.ConnectionStringExpression emits and Parse consumes.
        Assert.Equal(
            "Endpoint=ldap://localhost:1389;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=pw",
            Sample().Build());
    }

    [Fact]
    public void Build_Emits_CaCertFile_Only_When_Set()
    {
        Assert.DoesNotContain("CaCertFile", Sample(caCertFile: null).Build(), StringComparison.Ordinal);
        Assert.DoesNotContain("CaCertFile", Sample(caCertFile: "").Build(), StringComparison.Ordinal);

        var withCa = Sample(caCertFile: "/etc/ssl/ca.crt");
        Assert.EndsWith(";CaCertFile=/etc/ssl/ca.crt", withCa.Build(), StringComparison.Ordinal);
        Assert.Equal("/etc/ssl/ca.crt", OpenLdapConnectionStringBuilder.Parse(withCa.Build()).CaCertFile);
    }

    [Theory]
    [InlineData("ldap://h", 389)]              // portless ldap: System.Uri supplies the default
    [InlineData("ldaps://h", 636)]             // portless ldaps: unregistered, the parser fills it in
    [InlineData("ldap://[::1]:1389", 1389)]    // IPv6 literal keeps its brackets through Authority
    [InlineData("ldaps://h:1636", 1636)]
    public void Build_Round_Trips_Endpoints(string endpoint, int expectedPort)
    {
        var original = new OpenLdapConnectionStringBuilder
        {
            Endpoint = new Uri(endpoint),
            BaseDn = "a",
            BindDn = "b",
            BindPassword = "c",
        };

        var parsed = OpenLdapConnectionStringBuilder.Parse(original.Build());

        Assert.Equal(expectedPort, parsed.Endpoint.Port);
        Assert.Equal(new Uri(endpoint).Host, parsed.Endpoint.Host);
        Assert.Equal(new Uri(endpoint).Scheme, parsed.Endpoint.Scheme);
    }

    /// <summary>
    /// Write and read enforce one endpoint contract. Without this, Build could emit a string
    /// Parse rejects — the caller would get a failure at the far end of the wire, or worse, a
    /// silently dropped path/user-info component.
    /// </summary>
    [Theory]
    [InlineData("http://h:1389")]          // wrong scheme
    [InlineData("ldap://h:1389/path")]     // path
    [InlineData("ldap://h:1389?q=1")]      // query
    [InlineData("ldap://user:pw@h:1389")]  // user info
    [InlineData("ldap://h:1389#frag")]     // fragment
    public void Build_Rejects_Endpoints_That_Parse_Would_Reject(string endpoint)
    {
        var builder = new OpenLdapConnectionStringBuilder
        {
            Endpoint = new Uri(endpoint),
            BaseDn = "a",
            BindDn = "b",
            BindPassword = "c",
        };

        Assert.Throws<FormatException>(() => builder.Build());
        Assert.Throws<FormatException>(() => OpenLdapConnectionStringBuilder.Parse(
            $"Endpoint={endpoint};BaseDN=a;BindDN=b;BindPassword=c"));
    }

    /// <summary>
    /// ToString must NOT be overridden to emit the connection string: this type carries a
    /// password, and an override would turn any interpolation or log call that mentions the
    /// instance into a credential leak. Build() is opt-in for exactly that reason.
    /// </summary>
    [Fact]
    public void ToString_Does_Not_Leak_The_Password()
    {
        var builder = Sample(password: "super-secret");

        // MA0150 fires because this resolves to object.ToString — which is precisely the
        // property under test. Suppressed, not fixed: the analyzer flagging it is the guard
        // working, and an override added later would both silence MA0150 and fail this test.
#pragma warning disable MA0150
        Assert.DoesNotContain("super-secret", builder.ToString(), StringComparison.Ordinal);
#pragma warning restore MA0150
    }
}
