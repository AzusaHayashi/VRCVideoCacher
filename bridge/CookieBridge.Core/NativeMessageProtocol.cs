using System.Buffers.Binary;
using System.Text;

namespace CookieBridge.Core;

/// <summary>Chrome/Firefox native-messaging framing: 4-byte little-endian length + UTF-8 JSON.</summary>
public static class NativeMessageProtocol
{
    public const int MaxMessageBytes = 1024 * 1024; // browser hard limit

    public static string? ReadMessage(Stream input)
    {
        var lengthBuffer = new byte[4];
        if (!ReadExact(input, lengthBuffer, 4)) return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
        if (length == 0 || length > MaxMessageBytes) return null;

        var payload = new byte[length];
        if (!ReadExact(input, payload, (int)length)) return null;

        return Encoding.UTF8.GetString(payload);
    }

    public static void WriteMessage(Stream output, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes) throw new InvalidOperationException("message too large");

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        output.Write(header, 0, 4);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }
}

public sealed class CookieUpdateRequest
{
    public string? Type { get; set; }
    public string? Browser { get; set; }
    public string? ExtensionVersion { get; set; }
    public string? Cookies { get; set; }
    public DateTimeOffset? CapturedUtc { get; set; }
}

public sealed class NativeMessageResponse
{
    public bool Ok { get; set; }
    public bool VaultUpdated { get; set; }
    public int CookieCount { get; set; }
    public bool TargetWritten { get; set; }
    /// <summary>"vault", "vault+cookie-file", "none".</summary>
    public string Applied { get; set; } = "none";
    public string? Message { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? VaultUpdatedUtc { get; set; }
    public bool VrcvcRunning { get; set; }
    public bool HookPresent { get; set; }
}

/// <summary>
/// The whole brain of the native-messaging host, kept library-side so tests can drive it without
/// spawning a browser. Never returns cookie content in a response.
/// </summary>
public static class NativeMessageHandler
{
    public const string TypeCookieUpdate = "cookie.update";
    public const string TypeStatus = "status";
    public const string TypePing = "ping";

    public static NativeMessageResponse HandleJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("empty-message");

        CookieUpdateRequest? request;
        try
        {
            request = BridgeJson.Deserialize<CookieUpdateRequest>(json);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("host", $"Malformed native message: {ex.GetType().Name}");
            return Fail("malformed-json");
        }

        if (request == null) return Fail("malformed-json");

        return request.Type switch
        {
            TypePing => new NativeMessageResponse { Ok = true, Message = "pong" },
            TypeStatus => BuildStatus(),
            TypeCookieUpdate or null => HandleCookieUpdate(request),
            _ => Fail($"unknown-type:{request.Type}"),
        };
    }

    public static NativeMessageResponse HandleCookieUpdate(CookieUpdateRequest request)
    {
        var raw = request.Cookies ?? string.Empty;

        if (!NetscapeCookieFile.IsUsable(NetscapeCookieFile.Canonicalize(raw)))
        {
            BridgeLog.Warn("host", "Rejected cookie update: jar is not a usable YouTube login jar.");
            MarkExtensionFault("cookie-jar-not-usable");
            return Fail("invalid-cookie-format");
        }

        var metadata = new VaultMetadata
        {
            SourceBrowser = request.Browser,
            ExtensionVersion = request.ExtensionVersion,
            SourceHost = "youtube.com",
        };

        var save = CookieVault.Save(raw, metadata);
        if (!save.Success)
        {
            MarkExtensionFault(save.Error ?? "vault-save-failed");
            return Fail(save.Error ?? "vault-save-failed");
        }

        var running = VrcvcProcessState.IsRunning();
        var hookPresent = HookStatus.Read() is { IsFresh: true };

        var response = new NativeMessageResponse
        {
            Ok = true,
            VaultUpdated = true,
            CookieCount = save.Summary.CookieCount,
            VaultUpdatedUtc = save.Document?.UpdatedUtc,
            VrcvcRunning = running,
            HookPresent = hookPresent,
            Applied = "vault",
        };

        // In-process plugin (when loaded) picks the vault up through its FileSystemWatcher and writes the
        // jar under VRCVC's own YtdlCookieJar gate — the safest possible moment. Without the hook there is
        // no in-process gate, so we apply it ourselves; a stale cookie is worse than a lock race.
        if (hookPresent)
        {
            TouchPendingMarker();
            response.Message = "vault updated; hook will apply";
            BridgeLog.Info("host", "Vault updated; in-process hook will mirror it to the cookie file.");
            return response;
        }

        var outcome = CookieTargetWriter.Write(save.CookiesText!, out var detail,
            alreadyLocked: false, lockTimeout: TimeSpan.FromSeconds(3),
            backupPath: CookieTargetWriter.DefaultBackupPath,
            waitForYtdlp: running ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);

        response.TargetWritten = outcome == TargetWriteOutcome.Written;
        response.Applied = response.TargetWritten ? "vault+cookie-file" : "vault";
        response.Message = response.TargetWritten ? "vault+cookie file updated" : $"cookie file: {detail}";

        if (running)
        {
            // Not an error: the Steam build is a single-file publish that cannot load the in-process
            // plugin, so this is the normal contract-mode path. The write above already waited for any
            // running yt-dlp to finish, which is what the in-process gate would otherwise provide.
            BridgeLog.Info("host",
                "VRCVideoCacher is running; jar written in contract mode (yt-dlp was idle).");
        }

        return response;
    }

    public static NativeMessageResponse BuildStatus()
    {
        var vault = CookieVault.Load();
        var target = CookieTargetWriter.ReadState();
        var hook = HookStatus.Read();

        return new NativeMessageResponse
        {
            Ok = true,
            VaultUpdated = vault.Success,
            CookieCount = vault.Summary.CookieCount,
            VaultUpdatedUtc = vault.UpdatedUtc,
            TargetWritten = target.Valid,
            Applied = vault.Success ? "vault" : "none",
            VrcvcRunning = VrcvcProcessState.IsRunning(),
            HookPresent = hook is { IsFresh: true },
            Message = $"vault={vault.Success}, target={target.Valid}, hook={hook is { IsFresh: true }}",
        };
    }

    private static NativeMessageResponse Fail(string error) => new() { Ok = false, Error = error };

    private static void TouchPendingMarker()
    {
        try
        {
            Directory.CreateDirectory(BridgePaths.StateDir);
            File.WriteAllText(BridgePaths.PendingSyncMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // marker is advisory
        }
    }

    private static void MarkExtensionFault(string error)
    {
        try
        {
            Directory.CreateDirectory(BridgePaths.StateDir);
            File.WriteAllText(Path.Combine(BridgePaths.StateDir, "last-error.txt"),
                $"{DateTimeOffset.UtcNow:O}\t{BridgeLog.Format("ERROR", "host", error)}");
        }
        catch
        {
            // advisory only
        }
    }
}
