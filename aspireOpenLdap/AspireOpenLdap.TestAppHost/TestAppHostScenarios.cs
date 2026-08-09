namespace AspireOpenLdap.TestAppHost;

/// <summary>
/// The scenarios this test AppHost can run, selected by exactly one
/// <c>--OpenLdap:Scenario=&lt;name&gt;</c> switch. Modelling them as one selector rather than a
/// set of independent boolean flags makes the exclusivity structural: combinations that are
/// concretely broken (a seed directory plus the config-witness seed both mounting root-bearing
/// LDIFs into <c>/ldifs</c>, which aborts slapd with err 68) are no longer expressible.
/// </summary>
public static class TestAppHostScenarios
{
    /// <summary>Configuration key carrying the scenario name.</summary>
    public const string ScenarioKey = "OpenLdap:Scenario";

    /// <summary>Configuration key carrying the <see cref="LargeSeed"/> scenario's seed directory.</summary>
    public const string SeedDirKey = "OpenLdap:SeedDir";

    /// <summary>Plain <c>AddOpenLdap</c>, no extras.</summary>
    public const string Default = "default";

    /// <summary>Seed data loaded from the directory named by <see cref="SeedDirKey"/>.</summary>
    public const string LargeSeed = "seed";

    /// <summary>Generated CA plus required LDAPS.</summary>
    public const string Tls = "tls";

    /// <summary>Generated CA, LDAPS served alongside plain LDAP (not required).</summary>
    public const string TlsOptional = "tls-optional";

    /// <summary>memberOf overlay, raw seed records and a complete access policy.</summary>
    public const string ConfigWitness = "config-witness";

    /// <summary>Generated fake directory plus one bindable typed user.</summary>
    public const string FakeData = "fake-data";
}
