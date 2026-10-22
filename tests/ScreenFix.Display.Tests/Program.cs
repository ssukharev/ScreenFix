using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenFix.Windows.Services;
using static ScreenFix.Windows.Native.DisplayNative;

// These tests deliberately exercise only managed transformations of synthetic
// CCD data. They never instantiate DisplayService or call a user32 entry point.
var tests = new List<(string Name, Action Run)>
{
    ("Windows ABI sizes and offsets", () =>
    {
        Check.Equal(8, Marshal.SizeOf<Luid>());
        Check.Equal(48, Marshal.SizeOf<VideoSignalInfo>());
        Check.Equal(20, Marshal.SizeOf<SourceMode>());
        Check.Equal(40, Marshal.SizeOf<DesktopImageInfo>());
        Check.Equal(72, Marshal.SizeOf<PathInfo>());
        Check.Equal(64, Marshal.SizeOf<ModeInfo>());
        Check.Equal(220, Marshal.SizeOf<DevMode>());
        Check.Equal(84, Marshal.SizeOf<SourceDeviceName>());
        Check.Equal(420, Marshal.SizeOf<TargetDeviceName>());
        Check.Equal(80, Marshal.SizeOf<TargetPreferredMode>());
        Check.Equal(840, Marshal.SizeOf<DisplayDevice>());
        Check.Equal((nint)16, Marshal.OffsetOf<ModeInfo>(nameof(ModeInfo.Source)));
        Check.Equal((nint)16, Marshal.OffsetOf<ModeInfo>(nameof(ModeInfo.Target)));
        Check.Equal((nint)16, Marshal.OffsetOf<ModeInfo>(nameof(ModeInfo.Desktop)));
        Check.Equal((nint)32, Marshal.OffsetOf<TargetPreferredMode>(nameof(TargetPreferredMode.Mode)));
        Check.Equal((nint)168, Marshal.OffsetOf<DevMode>(nameof(DevMode.BitsPerPel)));
        Backend.Invoke("AssertNativeLayouts");
    }),
    ("Legacy and virtual union indices", () =>
    {
        var path = new PathInfo
        {
            SourceInfo = new PathSourceInfo { ModeInfoIdx = 12 },
            TargetInfo = new PathTargetInfo { ModeInfoIdx = 18 }
        };
        Check.Equal(12u, path.SourceIndex); Check.Equal(18u, path.TargetIndex);
        Check.Equal(uint.MaxValue, path.DesktopIndex);
        path.Flags = PathSupportsVirtualMode;
        path.SourceInfo.ModeInfoIdx = (12u << 16) | 0xffff;
        path.TargetInfo.ModeInfoIdx = (18u << 16) | 7;
        Check.Equal(12u, path.SourceIndex); Check.Equal(18u, path.TargetIndex);
        Check.Equal(7u, path.DesktopIndex);
        path.TargetInfo.ModeInfoIdx = (18u << 16) | 0xffff;
        Check.Equal(0xffffu, path.DesktopIndex);
    }),
    ("Primary shrinks and right neighbor reconnects", () =>
        Layout.Verify([new(0, 0), new(1920, 0)], 0, false, true, [new(0, 0), new(1706, 0)])),
    ("Selected secondary on right keeps its global origin", () =>
        Layout.Verify([new(0, 0), new(1920, 0)], 1, false, true, [new(0, 0), new(1920, 0)])),
    ("Selected secondary on left keeps primary at zero", () =>
        Layout.Verify([new(-1920, 0), new(0, 0)], 0, false, true, [new(-1706, 0), new(0, 0)])),
    ("Selected secondary above keeps primary at zero", () =>
        Layout.Verify([new(0, -1080), new(0, 0)], 0, false, true, [new(0, -960), new(0, 0)])),
    ("Primary shrinks and bottom neighbor reconnects", () =>
        Layout.Verify([new(0, 0), new(0, 1080)], 0, false, true, [new(0, 0), new(0, 960)])),
    ("Right neighbor keeps vertical offset", () =>
        Layout.Verify([new(0, 0), new(1920, 120)], 0, false, true, [new(0, 0), new(1706, 120)])),
    ("Three monitors preserve adjacency after shrink", () =>
        Layout.Verify([new(0, 0), new(1920, 0), new(3840, 0)], 0, false, true,
            [new(0, 0), new(1706, 0), new(3626, 0)])),
    ("Reconnection can be disabled in proposal", () =>
        Layout.Verify([new(0, 0), new(1920, 0)], 0, false, false, [new(0, 0), new(1920, 0)])),
    ("Virtual primary image uses local source coordinates", () =>
        Layout.Verify([new(0, 0), new(1920, 0)], 0, true, true, [new(0, 0), new(1706, 0)])),
    ("Virtual secondary at positive global X uses local image region", () =>
        Layout.Verify([new(0, 0), new(1920, 0)], 1, true, true, [new(0, 0), new(1920, 0)])),
    ("Virtual secondary at negative global X uses local image region", () =>
        Layout.Verify([new(-1920, 0), new(0, 0)], 0, true, true, [new(-1706, 0), new(0, 0)])),
    ("Virtual secondary at negative global Y uses local image region", () =>
        Layout.Verify([new(0, -1080), new(0, 0)], 0, true, true, [new(0, -960), new(0, 0)])),
    ("Invalid small source size is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        Check.Throws<ArgumentException>(() => layout.Propose(0, width: 319));
    }),
    ("Source larger than physical signal is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        Check.Throws<InvalidOperationException>(() => layout.Propose(0, width: 2000));
    }),
    ("Rotated monitor is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        layout.Paths[0].TargetInfo.Rotation = 2;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Unavailable physical target is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        layout.Paths[0].TargetInfo.TargetAvailable = 0;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Missing physical refresh rate is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        layout.Modes[layout.Paths[0].TargetIndex].Target.Signal.VSyncFreq = default;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Cloned source ID is rejected", () =>
    {
        var layout = new Layout([new(0, 0), new(1920, 0)], false);
        layout.Paths[1].SourceInfo.Id = layout.Paths[0].SourceInfo.Id;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Shared virtual source mode is rejected", () =>
    {
        var layout = new Layout([new(0, 0), new(1920, 0)], true);
        layout.Paths[1].SourceInfo.ModeInfoIdx = layout.Paths[0].SourceInfo.ModeInfoIdx;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Complex virtual crop is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], true);
        layout.Modes[layout.Paths[0].DesktopIndex].Desktop.ImageClip.Left = 10;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("New overlap in a staggered arrangement is rejected", () =>
    {
        var layout = new Layout([new(0, 0), new(1920, 1000), new(1000, 1080)], false);
        layout.Modes[layout.Paths[2].SourceIndex].Source.Width = 920;
        Check.Throws<InvalidOperationException>(() => layout.Propose(0));
    }),
    ("Physical refresh fallback is detected", () =>
    {
        var layout = new Layout([new(0, 0)], true);
        var expected = layout.Propose(0);
        var observed = layout.Propose(0);
        Backend.Modes(observed)[layout.Paths[0].TargetIndex].Target.Signal.VSyncFreq.Numerator = 60000;
        Check.NotNull(Backend.Invoke("Difference", expected, observed));
    }),
    ("Driver scaling substitution is detected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        var expected = layout.Propose(0);
        var observed = layout.Propose(0);
        Backend.Paths(observed)[0].TargetInfo.Scaling = 3;
        var text = (string?)Backend.Invoke("Difference", expected, observed);
        Check.NotNull(text);
        Check.True(text!.Contains("растягивание на весь экран"));
    }),
    ("Native timing change is detected even at the same nominal Hz", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        var expected = layout.Propose(0);
        var observed = layout.Propose(0);
        Backend.Modes(observed)[layout.Paths[0].TargetIndex].Target.Signal.PixelRate++;
        Check.NotNull(Backend.Invoke("Difference", expected, observed));
    }),
    ("JSON recovery snapshot round trip keeps exact topology", () =>
    {
        var layout = new Layout([new(-1920, 120), new(0, 0)], true);
        var saved = layout.RecoverySnapshot();
        var decoded = JsonSerializer.Deserialize<DisplaySnapshot>(JsonSerializer.Serialize(saved))!;
        var restored = Backend.Invoke("FromSnapshot", decoded)!;
        Check.Equal<object?>(null, Backend.Invoke("Difference", layout.Configuration(), restored));
    }),
    ("Recovery snapshot version mismatch is rejected", () =>
    {
        var snapshot = new Layout([new(0, 0)], false).RecoverySnapshot() with { FormatVersion = 99 };
        Check.Throws<InvalidOperationException>(() => Backend.Invoke("FromSnapshot", snapshot));
    }),
    ("Corrupt recovery mode index is rejected", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        layout.Paths[0].SourceInfo.ModeInfoIdx = uint.MaxValue;
        Check.Throws<InvalidOperationException>(() => Backend.Invoke("FromSnapshot", layout.RecoverySnapshot()));
    }),
    ("Gap closing finds screens by identity when the driver orders them differently", () =>
    {
        var before = new Layout([new(0, 0), new(1920, 0)], false);
        // The same two screens as a classic mode change may return them: reversed order, and the
        // first screen (identifier 0) already reduced to 1706 x 960.
        var after = new Layout([new(1920, 0), new(0, 0)], false, [1, 0]);
        var configuration = after.Configuration();
        foreach (ref var mode in Backend.Modes(configuration).AsSpan())
            if (mode.InfoType == 1 && mode.Id == 0) { mode.Source.Width = 1706; mode.Source.Height = 960; }
        Backend.Invoke("CloseDesktopGaps", before.Configuration(), configuration, 0, before.Modes[0].Source, 1706, 960);
        foreach (var mode in Backend.Modes(configuration).Where(m => m.InfoType == 1))
        {
            Check.Equal(mode.Id == 1 ? 1706 : 0, mode.Source.Position.X);
            Check.Equal(0, mode.Source.Position.Y);
        }
    }),
    ("Classic result check accepts the planned screen and rejects substitutions", () =>
    {
        var layout = new Layout([new(0, 0), new(1920, 0)], false);
        var monitor = new DisplayMonitor { SourceId = 0, TargetId = 0 };
        var size = new DisplaySize(1706, 960);
        Check.Equal<object?>(null, Backend.Invoke("VerifyCentered", layout.Configuration(), layout.Propose(0), monitor, size));

        var stretched = layout.Propose(0);
        Backend.Paths(stretched)[0].TargetInfo.Scaling = 1;
        Check.NotNull(Backend.Invoke("VerifyCentered", layout.Configuration(), stretched, monitor, size));

        var slower = layout.Propose(0);
        Backend.Modes(slower)[layout.Paths[0].TargetIndex].Target.Signal.VSyncFreq.Numerator = 60000;
        Check.NotNull(Backend.Invoke("VerifyCentered", layout.Configuration(), slower, monitor, size));

        Check.NotNull(Backend.Invoke("VerifyCentered", layout.Configuration(), layout.Propose(0), monitor, new DisplaySize(1600, 900)));
    }),
    ("The classic query is skipped when it would repeat the virtual one", () =>
    {
        Check.Equal(3, ((Array)Backend.Invoke("MethodsFor", new Layout([new(0, 0)], true).Configuration())!).Length);
        Check.Equal(2, ((Array)Backend.Invoke("MethodsFor", new Layout([new(0, 0)], false).Configuration())!).Length);
    }),
    ("Windows error 1610 is explained without the misleading corruption text", () =>
    {
        var text = (string)Backend.Invoke("Explain", 1610)!;
        Check.True(text.Contains("1610") && text.Contains("не повреждение данных") && !text.Contains("повреждены"));
    }),
    ("A refusal message names the sizes, the code and what was left unchanged", () =>
    {
        var sizes = new[] { new DisplaySize(1768, 992), new DisplaySize(1600, 900) };
        var driver = (string)Backend.Invoke("FailureMessage", sizes,
            Backend.Attempts(Backend.Rejected("1768 × 992 · CCD", "Код Windows 1610", 1610)), false)!;
        Check.True(driver.Contains("1768 × 992") && driver.Contains("1600 × 900") && driver.Contains("1610"));
        Check.True(driver.Contains("Исходные настройки не менялись") && driver.Contains("Без масштабирования"));

        var precondition = (string)Backend.Invoke("FailureMessage", new[] { new DisplaySize(1768, 992) },
            Backend.Attempts(Backend.Rejected("1768 × 992 · CCD", "Для этого режима установите обычную альбомную ориентацию.", 0)), false)!;
        Check.True(precondition.Contains("альбомную ориентацию") && !precondition.Contains("Без масштабирования"));
    }),
    ("Only the scaling mode can be left out of the comparison, nothing else", () =>
    {
        var layout = new Layout([new(0, 0)], false);
        var expected = layout.Propose(0);
        var stretched = layout.Propose(0);
        Backend.Paths(stretched)[0].TargetInfo.Scaling = 4;
        Check.NotNull(Backend.Invoke("Compare", expected, stretched, false));
        Check.Equal<object?>(null, Backend.Invoke("Compare", expected, stretched, true));
        Backend.Modes(stretched)[layout.Paths[0].TargetIndex].Target.Signal.VSyncFreq.Numerator = 60000;
        Check.NotNull(Backend.Invoke("Compare", expected, stretched, true));
    }),
    ("After a classic mode change only the scaling mode can be accepted on the user's word", () =>
    {
        var layout = new Layout([new(0, 0), new(1920, 0)], false);
        var monitor = new DisplayMonitor { SourceId = 0, TargetId = 0 };
        var size = new DisplaySize(1706, 960);
        var stretched = layout.Propose(0);
        Backend.Paths(stretched)[0].TargetInfo.Scaling = 3;
        Check.NotNull(Backend.Invoke("VerifyApplied", layout.Configuration(), stretched, monitor, size, false));
        Check.Equal<object?>(null, Backend.Invoke("VerifyApplied", layout.Configuration(), stretched, monitor, size, true));
        Check.NotNull(Backend.Invoke("VerifyApplied", layout.Configuration(), stretched, monitor, new DisplaySize(1600, 900), true));
    }),
    ("Scaling modes are named for the user", () =>
    {
        Check.Equal("центрирование", (string)Backend.Invoke("ScalingName", 2u)!);
        Check.Equal("растягивание на весь экран", (string)Backend.Invoke("ScalingName", 3u)!);
        Check.Equal("растягивание с сохранением пропорций", (string)Backend.Invoke("ScalingName", 4u)!);
        Check.Equal("режим 77", (string)Backend.Invoke("ScalingName", 77u)!);
    }),
    ("A substituted scaling mode is reported as such, with the mode named and the screen as restored", () =>
    {
        var replaced = Backend.RolledBack("1600 × 900 · CCD",
            "Драйвер не сохранил запрошенное центрирование, размер или частоту: драйвер заменил центрирование на «растягивание с сохранением пропорций»");
        var text = (string)Backend.Invoke("FailureMessage", new[] { new DisplaySize(1600, 900) }, Backend.Attempts(replaced), false)!;
        Check.True(text.Contains("принял размер 1600 × 900, но не оставил центрирование"));
        Check.True(text.Contains("растягивание с сохранением пропорций»."));
        Check.True(text.Contains("был возвращён") && !text.Contains("Исходные настройки не менялись"));
        Check.True(text.Contains("Переопределить режим масштабирования") && !text.Contains("Исходная конфигурация восстановлена"));
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + exception); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} managed display-backend checks passed.");
return failures == 0 ? 0 : 1;

