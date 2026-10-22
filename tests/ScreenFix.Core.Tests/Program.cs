using ScreenFix.Core;

// Standalone verification executable: no test framework packages or network access required.
var cases = new (string Name, Action Run)[]
{
    ("27 to 24 inches preserves both dimensions", () =>
    {
        var plan = ScreenSizeCalculator.Calculate(1920, 1080, 27, 24);
        Equal(1707, plan.VisibleWidth);
        Equal(960, plan.VisibleHeight);
        Near(24, plan.EffectiveDiagonalInches, 0.01);
        Equal(new PixelRect(106, 60, 1707, 960), plan.CenteredBounds);
    }),
    ("Full size is an exact no-op", () =>
    {
        var plan = ScreenSizeCalculator.Calculate(2560, 1440, 27, 27);
        Equal(true, plan.IsFullSize);
        Equal(0d, plan.DiagonalErrorInches);
        Equal(new PixelRect(0, 0, 2560, 1440), plan.CenteredBounds);
    }),
    ("Driver-friendly 1080p size reports its real approximate diagonal", () =>
    {
        var plan = ScreenSizeCalculator.CalculateAligned(1920, 1080, 27, 24);
        Equal(1704, plan.VisibleWidth);
        Equal(960, plan.VisibleHeight);
        Equal(24d, plan.TargetDiagonalInches);
        Near(23.97, plan.EffectiveDiagonalInches, 0.01);
        Equal(true, plan.DiagonalErrorInches < 0);
        Near(ScreenSizeCalculator.DiagonalForResolution(1920, 1080, 27, 1704, 960),
            plan.EffectiveDiagonalInches, 0.000000001);
    }),
    ("Driver-friendly 1440p size uses 2272 by 1280", () =>
    {
        var plan = ScreenSizeCalculator.CalculateAligned(2560, 1440, 27, 24);
        Equal(2272, plan.VisibleWidth);
        Equal(1280, plan.VisibleHeight);
        Equal(24d, plan.TargetDiagonalInches);
        Near(23.97, plan.EffectiveDiagonalInches, 0.01);
        Equal(2276, ScreenSizeCalculator.Calculate(2560, 1440, 27, 24).VisibleWidth);
    }),
    ("Full-size alignment preserves an unaligned native resolution", () =>
    {
        var plan = ScreenSizeCalculator.CalculateAligned(1366, 769, 24, 24);
        Equal(1366, plan.VisibleWidth);
        Equal(769, plan.VisibleHeight);
        Equal(24d, plan.EffectiveDiagonalInches);
        Equal(true, plan.IsFullSize);
    }),
    ("Aligned near-minimum dimensions never become zero or exceed native", () =>
    {
        foreach (var (width, height) in new[] { (1, 1), (3, 2), (320, 200), (1920, 1080) })
        {
            var plan = ScreenSizeCalculator.CalculateAligned(width, height, 250, 1);
            Equal(true, plan.VisibleWidth >= 1 && plan.VisibleWidth <= width);
            Equal(true, plan.VisibleHeight >= 1 && plan.VisibleHeight <= height);
            Equal(true, plan.EffectiveDiagonalInches > 0);
        }
    }),
    ("Alignment accepts bounded positive integer blocks and rejects invalid blocks", () =>
    {
        var precise = ScreenSizeCalculator.Calculate(1920, 1080, 27, 24);
        Equal(precise, ScreenSizeCalculator.CalculateAligned(1920, 1080, 27, 24, 1, 1));
        var thirds = ScreenSizeCalculator.CalculateAligned(1920, 1080, 27, 24, 3, 3);
        Equal(0, thirds.VisibleWidth % 3);
        Equal(0, thirds.VisibleHeight % 3);
        foreach (var alignment in new[] { 0, -1, 1025, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() =>
                ScreenSizeCalculator.CalculateAligned(1920, 1080, 27, 24, alignment));
            Throws<ArgumentOutOfRangeException>(() =>
                ScreenSizeCalculator.CalculateAligned(1920, 1080, 27, 24, 8, alignment));
        }
    }),
    ("Portrait and ultrawide sizes retain their native proportions", () =>
    {
        foreach (var (width, height) in new[] { (1080, 1920), (3440, 1440), (5120, 1440), (3840, 2160) })
        {
            var plan = ScreenSizeCalculator.Calculate(width, height, 34, 24);
            Near(width * 24d / 34, plan.VisibleWidth, 0.5);
            Near(height * 24d / 34, plan.VisibleHeight, 0.5);
        }
    }),
    ("Invalid diagonals and enlargement are rejected", () =>
    {
        foreach (var diagonal in new[] { 0d, -1, 0.5, 251, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Throws<ArgumentOutOfRangeException>(() => ScreenSizeCalculator.Calculate(1920, 1080, diagonal, 24));
            Throws<ArgumentOutOfRangeException>(() => ScreenSizeCalculator.Calculate(1920, 1080, 27, diagonal));
        }
        Throws<ArgumentOutOfRangeException>(() => ScreenSizeCalculator.Calculate(1920, 1080, 24, 27));
        Throws<ArgumentOutOfRangeException>(() => ScreenSizeCalculator.Calculate(0, 1080, 27, 24));
    }),
    ("Rounding stays within half a pixel across useful resolutions", () =>
    {
        var random = new Random(4532);
        for (var i = 0; i < 500; i++)
        {
            var width = random.Next(640, 7681);
            var height = random.Next(480, 4321);
            var actual = random.Next(20, 66);
            var target = random.Next(10, actual + 1);
            var plan = ScreenSizeCalculator.Calculate(width, height, actual, target);
            Near((double)width * target / actual, plan.VisibleWidth, 0.50000001);
            Near((double)height * target / actual, plan.VisibleHeight, 0.50000001);
        }
    }),
    ("Exact supported 240 Hz mode is recognized", () =>
    {
        var match = ModeSelector.FindBest(Plan(), [new(1707, 960, 240), new(1707, 960, 60)]);
        Required(match);
        Equal(true, match!.IsExactSize);
        Equal(true, match.MeetsPreferredRefreshRate);
        Equal(240d, match.Mode.RefreshRateHz);
    }),
    ("Fractional refresh is accepted without disguising size error", () =>
    {
        var match = ModeSelector.FindBest(Plan(), [new(1728, 972, 239.76)]);
        Required(match);
        Equal(false, match!.IsExactSize);
        Equal(true, match.MeetsPreferredRefreshRate);
        Equal(true, match.RelativeSizeError > 0);
        Equal(true, match.EffectiveDiagonalInches > 24);
    }),
    ("Acceptable 240 Hz mode precedes an exact 60 Hz mode", () =>
    {
        var match = ModeSelector.FindBest(Plan(), [new(1707, 960, 60), new(1728, 972, 240)]);
        Equal(new DisplayMode(1728, 972, 240), match!.Mode);
        Equal(false, match.IsExactSize);
    }),
    ("Refresh shortfall is explicit when no fast mode exists", () =>
    {
        var match = ModeSelector.FindBest(Plan(), [new(1707, 960, 144)]);
        Required(match);
        Equal(false, match!.MeetsPreferredRefreshRate);
    }),
    ("Distant, distorted, invalid and oversized modes are not called close", () =>
    {
        Equal<ModeMatch?>(null, ModeSelector.FindBest(Plan(),
            [new(1600, 900, 240), new(1707, 800, 240), new(0, 960, 240),
             new(1707, 960, double.NaN), new(3840, 2160, 240)]));
    }),
    ("Closest refresh is selected deterministically", () =>
    {
        var match = ModeSelector.FindBest(Plan(), [new(1707, 960, 360), new(1707, 960, 240)]);
        Equal(240d, match!.Mode.RefreshRateHz);
    }),
    ("Unlisted calculated size falls back to the nearest size the driver lists", () =>
    {
        // 1704 x 960 is what the 27 to 24 calculation gives; no driver lists it, so Windows rejects it.
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24, Listed1080p144(), 144.03);
        var best = ModeSelector.PickAutomatic(candidates);
        Required(best);
        Equal(1768, best!.Width);
        Equal(992, best.Height);
        Equal(true, best.IsDriverListed);
        Equal(false, best.IsRequestedSize);
        Near(24.85, best.EffectiveDiagonalInches, 0.01);
        var requested = candidates.Single(c => c.IsRequestedSize);
        Equal(1704, requested.Width);
        Equal(960, requested.Height);
        Equal(false, requested.IsDriverListed);
        Equal(true, candidates.Any(c => c.Width == 1600 && c.Height == 900));
    }),
    ("A listed calculated size is first and automatic", () =>
    {
        var modes = Listed1080p144().Append(new DisplayMode(1704, 960, 144)).ToArray();
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24, modes, 144.03);
        Equal(true, candidates[0].IsRequestedSize);
        Equal(true, candidates[0].IsDriverListed);
        Equal(candidates[0], ModeSelector.PickAutomatic(candidates));
    }),
    ("Candidates are ordered by distance from the requested diagonal", () =>
    {
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24, Listed1080p144(), 144.03);
        for (var i = 1; i < candidates.Count; i++)
            Equal(true, Math.Abs(candidates[i - 1].DiagonalErrorInches) <= Math.Abs(candidates[i].DiagonalErrorInches));
    }),
    ("Other proportions, other refresh rates and the native size are not offered", () =>
    {
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24,
            [new(1680, 1050, 144), new(1600, 900, 60), new(1600, 900, 144), new(1920, 1080, 144)], 144.03);
        Equal(false, candidates.Any(c => c.Width == 1680 || c.Width == 1920));
        Equal(1, candidates.Count(c => c.Width == 1600 && c.Height == 900));
    }),
    ("Without a listed mode at the current refresh rate only the calculated size remains", () =>
    {
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24, [new(1768, 992, 60), new(1600, 900, 60)], 144.03);
        Equal(1, candidates.Count);
        Equal(true, candidates[0].IsRequestedSize);
        Equal(false, candidates[0].IsDriverListed);
        Equal(candidates[0], ModeSelector.PickAutomatic(candidates));
        Equal(1, ModeSelector.RankSizes(1920, 1080, 27, 24, [], 144.03).Count);
    }),
    ("A full-size request needs no candidates", () =>
    {
        Equal(0, ModeSelector.RankSizes(1920, 1080, 27, 27, Listed1080p144(), 144.03).Count);
        Equal<SizeCandidate?>(null, ModeSelector.PickAutomatic([]));
    }),
    ("The number of listed candidates is limited", () =>
    {
        var modes = Enumerable.Range(0, 16).Select(k => new DisplayMode(1600 + 16 * k, 900 + 9 * k, 144)).ToArray();
        var candidates = ModeSelector.RankSizes(1920, 1080, 27, 24, modes, 144, maximumCount: 3);
        Equal(3, candidates.Count(c => c.IsDriverListed));
        Equal(true, candidates.Count <= 4);
    }),
    ("Invalid candidate arguments are rejected", () =>
    {
        foreach (var hertz in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
            Throws<ArgumentOutOfRangeException>(() => ModeSelector.RankSizes(1920, 1080, 27, 24, Listed1080p144(), hertz));
        Throws<ArgumentOutOfRangeException>(() => ModeSelector.RankSizes(1920, 1080, 27, 24, Listed1080p144(), 144, maximumCount: 0));
        Throws<ArgumentOutOfRangeException>(() => ModeSelector.RankSizes(1920, 1080, 27, 24, Listed1080p144(), 144, maximumRelativeAspectError: 2));
    }),
    ("Negative rectangles use exclusive right and bottom edges", () =>
    {
        var rect = new PixelRect(-1920, -100, 1920, 1080);
        Equal(true, rect.Contains(new PixelPoint(-1, 0)));
        Equal(false, rect.Contains(new PixelPoint(0, 0)));
        Equal(false, rect.Contains(new PixelPoint(-1, 980)));
        Throws<ArgumentOutOfRangeException>(() => _ = new PixelRect(0, 0, 0, 1));
        Throws<OverflowException>(() => _ = new PixelRect(int.MaxValue, 0, 10, 1));
    }),
    ("Neighbor lookup supports left and right with negative origins", () =>
    {
        var main = Monitor("main", -1920, -100, 1920, 1080);
        var left = Monitor("left", -3840, 0, 1920, 1080);
        var right = Monitor("right", 0, 300, 2560, 1440);
        Equal(left, NeighborSelector.FindHorizontalNeighbor(main, [main, right, left], MonitorSide.Left));
        Equal(right, NeighborSelector.FindHorizontalNeighbor(main, [left, main, right], MonitorSide.Right));
    }),
    ("Distant and diagonally touching screens are not neighbors", () =>
    {
        var main = Monitor("main", 0, 0, 1920, 1080);
        Equal<MonitorLayout?>(null, NeighborSelector.FindHorizontalNeighbor(main,
            [Monitor("far", 2300, 0, 1920, 1080), Monitor("diagonal", 1920, 1080, 1920, 1080)],
            MonitorSide.Right));
    }),
    ("Neighbor lookup prefers greater shared height when seams split", () =>
    {
        var main = Monitor("main", 0, 0, 1920, 1080);
        var upper = Monitor("upper", 1920, -600, 1920, 1080);
        var lower = Monitor("lower", 1920, 480, 1920, 1080);
        Equal(lower, NeighborSelector.FindHorizontalNeighbor(main, [upper, lower], MonitorSide.Right));
    }),
    ("Reduced desktop closes right gaps while retaining left screens", () =>
    {
        var original = new[]
        {
            Monitor("main", 0, 0, 1920, 1080), Monitor("left", -1920, 0, 1920, 1080),
            Monitor("right", 1920, 0, 1920, 1080), Monitor("far-right", 3840, 0, 1920, 1080)
        };
        var plan = DisplayLayoutPlanner.ResizeMonitor(original, "main", 1707, 960);
        Equal(false, plan.HasOverlaps);
        Equal(1707, plan.Monitors.Single(m => m.Id == "right").Bounds.Left);
        Equal(3627, plan.Monitors.Single(m => m.Id == "far-right").Bounds.Left);
        Equal(-1920, plan.Monitors.Single(m => m.Id == "left").Bounds.Left);
        Equal(1920, original[2].Bounds.Left);
    }),
    ("Reduced desktop closes bottom gaps for vertically stacked monitors", () =>
    {
        var plan = DisplayLayoutPlanner.ResizeMonitor(
            [Monitor("main", 0, 0, 1920, 1080), Monitor("below", 0, 1080, 1920, 1080)],
            "main", 1707, 960);
        Equal(false, plan.HasOverlaps);
        Equal(960, plan.Monitors.Single(m => m.Id == "below").Bounds.Top);
    }),
    ("Complex conflicting layouts are flagged for the caller", () =>
    {
        var plan = DisplayLayoutPlanner.ResizeMonitor(
            [Monitor("main", 0, 0, 1920, 1080), Monitor("above", 0, -1080, 1920, 1080),
             Monitor("above-right", 1920, -1080, 1920, 1080)], "main", 1707, 960);
        Equal(true, plan.HasOverlaps);
    }),
    ("Layout rejects duplicate and missing identifiers", () =>
    {
        Throws<ArgumentException>(() => DisplayLayoutPlanner.ResizeMonitor(
            [Monitor("same", 0, 0, 1920, 1080), Monitor("same", 1920, 0, 1920, 1080)], "same", 1707, 960));
        Throws<ArgumentException>(() => DisplayLayoutPlanner.ResizeMonitor(
            [Monitor("main", 0, 0, 1920, 1080)], "missing", 1707, 960));
    }),
    ("Right portal skips the black band in both directions", () =>
    {
        var portal = RightPortal();
        Equal(true, PortalGeometry.TryMap(portal, new(1812, 500), new(1813, 500), out var destination));
        Equal(new PixelPoint(1921, 500), destination);
        Equal(true, PortalGeometry.TryMap(portal, new(1920, 500), new(1919, 500), out destination));
        Equal(new PixelPoint(1811, 500), destination);
    }),
    ("Left portal skips the black band in both directions", () =>
    {
        var portal = CursorPortal.TryCreate(new(0, 0, 1920, 1080), new(106, 60, 1707, 960),
            new(-1920, 0, 1920, 1080), MonitorSide.Left)!;
        Equal(true, PortalGeometry.TryMap(portal, new(106, 500), new(105, 500), out var destination));
        Equal(new PixelPoint(-2, 500), destination);
        Equal(true, PortalGeometry.TryMap(portal, new(-1, 500), new(0, 500), out destination));
        Equal(new PixelPoint(107, 500), destination);
    }),
    ("Portals handle negative monitor coordinates", () =>
    {
        var portal = CursorPortal.TryCreate(new(-1920, -100, 1920, 1080), new(-1814, -40, 1707, 960),
            new(0, -100, 1920, 1080), MonitorSide.Right)!;
        Equal(true, PortalGeometry.TryMap(portal, new(-108, 250), new(-107, 250), out var destination));
        Equal(new PixelPoint(1, 250), destination);
        Equal(true, PortalGeometry.TryMap(portal, new(0, 250), new(-1, 250), out destination));
        Equal(new PixelPoint(-109, 250), destination);
    }),
    ("Only the shared vertical seam triggers a portal", () =>
    {
        var portal = CursorPortal.TryCreate(new(0, 0, 1920, 1080), new(106, 60, 1707, 960),
            new(1920, 700, 1920, 1080), MonitorSide.Right)!;
        Equal(false, PortalGeometry.TryMap(portal, new(1812, 699), new(1813, 699), out _));
        Equal(true, PortalGeometry.TryMap(portal, new(1812, 700), new(1813, 700), out var destination));
        Equal(700, destination.Y);
        Equal(false, PortalGeometry.TryMap(portal, new(1812, 1019), new(1813, 1020), out _));
    }),
    ("Distant edges, stationary points, and other borders never teleport", () =>
    {
        var portal = RightPortal();
        Equal(false, PortalGeometry.TryMap(portal, new(106, 500), new(105, 500), out _));
        Equal(false, PortalGeometry.TryMap(portal, new(1812, 60), new(1812, 59), out _));
        Equal(false, PortalGeometry.TryMap(portal, new(1850, 500), new(1851, 500), out _));
        Equal(false, PortalGeometry.TryMap(portal, new(1812, 500), new(1812, 500), out _));
        Equal(false, PortalGeometry.TryMap(portal, new(3839, 500), new(3840, 500), out _));
    }),
    ("Existing arrival on the destination needs no warp", () =>
    {
        var portal = RightPortal();
        Equal(false, PortalGeometry.TryMap(portal, new(1812, 500), new(1921, 500), out _));
        Equal(false, PortalGeometry.TryMap(portal, new(1921, 500), new(1811, 500), out _));
    }),
    ("A mapped point and a stationary injected event cannot oscillate", () =>
    {
        var portal = RightPortal();
        Equal(true, PortalGeometry.TryMap(portal, new(1812, 500), new(1813, 500), out var destination));
        Equal(false, PortalGeometry.TryMap(portal, destination, destination, out _));
        Equal(false, PortalGeometry.TryMap(portal, destination, new(destination.X + 1, destination.Y), out _));
    }),
    ("Portals reject distant neighbors and invalid visible bounds", () =>
    {
        Equal<CursorPortal?>(null, CursorPortal.TryCreate(new(0, 0, 1920, 1080), new(106, 60, 1707, 960),
            new(2300, 0, 1920, 1080), MonitorSide.Right));
        Equal<CursorPortal?>(null, CursorPortal.TryCreate(new(0, 0, 1920, 1080), new(-1, 60, 1707, 960),
            new(1920, 0, 1920, 1080), MonitorSide.Right));
    }),
    ("Contiguous GPU-centered desktops need no portal", () =>
    {
        var portal = CursorPortal.TryCreate(new(0, 0, 1707, 960), new(0, 0, 1707, 960),
            new(1707, 0, 1920, 1080), MonitorSide.Right)!;
        Equal(false, PortalGeometry.TryMap(portal, new(1706, 500), new(1707, 500), out _));
    })
};

var failures = 0;
foreach (var (name, run) in cases)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} checks passed.");
return failures == 0 ? 0 : 1;

// What a 1080p laptop or monitor driver typically lists when GPU scaling is on.
static DisplayMode[] Listed1080p144() =>
[
    new(1280, 720, 144), new(1600, 900, 144), new(1768, 992, 144), new(1920, 1080, 144),
    new(1280, 1024, 144), new(1680, 1050, 144), new(1768, 992, 60), new(1920, 1080, 60)
];
static DisplaySizePlan Plan() => ScreenSizeCalculator.Calculate(1920, 1080, 27, 24);
static MonitorLayout Monitor(string id, int x, int y, int width, int height) => new(id, new(x, y, width, height));
static CursorPortal RightPortal() => CursorPortal.TryCreate(new(0, 0, 1920, 1080),
    new(106, 60, 1707, 960), new(1920, 0, 1920, 1080), MonitorSide.Right)!;
static void Required(object? value)
{
    if (value is null) throw new InvalidOperationException("Expected a result, received null.");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}
static void Near(double expected, double actual, double tolerance)
{
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"Expected {expected} ± {tolerance}, received {actual}.");
}
static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
