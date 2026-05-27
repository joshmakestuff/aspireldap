using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel.Tracing;

/// <summary>
/// Parses slapd's <c>conn=N op=M ...</c> log lines into OTel spans. One <see cref="Activity"/>
/// is emitted per LDAP operation (BIND/SRCH/ADD/MOD/DEL/CMP) once its terminating
/// <c>RESULT</c> line is seen. Operations whose attribute list contains the
/// <c>aspire-healthcheck</c> sentinel are dropped without emitting a span.
/// </summary>
internal sealed partial class OpenLdapLogParser
{
    // Format examples this parser handles (slapd prepends a timestamp and a thread id;
    // we anchor on conn=N op=M rather than the prefix since both can vary by version):
    //   6a14c94f.21a1babb 0x7d5283fff6c0 conn=1004 op=0 BIND dn="cn=admin,dc=example,dc=org" method=128
    //   6a14c94f.21a26fd3 0x7d5283fff6c0 conn=1004 op=0 BIND dn="cn=admin,dc=example,dc=org" mech=SIMPLE
    //   6a14c94f.21a30189 0x7d5283fff6c0 conn=1004 op=0 RESULT tag=97 err=0 qtime=0.000021 etime=0.000122 text=
    //   6a14c94f.21a537a7 0x7d5288ffe6c0 conn=1004 op=1 SRCH base="dc=example,dc=org" scope=2 deref=0 filter="(uid=alice)"
    //   6a14c94f.21a5c48d 0x7d5288ffe6c0 conn=1004 op=1 SRCH attr=dn aspire-healthcheck
    //   6a14c94f.21a70b8e 0x7d5288ffe6c0 conn=1004 op=1 SEARCH RESULT tag=101 err=0 nentries=1 text=

    [GeneratedRegex(@"\bconn=(?<conn>\d+)\s+op=(?<op>\d+)\s+(?<kind>BIND|SRCH|ADD|MOD|DEL|CMP)(?:\s+(?<rest>.*))?$")]
    private static partial Regex StartLineRegex();

    [GeneratedRegex(@"\bconn=(?<conn>\d+)\s+op=(?<op>\d+)\s+(?<kind>BIND|SRCH)\s+attr=(?<attrs>.+)$")]
    private static partial Regex AttrLineRegex();

    [GeneratedRegex(@"\bconn=(?<conn>\d+)\s+op=(?<op>\d+)\s+(?:SEARCH\s+)?RESULT\s+(?<rest>.*)$")]
    private static partial Regex ResultLineRegex();

    [GeneratedRegex(@"\bconn=(?<conn>\d+)\s+fd=\d+\s+closed\b")]
    private static partial Regex ClosedLineRegex();

    [GeneratedRegex(@"(\w+)=(""[^""]*""|\S+)")]
    private static partial Regex KvRegex();

    private const string HealthcheckSentinel = "aspire-healthcheck";
    private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(60);

    private readonly string _resourceName;
    private readonly ILogger _logger;
    private readonly Dictionary<(long Conn, long Op), PendingOp> _pending = new();

    // BIND completes BEFORE the SRCH that carries the healthcheck sentinel — so we can't
    // know at BIND-result time whether the conn is a probe. Buffer BIND spans per conn
    // and either flush them at the first non-healthcheck op or drop them when the conn
    // is identified as a healthcheck.
    private readonly Dictionary<long, PendingResult> _deferredBinds = new();
    private readonly HashSet<long> _healthcheckConns = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public OpenLdapLogParser(string resourceName, ILogger logger)
    {
        _resourceName = resourceName;
        _logger = logger;
    }

    public void ProcessLine(string line)
    {
        SweepIfDue();

        // AttrLine must come before StartLine — a "SRCH attr=..." continuation line
        // matches both patterns and the attr handler is more specific.
        if (AttrLineRegex().Match(line) is { Success: true } attr)
        {
            HandleAttr(attr);
            return;
        }

        if (StartLineRegex().Match(line) is { Success: true } start)
        {
            HandleStart(start);
            return;
        }

        if (ResultLineRegex().Match(line) is { Success: true } result)
        {
            HandleResult(result);
            return;
        }

        if (ClosedLineRegex().Match(line) is { Success: true } closed)
        {
            HandleClosed(closed);
        }
    }

    private void HandleStart(Match m)
    {
        var conn = long.Parse(m.Groups["conn"].Value);
        var op = long.Parse(m.Groups["op"].Value);
        var kind = m.Groups["kind"].Value;
        var rest = m.Groups["rest"].Success ? m.Groups["rest"].Value : string.Empty;

        // BIND emits two start lines (method= then mech=). Don't overwrite the first.
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(_pending, (conn, op), out var existed);
        if (existed)
        {
            // Enrich with any new key/value pairs.
            EnrichTagsFromRest(slot!, rest);
            return;
        }

        slot = new PendingOp
        {
            Operation = kind,
            Started = DateTimeOffset.UtcNow,
            Conn = conn,
            Op = op,
        };
        EnrichTagsFromRest(slot, rest);
    }

    private void HandleAttr(Match m)
    {
        var conn = long.Parse(m.Groups["conn"].Value);
        var op = long.Parse(m.Groups["op"].Value);
        var attrs = m.Groups["attrs"].Value.Trim();

        if (!_pending.TryGetValue((conn, op), out var pending))
        {
            return;
        }

        if (attrs.Contains(HealthcheckSentinel, StringComparison.Ordinal))
        {
            pending.IsHealthcheck = true;
        }
        pending.Tags["ldap.attributes"] = attrs;
    }

