namespace ScreenFix.Windows.Services;

public sealed record DisplayMode(int Width, int Height, double RefreshRateHz, int BitsPerPixel);

/// <summary>A centered image size in pixels that the driver is asked to produce.</summary>
public readonly record struct DisplaySize(int Width, int Height);

public sealed record DisplayMonitor
{
    public string DeviceName { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string MonitorDevicePath { get; init; } = string.Empty;
    /// <summary>Name of the video adapter that drives this screen, for example the GPU in a hybrid laptop.</summary>
    public string AdapterDescription { get; init; } = string.Empty;
    public uint OutputTechnology { get; init; }
    public long AdapterId { get; init; }
    public uint SourceId { get; init; }
    public uint TargetId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public int Width => CurrentWidth;
    public int Height => CurrentHeight;
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }
    public int SignalWidth { get; init; }
    public int SignalHeight { get; init; }
    public double RefreshRateHz { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsClone { get; init; }
    public bool IsCentered { get; init; }
    public uint Rotation { get; init; }
    public IReadOnlyList<DisplayMode> AvailableModes { get; init; } = [];

    // LVDS, embedded DisplayPort, embedded UDI and the generic "internal" connector: laptop panels.
    public bool IsInternalPanel => OutputTechnology is 6 or 11 or 13 or 0x80000000;
    public string ShortDeviceName => DeviceName.TrimStart('\\', '.');
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FriendlyName) && !string.Equals(FriendlyName, DeviceName, StringComparison.Ordinal)
            ? FriendlyName
            : IsInternalPanel ? "Встроенный экран" : "Монитор";

    public override string ToString() =>
        $"{DisplayName} ({ShortDeviceName}) · {CurrentWidth} × {CurrentHeight} · {RefreshRateHz:0.##} Гц";
}

/// <summary>Plain JSON-serializable recovery record. Byte arrays are base64 in JSON.
/// It preserves all active paths, source positions and complete target timings.
/// Adapter identifiers remain valid within the current Windows login session.</summary>
public sealed record DisplaySnapshot
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public uint QueryFlags { get; init; }
    public int PathStructSize { get; init; }
    public int ModeStructSize { get; init; }
    public byte[] Paths { get; init; } = [];
    public byte[] Modes { get; init; } = [];
    public List<DisplayIdentity> Displays { get; init; } = [];
}

public sealed record DisplayIdentity(string DeviceName, string MonitorDevicePath,
    long AdapterId, uint SourceId, uint TargetId);

public sealed record DisplayApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int NativeErrorCode { get; init; }
    public int ActualWidth { get; init; }
    public int ActualHeight { get; init; }
    public double ActualRefreshRateHz { get; init; }
    public bool RolledBack { get; init; }
    public bool NeedsRecovery { get; init; }
    /// <summary>True when the request failed before anything was changed, so no restore is needed.</summary>
    public bool Unchanged { get; init; }
    public DisplayMonitor? Monitor { get; init; }
    public string Diagnostics { get; init; } = string.Empty;
}

/// <summary>Outcome of a dry-run check of one size with one way of applying it.</summary>
public sealed record ProbeResult(DisplaySize Size, string Method, bool Accepted, int Code, string Note);