internal readonly record struct Coordinate(int X, int Y);

internal sealed class Layout
{
    public PathInfo[] Paths { get; }
    public ModeInfo[] Modes { get; }
    public uint Flags { get; }
    public Layout(Coordinate[] coordinates, bool virtualMode, uint[]? ids = null)
    {
        Paths = new PathInfo[coordinates.Length];
        Flags = QueryActivePaths | (virtualMode ? QueryVirtualModeAware : 0);
        var modes = new List<ModeInfo>();
        for (var i = 0; i < coordinates.Length; i++)
        {
            var id = ids is null ? (uint)i : ids[i];
            var sourceIndex = (uint)modes.Count;
            modes.Add(new ModeInfo
            {
                InfoType = 1, Id = id,
                Source = new SourceMode { Width = 1920, Height = 1080, PixelFormat = 4,
                    Position = new PointL { X = coordinates[i].X, Y = coordinates[i].Y } }
            });
            var targetIndex = (uint)modes.Count;
            modes.Add(new ModeInfo
            {
                InfoType = 2, Id = id,
                Target = new TargetMode { Signal = new VideoSignalInfo
                {
                    PixelRate = 552960000,
                    ActiveSize = new Region { Width = 1920, Height = 1080 },
                    TotalSize = new Region { Width = 2080, Height = 1111 },
                    HSyncFreq = new Rational { Numerator = 266640, Denominator = 1 },
                    VSyncFreq = new Rational { Numerator = 240000, Denominator = 1000 },
                    ScanLineOrdering = 1
                } }
            });
            var desktopIndex = (uint)modes.Count;
            if (virtualMode)
                modes.Add(new ModeInfo { InfoType = 3, Id = id,
                    Desktop = new DesktopImageInfo
                    {
                        PathSourceSize = new PointL { X = 1920, Y = 1080 },
                        ImageRegion = new RectL { Right = 1920, Bottom = 1080 },
                        ImageClip = new RectL { Right = 1920, Bottom = 1080 }
                    } });
            Paths[i] = new PathInfo
            {
                Flags = virtualMode ? 9u : 1u,
                SourceInfo = new PathSourceInfo { Id = id,
                    ModeInfoIdx = virtualMode ? (sourceIndex << 16) | 0xffff : sourceIndex },
                TargetInfo = new PathTargetInfo { Id = id,
                    ModeInfoIdx = virtualMode ? (targetIndex << 16) | desktopIndex : targetIndex,
                    Rotation = 1, Scaling = 1, TargetAvailable = 1,
                    RefreshRate = new Rational { Numerator = 240000, Denominator = 1000 }, ScanLineOrdering = 1 }
            };
        }
        Modes = modes.ToArray();
    }

