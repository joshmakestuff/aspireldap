using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using Aspire.Hosting.OpenLdap;

namespace Aspire.Hosting;

// Fake-data seeding: generate realistic directory entries with LdifDotNet.Generator and load
// them through the record-seed pipeline (see OpenLdapSeedPipeline). (Class-level XML docs live
// on the main partial.)
public static partial class OpenLdapResourceBuilderExtensions
{
    /// <summary>
    /// Seeds <paramref name="count"/> generated <c>inetOrgPerson</c> entries under
    /// <c>ou={ou},{BaseDn}</c>. The OU is auto-declared — do not also call
    /// <see cref="WithOrganizationalUnit"/> for it afterwards (that would declare it twice
    /// and fail validation at start).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generation is deferred to resource start, so a later <c>WithBaseDn</c> call in the chain
    /// is honored. The records land in the same generated LDIF as <see cref="WithSeedRecords"/>
    /// data (<c>/ldifs/01-aspire-seed-records.ldif</c>), after any explicitly provided records.
    /// </para>
    /// <para>
    /// Determinism is per call: the same <paramref name="seed"/>, <paramref name="count"/>,
    /// <paramref name="ou"/>, and <c>LdifDotNet.Generator</c> package version always produce the
    /// same entries. A null <paramref name="seed"/> generates fresh random data on each AppHost
    /// run (stable across in-run container restarts). Two calls with the same seed into the same
    /// OU generate identical DNs and fail the seed load — vary the seed or the OU.
    /// </para>
    /// <para>
    /// Generated people carry no <c>userPassword</c>: they are searchable data, not bindable
    /// accounts. Use <see cref="WithUser"/> for accounts tests bind as.
    /// </para>
    /// </remarks>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="count">Number of people to generate. Must be at least 1.</param>
    /// <param name="ou">Organizational unit the entries live under. Must match <c>[A-Za-z0-9._-]+</c>.</param>
    /// <param name="seed">Generator seed. Null picks a random seed per AppHost run.</param>
    public static IResourceBuilder<OpenLdapResource> WithFakePeople(
        this IResourceBuilder<OpenLdapResource> builder,
        int count,
        string ou = "people",
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(ou);
        LdapSeedValidator.RequireSafeName(ou, "organizational unit");

        OpenLdapSeedPipeline.AddFakeDataSpec(builder, new FakeDataSpec(FakeDataKind.People, count, ou, seed));
        return builder;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> generated <c>groupOfNames</c> entries under
    /// <c>ou={ou},{BaseDn}</c>. Members are drawn from the people declared with
    /// <see cref="WithFakePeople"/> earlier in the chain. The OU is auto-declared — do not also
    /// call <see cref="WithOrganizationalUnit"/> for it afterwards.
    /// </summary>
    /// <remarks>
    /// Same determinism, timing, and file-placement contract as <see cref="WithFakePeople"/>:
    /// per-call seed, deferred generation, records in <c>/ldifs/01-aspire-seed-records.ldif</c>.
    /// </remarks>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="count">Number of groups to generate. Must be at least 1.</param>
    /// <param name="ou">Organizational unit the entries live under. Must match <c>[A-Za-z0-9._-]+</c>.</param>
    /// <param name="seed">Generator seed. Null picks a random seed per AppHost run.</param>
    /// <exception cref="DistributedApplicationException">
    /// No <see cref="WithFakePeople"/> call precedes this one, so there is no member pool.
    /// </exception>
    public static IResourceBuilder<OpenLdapResource> WithFakeGroups(
        this IResourceBuilder<OpenLdapResource> builder,
        int count,
        string ou = "groups",
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(ou);
        LdapSeedValidator.RequireSafeName(ou, "organizational unit");

        // Fake people can only come from WithFakePeople, so chain order is fully known here.
        // Failing at the fluent call points the stack trace at the user's line instead of a
        // start-time failure.
        if (builder.Resource.FakeDataSpecs?.Any(s => s.Kind == FakeDataKind.People) != true)
        {
            throw new DistributedApplicationException(
                $"WithFakeGroups on resource '{builder.Resource.Name}' requires fake people to draw " +
                "members from. Call WithFakePeople(...) earlier in the chain, or use WithFakeDirectory(...).");
        }

        OpenLdapSeedPipeline.AddFakeDataSpec(builder, new FakeDataSpec(FakeDataKind.Groups, count, ou, seed));
        return builder;
    }

    /// <summary>
    /// One-liner fake directory: <paramref name="people"/> generated people in
    /// <c>ou=people</c> plus <paramref name="groups"/> generated groups in <c>ou=groups</c>.
    /// Equivalent to <c>WithFakePeople(people, seed: seed).WithFakeGroups(groups, seed: seed)</c>.
    /// </summary>
    /// <remarks>
    /// See <see cref="WithFakePeople"/> for the determinism, timing, and no-<c>userPassword</c>
    /// contract.
    /// </remarks>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="people">Number of people to generate. Must be at least 1.</param>
    /// <param name="groups">Number of groups to generate. Must be at least 1.</param>
    /// <param name="seed">Generator seed, forwarded to both calls. Null picks random seeds per AppHost run.</param>
    public static IResourceBuilder<OpenLdapResource> WithFakeDirectory(
        this IResourceBuilder<OpenLdapResource> builder,
        int people = 25,
        int groups = 4,
        int? seed = null)
        => builder.WithFakePeople(people, seed: seed).WithFakeGroups(groups, seed: seed);
}
