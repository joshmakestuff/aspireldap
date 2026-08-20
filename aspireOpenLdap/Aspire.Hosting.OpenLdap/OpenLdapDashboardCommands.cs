using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// The resource commands surfaced on the Aspire dashboard for an OpenLDAP resource: the
/// read-only "show this value" commands, the slapcat LDIF export, and the data-volume reset.
/// Registration lives here rather than in <see cref="OpenLdapResourceBuilderExtensions"/> so
/// the fluent API file stays about the resource model.
/// </summary>
internal static class OpenLdapDashboardCommands
{
    /// <summary>
    /// Registers the commands every OpenLDAP resource gets. The reset command is registered
    /// separately by <see cref="RegisterResetDataVolume"/>, because it only makes sense once a
    /// data volume exists.
    /// </summary>
    internal static void Register(IResourceBuilder<OpenLdapResource> builder)
    {
        RegisterShowValueCommands(builder);
        RegisterExportLdifCommand(builder);
        RegisterShowAdminPasswordCommand(builder);
    }

    /// <summary>The two commands that just surface a value already known to the AppHost.</summary>
    private static void RegisterShowValueCommands(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;

        builder.WithCommand(
            name: "copy-base-dn",
            displayName: "Show base DN",
            executeCommand: _ => Task.FromResult(new ExecuteCommandResult
            {
                Success = true,
                Data = new CommandResultData
                {
                    Value = resource.BaseDn,
                    Format = CommandResultFormat.Text,
                    DisplayImmediately = true,
                },
            }),
            commandOptions: new CommandOptions
            {
                Description = "Show the directory's base DN.",
                IconName = "Copy",
            });

        builder.WithCommand(
            name: "copy-bind-dn",
            displayName: "Show admin bind DN",
            executeCommand: _ => Task.FromResult(new ExecuteCommandResult
            {
                Success = true,
                Data = new CommandResultData
                {
                    Value = resource.AdminBindDn,
                    Format = CommandResultFormat.Text,
                    DisplayImmediately = true,
                },
            }),
            commandOptions: new CommandOptions
            {
                Description = "Show the admin bind DN.",
                IconName = "Copy",
            });
    }

    /// <summary>Dumps the running container's directory contents via <c>slapcat</c>.</summary>
    private static void RegisterExportLdifCommand(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;

        builder.WithCommand(
            name: "export-ldif",
            displayName: "Export LDIF",
            executeCommand: async ctx =>
            {
                var containerId = TryGetContainerId(ctx);
                if (containerId is null)
                {
                    return new ExecuteCommandResult
                    {
                        Success = false,
                        Message = "Container is not running.",
                    };
                }

                var runner = ctx.Services.GetRequiredService<IContainerCliRunner>();
                var (runtime, resolveFailure) = await ContainerRuntime.ResolveAsync(
                    ctx.Services, ctx.CancellationToken).ConfigureAwait(false);
                if (runtime is null)
                {
                    return resolveFailure!;
                }
                var (exitCode, stdout, stderr) = await runner.RunAsync(
                    runtime,
                    ["exec", containerId, "slapcat", "-b", resource.BaseDn],
                    ctx.CancellationToken).ConfigureAwait(false);

                if (exitCode == ProcessContainerCliRunner.StartFailureExitCode)
                {
                    return new ExecuteCommandResult { Success = false, Message = stderr.Trim() };
                }
                if (exitCode != 0)
                {
                    return new ExecuteCommandResult
                    {
                        Success = false,
                        Message = $"slapcat via {runtime} failed (exit {exitCode}): {stderr.Trim()}",
                    };
                }

                return new ExecuteCommandResult
                {
                    Success = true,
                    Data = new CommandResultData
                    {
                        Value = $"```ldif\n{stdout}\n```",
                        Format = CommandResultFormat.Markdown,
                        DisplayImmediately = true,
                    },
                };
            },
            commandOptions: new CommandOptions
            {
                Description = "Dump the directory contents as LDIF (via slapcat).",
                IconName = "ArrowDownload",
            });
    }

    /// <summary>Reveals the generated admin password, behind a confirmation.</summary>
    private static void RegisterShowAdminPasswordCommand(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;

        builder.WithCommand(
            name: "copy-admin-password",
            displayName: "Show admin password",
            executeCommand: async ctx =>
            {
                var pw = await resource.AdminPasswordParameter.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult
                {
                    Success = true,
                    Data = new CommandResultData
                    {
                        Value = pw ?? string.Empty,
                        Format = CommandResultFormat.Text,
                        DisplayImmediately = true,
                    },
                };
            },
            commandOptions: new CommandOptions
            {
                Description = "Reveal the admin password (sensitive).",
                IconName = "Key",
                ConfirmationMessage = "Reveal the admin password? It will be shown in a dialog.",
            });
    }

    /// <summary>
    /// Registers the stop / delete-volume / start command for the named data volume.
    /// </summary>
    internal static void RegisterResetDataVolume(
        IResourceBuilder<OpenLdapResource> builder,
        string volumeName)
    {
        builder.WithCommand(
            name: "reset-data-volume",
            displayName: "Reset data volume",
            executeCommand: async ctx =>
            {
                var commandService = ctx.Services.GetRequiredService<ResourceCommandService>();

                // Capture the container ID before Stop — once stopped, the snapshot's
                // container.id property is gone.
                var containerId = TryGetContainerId(ctx);

                var stopResult = await commandService
                    .ExecuteCommandAsync(ctx.ResourceName, KnownResourceCommands.StopCommand, ctx.CancellationToken)
                    .ConfigureAwait(false);
                if (!stopResult.Success)
                {
                    return stopResult;
                }

                var removalFailure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
                    ctx.Services, containerId, volumeName, ctx.CancellationToken).ConfigureAwait(false);
                if (removalFailure is not null)
                {
                    return removalFailure;
                }

                return await commandService
                    .ExecuteCommandAsync(ctx.ResourceName, KnownResourceCommands.StartCommand, ctx.CancellationToken)
                    .ConfigureAwait(false);
            },
            commandOptions: new CommandOptions
            {
                Description = "Stop the container, delete the data volume, and start fresh.",
                IconName = "Delete",
                ConfirmationMessage = $"Delete the '{volumeName}' volume and restart? All directory data will be lost.",
            });
    }

    private static string? TryGetContainerId(ExecuteCommandContext ctx)
    {
        var notify = ctx.Services.GetRequiredService<ResourceNotificationService>();
        if (!notify.TryGetCurrentState(ctx.ResourceName, out var evt) || evt is null)
        {
            return null;
        }
        var prop = evt.Snapshot.Properties.FirstOrDefault(p => string.Equals(p.Name, "container.id", StringComparison.Ordinal));
        return prop?.Value as string;
    }
}
