using System.Runtime.InteropServices;

namespace ScreenFix.Windows.Native;

// The CCD structs use their Windows SDK layout. In particular MODE_INFO's union
// is 48 bytes, even when its active member is the 20-byte SOURCE_MODE.
internal static class DisplayNative
{
    internal const uint QueryActivePaths = 0x2;
    internal const uint QueryVirtualModeAware = 0x10;
    internal const uint QueryVirtualRefreshRateAware = 0x40;
    internal const uint UseSuppliedConfig = 0x20;
    internal const uint Validate = 0x40;
    internal const uint Apply = 0x80;
    internal const uint SetVirtualModeAware = 0x8000;
    internal const uint SetVirtualRefreshRateAware = 0x20000;
    internal const uint PathSupportsVirtualMode = 0x8;
    internal const uint Centered = 2;
    internal const uint Identity = 1;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int EnumCurrentSettings = -1;
    internal const uint CdsTest = 0x2;
    internal const uint DmPosition = 0x20;
    internal const uint DmDisplayOrientation = 0x80;
    internal const uint DmBitsPerPel = 0x40000;
    internal const uint DmPelsWidth = 0x80000;
    internal const uint DmPelsHeight = 0x100000;
    internal const uint DmDisplayFlags = 0x200000;
    internal const uint DmDisplayFrequency = 0x400000;
    internal const uint DmDisplayFixedOutput = 0x20000000;

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, nint topologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
        uint modeCount, [In] ModeInfo[] modes, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName packet);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetPreferredMode packet);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsExW(string deviceName, int modeNumber,
        ref DevMode mode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ChangeDisplaySettingsExW(string deviceName, ref DevMode mode,
        nint window, uint flags, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevicesW(string? device, uint deviceNumber,
        ref DisplayDevice displayDevice, uint flags);

    /// <summary>DEVMODE.dmDisplayFixedOutput value that asks the GPU to center the image.</summary>
    internal const uint FixedOutputCenter = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
        public readonly long Value => ((long)HighPart << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectL { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Region { public uint Width; public uint Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
        public readonly double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq;
        public Rational VSyncFreq;
        public Region ActiveSize;
        public Region TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetMode { public VideoSignalInfo Signal; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public PointL Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DesktopImageInfo
    {
        public PointL PathSourceSize;
        public RectL ImageRegion;
        public RectL ImageClip;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public TargetMode Target;
        [FieldOffset(16)] public SourceMode Source;
        [FieldOffset(16)] public DesktopImageInfo Desktop;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
        public readonly bool IsVirtual => (Flags & PathSupportsVirtualMode) != 0;
        public readonly uint SourceIndex => IsVirtual ? SourceInfo.ModeInfoIdx >> 16 : SourceInfo.ModeInfoIdx;
        public readonly uint TargetIndex => IsVirtual ? TargetInfo.ModeInfoIdx >> 16 : TargetInfo.ModeInfoIdx;
        public readonly uint DesktopIndex => IsVirtual ? TargetInfo.ModeInfoIdx & 0xffff : uint.MaxValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufacturerId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetPreferredMode
    {
        public DeviceInfoHeader Header;
        public uint Width;
        public uint Height;
        public TargetMode Mode;
    }

    // DISPLAY_DEVICEW: 4 + 64 + 256 + 4 + 256 + 256 = 840 bytes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;

        internal static DisplayDevice Create() => new()
        {
            Size = checked((uint)Marshal.SizeOf<DisplayDevice>()),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceId = string.Empty,
            DeviceKey = string.Empty
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public PointL Position;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;

        internal static DevMode Create() => new()
        {
            DeviceName = string.Empty,
            FormName = string.Empty,
            Size = checked((ushort)Marshal.SizeOf<DevMode>())
        };
    }
}
