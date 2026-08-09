using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.LdapAdmin.Core;
using Aspire.OpenLdap;
using LdifDotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The per-test deadline. Every operation in this assembly is one LDAP round trip against an
/// already-running container, so a test that has not finished inside this budget is stuck, not
/// slow — and a stuck test should fail with its own name rather than hang the run.
/// </summary>
internal static class TestCancellation
{
    public static CancellationTokenSource Source() => new(TimeSpan.FromMinutes(2));
}

/// <summary>
/// Serializes every test that needs the admin AppHost, and shares one boot across all of them:
/// multiple AppHosts in one process contend on orchestration host ports and hang, and the
/// OpenLDAP container start is by far the most expensive thing in this assembly.
/// </summary>
/// <remarks>
/// This assembly has no direct-docker test family, so there is nothing for a DockerHostGate to
/// serialize against — the collection itself is the whole gate here.
/// </remarks>
[CollectionDefinition(LdapAdminAppHostCollection.Name)]
public sealed class LdapAdminAppHostCollection : ICollectionFixture<LdapAdminAppHostFixture>
{
    public const string Name = "LdapAdminAppHost";
}

/// <summary>
/// Boots <c>Aspire.LdapAdmin.AppHost</c> (a real seeded OpenLDAP container) and stands the
/// admin service layer up on the connection string it publishes — through the same
/// <c>AddOpenLdapClient</c> + <c>AddLdapAdminCore</c> registration a consuming app uses, so the
/// tests exercise the wiring as well as the services.
/// </summary>
public sealed class LdapAdminAppHostFixture : IAsyncLifetime
{
    private const string ResourceName = "openldap";

    private DistributedApplication? _app;
    private IHost? _host;

    /// <summary>The connection string the hosting side published for the OpenLDAP resource.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The parsed form of <see cref="ConnectionString"/>.</summary>
    public OpenLdapConnectionStringBuilder Settings { get; private set; } = null!;

    /// <summary>The service under test, bound with the AppHost's admin credentials.</summary>
    public LdapDirectoryService Directory { get; private set; } = null!;

    /// <summary>The schema service under test, sharing the directory service's schema cache.</summary>
    public LdapSchemaService Schema { get; private set; } = null!;

    /// <summary>The directory's base DN.</summary>
    public string BaseDn => Settings.BaseDn;

    /// <summary>A DN under the base DN: <c>DnUnder("uid=alice", "ou=people")</c>.</summary>
    public string DnUnder(params string[] parts) => Dn.Combine([.. parts, BaseDn]);

    /// <summary>
    /// A second service layer bound as another identity, which is the only way to witness an
    /// ACL refusal: the app's own bind is the directory's administrator and slapd's rootdn
    /// bypasses access control entirely.
    /// </summary>
    public LdapDirectoryService DirectoryAs(string bindDn, string password)
    {
        var connectionString = new OpenLdapConnectionStringBuilder
        {
            Endpoint = Settings.Endpoint,
            BaseDn = Settings.BaseDn,
            BindDn = bindDn,
            BindPassword = password,
            CaCertFile = Settings.CaCertFile,
        }.Build();

        var settings = new OpenLdapClientSettings { ConnectionString = connectionString };
        var factory = new OpenLdapClientFactory(OpenLdapConnectionStringBuilder.Parse(connectionString), settings);
        return new LdapDirectoryService(factory, new LdapSchemaService(factory, NullLogger<LdapSchemaService>.Instance));
    }

    public async Task InitializeAsync()
    {
        // The bundled Dockerfile is built on first run, so a cold start can take minutes.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellationToken = cts.Token;

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Aspire_LdapAdmin_AppHost>([], cancellationToken);

        DistributedApplication app;
        try
        {
            app = await appHost.BuildAsync(cancellationToken);
        }
        catch
        {
            // The builder runs the AppHost entry point in the background and the token only
            // stops the waiting, so a failed build would otherwise leave that factory running.
            await appHost.DisposeAsync();
            throw;
        }

        try
        {
            var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await app.StartAsync(cancellationToken);
            await notifications.WaitForResourceHealthyAsync(ResourceName, cancellationToken);

            var connectionString = await app.GetConnectionStringAsync(ResourceName, cancellationToken);
            Assert.NotNull(connectionString);

            ConnectionString = connectionString!;
            Settings = OpenLdapConnectionStringBuilder.Parse(ConnectionString);
            _host = BuildServiceHost(ConnectionString);
            Directory = _host.Services.GetRequiredService<LdapDirectoryService>();
            Schema = _host.Services.GetRequiredService<LdapSchemaService>();
            _app = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static IHost BuildServiceHost(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[$"ConnectionStrings:{ResourceName}"] = connectionString;
        builder.AddOpenLdapClient(ResourceName);
        builder.Services.AddLdapAdminCore();
        return builder.Build();
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
