using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Model-level tests for <c>WithLdapAdmin()</c> (#78): resource shape, the configuration
/// contract the admin host reads, the packaged-payload guard, and the TLS arm. All tests share
/// one class because they create/delete the fabricated payload under the fixed
/// <c>AppContext.BaseDirectory/ldapadmin</c> path, and same-class tests run sequentially.
/// The real packed payload is exercised end-to-end by the clean-consumer test (#82).
/// </summary>
public class LdapAdminModelTests
{
    private static readonly string ContextPath = Path.Combine(AppContext.BaseDirectory, "ldapadmin");

    /// <summary>
    /// Fabricates the minimal payload shape the guard checks for. The test project consumes the
    /// hosting integration by project reference, so the packaged context is legitimately absent
    /// here — which is exactly the state the guard exists to catch.
    /// </summary>
    private static void CreateFakePayload()
    {
        Directory.CreateDirectory(Path.Combine(ContextPath, "app"));
        File.WriteAllText(Path.Combine(ContextPath, "Dockerfile"), "FROM scratch");
        File.WriteAllText(Path.Combine(ContextPath, "app", "Aspire.LdapAdmin.Web.dll"), string.Empty);
    }

    private static void DeletePayload()
    {
        if (Directory.Exists(ContextPath))
        {
            Directory.Delete(ContextPath, recursive: true);
        }
    }

    [Fact]
    public void WithLdapAdmin_Without_Packaged_Payload_Fails_With_Actionable_Error()
    {
        DeletePayload();
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        var ex = Assert.Throws<DistributedApplicationException>(() => ldap.WithLdapAdmin());

        // The message must tell the user both what is wrong and what to do about it.
        Assert.Contains("PackageReference", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddProject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLdapAdmin_Adds_Container_Built_From_The_Packaged_Context()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        builder.AddOpenLdap("ldap").WithLdapAdmin();

        var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
        Assert.Equal("ldap-ldapadmin", admin.Name);
        Assert.Equal("ldap", admin.Parent.Name);

        // Built from the bundled context in the AppHost's build output — never a registry pull,
        // never the source checkout.
        var build = Assert.Single(admin.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(ContextPath, build.ContextPath);

        var endpoint = Assert.Single(admin.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.Equal("http", endpoint.Name);

        // Health = the admin host's /health, which performs a real LDAP bind + root-DSE search.
        var health = Assert.Single(admin.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("ldap-ldapadmin_http_/health_200_check", health.Key);

        // The admin starts only after the directory is healthy.
        var wait = Assert.Single(admin.Annotations.OfType<WaitAnnotation>());
        Assert.Equal("ldap", wait.Resource.Name);
    }

    [Fact]
    public async Task WithLdapAdmin_Env_Contract_Round_Trips_Through_The_Client_Parser()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        builder.AddOpenLdap("ldap").WithLdapAdmin()
            // Applied AFTER the sidecar — the contract must resolve late, not freeze at the call.
            .WithBaseDn("dc=late,dc=org")
            .WithAdminUsername("root");

        var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
        var env = await EnvironmentEvaluation.EvaluateEnvironmentAsync(admin);

        Assert.Equal("ldap", env["LdapAdmin__ConnectionName"]);

        // The options contract (#98) is always emitted, defaults included, so the env the
        // admin binds states the whole surface even when no options were configured.
        Assert.Equal("System", env["LdapAdmin__Theme"]);
        Assert.Equal("100", env["LdapAdmin__DefaultSearchLimit"]);
        Assert.Equal("ServerOrder", env["LdapAdmin__DefaultSortOrder"]);
        Assert.Equal("20", env["LdapAdmin__AttributeValueDisplayCap"]);

        var settings = OpenLdapConnectionStringBuilder.Parse(env["ConnectionStrings__ldap"]);
        // Container-network address: the parent by resource name on its container target port.
        Assert.Equal(new Uri("ldap://ldap:1389"), settings.Endpoint);
        Assert.Equal("dc=late,dc=org", settings.BaseDn);
        Assert.Equal("cn=root,dc=late,dc=org", settings.BindDn);
        Assert.False(string.IsNullOrEmpty(settings.BindPassword));
        Assert.Null(settings.CaCertFile);
    }

    [Fact]
    public async Task WithLdapAdmin_Respects_Tls_Required_Later()
    {
        CreateFakePayload();
        var dir = Directory.CreateTempSubdirectory("aspire-ldapadmin-tls-test");
        try
        {
            var cert = Path.Combine(dir.FullName, "c.pem");
            var key = Path.Combine(dir.FullName, "k.pem");
            var ca = Path.Combine(dir.FullName, "ca.pem");
            File.WriteAllText(cert, "cert");
            File.WriteAllText(key, "key");
            File.WriteAllText(ca, "ca");

            var builder = DistributedApplication.CreateBuilder();
            builder.AddOpenLdap("ldap")
                .WithLdapAdmin()
                .WithTls(cert, key, ca)
                .WithRequiredTls();

            var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
            var env = await EnvironmentEvaluation.EvaluateEnvironmentAsync(admin);

            var settings = OpenLdapConnectionStringBuilder.Parse(env["ConnectionStrings__ldap"]);
            Assert.Equal(new Uri("ldaps://ldap:1636"), settings.Endpoint);
            // Encrypted but unverified in-container TLS (phpLDAPadmin precedent): no CaCertFile
            // — the certificate cannot name the dynamically-assigned container address — and
            // libldap's verification is switched off via its environment knob.
            Assert.Null(settings.CaCertFile);
            Assert.Equal("never", env["LDAPTLS_REQCERT"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithLdapAdmin_Options_Flow_As_LdapAdmin_Env_Configuration()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        builder.AddOpenLdap("ldap").WithLdapAdmin(options =>
        {
            options.Theme = LdapAdminTheme.Dark;
            options.DefaultSearchLimit = 250;
            options.DefaultSortOrder = LdapAdminSortOrder.Rdn;
            options.AttributeValueDisplayCap = 5;
        });

        var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
        var env = await EnvironmentEvaluation.EvaluateEnvironmentAsync(admin);

        // The env contract the admin host binds (LdapAdmin:* section): enum values travel by
        // name, numbers as invariant decimal strings.
        Assert.Equal("Dark", env["LdapAdmin__Theme"]);
        Assert.Equal("250", env["LdapAdmin__DefaultSearchLimit"]);
        Assert.Equal("Rdn", env["LdapAdmin__DefaultSortOrder"]);
        Assert.Equal("5", env["LdapAdmin__AttributeValueDisplayCap"]);
    }

    [Fact]
    public void WithLdapAdmin_Options_Compose_With_The_Container_Callback_And_Name()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        IResourceBuilder<LdapAdminResource>? configured = null;
        builder.AddOpenLdap("ldap").WithLdapAdmin(
            options => options.Theme = LdapAdminTheme.Light,
            configureContainer: admin => configured = admin,
            containerName: "directory-ui");

        var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
        Assert.Equal("directory-ui", admin.Name);
        Assert.NotNull(configured);
        Assert.Same(admin, configured!.Resource);
    }

    [Theory]
    [InlineData(0, 20)]     // below the search page's minimum
    [InlineData(1001, 20)]  // above the search page's maximum
    [InlineData(100, 0)]    // a cap of zero would render no values at all
    public void WithLdapAdmin_Rejects_Options_The_Ui_Could_Not_Honor(int searchLimit, int displayCap)
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        Assert.Throws<ArgumentOutOfRangeException>(() => ldap.WithLdapAdmin(options =>
        {
            options.DefaultSearchLimit = searchLimit;
            options.AttributeValueDisplayCap = displayCap;
        }));
    }

    [Fact]
    public void WithLdapAdmin_Rejects_Undefined_Enum_Options()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ldap.WithLdapAdmin(options => options.Theme = (LdapAdminTheme)42));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ldap.WithLdapAdmin(options => options.DefaultSortOrder = (LdapAdminSortOrder)42));
    }

    [Fact]
    public void WithLdapAdmin_Honors_Container_Name_And_Configure_Callback()
    {
        CreateFakePayload();
        var builder = DistributedApplication.CreateBuilder();
        IResourceBuilder<LdapAdminResource>? configured = null;
        builder.AddOpenLdap("ldap").WithLdapAdmin(
            configureContainer: admin => configured = admin,
            containerName: "directory-ui");

        var admin = Assert.Single(builder.Resources.OfType<LdapAdminResource>());
        Assert.Equal("directory-ui", admin.Name);
        Assert.NotNull(configured);
        Assert.Same(admin, configured!.Resource);
    }
}
