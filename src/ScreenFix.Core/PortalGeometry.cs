namespace ScreenFix.Core;

/// <summary>
/// A bidirectional cursor shortcut across a horizontal black border. SourceNativeBounds describe
/// the original Windows monitor. SourceVisibleBounds describe the centered image inside it.
/// This is useful only when borders occupy desktop coordinates; GPU-centered lower resolutions
/// put borders outside the desktop, where normal Windows cursor movement already skips them.
/// </summary>
public sealed record CursorPortal
{
    private CursorPortal(PixelRect native, PixelRect visible, PixelRect neighbor, MonitorSide side)
    {
        SourceNativeBounds = native;
        SourceVisibleBounds = visible;
        NeighborBounds = neighbor;
        Side = side;
    }

    public PixelRect SourceNativeBounds { get; }
    public PixelRect SourceVisibleBounds { get; }
    public PixelRect NeighborBounds { get; }
    public MonitorSide Side { get; }

    public static CursorPortal? TryCreate(
        PixelRect sourceNativeBounds,
        PixelRect sourceVisibleBounds,
        PixelRect neighborBounds,
        MonitorSide side,
        int maximumNativeGapPixels = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNativeGapPixels);
        if (!sourceNativeBounds.IsValid || !sourceVisibleBounds.IsValid || !neighborBounds.IsValid
            || !sourceNativeBounds.Contains(sourceVisibleBounds))
            return null;
        var gap = side == MonitorSide.Right
            ? (long)neighborBounds.Left - sourceNativeBounds.Right
            : (long)sourceNativeBounds.Left - neighborBounds.Right;
        if (gap < 0 || gap > maximumNativeGapPixels)
            return null;
        if (Math.Max(sourceVisibleBounds.Top, neighborBounds.Top)
            >= Math.Min(sourceVisibleBounds.Bottom, neighborBounds.Bottom))
            return null;
        return new(sourceNativeBounds, sourceVisibleBounds, neighborBounds, side);
    }
}

public static class PortalGeometry
{
    /// <summary>
    /// Maps an actual transition from one screen into the intervening border. Both coordinates
    /// must come from successive pointer events. Pointer positions already on either screen,
    /// distant movement, and movement outside the shared vertical span do not trigger a warp.
    /// The destination is inset by one pixel to prevent an immediate reverse transition.
    /// Preserve Y: a portal corresponds only to the physical vertical interval the displays share.
    /// </summary>
    public static bool TryMap(
        CursorPortal portal, PixelPoint previous, PixelPoint current, out PixelPoint destination)
    {
        ArgumentNullException.ThrowIfNull(portal);
        destination = current;
        var source = portal.SourceVisibleBounds;
        var neighbor = portal.NeighborBounds;
        var sharedTop = Math.Max(source.Top, neighbor.Top);
        var sharedBottom = Math.Min(source.Bottom, neighbor.Bottom);
        if (current.Y < sharedTop || current.Y >= sharedBottom)
            return false;

        var goingOut = source.Contains(previous);
        var comingBack = neighbor.Contains(previous);
        if (!goingOut && !comingBack)
            return false;

        // Require the pointer to have entered the black gap; never warp an unrelated edge
        // or a motion that has already arrived at the other screen in one event.
        var inGap = portal.Side == MonitorSide.Right
            ? current.X >= source.Right && current.X < neighbor.Left
            : current.X >= neighbor.Right && current.X < source.Left;
        if (!inGap)
            return false;

        if (portal.Side == MonitorSide.Right)
        {
            if (goingOut && current.X > previous.X)
                destination = new(neighbor.Left + Math.Min(1, neighbor.Width - 1), current.Y);
            else if (comingBack && current.X < previous.X)
                destination = new(source.Right - 1 - Math.Min(1, source.Width - 1), current.Y);
            else
                return false;
        }
        else
        {
            if (goingOut && current.X < previous.X)
                destination = new(neighbor.Right - 1 - Math.Min(1, neighbor.Width - 1), current.Y);
            else if (comingBack && current.X > previous.X)
                destination = new(source.Left + Math.Min(1, source.Width - 1), current.Y);
            else
                return false;
        }
        return true;
    }
}
