using System.Diagnostics;
using System.Text.Json.Serialization;

namespace CookieBridge.Core;

/// <summary>
/// Heartbeat published by the in-process plugin. The manager UI and the native host both read it to
/// answer "is the plugin actually inside VRCVideoCacher right now?".
/// </summary>
public sealed class HookStatus
{
    public int Pid { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public string? VrcvcVersion { get; set; }
    public string? HookVersion { get; set; }
    public string? VrcvcProcessPath { get; set; }
    public string? LastSyncOutcome { get; set; }
    public int SyncCount { get; set; }
    public string? LastError { get; set; }
    public bool VaultWatching { get; set; }

    /// <summary>Capability probe result: which reflection layers resolved (L1..L6).</summary>
    public Dictionary<string, string> Layers { get; set; } = new();

    [JsonIgnore]
    public bool IsFresh
    {
        get
        {
            if (LastHeartbeatUtc == default) return false;
            if (DateTimeOffset.UtcNow - LastHeartbeatUtc > TimeSpan.FromSeconds(90)) return false;
            return IsProcessAlive(Pid);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static HookStatus? Read()
    {
        try
        {
            var json = AtomicFile.TryReadAllText(BridgePaths.HookStatusFile);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return BridgeJson.Deserialize<HookStatus>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Write()
    {
        try
        {
            Directory.CreateDirectory(BridgePaths.StateDir);
            AtomicFile.WriteAllText(BridgePaths.HookStatusFile, BridgeJson.Serialize(this));
        }
        catch
        {
            // status is advisory
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(BridgePaths.HookStatusFile)) File.Delete(BridgePaths.HookStatusFile);
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>Named-pipe request/response used by the manager to poke a live hook.</summary>
public sealed class HookPipeCommand
{
    public string? Command { get; set; }
}

public sealed class HookPipeReply
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public HookStatus? Status { get; set; }
}

public static class HookPipeCommands
{
    public const string Status = "status";
    public const string SyncNow = "sync-now";
    public const string Reload = "reload";
}
