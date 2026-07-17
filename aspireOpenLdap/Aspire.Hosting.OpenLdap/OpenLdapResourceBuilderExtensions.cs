using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring an OpenLDAP container resource in an Aspire AppHost.
/// </summary>
public static class OpenLdapResourceBuilderExtensions
{
    /// <summary>
    /// Adds an OpenLDAP container resource built from the integration's bundled Dockerfile.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Resource name. Surfaces on the dashboard and is the connection-string key.</param>
    /// <param name="adminPassword">
    /// Optional parameter resource backing the admin password. When omitted, a 22-character random
    /// password is auto-generated via <see cref="ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter"/>
    /// and surfaced in the Aspire dashboard as a secret parameter named <c>{name}-password</c>.
    /// </param>
    /// <remarks>
    /// Defaults: base DN <c>dc=example,dc=org</c>, admin username <c>admin</c>, auto-allocated host ports.
    /// Override via <c>WithBaseDn</c>, <c>WithAdminUsername</c>, <c>WithLdapPort</c>, <c>WithLdapsPort</c>.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> AddOpenLdap(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? adminPassword = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // On Linux distros shipping OpenLDAP 2.6+ the runtime's hardcoded libldap-2.5 load
        // fails; register the soname fallback resolver so the health check's LdapConnection
        // works without a hand-made symlink.
        Aspire.Hosting.OpenLdap.OpenLdapNativeLibraryResolver.EnsureRegistered();

        var passwordParameter = adminPassword?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var resource = new OpenLdapResource(
            name,
            baseDn: OpenLdapResource.DefaultBaseDn,
            adminUsername: OpenLdapResource.DefaultAdminUsername,
            adminPasswordParameter: passwordParameter);

