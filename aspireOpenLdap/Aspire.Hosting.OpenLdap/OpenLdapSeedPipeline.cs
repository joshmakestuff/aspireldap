using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;
using LdifDotNet.Generator;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// The generated-seed machinery behind the typed seed helpers (<c>WithUser</c>,
/// <c>WithGroup</c>, …), the LdifDotNet record escape hatch (<c>WithSeedRecords</c>), and the
/// fake-data helpers (<c>WithFakePeople</c>, <c>WithFakeGroups</c>): the seed model, the two
/// generated LDIF files, their bind mounts, and the start-time hooks that write them.
/// </summary>
internal static class OpenLdapSeedPipeline
{
    private const string GeneratedSeedContainerPath = "/ldifs/00-aspire-seed.ldif";
    private const string GeneratedSeedRecordsContainerPath = "/ldifs/01-aspire-seed-records.ldif";
    private const string GeneratedSeedDirectoryName = "aspire-openldap-seed";

    /// <summary>Declares an organizational unit in the typed seed model.</summary>
    internal static void AddOrganizationalUnit(IResourceBuilder<OpenLdapResource> builder, string name) =>
        GetOrInitializeSeedModel(builder).OrganizationalUnits.Add(new OrganizationalUnitEntry(name));

    /// <summary>Declares a user entry in the typed seed model, defaulting cn/sn to the uid.</summary>
    internal static void AddUser(
        IResourceBuilder<OpenLdapResource> builder,
        string uid,
        string password,
        string? ou,
        string? cn,
        string? sn,
        string? mail) =>
        GetOrInitializeSeedModel(builder).Users.Add(new SeedUserEntry(
            Uid: uid,
            Password: password,
            OrganizationalUnit: string.IsNullOrWhiteSpace(ou) ? null : ou,
            Cn: string.IsNullOrWhiteSpace(cn) ? uid : cn,
            Sn: string.IsNullOrWhiteSpace(sn) ? uid : sn,
            Mail: string.IsNullOrWhiteSpace(mail) ? null : mail));

    /// <summary>Declares a group entry in the typed seed model.</summary>
    internal static void AddGroup(
        IResourceBuilder<OpenLdapResource> builder,
        string cn,
        IEnumerable<string> members,
        string? ou)
    {
        // Materialize before touching the model so a throwing sequence leaves nothing behind.
        var memberList = members.ToList();
        GetOrInitializeSeedModel(builder).Groups.Add(new SeedGroupEntry(
            Cn: cn,
            Members: memberList,
            OrganizationalUnit: string.IsNullOrWhiteSpace(ou) ? null : ou));
    }

    /// <summary>
    /// Appends caller-supplied LdifDotNet records to the record-seed file, initializing the
    /// pipeline on first use.
    /// </summary>
    internal static void AddRecords(
        IResourceBuilder<OpenLdapResource> builder, IEnumerable<LdifRecord> records, string parameterName)
    {
        EnsureSeedRecordsPipeline(builder);

        var resource = builder.Resource;
        foreach (var record in records)
        {
            if (record is null)
            {
                throw new ArgumentException("Seed records must not contain null.", parameterName);
            }
            resource.SeedRecords!.Add(record);
        }
    }

    /// <summary>
    /// Queues a fake-data spec, initializing the record-seed pipeline and auto-declaring the
    /// spec's organizational unit. Materialization is deferred to resource start (see
    /// <see cref="MaterializeFakeDataSpecs"/>) so later <c>WithBaseDn</c> calls are honored.
    /// </summary>
    internal static void AddFakeDataSpec(IResourceBuilder<OpenLdapResource> builder, FakeDataSpec spec)
    {
        EnsureSeedRecordsPipeline(builder);
        EnsureOrganizationalUnitDeclared(builder, spec.Ou);
        (builder.Resource.FakeDataSpecs ??= []).Add(spec);
    }