    public object Configuration() => Backend.Configuration(Paths, Modes, Flags);
    public object Propose(int selected, bool reconnect = true, int width = 1706, int height = 960) =>
        Backend.Invoke("CreateCentered", Configuration(),
            new DisplayMonitor { SourceId = Paths[selected].SourceInfo.Id, TargetId = Paths[selected].TargetInfo.Id },
            width, height, reconnect)!;
    public DisplaySnapshot RecoverySnapshot() => new()
    {
        QueryFlags = Flags, PathStructSize = Marshal.SizeOf<PathInfo>(), ModeStructSize = Marshal.SizeOf<ModeInfo>(),
        Paths = MemoryMarshal.AsBytes(Paths.AsSpan()).ToArray(), Modes = MemoryMarshal.AsBytes(Modes.AsSpan()).ToArray()
    };

    public static void Verify(Coordinate[] before, int selected, bool virtualMode, bool reconnect, Coordinate[] expected)
    {
        var layout = new Layout(before, virtualMode);
        var pathsBefore = MemoryMarshal.AsBytes(layout.Paths.AsSpan()).ToArray();
        var modesBefore = MemoryMarshal.AsBytes(layout.Modes.AsSpan()).ToArray();
        var proposal = layout.Propose(selected, reconnect);
        var after = Backend.Modes(proposal);
        for (var i = 0; i < before.Length; i++)
        {
            var source = after[layout.Paths[i].SourceIndex].Source;
            Check.Equal(expected[i].X, source.Position.X); Check.Equal(expected[i].Y, source.Position.Y);
            Check.Equal(i == selected ? 1706u : 1920u, source.Width);
            Check.Equal(i == selected ? 960u : 1080u, source.Height);
            Check.Equal(240d, after[layout.Paths[i].TargetIndex].Target.Signal.VSyncFreq.Hertz);
            var targetBefore = new[] { layout.Modes[layout.Paths[i].TargetIndex].Target };
            var targetAfter = new[] { after[layout.Paths[i].TargetIndex].Target };
            Check.True(MemoryMarshal.AsBytes(targetBefore.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(targetAfter.AsSpan())));
            if (virtualMode)
            {
                var image = after[layout.Paths[i].DesktopIndex].Desktop;
                Check.Equal(0, image.ImageRegion.Left); Check.Equal(0, image.ImageRegion.Top);
                Check.Equal(i == selected ? 1706 : 1920, image.PathSourceSize.X);
                Check.Equal(i == selected ? 960 : 1080, image.PathSourceSize.Y);
                Check.Equal(image.PathSourceSize.X, image.ImageRegion.Right);
                Check.Equal(image.PathSourceSize.Y, image.ImageRegion.Bottom);
            }
        }
        Check.True(pathsBefore.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(layout.Paths.AsSpan())));
        Check.True(modesBefore.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(layout.Modes.AsSpan())));
        Check.Equal(2u, Backend.Paths(proposal)[selected].TargetInfo.Scaling);
        Check.Equal<object?>(null, Backend.Invoke("Difference", proposal, proposal));
    }
}

