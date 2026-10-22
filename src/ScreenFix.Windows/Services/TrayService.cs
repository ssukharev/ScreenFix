using System.Windows;
using Forms = System.Windows.Forms;

namespace ScreenFix.Windows.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _drawingIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _toggle;
    public event Action? Open;
    public event Action? Toggle;
    public event Action? Restore;
    public event Action? Exit;

    public TrayService()
    {
        using var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ScreenFix.ico"))?.Stream;
        _drawingIcon = resource is null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone() : new System.Drawing.Icon(resource);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Открыть ScreenFix", null, (_, _) => Open?.Invoke());
        _toggle = new Forms.ToolStripMenuItem("Включить уменьшение    Ctrl+Alt+F10", null, (_, _) => Toggle?.Invoke());
        _menu.Items.Add(_toggle);
        _menu.Items.Add("Вернуть экран    Ctrl+Alt+F11", null, (_, _) => Restore?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Восстановить экран и выйти", null, (_, _) => Exit?.Invoke());
        _icon = new Forms.NotifyIcon
        {
            Icon = _drawingIcon,
            Text = "ScreenFix — полный экран",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Open?.Invoke();
        _icon.BalloonTipClicked += (_, _) => Open?.Invoke();
    }

    public void Update(bool active, bool pending)
    {
        _icon.Text = active ? "ScreenFix — уменьшение включено" : "ScreenFix — полный экран";
        _toggle.Text = active ? "Отключить уменьшение    Ctrl+Alt+F10" : "Включить уменьшение    Ctrl+Alt+F10";
        _toggle.Enabled = !pending;
    }

    public void Notify(string message, bool error = false)
    {
        _icon.ShowBalloonTip(5000, "ScreenFix", message.Length > 250 ? message[..247] + "…" : message,
            error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _drawingIcon.Dispose();
    }
}
