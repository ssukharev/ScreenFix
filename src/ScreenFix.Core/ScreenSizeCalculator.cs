namespace ScreenFix.Core;

/// <summary>A requested physical size, expressed as a centered display resolution.</summary>
public sealed record DisplaySizePlan(
    int NativeWidth,
    int NativeHeight,
    int VisibleWidth,
    int VisibleHeight,
    double ActualDiagonalInches,
    double TargetDiagonalInches,
    double EffectiveDiagonalInches,
    double Scale)
{
    public bool IsFullSize => VisibleWidth == NativeWidth && VisibleHeight == NativeHeight;
    public double DiagonalErrorInches => EffectiveDiagonalInches - TargetDiagonalInches;
    public PixelRect CenteredBounds => new(
        (NativeWidth - VisibleWidth) / 2,
        (NativeHeight - VisibleHeight) / 2,
        VisibleWidth,
        VisibleHeight);
}

public static class ScreenSizeCalculator
{
    // Explicitly entered by the user: EDID measurements are frequently rounded or missing.
    public const double MinimumDiagonalInches = 1;
    public const double MaximumDiagonalInches = 250;

    public static DisplaySizePlan Calculate(
        int nativeWidth,
        int nativeHeight,
        double actualDiagonalInches,
        double targetDiagonalInches)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeHeight);
        ValidateDiagonal(actualDiagonalInches, nameof(actualDiagonalInches));
        ValidateDiagonal(targetDiagonalInches, nameof(targetDiagonalInches));
        if (targetDiagonalInches > actualDiagonalInches)
            throw new ArgumentOutOfRangeException(nameof(targetDiagonalInches),
                "The desired diagonal cannot exceed the physical monitor diagonal.");

        var scale = targetDiagonalInches / actualDiagonalInches;
        var width = Math.Clamp((int)Math.Round(nativeWidth * scale, MidpointRounding.AwayFromZero), 1, nativeWidth);
        var height = Math.Clamp((int)Math.Round(nativeHeight * scale, MidpointRounding.AwayFromZero), 1, nativeHeight);
        var effective = DiagonalForResolution(nativeWidth, nativeHeight, actualDiagonalInches, width, height);
        return new(nativeWidth, nativeHeight, width, height, actualDiagonalInches,
            targetDiagonalInches, effective, scale);
    }

    /// <summary>
    /// Computes a driver-friendly approximation by reducing the rounded width/height to a
    /// multiple of the requested alignment. Full-size requests preserve the exact native
    /// resolution, even when it is unaligned. Dimensions smaller than one alignment block
    /// remain unchanged rather than becoming zero. Alignment does not guarantee driver support;
    /// callers must display EffectiveDiagonalInches as approximate and explain the rounding.
    /// </summary>
    public static DisplaySizePlan CalculateAligned(
        int nativeWidth,
        int nativeHeight,
        double actualDiagonalInches,
        double targetDiagonalInches,
        int widthAlignment = 8,
        int heightAlignment = 2)
    {
        ValidateAlignment(widthAlignment, nameof(widthAlignment));
        ValidateAlignment(heightAlignment, nameof(heightAlignment));
        var precise = Calculate(nativeWidth, nativeHeight, actualDiagonalInches, targetDiagonalInches);
        if (precise.IsFullSize) return precise;
        var width = AlignDown(precise.VisibleWidth, widthAlignment);
        var height = AlignDown(precise.VisibleHeight, heightAlignment);
        return precise with
        {
            VisibleWidth = width,
            VisibleHeight = height,
            EffectiveDiagonalInches = DiagonalForResolution(
                nativeWidth, nativeHeight, actualDiagonalInches, width, height)
        };
    }

    public static double DiagonalForResolution(
        int nativeWidth, int nativeHeight, double actualDiagonalInches, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ValidateDiagonal(actualDiagonalInches, nameof(actualDiagonalInches));
        return actualDiagonalInches * Math.Sqrt((double)width * width + (double)height * height)
            / Math.Sqrt((double)nativeWidth * nativeWidth + (double)nativeHeight * nativeHeight);
    }

    private static void ValidateDiagonal(double value, string name)
    {
        if (!double.IsFinite(value) || value < MinimumDiagonalInches || value > MaximumDiagonalInches)
            throw new ArgumentOutOfRangeException(name,
                $"The diagonal must be between {MinimumDiagonalInches} and {MaximumDiagonalInches} inches.");
    }

    private static int AlignDown(int value, int alignment) =>
        value >= alignment ? value - value % alignment : value;

    private static void ValidateAlignment(int value, string name)
    {
        if (value < 1 || value > 1024)
            throw new ArgumentOutOfRangeException(name, "Pixel alignment must be between 1 and 1024.");
    }
}
