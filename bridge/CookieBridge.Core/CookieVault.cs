using System.Security.Cryptography;
using System.Text;

namespace CookieBridge.Core;

/// <summary>Metadata stored in clear next to the encrypted payload. Contains no cookie names or values.</summary>
public sealed class VaultMetadata
{
    public string? SourceBrowser { get; set; }
    public string? ExtensionVersion { get; set; }
    public string? SourceHost { get; set; } = "youtube.com";
    public int CookieCount { get; set; }
    public bool LoginCookiePresent { get; set; }
    public DateTimeOffset? EarliestExpiryUtc { get; set; }
    public DateTimeOffset? LatestExpiryUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>On-disk vault document (docs/CONTRACT.md §3). Version independent of VRCVideoCacher.</summary>
public sealed class VaultDocument
{
    public int FormatVersion { get; set; } = CookieVault.CurrentFormatVersion;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Protection { get; set; } = "dpapi-current-user";
    public string PayloadEncoding { get; set; } = "base64";
    public string PayloadSha256 { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public VaultMetadata Metadata { get; set; } = new();
}

public sealed class VaultLoadResult
{
    public bool Success { get; init; }
    public string? CookiesText { get; init; }
    public VaultDocument? Document { get; init; }
    public CookieSummary Summary { get; init; } = CookieSummary.Empty;
    public string? Error { get; init; }
    public DateTimeOffset? UpdatedUtc => Document?.UpdatedUtc;
}

/// <summary>
/// The encrypted cookie vault: the single source of truth handed from the browser side to the
/// VRCVideoCacher side. Only DPAPI (current user) can open the payload, so copying the file to
/// another account or machine yields nothing.
/// </summary>
public static class CookieVault
{
    public const int CurrentFormatVersion = 1;

    public static string VaultPath => BridgePaths.VaultFile;

    public static VaultLoadResult Save(string netscapeText, VaultMetadata metadata)
    {
        try
        {
            BridgePaths.EnsureDirectories();

            var canonical = NetscapeCookieFile.Canonicalize(netscapeText);
            if (!NetscapeCookieFile.IsUsable(canonical))
                return new VaultLoadResult { Success = false, Error = "invalid-cookie-format" };

            var summary = NetscapeCookieFile.Summarize(canonical);
            metadata.CookieCount = summary.CookieCount;
            metadata.LoginCookiePresent = summary.LoginCookiePresent;
            metadata.EarliestExpiryUtc = summary.EarliestExpiryUtc;
            metadata.LatestExpiryUtc = summary.LatestExpiryUtc;

            var existing = ReadDocument();
            var payload = SecretProtector.Protect(Encoding.UTF8.GetBytes(canonical), BridgePaths.VaultEntropy);

            var document = new VaultDocument
            {
                FormatVersion = CurrentFormatVersion,
                CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                PayloadSha256 = summary.Sha256,
                Payload = Convert.ToBase64String(payload),
                Metadata = metadata,
            };

            AtomicFile.WriteAllText(VaultPath, BridgeJson.Serialize(document), restrictAcl: true);

            BridgeLog.Info("vault", $"Saved {summary.CookieCount} cookies (login={summary.LoginCookiePresent}, " +
                                    $"sha={summary.Sha256[..12]}) from {metadata.SourceBrowser ?? "unknown"}.");

            return new VaultLoadResult
            {
                Success = true,
                CookiesText = canonical,
                Document = document,
                Summary = summary,
            };
        }
        catch (Exception ex)
        {
            BridgeLog.Error("vault", "Failed to save vault", ex);
            return new VaultLoadResult { Success = false, Error = ex.GetType().Name };
        }
    }

    public static VaultDocument? ReadDocument()
    {
        try
        {
            var json = AtomicFile.TryReadAllText(VaultPath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return BridgeJson.Deserialize<VaultDocument>(json);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("vault", $"Vault header unreadable: {ex.GetType().Name}");
            return null;
        }
    }

    public static VaultLoadResult Load()
    {
        try
        {
            var json = AtomicFile.TryReadAllText(VaultPath);
            if (string.IsNullOrWhiteSpace(json))
                return new VaultLoadResult { Success = false, Error = "vault-missing" };

            var document = BridgeJson.Deserialize<VaultDocument>(json);
            if (document == null)
                return new VaultLoadResult { Success = false, Error = "vault-unreadable" };

            if (document.FormatVersion != CurrentFormatVersion)
                return new VaultLoadResult
                {
                    Success = false,
                    Document = document,
                    Error = $"unsupported-format-{document.FormatVersion}",
                };

            if (string.IsNullOrEmpty(document.Payload))
                return new VaultLoadResult { Success = false, Document = document, Error = "vault-empty-payload" };

            byte[] plain;
            try
            {
                plain = SecretProtector.Unprotect(Convert.FromBase64String(document.Payload), BridgePaths.VaultEntropy);
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("vault", $"DPAPI unprotect failed: {ex.GetType().Name}");
                return new VaultLoadResult { Success = false, Document = document, Error = "dpapi-failed" };
            }

            var text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);

            var summary = NetscapeCookieFile.Summarize(text);
            if (!string.IsNullOrEmpty(document.PayloadSha256) &&
                !string.Equals(document.PayloadSha256, summary.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                BridgeLog.Warn("vault", "Payload hash mismatch — vault was modified or corrupted.");
                return new VaultLoadResult
                {
                    Success = false, Document = document, Error = "hash-mismatch", Summary = summary,
                };
            }

            if (!NetscapeCookieFile.IsUsable(text))
                return new VaultLoadResult
                {
                    Success = false, Document = document, Error = "cookies-unusable", Summary = summary,
                };

            return new VaultLoadResult
            {
                Success = true,
                CookiesText = text,
                Document = document,
                Summary = summary,
            };
        }
        catch (Exception ex)
        {
            BridgeLog.Error("vault", "Failed to load vault", ex);
            return new VaultLoadResult { Success = false, Error = ex.GetType().Name };
        }
    }

    /// <summary>Vault file timestamp, or null when absent. Used by both UIs.</summary>
    public static DateTimeOffset? LastUpdatedUtc()
    {
        try
        {
            var info = new FileInfo(VaultPath);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(VaultPath)) File.Delete(VaultPath);
            BridgeLog.Info("vault", "Vault deleted.");
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("vault", $"Delete failed: {ex.GetType().Name}");
        }
    }
}
