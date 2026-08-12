using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Host-path resolution and the bind mounts / volumes the fluent API attaches to the container:
/// the data directory, custom schema LDIFs, and user-supplied seed data. Also owns the shared
/// "generated LDIF under the AppHost's obj directory" placement used by the seed, overlay, and
/// access-rule pipelines.
/// </summary>
internal static class OpenLdapMounts
{
    /// <summary>
    /// Resolves a user-supplied path the way Aspire's own <c>WithBindMount</c> does: relative
    /// paths are based at the AppHost project directory, not the process working directory, so
    /// an AppHost finds the same files whether launched from an IDE, the project directory, or
    /// the repository root. Rooted paths are only normalized.
    /// </summary>
    internal static string ResolveAppHostRelativePath(
        IResourceBuilder<OpenLdapResource> builder, string path) =>
        Path.GetFullPath(path, builder.ApplicationBuilder.AppHostDirectory);

    /// <summary>
    /// Returns the host path for a generated LDIF file, creating the directory and an empty
    /// placeholder file. The path is stable under the AppHost's obj directory so the bind mount
    /// target survives rebuilds, and the placeholder exists because a bind mount needs a file at
    /// start time — the real content is written by the caller's <c>OnBeforeResourceStarted</c> hook.
    /// The deterministic path means parallel builders can prepare the same file (test threads,
    /// concurrent test processes), so creation must be atomic and must never truncate (#128).
    /// </summary>
    internal static string PrepareGeneratedFile(
        IResourceBuilder<OpenLdapResource> builder, string directoryName, string fileName)
    {
        var directory = Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "obj", directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        // Atomic create-if-missing: OpenOrCreate never truncates existing content, and mutual
        // Write sharing lets every concurrent open succeed instead of throwing a Windows
        // sharing violation the way a check-then-WriteAllText pair could.
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
        }

        return path;
    }

    /// <summary>
    /// Mounts a named data volume at the OpenLDAP data path and registers the reset command
    /// that empties it. See <see cref="OpenLdapResourceBuilderExtensions.WithDataVolume"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> AddDataVolume(
        IResourceBuilder<OpenLdapResource> builder, string? name, bool isReadOnly)
    {
        var volumeName = name ?? VolumeNameGenerator.Generate(builder, "data");
        builder.WithVolume(volumeName, OpenLdapResource.DataPath, isReadOnly);
        OpenLdapDashboardCommands.RegisterResetDataVolume(builder, volumeName);
        return builder;
    }

    /// <summary>
    /// Mounts a single custom schema LDIF at the container's fixed schema path.
    /// See <see cref="OpenLdapResourceBuilderExtensions.WithSchema"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> AddSchemaFile(
        IResourceBuilder<OpenLdapResource> builder, string ldifFile)
    {
        var fullPath = ResolveAppHostRelativePath(builder, ldifFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Schema LDIF file not found: {fullPath}", fullPath);
        }

        return builder.WithBindMount(fullPath, "/schema/custom.ldif", isReadOnly: true);
    }

    /// <summary>
    /// Mounts a directory of custom schema LDIFs. See <see cref="OpenLdapResourceBuilderExtensions.WithSchemas"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> AddSchemaDirectory(
        IResourceBuilder<OpenLdapResource> builder, string directory)
    {
        var fullPath = ResolveAppHostRelativePath(builder, directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Schema directory not found: {fullPath}");
        }

        return builder.WithBindMount(fullPath, "/schemas", isReadOnly: true);
    }

    /// <summary>
    /// Mounts user-supplied seed data — either a directory of LDIFs or a single file — under the
    /// container's <c>/ldifs</c> path. See <see cref="OpenLdapResourceBuilderExtensions.WithSeedData"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> AddSeedData(
        IResourceBuilder<OpenLdapResource> builder, string ldifFileOrDirectory, bool continueOnError)
    {
        var fullPath = ResolveAppHostRelativePath(builder, ldifFileOrDirectory);

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
}
