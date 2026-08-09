using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.OpenLdap;
using LdifDotNet;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring an OpenLDAP container resource in an Aspire AppHost.
/// </summary>
/// <remarks>
/// The methods here are the public fluent surface; each validates its arguments and then hands
/// off to an internal collaborator in <c>Aspire.Hosting.OpenLdap</c> — resource construction to
/// <c>OpenLdapResourceFactory</c>, mounts to <c>OpenLdapMounts</c>, seeding to
/// <c>OpenLdapSeedPipeline</c>, <c>cn=config</c> declarations to <c>OpenLdapOverlayConfiguration</c>,
/// TLS to <c>OpenLdapTlsConfiguration</c>, dashboard commands to <c>OpenLdapDashboardCommands</c>,
/// and the admin sidecar to <c>PhpLdapAdminBuilder</c>.
/// </remarks>
public static partial class OpenLdapResourceBuilderExtensions
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

        return OpenLdapResourceFactory.Create(builder, name, adminPassword);
    }

    /// <summary>
    /// Overrides the directory's base DN (a.k.a. suffix / root). Default <c>dc=example,dc=org</c>.
    /// </summary>
    /// <remarks>
    /// The DN is validated here, before the container starts: it must be a well-formed RFC 4514
    /// DN with no control characters, and its leading RDN must be <c>dc=</c>, <c>o=</c>, or
    /// <c>c=</c> — the root-entry forms the container bootstrap and the seed generator support.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithBaseDn(
        this IResourceBuilder<OpenLdapResource> builder,
        string baseDn)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDn);
        OpenLdapDnValidation.ValidateBaseDn(baseDn, nameof(baseDn));
        builder.Resource.BaseDn = baseDn;
        return builder;
    }

    /// <summary>
    /// Overrides the admin username. Bind DN becomes <c>cn={username},{baseDn}</c>. Default <c>admin</c>.
    /// </summary>
    /// <remarks>
    /// The username is one CN value, but the container init composes the bind DN from it
    /// verbatim — so values containing DN special characters (<c>, + " \ &lt; &gt; ;</c>, a
    /// leading <c>#</c> or space, a trailing space) or control characters are rejected here,
    /// before the container starts, rather than producing a DN the host and container disagree on.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithAdminUsername(
        this IResourceBuilder<OpenLdapResource> builder,
        string username)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        OpenLdapDnValidation.ValidateAdminUsername(username, nameof(username));
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
        OpenLdapResourceFactory.SetEndpointPort(builder, OpenLdapResource.LdapEndpointName, port);
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
        OpenLdapResourceFactory.SetEndpointPort(builder, OpenLdapResource.LdapsEndpointName, port);
        return builder;
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

        return OpenLdapMounts.AddDataVolume(builder, name, isReadOnly);
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
    /// A relative <paramref name="ldifFile"/> resolves against the AppHost project directory
    /// (like Aspire's own bind mounts), not the process working directory.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSchema(
        this IResourceBuilder<OpenLdapResource> builder,
        string ldifFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldifFile);

        return OpenLdapMounts.AddSchemaFile(builder, ldifFile);
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
    /// A relative <paramref name="directory"/> resolves against the AppHost project directory
    /// (like Aspire's own bind mounts), not the process working directory.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSchemas(
        this IResourceBuilder<OpenLdapResource> builder,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return OpenLdapMounts.AddSchemaDirectory(builder, directory);
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
    /// A relative <paramref name="ldifFileOrDirectory"/> resolves against the AppHost project
    /// directory (like Aspire's own bind mounts), not the process working directory.
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

        return OpenLdapMounts.AddSeedData(builder, ldifFileOrDirectory, continueOnError);
    }

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

        OpenLdapSeedPipeline.AddOrganizationalUnit(builder, name);
        return builder;
    }

    /// <summary>
    /// Declares a user entry (objectClass <c>inetOrgPerson</c>) seeded into the directory.
    /// </summary>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="uid">The user's <c>uid</c>. Becomes the RDN. Must match <c>[A-Za-z0-9._-]+</c>.</param>
    /// <param name="password">
    /// The user's password. Hashed as <c>{SSHA}</c> into the generated LDIF, so the directory
    /// stores a salted hash rather than the cleartext; binds verify against the hash natively.
    /// A value that already carries an RFC 3112 scheme prefix (e.g. <c>{SSHA}...</c>,
    /// <c>{CRYPT}...</c>) is stored verbatim, so pre-hashed values keep working.
    /// </param>
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

        OpenLdapSeedPipeline.AddUser(builder, uid, password, ou, cn, sn, mail);
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

        OpenLdapSeedPipeline.AddGroup(builder, cn, members, ou);
        return builder;
    }

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

        OpenLdapSeedPipeline.AddRecords(builder, records, nameof(records));
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
    /// The declaration is validated here — and declaring the same overlay name twice throws —
    /// so mistakes fail at model construction rather than during container bootstrap.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithOverlay(
        this IResourceBuilder<OpenLdapResource> builder,
        OpenLdapOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(overlay);
        overlay.Validate();

        OpenLdapOverlayConfiguration.AddOverlay(builder, overlay);
        return builder;
    }

    /// <summary>
    /// Declares the <c>olcAccess</c> rules for the main (mdb) database so non-root principals —
    /// e.g. a dedicated service account — can read or write chosen subtrees. Each
    /// <paramref name="rules"/> entry is a full <c>olcAccess</c> rule body <em>without</em> the
    /// <c>{N}</c> ordering prefix; slapd evaluates them in the given order and the first
    /// matching <c>to</c> clause wins. Applied online at start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The declared rules are the <b>complete</b> access policy, not an addition to defaults:
    /// the bundled image's mdb database ships with no <c>olcAccess</c> rules, and the moment any
    /// rule exists slapd's implicit final rule is <c>to * by * none</c> (verified against the
    /// bundled image — unmatched targets and rules that fall through via <c>by * break</c> are
    /// both denied, including the auth access simple binds need on <c>userPassword</c>). A lone
    /// restricting rule therefore breaks every non-admin bind and read. A complete policy looks
    /// like:
    /// <code>
    /// .WithAccessControl(
    ///     // binds keep working (put this FIRST so a later subtree rule cannot shadow it)
    ///     "to attrs=userPassword by anonymous auth by self write by * none",
    ///     // the actual grant/restriction
    ///     "to dn.subtree=\"ou=secret,dc=example,dc=org\" by dn.exact=\"uid=svc,ou=users,dc=example,dc=org\" read by * none",
    ///     // everything else stays readable to authenticated users
    ///     "to * by users read by * none")
    /// </code>
    /// <c>by * break</c> only continues into the <em>later rules in this list</em> — there are no
    /// server defaults to fall back to.
    /// </para>
    /// <para>
    /// Like overlays, access rules are part of the seed-once bootstrap (they configure the database,
    /// not the data): applying new rules to an already-seeded data volume requires resetting the volume.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithAccessControl(
        this IResourceBuilder<OpenLdapResource> builder,
        params string[] rules)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rules);

        OpenLdapOverlayConfiguration.AddAccessRules(builder, rules, nameof(rules));
        return builder;
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

    private const OpenLdapLogLevel KnownLogLevels =
        OpenLdapLogLevel.Trace | OpenLdapLogLevel.Packets | OpenLdapLogLevel.Args |
        OpenLdapLogLevel.Connections | OpenLdapLogLevel.Ber | OpenLdapLogLevel.Filter |
        OpenLdapLogLevel.Config | OpenLdapLogLevel.Acl | OpenLdapLogLevel.Stats |
        OpenLdapLogLevel.StatsExtra | OpenLdapLogLevel.Shell | OpenLdapLogLevel.Parse |
        OpenLdapLogLevel.Sync | OpenLdapLogLevel.Urgent;

    /// <summary>
    /// Sets slapd's debug log level (<c>LDAP_LOGLEVEL</c>). Defaults to
    /// <see cref="OpenLdapLogLevel.Stats"/> — connection/operation/result lines. Combine flags
    /// for more detail (e.g. <c>Stats | Config</c>), or pass <see cref="OpenLdapLogLevel.None"/>
    /// to silence slapd's debug output entirely.
    /// </summary>
    /// <remarks>
    /// Health-check probe connections are filtered out of the stats log independently of this
    /// level — see <see cref="WithHealthCheckProbeLogging"/> to bring them back.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithLogLevel(
        this IResourceBuilder<OpenLdapResource> builder,
        OpenLdapLogLevel level)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if ((level & ~KnownLogLevels) != OpenLdapLogLevel.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), level, "Value contains bits that are not defined slapd log levels.");
        }
        return builder.WithEnvironment("LDAP_LOGLEVEL", ((int)level).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Keeps the AppHost health-check probe's connection lines in the container log
    /// (<c>LDAP_LOG_HEALTH_PROBES</c>). By default the container drops the stats-log block of
    /// each wholly-successful probe (identified by the <c>aspire-healthcheck</c> sentinel
    /// attribute in its root DSE search) so continuous polling doesn't drown out real traffic;
    /// probes that fail in any way are always logged in full.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithHealthCheckProbeLogging(
        this IResourceBuilder<OpenLdapResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment("LDAP_LOG_HEALTH_PROBES", enabled ? "yes" : "no");
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

        PhpLdapAdminBuilder.Add(builder, configureContainer, containerName, loginObjectClass);
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

        return OpenLdapTlsConfiguration.EnableGeneratedTls(builder);
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
    /// Relative file paths resolve against the AppHost project directory (like Aspire's own
    /// bind mounts), not the process working directory.
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

        return OpenLdapTlsConfiguration.EnableProvidedTls(
            builder, serverCertFile, serverKeyFile, caCertFile, disableHealthCheckHostnameValidation);
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

        return OpenLdapTlsConfiguration.RequireTls(builder);
    }
}
