using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// The <c>cn=config</c> side of the resource model: opt-in overlays and the mdb database's
/// <c>olcAccess</c> rules. Both follow the same shape — accumulate declarations on the resource,
/// bind-mount one generated LDIF, and write it from a start-time hook so the whole fluent chain
/// is visible before generation.
/// </summary>
internal static class OpenLdapOverlayConfiguration
{
    private const string GeneratedOverlayDirectoryName = "aspire-openldap-overlays";
    private const string GeneratedAccessDirectoryName = "aspire-openldap-access";

    /// <summary>
    /// Records a validated overlay declaration, initializing the generated-overlay pipeline on
    /// first use. See <see cref="OpenLdapResourceBuilderExtensions.WithOverlay"/>.
    /// </summary>
    internal static void AddOverlay(IResourceBuilder<OpenLdapResource> builder, OpenLdapOverlay overlay)
    {
        var resource = builder.Resource;
        if (resource.Overlays?.Any(o => string.Equals(o.Name, overlay.Name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new DistributedApplicationException(
                $"Overlay '{overlay.Name}' is already declared on resource '{resource.Name}'. " +
                "Each overlay can be added once; combine its settings into a single declaration.");
        }
        if (resource.Overlays is null)
        {
            resource.Overlays = [];

            var overlayPath = OpenLdapMounts.PrepareGeneratedFile(
                builder, GeneratedOverlayDirectoryName, $"{resource.Name}-overlays.ldif");
            resource.OverlayFilePath = overlayPath;

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
    }

    /// <summary>
    /// Records the declared <c>olcAccess</c> rules, initializing the generated-access pipeline on
    /// first use. See <see cref="OpenLdapResourceBuilderExtensions.WithAccessControl"/>.
    /// </summary>
    internal static void AddAccessRules(
        IResourceBuilder<OpenLdapResource> builder, string[] rules, string parameterName)
    {
        var resource = builder.Resource;
        if (resource.AccessRules is null)
        {
            resource.AccessRules = [];

            var accessPath = OpenLdapMounts.PrepareGeneratedFile(
                builder, GeneratedAccessDirectoryName, $"{resource.Name}-access.ldif");
            resource.AccessFilePath = accessPath;

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
            ArgumentException.ThrowIfNullOrWhiteSpace(rule, parameterName);
            resource.AccessRules.Add(rule.Trim());
        }
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
}
