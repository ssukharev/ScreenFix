namespace ScreenFix.Core;

/// <summary>Plain settings suitable for System.Text.Json; validate before use.</summary>
public sealed class ScreenFixSettings
{
    public int Version { get; set; } = 1;
    public string? MonitorDeviceName { get; set; }
    public double PhysicalDiagonalInches { get; set; } = 27;
    public double TargetDiagonalInches { get; set; } = 24;
    public double PreferredRefreshRateHz { get; set; } = 240;
    public bool EnableCursorPortals { get; set; } = true;
    public bool StartMinimized { get; set; }
}
