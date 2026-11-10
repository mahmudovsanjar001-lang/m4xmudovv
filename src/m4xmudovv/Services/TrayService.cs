using System;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace m4xmudovv.Services;

/// <summary>System tray icon (Windows Forms NotifyIcon) with Open / Settings / Exit.</summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public TrayService()
    {
        // No WinForms context menu: right-click raises ContextRequested and the app shows its own themed popup.
        _icon = new WinForms.NotifyIcon
        {
            Text = "m4xmudovv",
            Icon = LoadIcon(),
            Visible = false
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Right) return;
            var position = WinForms.Cursor.Position;   // physical pixels (the app is per-monitor DPI aware)
            ContextRequested?.Invoke(position.X, position.Y);
        };
    }

    /// <summary>Right-click on the tray icon; arguments are the pointer position in physical screen pixels.</summary>
    public event Action<int, int>? ContextRequested;
    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception) { }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
