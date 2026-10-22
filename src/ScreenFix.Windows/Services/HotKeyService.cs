using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenFix.Windows.Services;

internal sealed class HotKeyService : IDisposable
{
    private const int ToggleId = 0x5346;
    private const int RestoreId = 0x5347;
    private readonly HwndSource _source;
    public bool ToggleRegistered { get; }
    public bool RestoreRegistered { get; }
    public event Action? Toggle;
    public event Action? Restore;

    public HotKeyService(IntPtr handle)
    {
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Окно горячих клавиш недоступно.");
        _source.AddHook(Hook);
        const uint modifiers = 0x0001 | 0x0002 | 0x4000; // Alt + Ctrl + no-repeat.
        ToggleRegistered = RegisterHotKey(handle, ToggleId, modifiers, 0x79); // F10
        RestoreRegistered = RegisterHotKey(handle, RestoreId, modifiers, 0x7A); // F11
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        if (wParam.ToInt32() == ToggleId) { Toggle?.Invoke(); handled = true; }
        else if (wParam.ToInt32() == RestoreId) { Restore?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (ToggleRegistered) UnregisterHotKey(_source.Handle, ToggleId);
        if (RestoreRegistered) UnregisterHotKey(_source.Handle, RestoreId);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
