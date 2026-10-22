namespace ScreenFix.Core;

public readonly record struct DisplayMode(int Width, int Height, double RefreshRateHz);

public sealed record ModeMatch(
    DisplayMode Mode,
    bool IsExactSize,
    bool MeetsPreferredRefreshRate,
    double EffectiveDiagonalInches,
    double RelativeSizeError,
    double RelativeAspectError);

/// <summary>One centered size to offer or try, with the diagonal it produces on the physical monitor.</summary>
public sealed record SizeCandidate(
    int Width,
    int Height,
    double EffectiveDiagonalInches,
    double DiagonalErrorInches,
    bool IsRequestedSize,
    bool IsDriverListed);

public static class ModeSelector
{
    /// <summary>
    /// Finds a driver-advertised exact or close mode. Null means a custom mode is required.
    /// Refresh rate takes precedence among acceptable sizes: avoid silently reducing 240 Hz.
    /// Callers must show an approximate size or a refresh-rate shortfall before applying it.
    /// </summary>
    public static ModeMatch? FindBest(
        DisplaySizePlan plan,
        IEnumerable<DisplayMode> modes,
        double preferredRefreshRateHz = 240,
        double maximumRelativeSizeError = 0.02,
        double maximumRelativeAspectError = 0.005)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(modes);
        if (!double.IsFinite(preferredRefreshRateHz) || preferredRefreshRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(preferredRefreshRateHz));
        ValidateTolerance(maximumRelativeSizeError, nameof(maximumRelativeSizeError));
        ValidateTolerance(maximumRelativeAspectError, nameof(maximumRelativeAspectError));

        var nativeAspect = (double)plan.NativeWidth / plan.NativeHeight;
        return modes
            .Where(mode => mode.Width > 0 && mode.Height > 0
                && mode.Width <= plan.NativeWidth && mode.Height <= plan.NativeHeight
                && double.IsFinite(mode.RefreshRateHz) && mode.RefreshRateHz > 0)
            .Select(mode =>
            {
                var sizeError = Math.Max(
                    Math.Abs((double)mode.Width / plan.VisibleWidth - 1),
                    Math.Abs((double)mode.Height / plan.VisibleHeight - 1));
                var aspectError = Math.Abs(((double)mode.Width / mode.Height) / nativeAspect - 1);
                return new ModeMatch(mode,
                    mode.Width == plan.VisibleWidth && mode.Height == plan.VisibleHeight,
                    mode.RefreshRateHz >= preferredRefreshRateHz - 0.5,
                    ScreenSizeCalculator.DiagonalForResolution(plan.NativeWidth, plan.NativeHeight,
                        plan.ActualDiagonalInches, mode.Width, mode.Height), sizeError, aspectError);
            })
            // Rounding one dimension by half a pixel must not make an exact requested mode invalid.
            .Where(match => match.IsExactSize ||
                (match.RelativeSizeError <= maximumRelativeSizeError
                 && match.RelativeAspectError <= maximumRelativeAspectError))
            .OrderByDescending(match => match.MeetsPreferredRefreshRate)
            .ThenByDescending(match => match.IsExactSize)
            .ThenBy(match => match.RelativeSizeError)
            .ThenBy(match => Math.Abs(match.Mode.RefreshRateHz - preferredRefreshRateHz))
            .ThenByDescending(match => match.Mode.RefreshRateHz)
            .FirstOrDefault();
    }

    /// <summary>
    /// Lists centered image sizes a driver can realistically accept, nearest to the requested
    /// diagonal first. Drivers only take source sizes from their own mode list, so an arbitrary
    /// computed size (for example 1704 × 960) is usually rejected. Every returned size keeps the
    /// native proportions and never equals the native size. The calculated size is always
    /// included and flagged when the driver does not list it, so callers can offer it explicitly.
    /// </summary>
    public static IReadOnlyList<SizeCandidate> RankSizes(
        int nativeWidth,
        int nativeHeight,
        double actualDiagonalInches,
        double targetDiagonalInches,
        IEnumerable<DisplayMode> modes,
        double refreshRateHz,
        int maximumCount = 6,
        double refreshToleranceHz = 1.0,
        double maximumRelativeAspectError = 0.01,
        double maximumRelativeDiagonalError = 0.25)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        if (!double.IsFinite(refreshRateHz) || refreshRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(refreshRateHz));
        if (!double.IsFinite(refreshToleranceHz) || refreshToleranceHz < 0)
            throw new ArgumentOutOfRangeException(nameof(refreshToleranceHz));
        ValidateTolerance(maximumRelativeAspectError, nameof(maximumRelativeAspectError));
        ValidateTolerance(maximumRelativeDiagonalError, nameof(maximumRelativeDiagonalError));

        var plan = ScreenSizeCalculator.CalculateAligned(
            nativeWidth, nativeHeight, actualDiagonalInches, targetDiagonalInches);
        if (plan.IsFullSize) return [];

        var nativeAspect = (double)nativeWidth / nativeHeight;
        var atRefreshRate = modes
            .Where(mode => mode.Width >= 320 && mode.Height >= 200
                && mode.Width <= nativeWidth && mode.Height <= nativeHeight
                && !(mode.Width == nativeWidth && mode.Height == nativeHeight)
                && double.IsFinite(mode.RefreshRateHz)
                && Math.Abs(mode.RefreshRateHz - refreshRateHz) <= refreshToleranceHz)
            .Select(mode => (mode.Width, mode.Height))
            .Distinct()
            .ToList();

        SizeCandidate Make(int width, int height, bool listed)
        {
            var diagonal = ScreenSizeCalculator.DiagonalForResolution(
                nativeWidth, nativeHeight, actualDiagonalInches, width, height);
            return new SizeCandidate(width, height, diagonal, diagonal - targetDiagonalInches,
                width == plan.VisibleWidth && height == plan.VisibleHeight, listed);
        }

        var result = atRefreshRate
            .Where(size => Math.Abs(((double)size.Width / size.Height) / nativeAspect - 1) <= maximumRelativeAspectError)
            .Select(size => Make(size.Width, size.Height, true))
            .Where(candidate => Math.Abs(candidate.DiagonalErrorInches)
                <= targetDiagonalInches * maximumRelativeDiagonalError)
            .OrderBy(candidate => Math.Abs(candidate.DiagonalErrorInches))
            .ThenByDescending(candidate => candidate.Width)
            .Take(maximumCount)
            .ToList();

        if (!result.Any(candidate => candidate.IsRequestedSize))
            result.Add(Make(plan.VisibleWidth, plan.VisibleHeight,
                atRefreshRate.Contains((plan.VisibleWidth, plan.VisibleHeight))));
        return result
            .OrderBy(candidate => Math.Abs(candidate.DiagonalErrorInches))
            .ThenByDescending(candidate => candidate.Width)
            .ToList();
    }

    /// <summary>
    /// The size to use when the user did not choose one: the nearest size the driver lists,
    /// otherwise the calculated one. Null when no resizing is needed.
    /// </summary>
    public static SizeCandidate? PickAutomatic(IReadOnlyList<SizeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.FirstOrDefault(candidate => candidate.IsDriverListed) ?? candidates.FirstOrDefault();
    }

    private static void ValidateTolerance(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(name);
    }
}
