using Aspire.OpenLdap;

namespace Aspire.LdapAdmin.Web;

/// <summary>
/// The topbar's connection identity (design handoff § App shell): the server chip and the
/// <c>bind:</c> chip. Parsed once at startup from the same connection string the client
/// uses, with the library's own parser (never hand-parsed — AGENTS.md). Carries no
/// password, so it is safe to render and to register as a singleton.
/// </summary>
public sealed record ConsoleConnectionInfo(string ServerLabel, string BindDn)
{
    public static ConsoleConnectionInfo From(string connectionString)
    {
        var parsed = OpenLdapConnectionStringBuilder.Parse(connectionString);
        var port = parsed.Endpoint.Port >= 0 ? parsed.Endpoint.Port : (parsed.UsesLdaps ? 636 : 389);
        return new($"{parsed.Endpoint.Scheme}://{parsed.Endpoint.Host}:{port}", parsed.BindDn);
    }
}
