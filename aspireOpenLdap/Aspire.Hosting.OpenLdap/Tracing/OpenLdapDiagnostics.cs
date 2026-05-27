using System.Diagnostics;
using System.Reflection;

namespace Aspire.Hosting.ApplicationModel.Tracing;

/// <summary>
/// ActivitySource and well-known tag/source names used by the AppHost-side
/// slapd log parser. Consumers wire this source up via OTel by calling
/// <c>tracerProviderBuilder.AddSource(OpenLdapDiagnostics.SourceName)</c>;
/// <see cref="OpenLdapResourceBuilderExtensions.WithOpenTelemetry"/> does this automatically.
/// </summary>
internal static class OpenLdapDiagnostics
{
    public const string SourceName = "Aspire.OpenLdap.Server";

    public static readonly ActivitySource Source = new(
        SourceName,
        typeof(OpenLdapDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0");
}