        var openLdap = builder
            .AddResource(resource)
            // Sets the publish-time image name. The local docker build tag is content-hash-addressed
            // by Aspire's WithDockerfile and not affected by this call.
            .WithImage(OpenLdapResource.DefaultImageName, OpenLdapResource.DefaultImageTag)
            .WithDockerfile(OpenLdapResource.DefaultDockerContextPath, OpenLdapResource.DefaultDockerfilePath)
            // Proxied endpoints: Aspire allocates a free host port per run, so multiple
            // AppHosts (or multiple LDAP resources) never collide. Pin a fixed host port
            // via WithLdapPort / WithLdapsPort when a stable address is needed.
            .WithEndpoint(targetPort: OpenLdapResource.DefaultLdapTargetPort, name: OpenLdapResource.LdapEndpointName)
            .WithEndpoint(targetPort: OpenLdapResource.DefaultLdapsTargetPort, name: OpenLdapResource.LdapsEndpointName)
            // Late-binding env values so fluent overrides (e.g. WithBaseDn) take effect when the container starts.
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_ROOT"] = resource.BaseDn;
                context.EnvironmentVariables["LDAP_ADMIN_USERNAME"] = resource.AdminUsername;
                context.EnvironmentVariables["LDAP_ADMIN_PASSWORD"] = passwordParameter;
            });

        // Register LDAP root DSE health check
        var healthCheckName = $"openldap-{name}";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckName,
            sp => new OpenLdapHealthCheck(resource),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));

        openLdap.WithHealthCheck(healthCheckName);
        RegisterDashboardCommands(openLdap);

        // Surface the base DN next to the endpoint URL on the dashboard so users don't have to
        // click through env vars. Lambdas read resource.BaseDn lazily, so WithBaseDn(...) overrides
        // are picked up.
        openLdap
            .WithUrlForEndpoint(OpenLdapResource.LdapEndpointName, url =>
            {
                url.DisplayText = $"ldap (base={resource.BaseDn})";
            })
            .WithUrlForEndpoint(OpenLdapResource.LdapsEndpointName, url =>
            {
                url.DisplayText = $"ldaps (base={resource.BaseDn})";
            });

        return openLdap;
    }

    private static void RegisterDashboardCommands(IResourceBuilder<OpenLdapResource> builder)
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

                var (exitCode, stdout, stderr) = await RunProcessAsync(
                    "docker",
                    ["exec", containerId, "slapcat", "-b", resource.BaseDn],
                    ctx.CancellationToken).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    return new ExecuteCommandResult
                    {
                        Success = false,
                        Message = $"slapcat failed (exit {exitCode}): {stderr.Trim()}",
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
    /// Overrides the directory's base DN (a.k.a. suffix / root). Default <c>dc=example,dc=org</c>.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithBaseDn(
        this IResourceBuilder<OpenLdapResource> builder,
        string baseDn)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDn);
        builder.Resource.BaseDn = baseDn;
        return builder;
    }

    /// <summary>
    /// Overrides the admin username. Bind DN becomes <c>cn={username},{baseDn}</c>. Default <c>admin</c>.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithAdminUsername(
        this IResourceBuilder<OpenLdapResource> builder,
        string username)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        builder.Resource.AdminUsername = username;
        return builder;
    }

    /// <summary>
    /// Pins the host port for the plain LDAP endpoint. By default Aspire allocates a random port.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithLdapPort(
        this IResourceBuilder<OpenLdapResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SetEndpointPort(builder, OpenLdapResource.LdapEndpointName, port);
        return builder;
    }

    /// <summary>
    /// Pins the host port for the LDAPS endpoint. By default Aspire allocates a random port.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithLdapsPort(
        this IResourceBuilder<OpenLdapResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SetEndpointPort(builder, OpenLdapResource.LdapsEndpointName, port);
        return builder;
    }

    private static void SetEndpointPort(IResourceBuilder<OpenLdapResource> builder, string endpointName, int port)
    {
        // Validate here so a bad value fails at the fluent call rather than later inside
        // Aspire's endpoint allocation with a less attributable error.
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var annotation = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new DistributedApplicationException(
                $"Endpoint '{endpointName}' not found on OpenLDAP resource '{builder.Resource.Name}'.");
        annotation.Port = port;
    }

    /// <summary>
    /// Adds a named data volume for the OpenLDAP data directory (<c>/data/openldap</c>).
    /// On subsequent starts, the container detects existing data and skips reinitialization
    /// (including re-applying seed LDIFs), making startup fast even with large seed data.
    /// </summary>
    /// <remarks>
    /// When <paramref name="name"/> is omitted, the volume name is scoped to this AppHost
    /// (e.g. <c>myapp.apphost-64d61f24-ldap-data</c>) so different projects never share a
    /// volume by accident. Pass an explicit name to opt into cross-AppHost sharing.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithDataVolume(
        this IResourceBuilder<OpenLdapResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var volumeName = name ?? VolumeNameGenerator.Generate(builder, "data");
        builder.WithVolume(volumeName, OpenLdapResource.DataPath, isReadOnly);
        RegisterResetDataVolumeCommand(builder, volumeName);
        return builder;
    }

    private static void RegisterResetDataVolumeCommand(
        IResourceBuilder<OpenLdapResource> builder,
        string volumeName)
    {
        builder.WithCommand(
            name: "reset-data-volume",
            displayName: "Reset data volume",
            executeCommand: async ctx =>
            {
                var commandService = ctx.ServiceProvider.GetRequiredService<ResourceCommandService>();

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

                // Aspire's Stop only `docker stop`s the container; the volume stays bound
                // until the container is removed. Force-remove by ID so `docker volume rm`
                // can succeed. No-op if the container is already gone.
                if (containerId is not null)
                {
                    var (containerRmExit, _, containerRmErr) = await RunProcessAsync(
                        "docker",
                        ["rm", "-f", containerId],
                        ctx.CancellationToken).ConfigureAwait(false);
                    if (containerRmExit != 0 && !containerRmErr.Contains("no such container", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExecuteCommandResult
                        {
                            Success = false,
                            Message = $"docker rm -f failed (exit {containerRmExit}): {containerRmErr.Trim()}",
                        };
                    }
                }

                var (rmExit, _, rmErr) = await RunProcessAsync(
                    "docker",
                    ["volume", "rm", volumeName],
                    ctx.CancellationToken).ConfigureAwait(false);
                // Treat "volume not found" as success — the user wanted it gone.
                if (rmExit != 0 && !rmErr.Contains("no such volume", StringComparison.OrdinalIgnoreCase))
                {
                    return new ExecuteCommandResult
                    {
                        Success = false,
                        Message = $"docker volume rm failed (exit {rmExit}): {rmErr.Trim()}",
                    };
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
        var notify = ctx.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        if (!notify.TryGetCurrentState(ctx.ResourceName, out var evt) || evt is null)
        {
            return null;
        }
        var prop = evt.Snapshot.Properties.FirstOrDefault(p => string.Equals(p.Name, "container.id", StringComparison.Ordinal));
        return prop?.Value as string;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Bind-mounts a host directory at the OpenLDAP data path (<c>/data/openldap</c>).
    /// Same reinit-skipping behavior as <see cref="WithDataVolume"/>.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithDataBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, OpenLdapResource.DataPath, isReadOnly);
    }

    /// <summary>
    /// Adds a custom LDAP schema from a single LDIF file. The file is mounted into the container
    /// and loaded via <c>slapadd -n 0</c> during initialization.
    /// </summary>
    /// <remarks>
    /// The file must be in OpenLDAP <c>cn=config</c> form — a <c>dn: cn=NAME,cn=schema,cn=config</c>
    /// entry with <c>objectClass: olcSchemaConfig</c> and <c>olcAttributeTypes</c>/<c>olcObjectClasses</c>
    /// values. Legacy slapd.conf-style <c>.schema</c> files are NOT accepted. Convert one with
    /// <c>slaptest -f slapd.conf -F out</c>, then take the generated
    /// <c>out/cn=config/cn=schema/cn={N}NAME.ldif</c>, rewrite its relative <c>dn:</c>/<c>cn:</c> to the
    /// full <c>cn=NAME,cn=schema,cn=config</c>, and drop the trailing operational attributes
    /// (everything from <c>structuralObjectClass</c> onward).
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSchema(
        this IResourceBuilder<OpenLdapResource> builder,
        string ldifFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldifFile);

        var fullPath = Path.GetFullPath(ldifFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Schema LDIF file not found: {fullPath}", fullPath);
        }

        return builder.WithBindMount(fullPath, "/schema/custom.ldif", isReadOnly: true);
    }

    /// <summary>
    /// Adds a directory of custom LDAP schema LDIF files. Files with the <c>.ldif</c> extension
    /// are loaded in sorted (alphabetical) order via <c>slapadd -n 0</c> during initialization.
    /// </summary>
    /// <remarks>
    /// Each file must be in OpenLDAP <c>cn=config</c> form (see <see cref="WithSchema"/> for the format
    /// and a conversion recipe). Because files load alphabetically, prefix them to honor inter-schema
    /// dependencies (e.g. <c>10-foo.ldif</c> before <c>20-bar.ldif</c>). Note the image already loads
    /// <c>core</c> plus the <see cref="WithExtraSchemas"/> set (default <c>cosine,inetorgperson,nis</c>)
    /// before these — supplying your own copies of those here causes duplicate-OID errors, so disable
    /// the overlap with <see cref="WithExtraSchemas"/> or <see cref="WithDefaultSchemas"/>.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSchemas(
        this IResourceBuilder<OpenLdapResource> builder,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Schema directory not found: {fullPath}");
        }

        return builder.WithBindMount(fullPath, "/schemas", isReadOnly: true);
    }

    /// <summary>
    /// Controls whether the image loads its bundled default schemas during initialization
    /// (<c>LDAP_ADD_SCHEMAS</c>). Enabled by default. The schemas loaded are governed by
    /// <see cref="WithExtraSchemas"/> (default <c>cosine,inetorgperson,nis</c>).
    /// </summary>
    /// <remarks>
    /// Disable this (<c>WithDefaultSchemas(false)</c>) when you supply the full schema set yourself via
    /// <see cref="WithSchemas"/> and want to avoid duplicate-OID collisions. Note <c>core</c> is always
    /// bootstrapped by the image regardless of this setting, so don't also mount a <c>core</c> schema.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithDefaultSchemas(
        this IResourceBuilder<OpenLdapResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment("LDAP_ADD_SCHEMAS", enabled ? "yes" : "no");
    }

    /// <summary>
    /// Selects which image-bundled schemas are loaded before any <see cref="WithSchemas"/> files
    /// (<c>LDAP_EXTRA_SCHEMAS</c>). Replaces the default set (<c>cosine,inetorgperson,nis</c>).
    /// </summary>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="schemas">
    /// Schema names matching files under the image's <c>/etc/ldap/schema/{name}.ldif</c>
    /// (e.g. <c>cosine</c>, <c>inetorgperson</c>, <c>nis</c>, <c>dyngroup</c>). Pass none to load only
    /// the always-bootstrapped <c>core</c>.
    /// </param>
    /// <remarks>
    /// Use this to keep the image's vetted copies of standard schemas while dropping the ones you ship
    /// yourself via <see cref="WithSchemas"/> — supplying a name both here and as a mounted file causes
    /// duplicate-OID errors. Has no effect unless default schemas are enabled (see
    /// <see cref="WithDefaultSchemas"/>).
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithExtraSchemas(
        this IResourceBuilder<OpenLdapResource> builder,
        params string[] schemas)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schemas);
        return builder.WithEnvironment("LDAP_EXTRA_SCHEMAS", string.Join(",", schemas));
    }

    /// <summary>
    /// Seeds the directory with one or more LDIF files loaded via <c>ldapadd</c> after
    /// initialization completes. Accepts either a single LDIF file or a directory of LDIF files.
    /// </summary>
    /// <remarks>
    /// When seed data is present the container's default tree (the <c>LDAP_USERS</c>/<c>LDAP_PASSWORDS</c>
    /// users) is NOT created — your seed becomes the entire initial dataset. Pair with
    /// <see cref="WithDataVolume"/> to amortize the cost of large seeds across restarts.
    /// <para>
    /// By default a single rejected entry (bad DN, missing parent, schema violation) aborts the
    /// entire load — the directory fails to come up rather than silently coming up partial. Set
    /// <paramref name="continueOnError"/> to <see langword="true"/> to load with <c>ldapadd -c</c>,
    /// which skips past individual bad entries and logs them instead of failing the load. Use this
    /// for messy bulk data where a partial directory is acceptable.
    /// </para>
    /// </remarks>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="ldifFileOrDirectory">Path to a single LDIF file or a directory of LDIF files.</param>
    /// <param name="continueOnError">
    /// When <see langword="true"/>, load with <c>ldapadd -c</c> so a rejected entry does not abort
    /// the rest of the seed. Defaults to <see langword="false"/> (fail-loud on the first bad entry).
    /// </param>
    public static IResourceBuilder<OpenLdapResource> WithSeedData(
        this IResourceBuilder<OpenLdapResource> builder,
        string ldifFileOrDirectory,
        bool continueOnError = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldifFileOrDirectory);

        var fullPath = Path.GetFullPath(ldifFileOrDirectory);

        if (continueOnError)
        {
            builder.WithEnvironment("LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR", "yes");
        }

        if (Directory.Exists(fullPath))
        {
            return builder.WithBindMount(fullPath, "/ldifs", isReadOnly: true);
        }

        if (File.Exists(fullPath))
        {
            var fileName = Path.GetFileName(fullPath);
            return builder.WithBindMount(fullPath, $"/ldifs/{fileName}", isReadOnly: true);
        }

        throw new FileNotFoundException(
            $"Seed data path not found: {fullPath}", fullPath);
    }

    private const string GeneratedSeedContainerPath = "/ldifs/00-aspire-seed.ldif";

    /// <summary>
    /// Declares an organizational unit under the base DN. Other seed builder calls
    /// (<see cref="WithUser"/>, <see cref="WithGroup"/>) reference it by name.
    /// </summary>
    /// <remarks>
    /// Names must match <c>[A-Za-z0-9._-]+</c>. References to undeclared OUs throw a
    /// <see cref="DistributedApplicationException"/> with a "did you mean" suggestion
    /// when the resource starts.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithOrganizationalUnit(
        this IResourceBuilder<OpenLdapResource> builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var model = GetOrInitializeSeedModel(builder);
        model.OrganizationalUnits.Add(new OrganizationalUnitEntry(name));
        return builder;
    }

    /// <summary>
    /// Declares a user entry (objectClass <c>inetOrgPerson</c>) seeded into the directory.
    /// </summary>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="uid">The user's <c>uid</c>. Becomes the RDN. Must match <c>[A-Za-z0-9._-]+</c>.</param>
    /// <param name="password">Password stored as <c>userPassword</c>. OpenLDAP hashes it on add when configured to do so.</param>
    /// <param name="ou">Optional organizational unit. Must match a name passed to <see cref="WithOrganizationalUnit"/>.</param>
    /// <param name="cn">Common name. Defaults to <paramref name="uid"/>.</param>
    /// <param name="sn">Surname (required for <c>inetOrgPerson</c>). Defaults to <paramref name="uid"/>.</param>
    /// <param name="mail">Optional <c>mail</c> attribute.</param>
    public static IResourceBuilder<OpenLdapResource> WithUser(
        this IResourceBuilder<OpenLdapResource> builder,
        string uid,
        string password,
        string? ou = null,
        string? cn = null,
        string? sn = null,
        string? mail = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var model = GetOrInitializeSeedModel(builder);
        model.Users.Add(new SeedUserEntry(
            Uid: uid,
            Password: password,
            OrganizationalUnit: string.IsNullOrWhiteSpace(ou) ? null : ou,
            Cn: string.IsNullOrWhiteSpace(cn) ? uid : cn,
            Sn: string.IsNullOrWhiteSpace(sn) ? uid : sn,
            Mail: string.IsNullOrWhiteSpace(mail) ? null : mail));
        return builder;
    }

    /// <summary>
    /// Declares a group entry (objectClass <c>groupOfNames</c>) seeded into the directory.
    /// </summary>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="cn">Group's <c>cn</c>. Becomes the RDN.</param>
    /// <param name="members">
    /// Members. Each entry is either a previously-declared user <c>uid</c> (resolved to its DN
    /// at LDIF emission) or a literal DN (any string containing <c>=</c>). At least one member is required.
    /// </param>
    /// <param name="ou">Optional organizational unit; must match a <see cref="WithOrganizationalUnit"/> declaration.</param>
    public static IResourceBuilder<OpenLdapResource> WithGroup(
        this IResourceBuilder<OpenLdapResource> builder,
        string cn,
        IEnumerable<string> members,
        string? ou = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cn);
        ArgumentNullException.ThrowIfNull(members);

        var memberList = members.ToList();
        var model = GetOrInitializeSeedModel(builder);
        model.Groups.Add(new SeedGroupEntry(
            Cn: cn,
            Members: memberList,
            OrganizationalUnit: string.IsNullOrWhiteSpace(ou) ? null : ou));
        return builder;
    }

    private const string GeneratedSeedRecordsContainerPath = "/ldifs/01-aspire-seed-records.ldif";

    /// <summary>
    /// Seeds the directory from LDIF records built with the <c>LdifDotNet</c> object model
    /// (<see cref="LdifContentRecord"/>, <see cref="LdifAttribute"/>, …) — the escape hatch for
    /// entries the typed helpers (<see cref="WithUser"/>, <see cref="WithGroup"/>, …) don't cover,
    /// e.g. custom objectClasses or binary attributes. May be called multiple times; records
    /// accumulate into one generated LDIF file loaded via <c>ldapadd</c> after the typed seed.
    /// </summary>
    /// <remarks>
    /// Values are RFC 2849-encoded on write (base64 where required), so arbitrary strings and
    /// binary data are safe. The file may hold either content records or change records, not a
    /// mix — <c>LdifWriter</c> rejects mixed documents when the resource starts. Parent entries
    /// must exist: the base-DN root is created automatically only when the typed seed helpers are
    /// also used; otherwise include the root entry in the records.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSeedRecords(
        this IResourceBuilder<OpenLdapResource> builder,
        params IEnumerable<LdifRecord> records)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(records);

        var resource = builder.Resource;
        if (resource.SeedRecords is null)
        {
            resource.SeedRecords = [];

            // Stable path under the AppHost's obj directory so the bind mount target survives rebuilds.
            var seedDir = Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "obj", "aspire-openldap-seed");
            Directory.CreateDirectory(seedDir);
            var recordsPath = Path.Combine(seedDir, $"{resource.Name}-seed-records.ldif");
            resource.SeedRecordsFilePath = recordsPath;

            // Bind-mount needs an existing file at start time; real content is written by the handler below.
            if (!File.Exists(recordsPath))
            {
                File.WriteAllText(recordsPath, string.Empty);
            }

            builder.WithBindMount(recordsPath, GeneratedSeedRecordsContainerPath, isReadOnly: true);

            builder.OnBeforeResourceStarted((res, _, ct) =>
            {
                if (res.SeedRecords is not { Count: > 0 } seedRecords || res.SeedRecordsFilePath is null)
                {
                    return Task.CompletedTask;
                }
                var ldif = LdifWriter.WriteToString(seedRecords, LdapSeedLdifGenerator.WriterOptions);
                return File.WriteAllTextAsync(res.SeedRecordsFilePath, ldif, ct);
            });
        }

        foreach (var record in records)
        {
            if (record is null)
            {
                throw new ArgumentException("Seed records must not contain null.", nameof(records));
            }
            resource.SeedRecords.Add(record);
        }
        return builder;
    }

    /// <summary>
    /// Enables an OpenLDAP <paramref name="overlay"/> (opt-in). The overlay's <c>cn=config</c>
    /// entries (module load + config) are folded into the slapd bootstrap before the data load,
    /// so e.g. <c>memberof</c> populates as the seed loads. Call once per overlay.
    /// </summary>
    /// <remarks>
    /// Overlays are part of the seed-once bootstrap: enabling one on an already-seeded data
    /// volume requires resetting the volume so the bootstrap (and any seed-time population) re-runs.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithOverlay(
        this IResourceBuilder<OpenLdapResource> builder,
        OpenLdapOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(overlay);

        var resource = builder.Resource;
        if (resource.Overlays is null)
        {
            resource.Overlays = [];

            // Stable path under the AppHost's obj directory so the bind mount target survives rebuilds.
            var overlayDir = Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "obj", "aspire-openldap-overlays");
            Directory.CreateDirectory(overlayDir);
            var overlayPath = Path.Combine(overlayDir, $"{resource.Name}-overlays.ldif");
            resource.OverlayFilePath = overlayPath;

            // Bind-mount needs an existing file at start time; real content is written by the handler below.
            if (!File.Exists(overlayPath))
            {
                File.WriteAllText(overlayPath, string.Empty);
            }

            builder.WithBindMount(overlayPath, OpenLdapResource.GeneratedOverlayContainerPath, isReadOnly: true);

            builder.OnBeforeResourceStarted((res, _, ct) =>
            {
                if (res.Overlays is not { Count: > 0 } overlays || res.OverlayFilePath is null)
                {
                    return Task.CompletedTask;
                }
                return File.WriteAllTextAsync(res.OverlayFilePath, GenerateOverlayLdif(overlays), ct);
            });
        }

        resource.Overlays.Add(overlay);
        return builder;
    }

    /// <summary>
    /// Grants <c>olcAccess</c> rules on the main (mdb) database so non-root principals — e.g. a
    /// dedicated service account — can read or write chosen subtrees. Each <paramref name="rules"/>
    /// entry is a full <c>olcAccess</c> rule body <em>without</em> the <c>{N}</c> ordering prefix,
    /// for example:
    /// <code>to dn.subtree="ou=entity,dc=umd,dc=edu" by dn.exact="uid=svc,..." write by * break</code>
    /// Rules are prepended (indices <c>{0}</c>, <c>{1}</c>, …) ahead of the server defaults, so end
    /// each with <c>by * break</c> to let the remaining ACLs still apply. Applied online at start.
    /// </summary>
    /// <remarks>
    /// Like overlays, access rules are part of the seed-once bootstrap (they configure the database,
    /// not the data): applying new rules to an already-seeded data volume requires resetting the volume.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithAccessControl(
        this IResourceBuilder<OpenLdapResource> builder,
        params string[] rules)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rules);

        var resource = builder.Resource;
        if (resource.AccessRules is null)
        {
            resource.AccessRules = [];

            // Stable path under the AppHost's obj directory so the bind mount target survives rebuilds.
            var accessDir = Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "obj", "aspire-openldap-access");
            Directory.CreateDirectory(accessDir);
            var accessPath = Path.Combine(accessDir, $"{resource.Name}-access.ldif");
            resource.AccessFilePath = accessPath;

            // Bind-mount needs an existing file at start time; real content is written by the handler below.
            if (!File.Exists(accessPath))
            {
                File.WriteAllText(accessPath, string.Empty);
            }

            builder.WithBindMount(accessPath, OpenLdapResource.GeneratedAccessContainerPath, isReadOnly: true);

            builder.OnBeforeResourceStarted((res, _, ct) =>
            {
                if (res.AccessRules is not { Count: > 0 } accessRules || res.AccessFilePath is null)
                {
                    return Task.CompletedTask;
                }
                return File.WriteAllTextAsync(res.AccessFilePath, GenerateAccessLdif(accessRules), ct);
            });
        }

        foreach (var rule in rules)
        {
            // Name the real parameter: CallerArgumentExpression would report "rule", which is
            // not an argument the caller can see.
            ArgumentException.ThrowIfNullOrWhiteSpace(rule, nameof(rules));
            resource.AccessRules.Add(rule.Trim());
        }
        return builder;
    }

    // A single olcAccess modify on the mdb database, prepending the declared rules ({0}, {1}, …).
    // Applied online via ldapmodify inside the container.
    internal static string GenerateAccessLdif(IReadOnlyList<string> rules)
    {
        var record = new LdifModifyRecord(
            OpenLdapResource.MdbDatabaseDn,
            new LdifModification(
                LdifModificationType.Add,
                "olcAccess",
                rules.Select((rule, i) => (LdifValue)$"{{{i}}}{rule}")));
        return LdifWriter.WriteToString([record], LdapSeedLdifGenerator.WriterOptions);
    }

    // Applied online via ldapadd inside the container.
    internal static string GenerateOverlayLdif(IReadOnlyList<OpenLdapOverlay> overlays)
    {
        var records = new List<LdifRecord>();

        // A single extra module list ({0} is the bootstrap one) carrying every overlay's modules.
        var modules = overlays
            .SelectMany(o => o.ModuleLoads)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modules.Count > 0)
        {
            records.Add(new LdifContentRecord(
                "cn=module{1},cn=config",
                new LdifAttribute("objectClass", "olcModuleList"),
                new LdifAttribute("cn", "module{1}"),
                new LdifAttribute("olcModulePath", "/usr/lib/ldap"),
                new LdifAttribute("olcModuleLoad", modules.Select(m => (LdifValue)m))));
        }

        records.AddRange(overlays.Select(o => o.ToOverlayEntry(OpenLdapResource.MdbDatabaseDn)));

        return LdifWriter.WriteToString(records, LdapSeedLdifGenerator.WriterOptions);
    }

    private static LdapSeedModel GetOrInitializeSeedModel(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;
        if (resource.SeedModel is { } existing)
        {
            return existing;
        }

        var model = new LdapSeedModel();
        resource.SeedModel = model;

        // Stable path under the AppHost's obj directory so the bind mount target survives rebuilds.
        var seedDir = Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "obj", "aspire-openldap-seed");
        Directory.CreateDirectory(seedDir);
        var seedPath = Path.Combine(seedDir, $"{resource.Name}-seed.ldif");
        resource.SeedFilePath = seedPath;

        // Bind-mount needs an existing file at start time; the real content is written
        // by the OnBeforeResourceStarted handler below.
        if (!File.Exists(seedPath))
        {
            File.WriteAllText(seedPath, string.Empty);
        }

        builder.WithBindMount(seedPath, GeneratedSeedContainerPath, isReadOnly: true);

        builder.OnBeforeResourceStarted((res, _, ct) =>
        {
            if (res.SeedModel is not { } m || m.IsEmpty || res.SeedFilePath is null)
            {
                return Task.CompletedTask;
            }
            LdapSeedValidator.Validate(res, m);
            var ldif = LdapSeedLdifGenerator.Generate(res, m);
            return File.WriteAllTextAsync(res.SeedFilePath, ldif, ct);
        });

        return model;
    }

    /// <summary>
    /// Adds a bind mount for custom LDIF files loaded during initialization.
    /// </summary>
    [Obsolete("Use WithSeedData(...) instead. This method will be removed in a future release.")]
    public static IResourceBuilder<OpenLdapResource> WithCustomLdifsBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, "/ldifs", isReadOnly);
    }

    /// <summary>
    /// Enables anonymous LDAP binding on the container.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithAnonymousBinding(
        this IResourceBuilder<OpenLdapResource> builder,
        bool allow = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment("LDAP_ALLOW_ANON_BINDING", allow ? "yes" : "no");
    }

    private const string ContainerTlsDir = "/tls";
    private const string ContainerServerCertPath = "/tls/server.crt";
    private const string ContainerServerKeyPath = "/tls/server.key";
    private const string ContainerCaCertPath = "/tls/ca.crt";

    /// <summary>
    /// Adds a phpLDAPadmin web UI container that targets this OpenLDAP resource.
    /// The admin container connects to the parent over the Aspire-managed container network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Login expects the seeded user's <c>uid</c> (e.g. <c>user01</c>), not a full DN.
    /// phpLDAPadmin v2 searches for entries matching <c>(&amp;(uid={input})(objectClass=inetOrgPerson))</c>
    /// and binds with the matched entry's DN — so OpenLDAP's <c>rootDN</c>
    /// (<c>cn=admin,...</c>) cannot log in here, since it's a config-only credential rather than
    /// a real directory entry.
    /// </para>
    /// <para>
    /// Login users are matched with <c>(&amp;(uid={input})(objectClass={loginObjectClass}))</c>, defaulting
    /// to <c>inetOrgPerson</c>. If your directory's people use a different structural/auxiliary class
    /// (e.g. <c>eduPerson</c>, <c>posixAccount</c>, or a site-specific class) and are NOT also
    /// <c>inetOrgPerson</c>, logins fail with otherwise-valid credentials — set
    /// <paramref name="loginObjectClass"/> to a class those entries actually have. To change the
    /// login attribute itself (e.g. <c>uid</c> → full <c>dn</c>), set <c>LDAP_LOGIN_ATTR</c> via the
    /// <paramref name="configureContainer"/> callback.
    /// </para>
    /// </remarks>
    /// <param name="builder">The parent OpenLDAP builder.</param>
    /// <param name="configureContainer">Optional callback to further configure the admin container.</param>
    /// <param name="containerName">Override the admin resource name. Defaults to <c>{parent}-admin</c>.</param>
    /// <param name="loginObjectClass">
    /// Object class used to find login users (<c>LDAP_LOGIN_OBJECTCLASS</c>). Defaults to
    /// <c>inetOrgPerson</c>.
    /// </param>
    /// <returns>The parent OpenLDAP builder (admin runs alongside as a sibling resource).</returns>
    public static IResourceBuilder<OpenLdapResource> WithPhpLdapAdmin(
        this IResourceBuilder<OpenLdapResource> builder,
        Action<IResourceBuilder<PhpLdapAdminResource>>? configureContainer = null,
        string? containerName = null,
        string? loginObjectClass = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var parent = builder.Resource;
        var adminName = containerName ?? $"{parent.Name}-admin";
        var adminResource = new PhpLdapAdminResource(adminName, parent);

        var admin = builder.ApplicationBuilder
            .AddResource(adminResource)
            .WithImage(PhpLdapAdminResource.DefaultImageName, PhpLdapAdminResource.DefaultImageTag)
            .WithHttpEndpoint(targetPort: PhpLdapAdminResource.ContainerHttpPort, name: PhpLdapAdminResource.HttpEndpointName)
            .WithEnvironment("LDAP_LOGIN_OBJECTCLASS", loginObjectClass ?? "inetOrgPerson")
            // All parent-derived settings resolve when the admin container starts, so fluent
            // calls chained on the parent AFTER WithPhpLdapAdmin (WithBaseDn, WithAdminUsername,
            // WithTls().WithRequiredTls()) still take effect here.
            .WithEnvironment(context =>
            {
                // Inside the container network the admin connects to the parent by resource name.
                // If TLS is required we point at the LDAPS target port; otherwise plain LDAP.
                context.EnvironmentVariables["LDAP_HOST"] = parent.Name;
                context.EnvironmentVariables["LDAP_PORT"] = (parent.TlsRequired
                    ? OpenLdapResource.DefaultLdapsTargetPort
                    : OpenLdapResource.DefaultLdapTargetPort).ToString(CultureInfo.InvariantCulture);
                context.EnvironmentVariables["LDAP_BASE_DN"] = parent.BaseDn;
                context.EnvironmentVariables["LDAP_USERNAME"] = parent.AdminBindDn;
                context.EnvironmentVariables["LDAP_PASSWORD"] = parent.AdminPasswordParameter;

                if (parent.TlsRequired)
                {
                    // Use the image's preconfigured 'ldaps' connection (use_ssl=true). Self-signed
                    // CA isn't trusted inside the admin container so disable libldap's cert
                    // verification for local dev.
                    context.EnvironmentVariables["LDAP_CONNECTION"] = "ldaps";
                    context.EnvironmentVariables["LDAP_SSL"] = "true";
                    context.EnvironmentVariables["LDAPTLS_REQCERT"] = "never";
                }
            })
            // The login page only renders 200 when the LDAP bind succeeds (it does a root-DSE
            // query during page construction), so this also doubles as an end-to-end connectivity
            // probe between the admin container and the LDAP server.
            .WithHttpHealthCheck(path: "/", statusCode: 200, endpointName: PhpLdapAdminResource.HttpEndpointName)
            .WaitFor(builder);

        configureContainer?.Invoke(admin);
        return builder;
    }

    /// <summary>
    /// Enables TLS using an auto-generated self-signed CA and server certificate.
    /// Certificates are cached under <c>{AppHostDir}/obj/aspire-openldap-certs/{name}/</c> and
    /// regenerated only when missing or near expiry.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithTls(
        this IResourceBuilder<OpenLdapResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;
        var appHostDir = builder.ApplicationBuilder.AppHostDirectory;
        var generated = OpenLdapCertificateGenerator.EnsureCertificates(appHostDir, resource.Name);

        return ApplyTls(builder, generated.Directory, generated.CaCertPath);
    }

    /// <summary>
    /// Enables TLS using caller-provided PEM files. Each file is bind-mounted read-only at its
    /// fixed container path (<c>/tls/server.crt</c>, <c>/tls/server.key</c>, <c>/tls/ca.crt</c>),
    /// so the host files can live anywhere and use any names.
    /// </summary>
    /// <remarks>
    /// The AppHost health check requires the server certificate to both chain to
    /// <paramref name="caCertFile"/> and name the host it dials (usually <c>localhost</c>).
    /// If your certificate doesn't include a <c>localhost</c>/loopback SAN, either reissue it
    /// with one, or pass <paramref name="disableHealthCheckHostnameValidation"/> —
    /// a local-development-only relaxation that is unavailable on Linux, where libldap
    /// performs hostname validation natively with no hostname-only opt-out.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithTls(
        this IResourceBuilder<OpenLdapResource> builder,
        string serverCertFile,
        string serverKeyFile,
        string caCertFile,
        bool disableHealthCheckHostnameValidation = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (disableHealthCheckHostnameValidation && OperatingSystem.IsLinux())
        {
            throw new DistributedApplicationException(
                "disableHealthCheckHostnameValidation is not supported on Linux: libldap validates " +
                "the server hostname natively during the TLS handshake and offers no hostname-only " +
                "opt-out. Reissue the server certificate with a localhost/loopback SAN instead.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCertFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKeyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(caCertFile);

        builder.Resource.TlsHostnameValidationDisabled = disableHealthCheckHostnameValidation;

        var certPath = RequireTlsFile(serverCertFile, "server certificate");
        var keyPath = RequireTlsFile(serverKeyFile, "server private key");
        var caPath = RequireTlsFile(caCertFile, "CA certificate");

        builder
            .WithBindMount(certPath, ContainerServerCertPath, isReadOnly: true)
            .WithBindMount(keyPath, ContainerServerKeyPath, isReadOnly: true)
            .WithBindMount(caPath, ContainerCaCertPath, isReadOnly: true);

        return ApplyTlsEnvironment(builder, caPath);
    }

    private static string RequireTlsFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new DistributedApplicationException(
                $"TLS {description} file not found: {fullPath}");
        }
        return fullPath;
    }

    /// <summary>
    /// Requires TLS for all LDAP connections. Switches the connection string scheme to <c>ldaps://</c>.
    /// Must be chained after <c>WithTls(...)</c>.
    /// </summary>
    /// <remarks>
    /// On macOS the server-side <c>LDAP_REQUIRE_TLS=yes</c> enforcement is skipped so that the
    /// AppHost can health-check the resource over plain LDAP. .NET on macOS loads Apple's
    /// <c>LDAP.framework</c> (SecureTransport), which rejects every OpenSSL-style TLS option
    /// (<c>LDAP_OPT_SERVER_CERTIFICATE</c>, <c>LDAP_OPT_X_TLS_CACERTDIR</c>,
    /// <c>LDAPTLS_REQCERT</c>), so a self-signed CA cannot be trusted from managed code without
    /// admin/GUI Keychain interaction. The connection string still advertises <c>ldaps://</c>
    /// and the LDAPS port is still exposed; only the server-side requirement is relaxed.
    /// On Linux the health check (and the client integration) trust the CA natively via
    /// <c>TrustedCertificatesDirectory</c> + <c>StartNewTlsSessionContext()</c>, so no
    /// carve-out is needed there.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithRequiredTls(
        this IResourceBuilder<OpenLdapResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Resource.TlsEnabled)
        {
            throw new DistributedApplicationException(
                "WithRequiredTls() must be called after WithTls(...).");
        }

        builder.Resource.TlsRequired = true;
        if (OperatingSystem.IsMacOS())
        {
            return builder;
        }
        return builder.WithEnvironment("LDAP_REQUIRE_TLS", "yes");
    }

    private static IResourceBuilder<OpenLdapResource> ApplyTls(
        IResourceBuilder<OpenLdapResource> builder,
        string hostCertDir,
        string caCertHostPath)
    {
        builder.WithBindMount(hostCertDir, ContainerTlsDir, isReadOnly: true);
        return ApplyTlsEnvironment(builder, caCertHostPath);
    }

    private static IResourceBuilder<OpenLdapResource> ApplyTlsEnvironment(
        IResourceBuilder<OpenLdapResource> builder,
        string caCertHostPath)
    {
        builder.Resource.TlsEnabled = true;
        builder.Resource.CaCertHostPath = caCertHostPath;

        return builder
            .WithEnvironment("LDAP_ENABLE_TLS", "yes")
            .WithEnvironment("LDAP_TLS_CERT_FILE", ContainerServerCertPath)
            .WithEnvironment("LDAP_TLS_KEY_FILE", ContainerServerKeyPath)
            .WithEnvironment("LDAP_TLS_CA_FILE", ContainerCaCertPath);
    }
}
