using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Timers;
using CookieBridge.Core;
using Timer = System.Timers.Timer;

namespace CookieBridge.Hook;

/// <summary>
/// Everything the plugin does after the CLR has started it inside VRCVideoCacher.
///
/// Lifecycle: wait for VRCVideoCacher's assembly → probe the reflection bridge → initial sync →
/// watch the vault (live reload, no restart) → heartbeat + control pipe.
/// </summary>
internal sealed class HookRuntime
{
    public static HookRuntime Instance { get; } = new();

    private const string HostAssemblyName = "VRCVideoCacher";

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _hostLoaded = new(false);

    private VrcvcBridge? _bridge;
    private HookStatus _status = new();
    private Timer? _debounce;
    private Timer? _heartbeat;
    private FileSystemWatcher? _vaultWatcher;
    private FileSystemWatcher? _targetWatcher;
    private bool _suppressUntilVaultChanges;
    private DateTimeOffset _lastOwnWriteUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuppressedWarningUtc = DateTimeOffset.MinValue;
    private int _syncCount;

    private HookRuntime() { }

    public void Run()
    {
        try
        {
            var hookVersion = typeof(HookRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            _status = new HookStatus
            {
                Pid = Environment.ProcessId,
                StartedUtc = DateTimeOffset.UtcNow,
                LastHeartbeatUtc = DateTimeOffset.UtcNow,
                HookVersion = hookVersion,
                VrcvcProcessPath = Environment.ProcessPath,
            };
            _status.Write();

            BridgeLog.Info("hook", $"Attached to host PID {_status.Pid} ({Path.GetFileName(Environment.ProcessPath)}).");

            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            TryAttachToHost();

            if (!_hostLoaded.Wait(TimeSpan.FromMinutes(5)))
            {
                BridgeLog.Warn("hook", "VRCVideoCacher assembly never appeared; plugin stays passive.");
                _status.LastError = "host-assembly-not-found";
                _status.Write();
                return;
            }

            Attach();

            SyncOnce("startup");
            StartVaultWatcher();
            StartTargetWatcher();
            StartHeartbeat();
            StartPipeServer();

            _status.VaultWatching = _vaultWatcher != null;
            _status.Write();

            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("hook", "Hook runtime failed", ex);
            try
            {
                _status.LastError = $"runtime:{ex.GetType().Name}";
                _status.Write();
            }
            catch
            {
                // nothing left to do
            }
        }
    }

    // ---- host discovery --------------------------------------------------

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            if (IsHostAssembly(args.LoadedAssembly)) _hostLoaded.Set();
        }
        catch
        {
            // ignore
        }
    }

    private void TryAttachToHost()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (IsHostAssembly(assembly))
                {
                    _hostLoaded.Set();
                    return;
                }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsHostAssembly(Assembly assembly)
    {
        try
        {
            return string.Equals(assembly.GetName().Name, HostAssemblyName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Attach()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(IsHostAssembly);

        if (assembly == null)
        {
            BridgeLog.Warn("hook", "Host assembly resolved but not enumerable — using contract-only mode.");
            _status.Layers["mode"] = "contract-only";
            return;
        }

        _bridge = VrcvcBridge.TryCreate(assembly);
        if (_bridge == null)
        {
            BridgeLog.Warn("hook", "VRCVideoCacher.Program not found — using contract-only mode.");
            _status.Layers["mode"] = "contract-only";
            _status.VrcvcVersion = VrcvcLocator.ReadVersionFromLogs();
            return;
        }

        _status.VrcvcVersion = _bridge.Version;
        foreach (var (key, value) in _bridge.Layers) _status.Layers[key] = value;

        var compat = CompatibilityMatrix.Evaluate(_bridge.Version);
        BridgeLog.Info("hook", $"Host version {_bridge.Version ?? "unknown"} → {compat.Level} ({compat.Headline}).");
        BridgeLog.Info("hook", $"Capabilities: {_bridge.DescribeLayers()}");

        _bridge.SubscribeCookieUpdates(OnHostCookieEvent);
    }

    private void OnHostCookieEvent()
    {
        // VRCVideoCacher received cookies through its own HTTP API (official extension). Nothing to
        // overwrite: the vault stays authoritative, we only record that the host is alive and healthy.
        BridgeLog.Debug("hook", "Host reported a cookie update from its own API.");
    }

    // ---- synchronisation -------------------------------------------------

    private void SyncOnce(string reason, bool force = false)
    {
        if (!_syncGate.Wait(TimeSpan.FromSeconds(20)))
        {
            BridgeLog.Warn("sync", "Another sync is still running; skipping this one.");
            return;
        }

        try
        {
            if (File.Exists(BridgePaths.DisabledFlag))
            {
                BridgeLog.Info("sync", "Plugin disabled; nothing to do.");
                return;
            }

            if (_suppressUntilVaultChanges && !force && reason != "vault-changed")
            {
                WarnSuppressed(reason);
                return;
            }

            var vault = CookieVault.Load();
            if (!vault.Success)
            {
                _status.LastError = vault.Error;
                _status.LastSyncOutcome = $"vault:{vault.Error}";
                _status.Write();
                WarnRateLimited($"Vault not usable ({vault.Error}); leaving VRCVideoCacher untouched.");
                return;
            }

            var targetPath = ResolveTargetPath();
            var current = AtomicFile.TryReadAllText(targetPath);
            var merged = CookieMerger.Merge(vault.CookiesText!, current);

            if (!(_bridge?.IsCookiesValid(merged) ?? NetscapeCookieFile.IsValidForVrcvc(merged)))
            {
                _status.LastSyncOutcome = "rejected-invalid";
                _status.Write();
                WarnRateLimited("Merged jar failed VRCVideoCacher's validity check; not writing.");
                return;
            }

            if (current != null &&
                string.Equals(NetscapeCookieFile.Canonicalize(current), merged, StringComparison.Ordinal))
            {
                _status.LastSyncOutcome = current == null ? "written" : "unchanged";
                _status.LastSyncUtc = DateTimeOffset.UtcNow;
                _status.LastError = null;
                _status.Write();
                return;
            }

            var outcome = ApplyToHost(targetPath, merged);
            _syncCount++;
            _status.SyncCount = _syncCount;
            _status.LastSyncUtc = DateTimeOffset.UtcNow;
            _status.LastSyncOutcome = $"{reason}:{outcome}";
            if (outcome.StartsWith("written", StringComparison.Ordinal)) _status.LastError = null;
            _status.Write();

            if (outcome.StartsWith("written", StringComparison.Ordinal))
            {
                _suppressUntilVaultChanges = false;
                ClearPendingMarker();
                _bridge?.NotifyCookiesUpdated();
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("sync", "Sync failed", ex);
            _status.LastError = $"sync:{ex.GetType().Name}";
            _status.Write();
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private string ApplyToHost(string targetPath, string merged)
    {
        IDisposable? jarGate = null;
        try
        {
            if (_bridge is { HasJarGate: true })
            {
                jarGate = _bridge.AcquireJarAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (jarGate != null) BridgeLog.Debug("sync", "Acquired VRCVideoCacher's YtdlCookieJar gate.");
            }

            var writerLock = jarGate == null ? CookieWriteLock.TryAcquire(TimeSpan.FromSeconds(5)) : null;
            if (jarGate == null && writerLock == null) return "locked";

            _lastOwnWriteUtc = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            AtomicFile.WriteAllText(targetPath, merged, restrictAcl: false, backupPath: CookieTargetWriter.DefaultBackupPath);
            return "written";
        }
        catch (Exception ex)
        {
            BridgeLog.Error("sync", "Writing the host cookie file failed", ex);
            return $"failed:{ex.GetType().Name}";
        }
        finally
        {
            jarGate?.Dispose();
        }
    }

    // ---- watchers --------------------------------------------------------

    private void StartVaultWatcher()
    {
        try
        {
            BridgePaths.EnsureDirectories();
            _debounce = new Timer(1000) { AutoReset = false };
            _debounce.Elapsed += (_, _) =>
            {
                try { SyncOnce("vault-changed"); } catch { /* never throw on a timer thread */ }
            };

            _vaultWatcher = new FileSystemWatcher(BridgePaths.Root)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*",
                EnableRaisingEvents = true,
            };
            _vaultWatcher.Changed += OnVaultChanged;
            _vaultWatcher.Created += OnVaultChanged;
            _vaultWatcher.Renamed += OnVaultChanged;
            BridgeLog.Info("hook", "Watching the vault for live updates (no restart needed).");
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("hook", $"Vault watcher unavailable: {ex.GetType().Name}");
        }
    }

    private void OnVaultChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var name = Path.GetFileName(e.FullPath);
            if (!string.Equals(name, "vault.json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "pending.sync", StringComparison.OrdinalIgnoreCase))
                return;

            _debounce?.Stop();
            _debounce?.Start();
        }
        catch
        {
            // ignore
        }
    }

    private void StartTargetWatcher()
    {
        try
        {
            var path = ResolveTargetPath();
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            _targetWatcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = Path.GetFileName(path),
                EnableRaisingEvents = true,
            };
            _targetWatcher.Deleted += OnTargetChanged;
            _targetWatcher.Changed += OnTargetChanged;
        }
        catch (Exception ex)
        {
            BridgeLog.Debug("hook", $"Target watcher unavailable: {ex.GetType().Name}");
        }
    }

    private void OnTargetChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Ignore our own writes and the backup churn that follows them.
            if (DateTimeOffset.UtcNow - _lastOwnWriteUtc < TimeSpan.FromSeconds(2)) return;

            Thread.Sleep(200);
            var text = AtomicFile.TryReadAllText(ResolveTargetPath());
            if (NetscapeCookieFile.IsUsable(text)) return;

            // The user cleared cookies inside VRCVideoCacher. Respect that: stay quiet until the browser
            // produces a new jar.
            _suppressUntilVaultChanges = true;
            BridgeLog.Info("hook",
                "VRCVideoCacher's cookie file was cleared externally; pausing re-sync until a new vault update.");
        }
        catch
        {
            // ignore
        }
    }

    // ---- heartbeat and control pipe --------------------------------------

    private void StartHeartbeat()
    {
        _heartbeat = new Timer(30_000) { AutoReset = true };
        _heartbeat.Elapsed += (_, _) =>
        {
            try
            {
                _status.LastHeartbeatUtc = DateTimeOffset.UtcNow;
                _status.VaultWatching = _vaultWatcher != null;
                if (_bridge != null)
                    foreach (var (key, value) in _bridge.Layers)
                        _status.Layers[key] = value;
                _status.Write();
            }
            catch
            {
                // ignore
            }
        };
        _heartbeat.Start();
    }

    private void StartPipeServer()
    {
        // Guarded: a trimmed single-file host may not carry System.IO.Pipes.dll at all.
        if (HookControlPipe.TryStart(HandlePipeCommand, out var error))
            BridgeLog.Debug("pipe", "Control pipe listening.");
        else
            BridgeLog.Debug("pipe", $"Control pipe unavailable in this host ({error}).");
    }

    private HookPipeReply HandlePipeCommand(string? line)
    {
        try
        {
            var command = BridgeJson.Deserialize<HookPipeCommand>(line ?? string.Empty)?.Command
                          ?? HookPipeCommands.Status;

            switch (command)
            {
                case HookPipeCommands.SyncNow:
                    SyncOnce("manual", force: true);
                    return new HookPipeReply { Ok = true, Message = _status.LastSyncOutcome, Status = _status };

                case HookPipeCommands.Reload:
                    _bridge = null;
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(IsHostAssembly);
                    if (assembly != null) _bridge = VrcvcBridge.TryCreate(assembly);
                    SyncOnce("manual-reload", force: true);
                    return new HookPipeReply { Ok = true, Message = "reloaded", Status = _status };

                default:
                    return new HookPipeReply { Ok = true, Message = "status", Status = _status };
            }
        }
        catch (Exception ex)
        {
            return new HookPipeReply { Ok = false, Message = ex.GetType().Name, Status = _status };
        }
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Cookie file the plugin feeds. Normally VRCVideoCacher's own path (read reflectively so a future
    /// rename still works); <c>VRCCB_TARGET_FILE</c> exists purely so integration tests can prove the
    /// whole chain without touching a real installation.
    /// </summary>
    private string ResolveTargetPath()
    {
        var custom = Environment.GetEnvironmentVariable(CookieTargetWriter.TargetOverrideVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { return Path.GetFullPath(custom); } catch { /* fall through */ }
        }
        return _bridge?.CookiePath ?? CookieTargetWriter.TargetPath;
    }

    private void ClearPendingMarker()
    {
        try
        {
            if (File.Exists(BridgePaths.PendingSyncMarker)) File.Delete(BridgePaths.PendingSyncMarker);
        }
        catch
        {
            // ignore
        }
    }

    private void WarnRateLimited(string message)
    {
        if (DateTimeOffset.UtcNow - _lastSuppressedWarningUtc < TimeSpan.FromMinutes(10)) return;
        _lastSuppressedWarningUtc = DateTimeOffset.UtcNow;
        BridgeLog.Warn("sync", message);
    }

    private void WarnSuppressed(string reason)
    {
        if (DateTimeOffset.UtcNow - _lastSuppressedWarningUtc < TimeSpan.FromMinutes(30)) return;
        _lastSuppressedWarningUtc = DateTimeOffset.UtcNow;
        BridgeLog.Debug("sync", $"Skipping sync ({reason}); waiting for a new vault update.");
    }
}
