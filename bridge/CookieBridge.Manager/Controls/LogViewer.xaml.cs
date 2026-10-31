using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CookieBridge.Core;

namespace CookieBridge.Manager.Controls;

/// <summary>
/// 插件日志查看器。
///
/// 做法：以 <see cref="FileShare.ReadWrite"/> + <see cref="FileShare.Delete"/> 打开 bridge.log，
/// 记住读位置，每 1 秒只追加新增字节（等价于 tail -f）。文件被轮转（长度变小）时自动从头重建。
/// 落盘日志由 Core 的 <see cref="BridgeLog"/> 写，格式：
/// <c>2026-11-01T00:30:00.123Z [INFO ] hook        message</c>
/// </summary>
public partial class LogViewer : UserControl
{
    private const int MaxBlocks = 2500;
    private const int MaxChunkBytes = 256 * 1024;

    private static readonly Brush ErrorBrush = Freeze(0xC6, 0x2D, 0x28);
    private static readonly Brush WarnBrush = Freeze(0x8A, 0x5C, 0x00);
    private static readonly Brush DebugBrush = Freeze(0x7A, 0x82, 0x94);
    private static readonly Brush InfoBrush = Freeze(0x1F, 0x24, 0x30);
    private static readonly Brush OkBrush = Freeze(0x1B, 0x6E, 0x3A);

    private readonly DispatcherTimer _timer;
    private FileStream? _stream;
    private long _position;
    private string _carry = string.Empty;
    private bool _paused;

    public LogViewer()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Poll();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        UpdateStateText();
        PathText.Text = BridgePaths.LogFile;
    }

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            BridgePaths.EnsureDirectories();
        }
        catch
        {
            // 目录建不出来时 Poll() 会自己显示错误
        }

        PathText.Text = BridgePaths.LogFile;
        Poll();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        CloseStream();
    }

    // ---------------------------------------------------------------- 轮询

    private void Poll()
    {
        if (_paused) return;

        try
        {
            var path = BridgePaths.LogFile;
            if (_stream == null)
            {
                if (!File.Exists(path))
                {
                    UpdateStateText("日志文件尚未生成（等 Core 写第一条日志）", false);
                    return;
                }

                _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _position = 0;
                _carry = string.Empty;
                LogBox.Document.Blocks.Clear();
            }

            var length = _stream.Length;

            // 轮转或截断：文件变小说明换了新文件，从头重建视图。
            if (length < _position)
            {
                _position = 0;
                _carry = string.Empty;
                LogBox.Document.Blocks.Clear();
            }
            else if (length == _position)
            {
                UpdateStateText();
                return;
            }

            _stream.Seek(_position, SeekOrigin.Begin);

            var remaining = length - _position;
            var want = (int)Math.Min(remaining, MaxChunkBytes);
            var buffer = new byte[want];
            var read = _stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                UpdateStateText();
                return;
            }

            _position += read;
            AppendText(Encoding.UTF8.GetString(buffer, 0, read));
            UpdateStateText();
        }
        catch (Exception ex)
        {
            UpdateStateText($"读取失败：{ex.GetType().Name} - {ex.Message}", false);
            CloseStream();
        }
    }

    private void AppendText(string chunk)
    {
        var text = _carry + chunk;
        var lines = text.Split('\n');

        // 最后一段可能是半行，留到下次再拼。
        _carry = lines[^1];

        for (var i = 0; i < lines.Length - 1; i++)
            AppendLine(lines[i].TrimEnd('\r'));

        // 极端情况下（超长单行）避免 _carry 无限增长。
        if (_carry.Length > 64 * 1024)
        {
            AppendLine(_carry);
            _carry = string.Empty;
        }

        Trim();
        LogBox.ScrollToEnd();
    }

    private void AppendLine(string line)
    {
        var brush = BrushFor(line);
        var run = new Run(line) { Foreground = brush };
        var paragraph = new Paragraph(run)
        {
            Margin = new Thickness(0),
            LineHeight = 15,
        };

        LogBox.Document.Blocks.Add(paragraph);
    }

    private static Brush BrushFor(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.Ordinal)) return ErrorBrush;
        if (line.Contains("[WARN", StringComparison.Ordinal)) return WarnBrush;
        if (line.Contains("[DEBUG", StringComparison.Ordinal)) return DebugBrush;
        return InfoBrush;
    }

    private void Trim()
    {
        while (LogBox.Document.Blocks.Count > MaxBlocks)
        {
            var first = LogBox.Document.Blocks.FirstBlock;
            if (first == null) return;
            LogBox.Document.Blocks.Remove(first);
        }
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _stream = null;
        }
    }

    private void UpdateStateText()
    {
        if (_paused)
        {
            UpdateStateText("暂停中（不会自动刷新，点“暂停”恢复）", true);
            return;
        }

        var size = 0L;
        try
        {
            var info = new FileInfo(BridgePaths.LogFile);
            if (info.Exists) size = info.Length;
        }
        catch
        {
            // ignore
        }

        UpdateStateText($"实时 · 每 1 秒轮询 · 已读 {_position} / {size} 字节", null);
    }

    private void UpdateStateText(string text, bool? warn)
    {
        StateText.Text = text;
        StateText.Foreground = warn switch
        {
            true => WarnBrush,
            null => OkBrush,
            false => ErrorBrush,
        };
    }

    // ---------------------------------------------------------------- 按钮

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "继续" : "暂停";

        if (_paused)
        {
            UpdateStateText("暂停中（不会自动刷新，点“暂停”恢复）", true);
        }
        else
        {
            Poll();
            UpdateStateText();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 只清空显示，读位置保持不动 —— 新日志继续从这里追加。
        LogBox.Document.Blocks.Clear();
        _carry = string.Empty;
        UpdateStateText();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CloseStream();
            _position = 0;
            _carry = string.Empty;
            LogBox.Document.Blocks.Clear();
            Poll();
        }
        catch (Exception ex)
        {
            UpdateStateText($"重新读取失败：{ex.GetType().Name} - {ex.Message}", false);
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BridgePaths.EnsureDirectories();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{BridgePaths.LogDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            UpdateStateText($"打开目录失败：{ex.GetType().Name}", false);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!LogBox.Selection.IsEmpty)
            {
                LogBox.Copy();
                UpdateStateText("已复制选中行到剪贴板", null);
                return;
            }

            var all = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text;
            if (string.IsNullOrWhiteSpace(all))
            {
                UpdateStateText("当前没有可复制的内容", true);
                return;
            }

            Clipboard.SetText(all);
            UpdateStateText("已复制全部可见日志到剪贴板", null);
        }
        catch (Exception ex)
        {
            UpdateStateText($"复制失败：{ex.GetType().Name}", false);
        }
    }
}
