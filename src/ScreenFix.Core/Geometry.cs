namespace ScreenFix.Core;

public readonly record struct PixelPoint(int X, int Y);

/// <summary>Windows desktop coordinates. Right and bottom are exclusive, including at negative coordinates.</summary>
public readonly record struct PixelRect
{
    public PixelRect(int left, int top, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ = checked(left + width);
        _ = checked(top + height);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsValid => Width > 0 && Height > 0;
    public bool Contains(PixelPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    public bool Contains(PixelRect rect) => rect.IsValid &&
        rect.Left >= Left && rect.Right <= Right && rect.Top >= Top && rect.Bottom <= Bottom;
    public bool Overlaps(PixelRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

public sealed record MonitorLayout(string Id, PixelRect Bounds);
public enum MonitorSide { Left, Right }

public static class NeighborSelector
{
    /// <summary>Only horizontal neighbors sharing a nonempty vertical interval qualify.</summary>
    public static MonitorLayout? FindHorizontalNeighbor(
        MonitorLayout source,
        IEnumerable<MonitorLayout> monitors,
        MonitorSide side,
        int maximumGapPixels = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumGapPixels);
        if (!source.Bounds.IsValid)
            throw new ArgumentException("Source monitor bounds must have positive dimensions.", nameof(source));
        return monitors
            .Where(m => m.Id != source.Id && m.Bounds.IsValid)
            .Select(m => new
            {
                Monitor = m,
                Gap = side == MonitorSide.Right
                    ? (long)m.Bounds.Left - source.Bounds.Right
                    : (long)source.Bounds.Left - m.Bounds.Right,
                Overlap = Math.Min(source.Bounds.Bottom, m.Bounds.Bottom)
                    - (long)Math.Max(source.Bounds.Top, m.Bounds.Top)
            })
            .Where(item => item.Gap >= 0 && item.Gap <= maximumGapPixels && item.Overlap > 0)
            .OrderBy(item => item.Gap)
            .ThenByDescending(item => item.Overlap)
            .ThenBy(item => item.Monitor.Id, StringComparer.Ordinal)
            .Select(item => item.Monitor)
            .FirstOrDefault();
    }
}

public sealed record DisplayLayoutPlan(IReadOnlyList<MonitorLayout> Monitors, bool HasOverlaps);

public static class DisplayLayoutPlanner
{
    /// <summary>
    /// Shrinks the selected monitor while keeping its logical origin. Monitors wholly to its
    /// right/below move with those edges, closing the former desktop gaps. The original monitor
    /// rectangles must be saved separately for restoration. Complex arrangements can conflict;
    /// callers must not apply a plan whose HasOverlaps is true.
    /// </summary>
    public static DisplayLayoutPlan ResizeMonitor(
        IReadOnlyList<MonitorLayout> monitors, string monitorId, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (monitors.Any(m => !m.Bounds.IsValid))
            throw new ArgumentException("Monitor bounds must have positive dimensions.", nameof(monitors));
        if (monitors.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != monitors.Count)
            throw new ArgumentException("Monitor identifiers must be unique.", nameof(monitors));
        var source = monitors.SingleOrDefault(m => m.Id == monitorId)
            ?? throw new ArgumentException("The selected monitor is missing.", nameof(monitorId));
        if (width > source.Bounds.Width || height > source.Bounds.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The new dimensions cannot exceed the source dimensions.");

        var shrinkX = source.Bounds.Width - width;
        var shrinkY = source.Bounds.Height - height;
        var result = monitors.Select(m =>
        {
            if (m.Id == monitorId)
                return m with { Bounds = new(source.Bounds.Left, source.Bounds.Top, width, height) };
            var x = m.Bounds.Left >= source.Bounds.Right ? checked(m.Bounds.Left - shrinkX) : m.Bounds.Left;
            var y = m.Bounds.Top >= source.Bounds.Bottom ? checked(m.Bounds.Top - shrinkY) : m.Bounds.Top;
            return m with { Bounds = new(x, y, m.Bounds.Width, m.Bounds.Height) };
        }).ToArray();
        var overlaps = false;
        for (var i = 0; i < result.Length; i++)
            for (var j = i + 1; j < result.Length; j++)
                overlaps |= result[i].Bounds.Overlaps(result[j].Bounds);
        return new(Array.AsReadOnly(result), overlaps);
    }
}