    /// <summary>
    /// Declares <paramref name="ou"/> in the typed seed model unless an entry with that name
    /// (ordinal-ignore-case, matching <see cref="LdapSeedValidator"/>) already exists. Routing the
    /// OU through the typed model also emits the base-DN root entry, so a fake-only chain gets a
    /// complete parent tree.
    /// </summary>
    private static void EnsureOrganizationalUnitDeclared(IResourceBuilder<OpenLdapResource> builder, string ou)
    {
        var model = GetOrInitializeSeedModel(builder);
        if (!model.OrganizationalUnits.Any(entry => string.Equals(entry.Name, ou, StringComparison.OrdinalIgnoreCase)))
        {
            model.OrganizationalUnits.Add(new OrganizationalUnitEntry(ou));
        }
    }

    /// <summary>
    /// Turns pending fake-data specs into LDIF records appended to
    /// <see cref="OpenLdapResource.SeedRecords"/>, then consumes the specs. All People specs
    /// materialize first (building the group member pool and putting people before the groups
    /// that reference them in the emitted LDIF), then all Groups specs. Runs inside the
    /// record-seed serialization hook; internal for direct fast-test invocation.
    /// </summary>
    internal static void MaterializeFakeDataSpecs(OpenLdapResource resource)
    {
        if (resource.FakeDataSpecs is not { Count: > 0 } specs)
        {
            return;
        }

        var pool = new List<LdifContentRecord>();
        foreach (var spec in specs.Where(s => s.Kind == FakeDataKind.People))
        {
            var generator = new LdifGenerator(new LdifGeneratorOptions { Seed = spec.Seed });
            var people = generator.People(spec.Count, Dn.Combine(Dn.Rdn("ou", spec.Ou), resource.BaseDn));
            pool.AddRange(people);
            resource.SeedRecords!.AddRange(people);
        }
        foreach (var spec in specs.Where(s => s.Kind == FakeDataKind.Groups))
        {
            var generator = new LdifGenerator(new LdifGeneratorOptions { Seed = spec.Seed });
            resource.SeedRecords!.AddRange(
                generator.Groups(spec.Count, Dn.Combine(Dn.Rdn("ou", spec.Ou), resource.BaseDn), pool));
        }

        // Consume: the hook re-fires on an in-run container restart and must not duplicate.
        resource.FakeDataSpecs = null;
    }

    /// <summary>
    /// Sets up the record-seed pipeline once per resource: the generated LDIF file under the
    /// AppHost's obj directory, its read-only bind mount, and the single
    /// <c>OnBeforeResourceStarted</c> hook that first materializes any pending fake-data specs
    /// and then serializes the accumulated records. Materialization and serialization share one
    /// hook on purpose — correctness must not depend on handler registration order.
    /// </summary>
    private static void EnsureSeedRecordsPipeline(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;
        if (resource.SeedRecords is not null)
        {
            return;
        }
        resource.SeedRecords = [];

        var recordsPath = OpenLdapMounts.PrepareGeneratedFile(
            builder, GeneratedSeedDirectoryName, $"{resource.Name}-seed-records.ldif");
        resource.SeedRecordsFilePath = recordsPath;

        builder.WithBindMount(recordsPath, GeneratedSeedRecordsContainerPath, isReadOnly: true);

        builder.OnBeforeResourceStarted((res, _, ct) =>
        {
            MaterializeFakeDataSpecs(res);
            if (res.SeedRecords is not { Count: > 0 } seedRecords || res.SeedRecordsFilePath is null)
            {
                return Task.CompletedTask;
            }
            var ldif = LdifWriter.WriteToString(seedRecords, LdapSeedLdifGenerator.WriterOptions);
            return File.WriteAllTextAsync(res.SeedRecordsFilePath, ldif, ct);
        });
    }

    /// <summary>
    /// Returns the typed seed model, creating it — plus its generated LDIF file, bind mount, and
    /// validate-then-write start hook — on first use.
    /// </summary>
    private static LdapSeedModel GetOrInitializeSeedModel(IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;
        if (resource.SeedModel is { } existing)
        {
            return existing;
        }

        var model = new LdapSeedModel();
        resource.SeedModel = model;

        var seedPath = OpenLdapMounts.PrepareGeneratedFile(
            builder, GeneratedSeedDirectoryName, $"{resource.Name}-seed.ldif");
        resource.SeedFilePath = seedPath;

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
}
