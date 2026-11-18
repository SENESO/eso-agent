namespace EsoAgent.Core;

/// <summary>
/// Env-injection audit discipline (v1.1): exec/shell/job payloads may carry an
/// environment map, and those values can be secrets (tokens, passwords). The
/// rule is absolute — env VALUES never appear in the audit log, in a persisted
/// job record, or in any summary string. Only key NAMES are ever written.
/// Every audit/record summary that mentions env goes through here.
/// </summary>
public static class EnvAudit
{
    /// <summary>Sorted key names of an env map (empty list for null/empty).</summary>
    public static List<string> KeyNames(IDictionary<string, string>? env)
        => env == null || env.Count == 0
            ? new List<string>()
            : env.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Audit-safe fragment: "" when no env, otherwise " env=[KEY1,KEY2]"
    /// — names only, by construction (values are never read here).</summary>
    public static string Describe(IDictionary<string, string>? env)
    {
        var keys = KeyNames(env);
        return keys.Count == 0 ? "" : $" env=[{string.Join(",", keys)}]";
    }
}
