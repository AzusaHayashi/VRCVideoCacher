using System.Windows;
using Microsoft.Win32;

namespace CookieBridge.Manager.Services;

/// <summary>
/// ViewModel 需要的交互能力（确认框、信息框、选目录、复制）。抽出接口是为了让 VM 不直接摸 Window，
/// 但仍然保持“零框架”的轻量做法。
/// </summary>
public interface IDialogService
{
    /// <summary>二次确认。默认按钮是“否”，不会误点。</summary>
    bool Confirm(string title, string message, string yesText = "继续", string noText = "取消");

    void Info(string title, string message);

    void Warn(string title, string message);

    /// <summary>选择安装目录；取消时返回 false。</summary>
    bool PickFolder(string title, string? initialDirectory, out string path);

    void Copy(string text);
}

/// <summary>基于 WPF 自带对话框的实现（MessageBox + Microsoft.Win32.OpenFolderDialog，无第三方依赖）。</summary>
public sealed class WindowDialogService : IDialogService
{
    private readonly Window _owner;

    public WindowDialogService(Window owner) => _owner = owner;

    public bool Confirm(string title, string message, string yesText = "继续", string noText = "取消")
    {
        try
        {
            var result = MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }
        catch
        {
            // 弹窗失败绝不等于同意。
            return false;
        }
    }

    public void Info(string title, string message)
    {
        try
        {
            MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // ignore
        }
    }

    public void Warn(string title, string message)
    {
        try
        {
            MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // ignore
        }
    }

    public bool PickFolder(string title, string? initialDirectory, out string path)
    {
        path = string.Empty;
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                dialog.InitialDirectory = initialDirectory;

            if (dialog.ShowDialog(_owner) != true) return false;

            path = dialog.FolderName ?? string.Empty;
            return path.Length > 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_owner, $"打开目录选择框失败：{ex.GetType().Name} - {ex.Message}",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public void Copy(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被别的程序占用时静默失败，状态栏会提示用户手动复制。
        }
    }
}