internal static class Backend
{
    private static readonly Type Type = typeof(DisplayService);
    private static readonly Type Config = Type.GetNestedType("Configuration", BindingFlags.NonPublic)!;
    public static object Configuration(PathInfo[] paths, ModeInfo[] modes, uint flags) => Activator.CreateInstance(Config, paths, modes, flags)!;
    public static object Rejected(string label, string reason, int code) =>
        Type.GetNestedType("Attempt", BindingFlags.NonPublic)!.GetMethod("Rejected")!.Invoke(null, new object?[] { label, reason, code })!;
    public static object RolledBack(string label, string reason) =>
        Type.GetNestedType("Attempt", BindingFlags.NonPublic)!.GetMethod("FromRollBack")!.Invoke(null,
            new object?[] { label, new DisplayApplyResult { Message = reason + " Исходная конфигурация восстановлена.", RolledBack = true } })!;
    public static object Attempts(params object[] attempts)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(Type.GetNestedType("Attempt", BindingFlags.NonPublic)!))!;
        foreach (var attempt in attempts) list.Add(attempt);
        return list;
    }
    public static ModeInfo[] Modes(object configuration) => (ModeInfo[])Config.GetProperty("Modes")!.GetValue(configuration)!;
    public static PathInfo[] Paths(object configuration) => (PathInfo[])Config.GetProperty("Paths")!.GetValue(configuration)!;
    public static object? Invoke(string method, params object[] arguments)
    {
        try { return Type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }
    public static void NotNull(object? value)
    {
        if (value is null) throw new InvalidOperationException("Expected a detected configuration difference.");
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
}
