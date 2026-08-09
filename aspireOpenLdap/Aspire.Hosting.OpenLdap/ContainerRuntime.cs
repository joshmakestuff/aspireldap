using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Container-runtime interactions behind the dashboard commands (#58): which CLI to shell out
/// to, and the container/volume removal the reset command is built on. Kept apart from the
/// resource-model construction in <see cref="OpenLdapResourceBuilderExtensions"/> so process
/// execution is testable against a fake <see cref="IContainerCliRunner"/>.
/// </summary>
internal static class ContainerRuntime
{
    /// <summary>
    /// The explicitly configured container runtime, or <see langword="null"/> when none is
    /// set. Reads the same configuration keys Aspire binds its (internal) DCP options from:
    /// <c>DcpPublisher:ContainerRuntime</c>, then <c>ASPIRE_CONTAINER_RUNTIME</c> (the
    /// documented selector, which environment/host config surfaces as this key).
    /// </summary>
    internal static string? GetConfigured(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        var fromConfiguration = configuration?["DcpPublisher:ContainerRuntime"]
            ?? configuration?["ASPIRE_CONTAINER_RUNTIME"];
        return string.IsNullOrWhiteSpace(fromConfiguration) ? null : fromConfiguration.Trim();
    }

    /// <summary>
    /// Resolves the container runtime the dashboard commands shell out to (#58). An explicit
    /// configuration is authoritative — used as-is so a misconfiguration fails loudly on use
    /// rather than being silently papered over. With no configuration, mirror Aspire's own
    /// behavior of probing known runtimes (DCP auto-detects when unconfigured, so LDAP can be
    /// running under podman on a docker-less machine): try <c>docker --version</c>, then
    /// <c>podman --version</c>. Returns the runtime, or a failure result naming what was tried.
    /// </summary>
    internal static async Task<(string? Runtime, ExecuteCommandResult? Failure)> ResolveAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var configured = GetConfigured(services);
        if (configured is not null)
        {
            return (configured, null);
        }

        var runner = services.GetRequiredService<IContainerCliRunner>();
        foreach (var candidate in (string[])["docker", "podman"])
        {
            var (exitCode, _, _) = await runner.RunAsync(candidate, ["--version"], cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                return (candidate, null);
            }
        }

        return (null, new ExecuteCommandResult
        {
            Success = false,
            Message = "No container runtime found: tried 'docker --version' and 'podman --version'. " +
                "Install one, or set ASPIRE_CONTAINER_RUNTIME to the runtime this AppHost uses.",
        });
    }

    /// <summary>
    /// Force-removes the stopped container (Aspire's Stop only stops it; the volume stays
    /// bound until the container is gone) and then removes the data volume. Returns a failure
    /// <see cref="ExecuteCommandResult"/>, or <see langword="null"/> when both are gone —
    /// "no such container"/"no such volume" count as gone (the user wanted them gone).
    /// Internal so tests can drive it against a fake <see cref="IContainerCliRunner"/>.
    /// </summary>
    internal static async Task<ExecuteCommandResult?> RemoveContainerAndVolumeAsync(
        IServiceProvider services,
        string? containerId,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var runner = services.GetRequiredService<IContainerCliRunner>();
        var (runtime, resolveFailure) = await ResolveAsync(services, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            return resolveFailure;
        }

        if (containerId is not null)
        {
            var (containerRmExit, _, containerRmErr) = await runner.RunAsync(
                runtime, ["rm", "-f", containerId], cancellationToken).ConfigureAwait(false);
            if (containerRmExit == ProcessContainerCliRunner.StartFailureExitCode)
            {
                return new ExecuteCommandResult { Success = false, Message = containerRmErr.Trim() };
            }
            if (containerRmExit != 0 && !containerRmErr.Contains("no such container", StringComparison.OrdinalIgnoreCase))
            {
                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = $"{runtime} rm -f failed (exit {containerRmExit}): {containerRmErr.Trim()}",
                };
            }
        }

        var (rmExit, _, rmErr) = await runner.RunAsync(
            runtime, ["volume", "rm", volumeName], cancellationToken).ConfigureAwait(false);
        if (rmExit == ProcessContainerCliRunner.StartFailureExitCode)
        {
            return new ExecuteCommandResult { Success = false, Message = rmErr.Trim() };
        }
        if (rmExit != 0 && !rmErr.Contains("no such volume", StringComparison.OrdinalIgnoreCase))
        {
            return new ExecuteCommandResult
            {
                Success = false,
                Message = $"{runtime} volume rm failed (exit {rmExit}): {rmErr.Trim()}",
            };
        }

        return null;
    }
}
