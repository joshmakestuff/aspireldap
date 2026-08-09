using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using AspireOpenLdap.TestAppHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// A started TestAppHost plus the connection string it published and the parsed settings —
/// the three things every AppHost-backed integration test needed and used to re-derive by hand.
/// </summary>
/// <param name="App">The running application. Owned by the fixture; tests must not dispose it.</param>
/// <param name="ConnectionString">The raw connection string the hosting side emitted.</param>
/// <param name="Settings">The parsed form of <paramref name="ConnectionString"/>.</param>
public sealed record StartedAppHost(
    DistributedApplication App,
    string ConnectionString,
    OpenLdapConnectionStringBuilder Settings);

/// <summary>
/// Starts the TestAppHost for a named scenario and keeps it running so consecutive facts on the
/// same scenario share one boot instead of paying a second container start (the two
/// config-witness facts, for example). Replaces the CreateAsync → BuildAsync → StartAsync →
/// WaitForResourceHealthyAsync → GetConnectionStringAsync → Parse ritual that was copied into
/// every AppHost-backed test class.
/// </summary>
/// <remarks>
/// Exactly one AppHost is alive at a time: asking for a different scenario disposes the current
/// one first. Multiple AppHosts in one process contend on orchestration host ports and hang,
/// which is why <see cref="AppHostCollection"/> serializes these tests in the first place.
/// </remarks>
public sealed class AppHostFixture : IAsyncLifetime
{
    private const string ResourceName = "openldap";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DistributedApplication? _app;
    private string? _key;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Returns a healthy AppHost running <paramref name="scenario"/>, reusing the currently
    /// running one when it is already that scenario (with the same <paramref name="extraArgs"/>).
    /// </summary>
    public async Task<StartedAppHost> StartAsync(
        string scenario, CancellationToken cancellationToken, params string[] extraArgs)
    {
        // '\n' as separator: a command-line argument never contains one, so distinct
        // (scenario, args) tuples can never collide onto one key.
        var key = string.Join('\n', [scenario, .. extraArgs]);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_key != key || _app is null)
            {
                // Drop the previous scenario before starting the next: never two at once.
                await StopCurrentAsync();
                _app = await StartScenarioAsync(scenario, extraArgs, cancellationToken);
                _key = key;
            }

            var connectionString = await _app.GetConnectionStringAsync(ResourceName, cancellationToken);
            Assert.NotNull(connectionString);
            return new StartedAppHost(_app, connectionString!, OpenLdapConnectionStringBuilder.Parse(connectionString!));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops whatever scenario is running. Needed by tests whose scenario is fed from a temp
    /// directory: the bind mount must be released before the directory can be deleted.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<DistributedApplication> StartScenarioAsync(
        string scenario, string[] extraArgs, CancellationToken cancellationToken)
    {
        string[] args = [$"--{TestAppHostScenarios.ScenarioKey}={scenario}", .. extraArgs];

        // Held from builder creation through healthy: DCP startup (and the image build it may
        // trigger) must never overlap the direct-docker bundled-image build (#54).
        using (await DockerHostGate.AcquireAsync(cancellationToken))
        {
            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(args, cancellationToken);

            DistributedApplication app;
            try
            {
                app = await appHost.BuildAsync(cancellationToken);
            }
            catch
            {
                // The builder runs the AppHost entry point in the background, and the token only
                // stops the waiting — without this dispose, a failed or timed-out build leaves
                // that factory running alongside the next scenario's AppHost.
                await appHost.DisposeAsync();
                throw;
            }

            try
            {
                var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
                await app.StartAsync(cancellationToken);
                // The bundled Dockerfile is built on first run, so this can take a while cold.
                await notifications.WaitForResourceHealthyAsync(ResourceName, cancellationToken);
                return app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
    }

    private async Task StopCurrentAsync()
    {
        // Clear the key first: a failed dispose must not leave a stale scenario advertised as
        // running for the next caller.
        _key = null;
        var app = _app;
        _app = null;
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await StopCurrentAsync();
        _gate.Dispose();
    }
}
