using System.Text;

namespace CookieBridge.Core;

/// <summary>
/// Minimal, allocation-friendly logger shared by every component.
///
/// Hard rule (docs/CONTRACT.md §8): cookie names/values must never reach a log line. Callers only
/// ever pass counts, paths, byte sizes and error types — never the payload.
/// </summary>
public static class BridgeLog
{
    private const long MaxBytes = 1024 * 1024;
    private const int MaxFiles = 3;
    private static readonly object Gate = new();
    private static string? _overridePath;

    /// <summary>Optional secondary sink (used by the hook to also mirror into the app log viewer).</summary>
    public static event Action<string>? LineWritten;

    public static bool Enabled { get; set; } = true;

    public static string? LogPath
    {
        get => _overridePath ?? BridgePaths.LogFile;
        set => _overridePath = value;
    }

    public static void Info(string source, string message) => Write("INFO ", source, message, null);
    public static void Warn(string source, string message) => Write("WARN ", source, message, null);
    public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);
    public static void Debug(string source, string message) => Write("DEBUG", source, message, null);

    private static void Write(string level, string source, string message, Exception? ex)
    {
        if (!Enabled) return;

        var line = Format(level, source, message, ex);
        try { LineWritten?.Invoke(line); } catch { /* observer must never break logging */ }

        try
        {
            lock (Gate)
            {
                var path = LogPath!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Rotate(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // A logger that throws is worse than no logger. Swallow.
        }
    }

    public static string Format(string level, string source, string message, Exception? ex = null)
    {
        var sb = new StringBuilder(160);
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        sb.Append(" [").Append(level).Append("] ");
        sb.Append(source.PadRight(11)).Append(' ');
        sb.Append(SecretRedactor.Redact(message));
        if (ex != null)
        {
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(SecretRedactor.Redact(ex.Message));
            var inner = ex.InnerException;
            var depth = 0;
            while (inner != null && depth++ < 3)
            {
                sb.Append(" -> ").Append(inner.GetType().Name).Append(": ")
                  .Append(SecretRedactor.Redact(inner.Message));
                inner = inner.InnerException;
            }
        }
        return sb.ToString();
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            for (var i = MaxFiles - 1; i >= 1; i--)
            {
                var from = $"{path}.{i}";
                var to = $"{path}.{i + 1}";
                if (File.Exists(from))
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            if (File.Exists($"{path}.1")) File.Delete($"{path}.1");
            File.Move(path, $"{path}.1");
        }
        catch
        {
            // rotation is best effort
        }
    }
}

/// <summary>
/// Last line of defence against accidental secret leakage into logs. Netscape cookie files are
/// tab separated with 7 fields and the value in field 7; those never get logged by our code, but a
/// third party error message could embed one, so scrub the obvious shapes.
/// </summary>
public static class SecretRedactor
{
    private static readonly string[] KnownSecretNames =
    [
        "LOGIN_INFO", "SID", "HSID", "SSID", "APISID", "SAPISID", "__Secure-1PSID", "__Secure-3PSID",
        "__Secure-1PAPISID", "__Secure-3PAPISID", "__Secure-1PSIDTS", "__Secure-3PSIDTS", "SIDCC",
        "__Secure-1PSIDCC", "__Secure-3PSIDCC", "YSC", "PREF",
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // A log line carrying a tab-separated cookie row is a bug; cut it at the first tab run.
        var tab = text.IndexOf('\t');
        if (tab > 0 && text.Count(c => c == '\t') >= 3)
            text = text[..tab] + "\t<redacted-cookie-row>";

        foreach (var name in KnownSecretNames)
        {
            // The replacement text still contains "name<TAB>", so the search must resume *after* what
            // was just written — searching from 0 again re-matched the same spot and looped forever.
            // (Regression test: SecretRedactorTests.Redact_RemovesValueOfKnownCookieNames_EvenWithoutSevenFields)
            var searchFrom = 0;
            while (true)
            {
                var idx = text.IndexOf(name + "\t", searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                var end = text.IndexOf('\n', idx);
                if (end < 0) end = text.Length;

                var replacement = name + "\t<redacted>";
                text = string.Concat(text.AsSpan(0, idx), replacement, text.AsSpan(end));
                searchFrom = idx + replacement.Length;
            }
        }
        return text;
    }
}
