using System.Text.RegularExpressions;

namespace Aspire.LdapAdmin.Core;

// TODO(ldifdotnet#78): replace with LdifDotNet's RFC 4515 filter model when it exists.
// This is deliberately NOT a filter parser — it is the query builder's flat projection:
// simple conditions under one &/| join. Anything richer (nesting, !, ~=, extensible
// match) is beyond what the builder can represent and is reported as such.
/// <summary>
/// The search query builder's filter model: a flat list of simple
/// conditions joined by one <c>&amp;</c> or <c>|</c>, composable to an RFC 4515 string and
/// parseable back from one. Parsing is honest about its limits: a filter the flat shape
/// cannot fully represent comes back with <see cref="LdapFilterParse.Lossy"/> set, so the
/// caller can say so instead of silently dropping structure.
/// </summary>
public static class LdapFilterConditions
{
    /// <summary>Everything a raw filter can express is representable; the default filter.</summary>
    public const string MatchEverything = "(objectClass=*)";

    private static readonly Regex ConditionRe = new(
        @"\((?<attr>[A-Za-z][A-Za-z0-9-]*)(?<op>>=|<=|~=|:=|=)(?<value>[^()]*)\)",
        RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Composes the builder state into an RFC 4515 filter. Conditions with an empty
    /// attribute are skipped; an empty builder means match-everything; an empty value means
    /// presence (the prototype's `(attr=*)`). Values travel verbatim — `*` is the caller's
    /// wildcard, and escaping is the raw filter's own business.
    /// </summary>
    public static string Compose(IEnumerable<LdapFilterCondition> conditions, LdapFilterJoin join)
    {
        var parts = conditions
            .Where(static c => !string.IsNullOrWhiteSpace(c.Attribute))
            .Select(static c => c.Operator == LdapFilterOperator.Present || string.IsNullOrWhiteSpace(c.Value)
                ? $"({c.Attribute.Trim()}=*)"
                : $"({c.Attribute.Trim()}{Symbol(c.Operator)}{c.Value})")
            .ToList();
        return parts.Count switch
        {
            0 => MatchEverything,
            1 => parts[0],
            _ => $"({(join == LdapFilterJoin.Any ? "|" : "&")}{string.Concat(parts)})",
        };
    }

    /// <summary>
    /// Projects a raw filter onto the flat builder shape: the simple conditions it
    /// contains, the outermost join (<c>(|…</c> means any; everything else, including a
    /// single condition, means all), and whether the projection lost anything —
    /// nesting beyond one level, <c>!</c>, <c>~=</c>/<c>:=</c> matches, or text the
    /// condition pattern does not cover.
    /// </summary>
    public static LdapFilterParse Parse(string raw)
    {
        raw = raw?.Trim() ?? string.Empty;
        var join = raw.StartsWith("(|", StringComparison.Ordinal) ? LdapFilterJoin.Any : LdapFilterJoin.All;

        var conditions = new List<LdapFilterCondition>();
        var covered = 0;
        var lossy = false;
        foreach (Match match in ConditionRe.Matches(raw))
        {
            covered += match.Length;
            var op = match.Groups["op"].Value;
            var value = match.Groups["value"].Value;
            if (op is "~=" or ":=")
            {
                lossy = true; // Real operators the builder has no row for.
                continue;
            }

            conditions.Add(op is "=" && value is "*"
                ? new LdapFilterCondition(match.Groups["attr"].Value, LdapFilterOperator.Present, string.Empty)
                : new LdapFilterCondition(match.Groups["attr"].Value, op switch
                {
                    ">=" => LdapFilterOperator.GreaterOrEqual,
                    "<=" => LdapFilterOperator.LessOrEqual,
                    _ => LdapFilterOperator.Equals,
                }, value));
        }

        // Whatever the condition pattern did not consume is structure the flat shape lost —
        // beyond the one optional outer (&…)/(|…) wrapper and whitespace.
        var residue = ConditionRe.Replace(raw, string.Empty);
        if (conditions.Count > 0)
        {
            residue = Regex.Replace(residue, @"^\((&|\|)\)$", string.Empty, RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        }
        lossy = lossy || residue.Trim().Length > 0 || (covered == 0 && raw.Length > 0);

        return new LdapFilterParse(conditions, join, lossy);
    }

    private static string Symbol(LdapFilterOperator op) => op switch
    {
        LdapFilterOperator.GreaterOrEqual => ">=",
        LdapFilterOperator.LessOrEqual => "<=",
        _ => "=",
    };
}

/// <summary>The condition operators the builder offers.</summary>
public enum LdapFilterOperator
{
    /// <summary>Equality / substring match — the value may carry <c>*</c> wildcards.</summary>
    Equals,

    /// <summary>Attribute presence: <c>(attr=*)</c>.</summary>
    Present,

    /// <summary>Ordering match <c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary>Ordering match <c>&lt;=</c>.</summary>
    LessOrEqual,
}

/// <summary>How the builder joins its conditions.</summary>
public enum LdapFilterJoin
{
    /// <summary>Every condition must match: <c>&amp;</c>.</summary>
    All,

    /// <summary>Any condition may match: <c>|</c>.</summary>
    Any,
}

/// <summary>One builder condition row.</summary>
public sealed record LdapFilterCondition(string Attribute, LdapFilterOperator Operator, string Value);

/// <summary>
/// A raw filter projected onto the builder: the conditions and join it could represent,
/// and whether anything was lost doing so (<see cref="Lossy"/>).
/// </summary>
public sealed record LdapFilterParse(IReadOnlyList<LdapFilterCondition> Conditions, LdapFilterJoin Join, bool Lossy);
