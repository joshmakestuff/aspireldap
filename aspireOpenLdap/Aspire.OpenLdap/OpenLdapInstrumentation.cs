using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.DirectoryServices.Protocols;

namespace Aspire.OpenLdap;

/// <summary>
/// Process-lifetime OpenTelemetry instrumentation for the OpenLDAP client: a single
/// <see cref="ActivitySource"/> and <see cref="Meter"/> (both named <see cref="Name"/>),
/// plus the telemetry-mapping helpers used by <see cref="OpenLdapClient"/>.
/// </summary>
/// <remarks>
/// The mapping helpers are deliberately PII-safe: they record operation type, server
/// address/port, search scope, control OIDs (never values), result codes, exception
/// <em>types</em>, and entry <em>counts</em> — but never search filters, DNs, entry
/// attributes, control values, paging-cookie bytes, or exception messages (server-supplied
/// diagnostics can embed directory data).
/// </remarks>
internal static class OpenLdapInstrumentation
{
    /// <summary>Name shared by the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    internal const string Name = "Aspire.OpenLdap";

    private static readonly string Version =
        typeof(OpenLdapInstrumentation).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal static readonly ActivitySource ActivitySource = new(Name, Version);

    internal static readonly Meter Meter = new(Name, Version);

    /// <summary>Duration of LDAP client operations, in seconds.</summary>
    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>(
            "db.client.operation.duration",
            unit: "s",
            description: "Duration of LDAP client operations.");

    /// <summary>Maps a request to a low-cardinality operation name (e.g. "search", "add").</summary>
    internal static string OperationName(DirectoryRequest request) => request switch
    {
        SearchRequest => "search",
        AddRequest => "add",
        ModifyRequest => "modify",
        DeleteRequest => "delete",
        ModifyDNRequest => "modifydn",
        CompareRequest => "compare",
        ExtendedRequest => "extended",
        _ => request.GetType().Name
                    .Replace("Request", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant(),
    };

    internal static string? ScopeName(SearchScope scope) => scope switch
    {
        SearchScope.Base => "base",
        SearchScope.OneLevel => "onelevel",
        SearchScope.Subtree => "subtree",
        _ => null,
    };

    /// <summary>Joined control OIDs (types only, never values). Null when there are none.</summary>
    internal static string? ControlOids(DirectoryControlCollection? controls)
    {
        if (controls is null || controls.Count == 0)
        {
            return null;
        }

        var oids = new List<string>(controls.Count);
        foreach (DirectoryControl control in controls)
        {
            if (!string.IsNullOrEmpty(control.Type))
            {
                oids.Add(control.Type);
            }
        }

        return oids.Count == 0 ? null : string.Join(",", oids);
    }

    /// <summary>
    /// True for result codes that are NOT failures: success, compare true/false (the two
    /// successful outcomes of a compare), and referrals (a redirect, not an error).
    /// </summary>
    internal static bool IsErrorCode(ResultCode? code) => code switch
    {
        null => false,
        ResultCode.Success => false,
        ResultCode.CompareTrue => false,
        ResultCode.CompareFalse => false,
        ResultCode.Referral => false,
        ResultCode.ReferralV2 => false,
        _ => true,
    };

    /// <summary>Sets request-time span tags. PII (filters, DNs, attribute values) is never recorded.</summary>
    internal static void SetRequestTags(
        Activity activity, DirectoryRequest request, string operation, string serverAddress, int serverPort)
    {
        activity.SetTag("db.system.name", "openldap");
        activity.SetTag("db.operation.name", operation);
        activity.SetTag("server.address", serverAddress);
        activity.SetTag("server.port", serverPort);

        if (request is SearchRequest search && ScopeName(search.Scope) is { } scope)
        {
            activity.SetTag("db.ldap.scope", scope);
        }

        if (ControlOids(request.Controls) is { } oids)
        {
            activity.SetTag("db.ldap.controls", oids);
        }
    }

    /// <summary>Sets response-time span tags and the span status.</summary>
    internal static void SetResponseTags(
        Activity activity, DirectoryResponse? response, ResultCode? code, Exception? failure)
    {
        if (response is SearchResponse search)
        {
            activity.SetTag("db.ldap.entries_returned", search.Entries.Count);
        }

        if (code is { } resultCode)
        {
            activity.SetTag("db.response.status_code", (int)resultCode);
            activity.SetTag("db.ldap.result_code", resultCode.ToString());
        }

        if (response is not null && HasPagingCookie(response.Controls))
        {
            activity.SetTag("db.ldap.paged", true);
        }

        if (failure is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, code?.ToString() ?? failure.GetType().Name);
            // Only the exception TYPE is recorded (mirroring the metric tags). Exception
            // messages can carry server-supplied diagnostics — DNs, matched values, referral
            // URLs — which the no-PII contract above promises never to export.
            activity.SetTag("error.type", failure.GetType().FullName);
        }
        else if (IsErrorCode(code))
        {
            activity.SetStatus(ActivityStatusCode.Error, code?.ToString());
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    /// <summary>Builds the low-cardinality tag set for the duration metric.</summary>
    internal static TagList BuildMetricTags(
        string operation, string serverAddress, int serverPort, ResultCode? code, Exception? failure)
    {
        var tags = new TagList
        {
            { "db.system.name", "openldap" },
            { "db.operation.name", operation },
            { "server.address", serverAddress },
            { "server.port", serverPort },
        };

        if (code is { } resultCode)
        {
            tags.Add("db.response.status_code", (int)resultCode);
        }

        if (failure is not null)
        {
            tags.Add("error.type", failure.GetType().FullName);
        }

        return tags;
    }

    private static bool HasPagingCookie(DirectoryControl[]? controls)
    {
        if (controls is null)
        {
            return false;
        }

        foreach (var control in controls)
        {
            if (control is PageResultResponseControl { Cookie.Length: > 0 })
            {
                return true;
            }
        }

        return false;
    }
}
