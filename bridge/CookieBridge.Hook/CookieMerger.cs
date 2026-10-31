using CookieBridge.Core;

namespace CookieBridge.Hook;

/// <summary>
/// Combines "what the browser has" with "what yt-dlp has rotated" into one jar.
///
/// yt-dlp rewrites the cookie file on exit because YouTube rotates session tokens
/// (<c>__Secure-1PSIDTS</c>, <c>SIDCC</c>, ...). Blindly overwriting with the browser's copy throws
/// those rotations away; blindly keeping the file ignores a re-login. The rule used here:
///
/// * identical <c>LOGIN_INFO</c> → same session, so keep yt-dlp's rotated values for cookies the
///   browser did not refresh and overlay everything the browser did send;
/// * different <c>LOGIN_INFO</c> → the account changed (or logged out), the browser wins outright.
/// </summary>
public static class CookieMerger
{
    public static string Merge(string vaultText, string? targetText)
    {
        var vaultCanonical = NetscapeCookieFile.Canonicalize(vaultText);
        if (string.IsNullOrWhiteSpace(targetText)) return vaultCanonical;

        var targetCanonical = NetscapeCookieFile.Canonicalize(targetText);
        if (string.IsNullOrWhiteSpace(targetCanonical)) return vaultCanonical;

        var vault = NetscapeCookieFile.Parse(vaultCanonical);
        var target = NetscapeCookieFile.Parse(targetCanonical);

        var vaultLogin = LoginValue(vault);
        var targetLogin = LoginValue(target);

        if (vaultLogin == null) return targetCanonical;              // nothing to say, keep rotation
        if (targetLogin == null) return vaultCanonical;             // target has no session, browser wins
        if (!string.Equals(vaultLogin, targetLogin, StringComparison.Ordinal))
        {
            BridgeLog.Info("merge", "LOGIN_INFO differs between vault and cookie file — browser copy wins.");
            return vaultCanonical;
        }

        // Same session: start from the rotated file, then overlay every cookie the browser just sent.
        var merged = new Dictionary<string, CookieRecord>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var record in target)
        {
            var key = Key(record);
            if (!merged.ContainsKey(key)) order.Add(key);
            merged[key] = record;
        }

        var overlaid = 0;
        foreach (var record in vault)
        {
            var key = Key(record);
            if (!merged.ContainsKey(key)) order.Add(key);
            else if (!string.Equals(merged[key].Value, record.Value, StringComparison.Ordinal)) overlaid++;
            merged[key] = record;
        }

        BridgeLog.Debug("merge",
            $"Merged jar: target={target.Count}, vault={vault.Count}, overlaid={overlaid}, total={merged.Count}.");

        return NetscapeCookieFile.Canonicalize(
            string.Concat(order.Select(key => merged[key].ToLine() + "\n")));
    }

    private static string? LoginValue(IReadOnlyList<CookieRecord> records) =>
        records.FirstOrDefault(r =>
            r.Name.Equals("LOGIN_INFO", StringComparison.Ordinal) &&
            r.Domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string Key(CookieRecord record) => string.Join('\u0001', record.Domain, record.Path, record.Name);
}
