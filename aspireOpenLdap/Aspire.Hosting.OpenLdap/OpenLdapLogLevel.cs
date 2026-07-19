namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// slapd debug/log level bits (<c>olcLogLevel</c> / <c>-d</c>), combinable as flags.
/// The container default is <see cref="Stats"/>. Set via
/// <see cref="OpenLdapResourceBuilderExtensions.WithLogLevel"/>.
/// </summary>
[Flags]
public enum OpenLdapLogLevel
{
    /// <summary>No debug logging.</summary>
    None = 0,

    /// <summary>Trace function calls.</summary>
    Trace = 1,

    /// <summary>Packet handling debugging.</summary>
    Packets = 2,

    /// <summary>Heavy trace debugging (function arguments).</summary>
    Args = 4,

    /// <summary>Connection management.</summary>
    Connections = 8,

    /// <summary>Packets sent and received (BER dumps).</summary>
    Ber = 16,

    /// <summary>Search filter processing.</summary>
    Filter = 32,

    /// <summary>Configuration processing.</summary>
    Config = 64,

    /// <summary>Access control list processing.</summary>
    Acl = 128,

    /// <summary>
    /// Connections, LDAP operations, and results — slapd's recommended level and the
    /// container default.
    /// </summary>
    Stats = 256,

    /// <summary>Stats log entries sent (slapd's <c>stats2</c>).</summary>
    StatsExtra = 512,

    /// <summary>Communication with shell backends.</summary>
    Shell = 1024,

    /// <summary>Entry parsing.</summary>
    Parse = 2048,

    /// <summary>LDAPSync replication.</summary>
    Sync = 16384,

    /// <summary>
    /// Only messages slapd logs regardless of the configured level (slapd's confusingly
    /// named <c>none</c> keyword — high-priority messages, not silence; for silence use
    /// <see cref="None"/>).
    /// </summary>
    Urgent = 32768,
}
