using System.IO.Pipes;
using System.Text;
using CookieBridge.Core;

namespace CookieBridge.Hook;

/// <summary>
/// Optional control channel used by the manager UI to ask a live plugin for a status snapshot or an
/// immediate sync.
///
/// Kept in its own type on purpose: a trimmed single-file host may not carry System.IO.Pipes.dll at
/// all (the Steam build of VRCVideoCacher does not), and touching pipe types from the runtime's main
/// path would fail type loading there. Everything here is therefore called through a guarded factory.
/// </summary>
internal static class HookControlPipe
{
    public static bool TryStart(Func<string?, HookPipeReply> handler, out string? error)
    {
        error = null;
        try
        {
            var thread = new Thread(() => Loop(handler)) { IsBackground = true, Name = "CookieBridge.Hook.Pipe" };
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }

    private static void Loop(Func<string?, HookPipeReply> handler)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(BridgePaths.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true);

                var line = reader.ReadLine();
                writer.WriteLine(BridgeJson.Serialize(handler(line), compact: true));
                writer.Flush();
            }
            catch (Exception ex)
            {
                BridgeLog.Debug("pipe", $"Control pipe cycle ended: {ex.GetType().Name}");
                Thread.Sleep(500);
            }
        }
    }
}
