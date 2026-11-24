using System.Runtime.InteropServices;
using System.Windows;
using Rifflo.Settings;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Rifflo;

internal sealed class Tray : IDisposable
{
    private const int SmallIconWidth = 49;
    private const int SmallIconHeight = 50;
    private readonly Drawing.Icon _icon = LoadIcon();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _position;
    private readonly Forms.ToolStripMenuItem _broadcastCapture;
    private readonly Forms.ToolStripMenuItem _visibility;
    private readonly Forms.ToolStripMenuItem _frameRate30;
    private readonly Forms.ToolStripMenuItem _frameRate60;

    public Tray(string name, Action openSettings, Action togglePositionLock, Action toggleBroadcastCapture, Action toggleVisibility,
        Action<int> setFrameRate, Action quit)
    {
        var settings = CreateItem("设置", openSettings);
        _position = CreateItem("锁定位置并点击穿透", togglePositionLock);
        _broadcastCapture = CreateItem("直播捕获模式", toggleBroadcastCapture);
        _visibility = CreateItem("隐藏悬浮窗", toggleVisibility);
        _frameRate30 = CreateItem("30 帧（默认）", () => setFrameRate(AppSettings.DefaultAnimationFrameRate));
        _frameRate60 = CreateItem("60 帧", () => setFrameRate(AppSettings.HighAnimationFrameRate));
        var frameRate = new Forms.ToolStripMenuItem("悬浮窗动画帧率");
        frameRate.DropDownItems.AddRange([_frameRate30, _frameRate60]);
        _menu.Items.AddRange([settings, _position, _broadcastCapture, _visibility, frameRate,
            new Forms.ToolStripSeparator(), CreateItem("退出", quit)]);
        _notifyIcon = new Forms.NotifyIcon { Icon = _icon, Text = name, ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                openSettings();
        };
    }

    public void Update(bool positionLocked, bool broadcastCaptureEnabled, bool hidden, int frameRate)
    {
        _position.Checked = positionLocked;
        _broadcastCapture.Checked = broadcastCaptureEnabled;
        _visibility.Text = hidden ? "恢复悬浮窗" : "隐藏悬浮窗";
        _frameRate60.Checked = frameRate == AppSettings.HighAnimationFrameRate;
        _frameRate30.Checked = !_frameRate60.Checked;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Forms.ToolStripMenuItem CreateItem(string name, Action action)
    {
        var item = new Forms.ToolStripMenuItem(name);
        item.Click += (_, _) => action();
        return item;
    }

    private static Drawing.Icon LoadIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/Rifflo.ico");
        using var stream = Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidOperationException("The Rifflo icon resource is missing.");

        // The notification area uses the small-icon metrics for Explorer's taskbar DPI.
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != 0 ? GetDpiForWindow(taskbar) : 0;
        var size = dpi != 0
            ? new Drawing.Size(GetSystemMetricsForDpi(SmallIconWidth, dpi), GetSystemMetricsForDpi(SmallIconHeight, dpi))
            : Forms.SystemInformation.SmallIconSize;
        return new Drawing.Icon(stream, size);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