    private void HandleResult(Match m)
    {
        var conn = long.Parse(m.Groups["conn"].Value);
        var op = long.Parse(m.Groups["op"].Value);
        var rest = m.Groups["rest"].Value;

        if (!_pending.Remove((conn, op), out var pending))
        {
            return;
        }

        // Healthcheck SRCH triggers connection-wide drop: discard the deferred BIND
        // (if any) and remember this conn so any further ops are also dropped.
        if (pending.IsHealthcheck)
        {
            _healthcheckConns.Add(conn);
            _deferredBinds.Remove(conn);
            return;
        }

        if (_healthcheckConns.Contains(conn))
        {
            return;
        }

        int? err = null;
        int? nentries = null;
        foreach (Match kv in KvRegex().Matches(rest))
        {
            var key = kv.Groups[1].Value;
            var value = StripQuotes(kv.Groups[2].Value);
            switch (key)
            {
                case "err":
                    if (int.TryParse(value, out var e)) err = e;
                    break;
                case "nentries":
                    if (int.TryParse(value, out var n)) nentries = n;
                    break;
            }
        }

        var result = new PendingResult
        {
            Operation = pending.Operation,
            Started = pending.Started,
            Ended = DateTimeOffset.UtcNow,
            Conn = pending.Conn,
            Op = pending.Op,
            Tags = pending.Tags,
            Err = err,
            Nentries = nentries,
        };

        // BIND completes before the sentinel-bearing SRCH on the same conn arrives.
        // Buffer it until either (a) a non-healthcheck op flushes, (b) conn close, or
        // (c) TTL sweep gives up. Replacing an existing deferred BIND is intentional:
        // a conn may re-bind after the first SRCH.
        if (pending.Operation == "BIND")
        {
            _deferredBinds[conn] = result;
            return;
        }

        // Non-BIND op on a non-healthcheck conn — flush any deferred BIND, then emit.
        if (_deferredBinds.Remove(conn, out var deferred))
        {
            EmitActivity(deferred);
        }
        EmitActivity(result);
    }

    private void HandleClosed(Match m)
    {
        var conn = long.Parse(m.Groups["conn"].Value);
        // Conn ended without any non-healthcheck op flushing the BIND; flush it now
        // unless we already classified the conn as a healthcheck.
        if (_deferredBinds.Remove(conn, out var deferred) && !_healthcheckConns.Contains(conn))
        {
            EmitActivity(deferred);
        }
        _healthcheckConns.Remove(conn);
    }

    private void EmitActivity(PendingResult r)
    {
        r.Tags["ldap.operation"] = r.Operation.ToLowerInvariant();
        r.Tags["ldap.connection_id"] = r.Conn;
        r.Tags["ldap.message_id"] = r.Op;
        if (r.Err is { } e) r.Tags["ldap.result_code"] = e;
        if (r.Nentries is { } n) r.Tags["ldap.entries_returned"] = n;
        r.Tags["db.system"] = "ldap";

        using var activity = OpenLdapDiagnostics.Source.StartActivity(
            $"LDAP {r.Operation.ToLowerInvariant()}",
            ActivityKind.Server,
            default(ActivityContext),
            r.Tags!,
            links: null,
            startTime: r.Started);

        if (activity is null)
        {
            return;
        }

        if (r.Err is > 0)
        {
            activity.SetStatus(ActivityStatusCode.Error, $"LDAP result code {r.Err}");
        }
        activity.SetEndTime(r.Ended.UtcDateTime);

        _logger.LogInformation(
            "Emitted LDAP {Op} span (conn={Conn} op={OpId} duration={DurationMs}ms err={Err})",
            r.Operation, r.Conn, r.Op,
            (r.Ended - r.Started).TotalMilliseconds, r.Err);
    }

    private void EnrichTagsFromRest(PendingOp pending, string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return;
        }

        foreach (Match kv in KvRegex().Matches(rest))
        {
            var key = kv.Groups[1].Value;
            var value = StripQuotes(kv.Groups[2].Value);
            switch (key)
            {
                case "dn":
                    pending.Tags["ldap.dn"] = value;
                    break;
                case "base":
                    pending.Tags["ldap.base_dn"] = value;
                    break;
                case "scope":
                    pending.Tags["ldap.scope"] = ScopeName(value);
                    break;
                case "mech":
                    pending.Tags["ldap.bind_mech"] = value;
                    break;
            }
        }
    }

    private static string ScopeName(string raw) => raw switch
    {
        "0" => "base",
        "1" => "onelevel",
        "2" => "subtree",
        "3" => "children",
        _ => raw,
    };

    private static string StripQuotes(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private void SweepIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastSweep = now;

        var stale = _pending
            .Where(kv => now - kv.Value.Started > PendingTtl)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in stale)
        {
            _pending.Remove(key);
        }

        if (stale.Count > 0)
        {
            _logger.LogDebug(
                "Dropped {Count} stale openldap operations awaiting RESULT (resource={Resource})",
                stale.Count, _resourceName);
        }
    }

    private sealed class PendingOp
    {
        public required string Operation { get; init; }
        public required DateTimeOffset Started { get; init; }
        public required long Conn { get; init; }
        public required long Op { get; init; }
        public bool IsHealthcheck { get; set; }
        public Dictionary<string, object?> Tags { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingResult
    {
        public required string Operation { get; init; }
        public required DateTimeOffset Started { get; init; }
        public required DateTimeOffset Ended { get; init; }
        public required long Conn { get; init; }
        public required long Op { get; init; }
        public required Dictionary<string, object?> Tags { get; init; }
        public int? Err { get; init; }
        public int? Nentries { get; init; }
    }
}
