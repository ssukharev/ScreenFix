using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScreenFix.Windows.Native;
using static ScreenFix.Windows.Native.DisplayNative;

namespace ScreenFix.Windows.Services;

/// <summary>
/// Applies temporary, exact CCD configurations. No custom timings, registry writes,
/// best-mode substitutions, GPU hooks, or game process access are used.
/// Drivers accept only source sizes from their own mode list, and different drivers accept
/// the request through different Windows entry points, so each size is tried with several
/// methods. Every attempt is validated first, verified afterwards and rolled back on any doubt.
/// </summary>
public sealed class DisplayService
{
    // The recovery deadline written before applying is longer than this search.
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(40);

    private const string DriverHint =
        " Проверьте в панели видеокарты «Без масштабирования» и масштабирование на стороне ГП, а монитор подключите к порту, " +
        "который работает от дискретной видеокарты. Другой вариант — создать в драйвере пользовательское разрешение нужного размера.";

    private const string ScalingHint =
        " Драйвер принимает размер, но сам включает растягивание вместо центрирования. В панели NVIDIA откройте «Регулировка размера и " +
        "положения рабочего стола» для этого монитора, выберите «Без масштабирования», масштабирование на стороне ГП и отметьте " +
        "«Переопределить режим масштабирования, заданный играми и программами». На встроенной графике Intel выберите масштабирование «Центр». " +
        "Если такого пункта для монитора нет, он подключён через другую видеокарту.";

    private const string RestoredSuffix = " Исходная конфигурация восстановлена.";
    private const string ScalingReplaced = "драйвер заменил центрирование на";

    private readonly object _sync = new();

    private enum Method { CcdVirtual, CcdClassic, Legacy }

    public DisplayService()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Управление мониторами доступно только в Windows.");
        AssertNativeLayouts();
    }

    public IReadOnlyList<DisplayMonitor> GetDisplays()
    {
        lock (_sync)
            return Describe(Query());
    }

    public DisplaySnapshot CaptureSnapshot()
    {
        lock (_sync)
            return Snapshot(Query());
    }

    /// <summary>Asks Windows whether each size would be accepted with the existing signal and
    /// refresh rate, using every method, without changing the screen.</summary>
    public IReadOnlyList<ProbeResult> Probe(DisplayMonitor monitor, IReadOnlyList<DisplaySize> sizes, bool reconnectNeighbors = true)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(sizes);
        lock (_sync)
        {
            Method[] methods;
            try { methods = MethodsFor(Query()); }
            catch (Exception exception) when (IsExpected(exception))
            {
                return [new ProbeResult(default, "—", false, 0, exception.Message)];
            }
            var results = new List<ProbeResult>();
            foreach (var size in sizes)
                foreach (var method in methods)
                    results.Add(ProbeOne(monitor, size, method, reconnectNeighbors));
            return results;
        }
    }

    /// <summary>The caller must persist CaptureSnapshot() before invoking this. Sizes are tried in
    /// the given order, each with every method, until one is accepted and verified. Nothing is
    /// changed unless an attempt is both validated by Windows and confirmed afterwards.</summary>
    public DisplayApplyResult ApplyCentered(DisplayMonitor monitor, IReadOnlyList<DisplaySize> sizes, bool reconnectNeighbors = true)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(sizes);
        lock (_sync)
        {
            if (sizes.Count == 0)
                return Result(false, "Не выбран размер изображения.", unchanged: true);

            Method[] methods;
            try { methods = MethodsFor(Query()); }
            catch (Exception exception) when (IsExpected(exception))
            {
                return Result(false, exception.Message, unchanged: true);
            }

            var clock = Stopwatch.StartNew();
            var attempts = new List<Attempt>();
            var log = new StringBuilder();
            var timedOut = false;
            foreach (var size in sizes)
            {
                foreach (var method in methods)
                {
                    if (clock.Elapsed > SearchBudget) { timedOut = true; break; }
                    var attempt = (method == Method.Legacy
                        ? TryLegacy(monitor, size, reconnectNeighbors)
                        : TryCcd(monitor, size, method == Method.CcdVirtual, reconnectNeighbors)) with { Size = size, Via = method };
                    log.AppendLine(attempt.Describe());
                    if (attempt.Final is { } final)
                        return final with { Diagnostics = log.ToString() + final.Diagnostics };
                    attempts.Add(attempt);
                }
                if (timedOut) break;
            }

            // Some drivers keep the requested size and frequency but report another scaling mode than the one
            // asked for, and they may or may not actually center. If that was the only difference, repeat the
            // first such attempt and leave the judgement to the user, who confirms by looking at the screen.
            if (!timedOut && clock.Elapsed <= SearchBudget && attempts.FirstOrDefault(a => a.ScalingOnly) is { } scalingOnly)
            {
                var again = (scalingOnly.Via == Method.Legacy
                    ? TryLegacy(monitor, scalingOnly.Size, reconnectNeighbors, acceptScaling: true)
                    : TryCcd(monitor, scalingOnly.Size, scalingOnly.Via == Method.CcdVirtual, reconnectNeighbors, acceptScaling: true))
                    with { Size = scalingOnly.Size, Via = scalingOnly.Via };
                log.AppendLine("Повтор без проверки режима масштабирования: " + again.Describe());
                if (again.Final is { } accepted)
                    return accepted with { Diagnostics = log.ToString() + accepted.Diagnostics };
                attempts.Add(again);
            }
            return Result(false, FailureMessage(sizes, attempts, timedOut), error: attempts.Select(a => a.Code).FirstOrDefault(c => c != 0),
                diagnostics: log.ToString(), unchanged: true);
        }
    }

    public DisplayApplyResult Restore(DisplaySnapshot snapshot)
    {
        lock (_sync)
        {
            try
            {
                var saved = FromSnapshot(snapshot);
                // A recovery file is session-specific. Refuse to send stale LUIDs
                // to a different display or to replace a newly connected topology.
                ValidateConnectedIdentities(snapshot, Query());
                var error = Set(saved, Validate);
                if (error != 0)
                    return Result(false, $"Исходная конфигурация сейчас недоступна. {Win32Message(error)}", error: error, needsRecovery: true);
                error = Set(saved, Apply);
                if (error != 0)
                    return Result(false, $"Не удалось восстановить исходную конфигурацию. {Win32Message(error)}", error: error, needsRecovery: true);
                var difference = Difference(saved, Query());
                return difference is null
                    ? Result(true, "Исходные разрешения, частоты и расположение мониторов восстановлены.")
                    : Result(false, $"Windows изменила исходную конфигурацию при восстановлении: {difference}", needsRecovery: true);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return Result(false, exception.Message, needsRecovery: true);
            }
        }
    }

    /// <summary>Read-only check for WM_DISPLAYCHANGE / DisplaySettingsChanged.
    /// A fullscreen game's mode changes must be reported, not fought in a loop.</summary>
    public DisplayApplyResult CheckCentered(DisplayMonitor monitor, int width, int height, double expectedRefreshRateHz)
    {
        lock (_sync)
        {
            try
            {
                var current = FindMonitor(Query(), monitor);
                if (current is null)
                    return Result(false, "Выбранный монитор отключён или его идентификатор изменился.");
                if (current.CurrentWidth != width || current.CurrentHeight != height ||
                    current.SignalWidth != monitor.SignalWidth || current.SignalHeight != monitor.SignalHeight ||
                    current.Rotation != monitor.Rotation || current.X != monitor.X || current.Y != monitor.Y ||
                    current.Scaling != monitor.Scaling || Math.Abs(current.RefreshRateHz - expectedRefreshRateHz) > 0.05)
                    return Result(false,
                        "Игра или видеодрайвер изменили режим экрана. Уменьшение больше не подтверждено; повторное включение доступно после освобождения режима игрой.", current);
                return Result(true, "Размер, центрирование и частота сохранены.", current);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return Result(false, exception.Message);
            }
        }
    }

    private static Method[] MethodsFor(Configuration current)
    {
        // Without virtual paths the classic query returns the same configuration: skip the duplicate.
        if (current.Paths.Any(p => p.IsVirtual))
            return [Method.CcdVirtual, Method.CcdClassic, Method.Legacy];
        return [Method.CcdVirtual, Method.Legacy];
    }

    private static string Name(Method method) => method switch
    {
        Method.CcdVirtual => "CCD",
        Method.CcdClassic => "CCD (классический)",
        _ => "ChangeDisplaySettings"
    };

    private static string Label(DisplaySize size, Method method) => $"{size.Width} × {size.Height} · {Name(method)}";

    private static Attempt TryCcd(DisplayMonitor monitor, DisplaySize size, bool virtualAware, bool reconnectNeighbors, bool acceptScaling = false)
    {
        var label = Label(size, virtualAware ? Method.CcdVirtual : Method.CcdClassic);
        Configuration? before = null;
        var attempted = false;
        try
        {
            before = Query(virtualAware);
            var proposed = CreateCentered(before, monitor, size.Width, size.Height, reconnectNeighbors);
            var diagnostic = DescribeRequest(before, proposed, monitor);
            var validation = Set(proposed, Validate);
            if (validation != 0)
                return Attempt.Rejected(label, Explain(validation), validation);

            // ALLOW_CHANGES and SAVE_TO_DATABASE are deliberately absent:
            // neither a 60 Hz fallback nor a persistent mode change is allowed.
            attempted = true;
            var error = Set(proposed, Apply);
            if (error != 0)
                return Attempt.FromRollBack(label, RollBack(before, $"Windows не применила режим. {Explain(error)}", error, diagnostic));

            var actual = Query(virtualAware);
            var difference = Compare(proposed, actual, acceptScaling);
            if (difference is not null)
                return Attempt.FromRollBack(label, RollBack(before,
                    $"Драйвер не сохранил запрошенное центрирование, размер или частоту: {difference}", 0, diagnostic))
                    with { ScalingOnly = !acceptScaling && Compare(proposed, actual, true) is null };

            var applied = FindMonitor(actual, monitor);
            if (applied is null)
                return Attempt.FromRollBack(label, RollBack(before, "После переключения выбранный монитор не найден.", 0, diagnostic));

            return Attempt.Done(label, Annotate(Result(true,
                $"Включено {size.Width} × {size.Height} по центру · {applied.RefreshRateHz:0.##} Гц.", applied,
                diagnostics: diagnostic + " Фактическая конфигурация проверена после переключения."), applied, acceptScaling));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return attempted && before is not null
                ? Attempt.FromRollBack(label, RollBack(before, exception.Message, 0, string.Empty))
                : Attempt.Rejected(label, exception.Message);
        }
    }

    /// <summary>The classic ChangeDisplaySettingsEx entry point with DMDFO_CENTER. Some drivers accept a
    /// centered size through it while rejecting the equivalent CCD structures. The mode is dynamic
    /// (nothing is written to the registry) and the full CCD snapshot remains the way back.</summary>
    private static Attempt TryLegacy(DisplayMonitor monitor, DisplaySize size, bool reconnectNeighbors, bool acceptScaling = false)
    {
        var label = Label(size, Method.Legacy);
        Configuration? before = null;
        var attempted = false;
        try
        {
            before = Query();
            // Shares the CCD preconditions: extended desktop, landscape, size within the physical signal.
            _ = CreateCentered(before, monitor, size.Width, size.Height, reconnectNeighbors);
            if (!TryBuildLegacyRequest(monitor.DeviceName, size, out var request))
                return Attempt.Rejected(label, "Не удалось прочитать текущий режим монитора.");
            var test = ChangeDisplaySettingsExW(monitor.DeviceName, ref request, 0, CdsTest, 0);
            if (test != 0)
                return Attempt.Rejected(label, DescribeDispChange(test), test);

            attempted = true;
            var change = ChangeDisplaySettingsExW(monitor.DeviceName, ref request, 0, 0, 0);
            if (change != 0)
                return Attempt.FromRollBack(label, RollBack(before, "Windows не применила режим. " + DescribeDispChange(change), change, string.Empty));

            var afterChange = Query();
            var difference = VerifyApplied(before, afterChange, monitor, size, acceptScaling);
            if (difference is not null)
                return Attempt.FromRollBack(label, RollBack(before,
                    "Драйвер не сохранил запрошенное центрирование, размер или частоту: " + difference, 0, string.Empty))
                    with { ScalingOnly = !acceptScaling && VerifyApplied(before, afterChange, monitor, size, true) is null };

            var note = reconnectNeighbors ? ReconnectDesktop(before, monitor, size) : null;
            var applied = FindMonitor(Query(), monitor);
            if (applied is null)
                return Attempt.FromRollBack(label, RollBack(before, "После переключения выбранный монитор не найден.", 0, string.Empty));

            return Attempt.Done(label, Annotate(Result(true,
                $"Включено {size.Width} × {size.Height} по центру · {applied.RefreshRateHz:0.##} Гц.", applied,
                diagnostics: "Применено через ChangeDisplaySettings; результат проверен. " + note), applied, acceptScaling));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return attempted && before is not null
                ? Attempt.FromRollBack(label, RollBack(before, exception.Message, 0, string.Empty))
                : Attempt.Rejected(label, exception.Message);
        }
    }

    private static ProbeResult ProbeOne(DisplayMonitor monitor, DisplaySize size, Method method, bool reconnectNeighbors)
    {
        var name = Name(method);
        try
        {
            var before = Query(method != Method.CcdClassic);
            var proposed = CreateCentered(before, monitor, size.Width, size.Height, reconnectNeighbors);
            if (method == Method.Legacy)
            {
                if (!TryBuildLegacyRequest(monitor.DeviceName, size, out var request))
                    return new ProbeResult(size, name, false, 0, "Не удалось прочитать текущий режим монитора.");
                var test = ChangeDisplaySettingsExW(monitor.DeviceName, ref request, 0, CdsTest, 0);
                return new ProbeResult(size, name, test == 0, test, DescribeDispChange(test));
            }
            var error = Set(proposed, Validate);
            return new ProbeResult(size, name, error == 0, error, error == 0 ? "принято" : Explain(error));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new ProbeResult(size, name, false, 0, exception.Message);
        }
    }

    private static bool TryBuildLegacyRequest(string deviceName, DisplaySize size, out DevMode request)
    {
        var current = DevMode.Create();
        request = current;
        if (!EnumDisplaySettingsExW(deviceName, EnumCurrentSettings, ref current, 0))
            return false;
        request = current;
        request.PelsWidth = checked((uint)size.Width);
        request.PelsHeight = checked((uint)size.Height);
        request.DisplayFixedOutput = FixedOutputCenter;
        request.DriverExtra = 0;
        // The current position and frequency are passed back unchanged.
        request.Fields = DmPosition | DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency | DmDisplayFixedOutput;
        return true;
    }

    /// <summary>After a classic mode change Windows keeps the neighbours where they were, which leaves a
    /// gap. Moves them next to the reduced screen with a position-only CCD update. A failure here
    /// does not undo the reduction: the cursor simply cannot cross until the layout is adjusted.</summary>
    private static string? ReconnectDesktop(Configuration before, DisplayMonitor monitor, DisplaySize size)
    {
        try
        {
            var after = Query();
            var adjusted = after.Copy();
            var path = before.Paths.First(p => Matches(p, monitor));
            var selectedIndex = RequireIndex(before, path.SourceIndex, 1);
            CloseDesktopGaps(before, adjusted, selectedIndex, before.Modes[selectedIndex].Source, size.Width, size.Height);
            if (ToBytes(adjusted.Modes).AsSpan().SequenceEqual(ToBytes(after.Modes)))
                return null;
            var error = Set(adjusted, Validate);
            if (error == 0) error = Set(adjusted, Apply);
            return error == 0
                ? null
                : "Соседний монитор не придвинут: " + Explain(error) + " Расположите мониторы вплотную в настройках Windows.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return "Соседний монитор не придвинут: " + exception.Message;
        }
    }

    private static string FailureMessage(IReadOnlyList<DisplaySize> sizes, List<Attempt> attempts, bool timedOut)
    {
        var codes = attempts.Select(a => a.Code).Where(code => code != 0).Distinct().ToList();
        var reasons = attempts.Where(a => a.Code == 0 && !string.IsNullOrWhiteSpace(a.Failure))
            .Select(a => a.Failure!.Trim()).Distinct().ToList();
        var scalingReplaced = reasons.Any(reason => reason.Contains(ScalingReplaced, StringComparison.Ordinal));
        var rolledBack = attempts.Any(a => a.RolledBack);
        var subject = sizes.Count == 1
            ? $"размер {sizes[0].Width} × {sizes[0].Height}"
            : "размеры " + string.Join(", ", sizes.Select(s => $"{s.Width} × {s.Height}"));
        var builder = new StringBuilder();
        if (scalingReplaced)
        {
            // The driver accepted the mode, but it does not keep centering: not a refusal of the size.
            builder.Append($"Драйвер принял {subject}, но не оставил центрирование.");
            builder.Append(' ').Append(Sentence(reasons.First(reason => reason.Contains(ScalingReplaced, StringComparison.Ordinal))));
        }
        else
        {
            builder.Append(sizes.Count == 1 ? $"Драйвер не принял {subject}" : $"Драйвер не принял ни один из проверенных размеров ({string.Join(", ", sizes.Select(s => $"{s.Width} × {s.Height}"))})");
            builder.Append(" при текущей частоте.");
            if (codes.Contains(1610))
                builder.Append(" Код Windows 1610 означает, что драйвер не может собрать такой режим с центрированием; это не повреждение данных.");
            else if (codes.Count > 0)
                builder.Append(" Коды ответа: ").Append(string.Join(", ", codes)).Append('.');
            if (reasons.Count > 0 && codes.Count == 0)
                builder.Append(' ').Append(Sentence(reasons[0]));
        }
        if (timedOut)
            builder.Append(" Поиск остановлен по времени.");
        builder.Append(rolledBack
            ? " Экран на мгновение переключался и был возвращён в исходное состояние."
            : " Исходные настройки не менялись.");
        builder.Append(scalingReplaced ? ScalingHint : codes.Count > 0 ? DriverHint : string.Empty);
        return builder.ToString();
    }

    private static Configuration CreateCentered(Configuration original, DisplayMonitor monitor, int width, int height, bool reconnectNeighbors)
    {
        if (width < 320 || height < 200 || width > 16384 || height > 16384)
            throw new ArgumentException("Размер рабочей области должен быть от 320 × 200 до 16384 × 16384 пикселей.");
        var proposed = original.Copy();
        var index = Array.FindIndex(proposed.Paths, p => Matches(p, monitor));
        if (index < 0)
            throw new InvalidOperationException("Выбранный монитор больше не подключён. Обновите список мониторов.");
        var path = proposed.Paths[index];
        if (path.TargetInfo.TargetAvailable == 0)
            throw new InvalidOperationException("Выбранный монитор сейчас недоступен.");
        if (original.Paths.Count(p => SameSource(p, path) || p.SourceIndex == path.SourceIndex) != 1)
            throw new InvalidOperationException("Сначала выберите в Windows режим «Расширить экраны»: уменьшение дублируемого монитора не поддерживается.");
        if (path.TargetInfo.Rotation != 1)
            throw new InvalidOperationException("Для этого режима установите обычную альбомную ориентацию выбранного монитора.");

        var sourceIndex = RequireIndex(proposed, path.SourceIndex, 1);
        var targetIndex = RequireIndex(proposed, path.TargetIndex, 2);
        var oldSource = proposed.Modes[sourceIndex].Source;
        var signal = proposed.Modes[targetIndex].Target.Signal;
        if (width > signal.ActiveSize.Width || height > signal.ActiveSize.Height)
            throw new InvalidOperationException("Желаемая область больше текущего физического сигнала монитора. Сначала установите родное разрешение в Windows.");
        if (signal.VSyncFreq.Hertz <= 1)
            throw new InvalidOperationException("Драйвер не сообщил физическую частоту обновления. Без неё безопасное сохранение 240 Гц невозможно.");

        var source = oldSource;
        source.Width = (uint)width;
        source.Height = (uint)height;
        proposed.Modes[sourceIndex].Source = source;
        path.TargetInfo.Scaling = width == signal.ActiveSize.Width && height == signal.ActiveSize.Height
            ? Identity : Centered;
        proposed.Paths[index] = path;

        if (path.IsVirtual && path.DesktopIndex != 0xffff)
        {
            var desktopIndex = RequireIndex(proposed, path.DesktopIndex, 3);
            var desktop = proposed.Modes[desktopIndex].Desktop;
            // ImageRegion belongs to the local path-source surface, whereas
            // SourceMode.Position belongs to the global desktop. A secondary
            // screen at X != 0 must therefore still use a local (0,0,W,H) image.
            // Complex virtual crops or clone surfaces cannot be safely inferred.
            if (desktop.PathSourceSize.X != (int)oldSource.Width || desktop.PathSourceSize.Y != (int)oldSource.Height ||
                desktop.ImageRegion.Left != 0 || desktop.ImageRegion.Top != 0 ||
                desktop.ImageRegion.Right != (int)oldSource.Width || desktop.ImageRegion.Bottom != (int)oldSource.Height ||
                desktop.ImageClip.Left != 0 || desktop.ImageClip.Top != 0 ||
                desktop.ImageClip.Right != (int)oldSource.Width || desktop.ImageClip.Bottom != (int)oldSource.Height)
                throw new InvalidOperationException("У монитора используется сложная виртуальная область или обрезка. Этот режим требует обычного расширенного рабочего стола Windows.");
            desktop.PathSourceSize = new PointL { X = width, Y = height };
            desktop.ImageRegion = new RectL { Right = width, Bottom = height };
            desktop.ImageClip = new RectL { Right = width, Bottom = height };
            proposed.Modes[desktopIndex].Desktop = desktop;
        }

        if (reconnectNeighbors)
            CloseDesktopGaps(original, proposed, sourceIndex, oldSource, width, height);
        return proposed;
    }

    /// <summary>Moves monitors that lie beyond the shrunk one so no desktop gap remains, and keeps the
    /// primary monitor at the origin. <paramref name="selectedIndex"/> and the mode indices of
    /// <paramref name="before"/> identify monitors; <paramref name="after"/> may order its modes
    /// differently, so its entries are found by path identity.</summary>
    private static void CloseDesktopGaps(Configuration before, Configuration after, int selectedIndex,
        SourceMode selected, int width, int height)
    {
        var sourceIndices = before.Paths.Select(p => RequireIndex(before, p.SourceIndex, 1)).Distinct().ToArray();
        var primaryIndex = sourceIndices.FirstOrDefault(i => before.Modes[i].Source.Position.X == 0 &&
            before.Modes[i].Source.Position.Y == 0, -1);
        if (primaryIndex < 0)
            throw new InvalidOperationException("Не удалось определить основной экран Windows.");
        var target = sourceIndices.ToDictionary(i => i, i => AfterSourceIndex(before, after, i));
        var deltaWidth = (int)selected.Width - width;
        var deltaHeight = (int)selected.Height - height;
        foreach (var i in sourceIndices)
        {
            if (i == selectedIndex) continue;
            var source = after.Modes[target[i]].Source;
            var original = before.Modes[i].Source;
            if (original.Position.X >= selected.Position.X + (int)selected.Width)
                source.Position.X -= deltaWidth;
            if (original.Position.Y >= selected.Position.Y + (int)selected.Height)
                source.Position.Y -= deltaHeight;
            after.Modes[target[i]].Source = source;
        }
        var offset = after.Modes[target[primaryIndex]].Source.Position;
        foreach (var i in sourceIndices)
        {
            var source = after.Modes[target[i]].Source;
            source.Position.X -= offset.X;
            source.Position.Y -= offset.Y;
            after.Modes[target[i]].Source = source;
        }
        for (var i = 0; i < sourceIndices.Length; i++)
        for (var j = i + 1; j < sourceIndices.Length; j++)
        {
            var a = sourceIndices[i];
            var b = sourceIndices[j];
            if (Overlaps(after.Modes[target[a]].Source, after.Modes[target[b]].Source) &&
                !Overlaps(before.Modes[a].Source, before.Modes[b].Source))
                throw new InvalidOperationException("При этом размере мониторы перекрываются в схеме рабочего стола. Расположите второй монитор ровно слева или справа в настройках Windows.");
        }
    }

    private static int AfterSourceIndex(Configuration before, Configuration after, int beforeIndex)
    {
        var path = before.Paths.First(p => p.SourceIndex == (uint)beforeIndex);
        var found = Array.FindIndex(after.Paths, p => SameSource(p, path));
        if (found < 0)
            throw new InvalidOperationException("Состав мониторов изменился во время переключения.");
        return RequireIndex(after, after.Paths[found].SourceIndex, 1);
    }

    private static bool Overlaps(SourceMode a, SourceMode b) =>
        a.Position.X < b.Position.X + (long)b.Width && b.Position.X < a.Position.X + (long)a.Width &&
        a.Position.Y < b.Position.Y + (long)b.Height && b.Position.Y < a.Position.Y + (long)a.Height;

    private static Configuration Query(bool virtualAware = true)
    {
        var flags = QueryActivePaths;
        if (virtualAware)
        {
            flags |= QueryVirtualModeAware;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                flags |= QueryVirtualRefreshRateAware;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != 0) throw new Win32Exception(error, "Windows не предоставила конфигурацию дисплеев: " + Win32Message(error));
            if (pathCount == 0 || pathCount > 256 || modeCount > 4096)
                throw new InvalidOperationException("Windows не сообщила доступный активный монитор.");
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (error == ErrorInsufficientBuffer) continue;
            if (error != 0) throw new Win32Exception(error, "Не удалось прочитать параметры мониторов: " + Win32Message(error));
            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return new Configuration(paths, modes, flags);
        }
        throw new InvalidOperationException("Мониторы переключаются. Подождите несколько секунд и повторите.");
    }

    private static int Set(Configuration configuration, uint operation)
    {
        var flags = UseSuppliedConfig | operation;
        if ((configuration.QueryFlags & QueryVirtualModeAware) != 0) flags |= SetVirtualModeAware;
        if ((configuration.QueryFlags & QueryVirtualRefreshRateAware) != 0) flags |= SetVirtualRefreshRateAware;
        return SetDisplayConfig((uint)configuration.Paths.Length, configuration.Paths,
            (uint)configuration.Modes.Length, configuration.Modes, flags);
    }

    private static IReadOnlyList<DisplayMonitor> Describe(Configuration configuration, bool includeAvailableModes = true)
    {
        var adapters = includeAvailableModes ? AdapterDescriptions() : new Dictionary<string, string>();
        var displays = new List<DisplayMonitor>();
        foreach (var path in configuration.Paths)
        {
            var source = configuration.Modes[RequireIndex(configuration, path.SourceIndex, 1)].Source;
            var target = configuration.Modes[RequireIndex(configuration, path.TargetIndex, 2)].Target.Signal;
            var sourceName = new SourceDeviceName { Header = Header<SourceDeviceName>(1, path.SourceInfo.AdapterId, path.SourceInfo.Id) };
            var targetName = new TargetDeviceName { Header = Header<TargetDeviceName>(2, path.TargetInfo.AdapterId, path.TargetInfo.Id) };
            var preferred = new TargetPreferredMode { Header = Header<TargetPreferredMode>(3, path.TargetInfo.AdapterId, path.TargetInfo.Id) };
            var sourceError = DisplayConfigGetDeviceInfo(ref sourceName);
            if (sourceError != 0) throw new Win32Exception(sourceError, "Не удалось получить имя экрана Windows.");
            var gdiName = sourceName.GdiDeviceName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(gdiName)) throw new InvalidOperationException("Windows не сообщила системное имя монитора.");
            _ = DisplayConfigGetDeviceInfo(ref targetName);
            var preferredError = DisplayConfigGetDeviceInfo(ref preferred);
            var rotated = path.TargetInfo.Rotation is 2 or 4;
            var nativeWidth = preferredError == 0 && preferred.Width != 0 ? preferred.Width : target.ActiveSize.Width;
            var nativeHeight = preferredError == 0 && preferred.Height != 0 ? preferred.Height : target.ActiveSize.Height;
            adapters.TryGetValue(gdiName, out var adapterName);
            displays.Add(new DisplayMonitor
            {
                DeviceName = gdiName,
                FriendlyName = string.IsNullOrWhiteSpace(targetName.FriendlyName) ? gdiName : targetName.FriendlyName,
                MonitorDevicePath = targetName.MonitorDevicePath ?? string.Empty,
                AdapterDescription = adapterName ?? string.Empty,
                OutputTechnology = path.TargetInfo.OutputTechnology,
                AdapterId = path.SourceInfo.AdapterId.Value,
                SourceId = path.SourceInfo.Id,
                TargetId = path.TargetInfo.Id,
                X = source.Position.X, Y = source.Position.Y,
                CurrentWidth = checked((int)source.Width), CurrentHeight = checked((int)source.Height),
                NativeWidth = checked((int)(rotated ? nativeHeight : nativeWidth)),
                NativeHeight = checked((int)(rotated ? nativeWidth : nativeHeight)),
                SignalWidth = checked((int)(rotated ? target.ActiveSize.Height : target.ActiveSize.Width)),
                SignalHeight = checked((int)(rotated ? target.ActiveSize.Width : target.ActiveSize.Height)),
                RefreshRateHz = target.VSyncFreq.Hertz > 0 ? target.VSyncFreq.Hertz : path.TargetInfo.RefreshRate.Hertz,
                IsPrimary = source.Position.X == 0 && source.Position.Y == 0,
                IsClone = configuration.Paths.Count(p => SameSource(p, path) || p.SourceIndex == path.SourceIndex) > 1,
                IsCentered = path.TargetInfo.Scaling == Centered ||
                    (path.TargetInfo.Scaling == Identity && source.Width == target.ActiveSize.Width && source.Height == target.ActiveSize.Height),
                Rotation = path.TargetInfo.Rotation,
                Scaling = path.TargetInfo.Scaling,
                AvailableModes = includeAvailableModes ? EnumerateModes(gdiName) : []
            });
        }
        return displays.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.X).ThenBy(d => d.Y).ToArray();
    }

    /// <summary>GDI device name (\\.\DISPLAY1) to the video adapter that drives it, for example
    /// "NVIDIA GeForce RTX 3050 Laptop GPU" or "Intel(R) UHD Graphics" in a hybrid laptop.</summary>
    private static Dictionary<string, string> AdapterDescriptions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 64; i++)
        {
            var device = DisplayDevice.Create();
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
            if (!string.IsNullOrWhiteSpace(device.DeviceName))
                result[device.DeviceName] = device.DeviceString ?? string.Empty;
        }
        return result;
    }

    private static IReadOnlyList<DisplayMode> EnumerateModes(string name)
    {
        var results = new HashSet<DisplayMode>();
        for (var i = 0; i < 16384; i++)
        {
            var mode = DevMode.Create();
            if (!EnumDisplaySettingsExW(name, i, ref mode, 0)) break;
            if (mode.PelsWidth > 0 && mode.PelsHeight > 0 && mode.DisplayFrequency > 1)
                results.Add(new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight,
                    mode.DisplayFrequency, (int)mode.BitsPerPel));
        }
        return results.OrderByDescending(m => m.RefreshRateHz).ThenByDescending(m => m.Width).ThenByDescending(m => m.Height).ToArray();
    }

    private static DisplayMonitor? FindMonitor(Configuration configuration, DisplayMonitor monitor) =>
        Describe(configuration, false).FirstOrDefault(d => d.AdapterId == monitor.AdapterId &&
            d.SourceId == monitor.SourceId && d.TargetId == monitor.TargetId &&
            (string.IsNullOrEmpty(monitor.MonitorDevicePath) ||
                string.Equals(d.MonitorDevicePath, monitor.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)));

    private static DisplaySnapshot Snapshot(Configuration configuration) => new()
    {
        QueryFlags = configuration.QueryFlags,
        PathStructSize = Marshal.SizeOf<PathInfo>(), ModeStructSize = Marshal.SizeOf<ModeInfo>(),
        Paths = ToBytes(configuration.Paths), Modes = ToBytes(configuration.Modes),
        Displays = Describe(configuration, false).Select(d => new DisplayIdentity(d.DeviceName, d.MonitorDevicePath,
            d.AdapterId, d.SourceId, d.TargetId)).ToList()
    };

    private static Configuration FromSnapshot(DisplaySnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1 || snapshot.MachineName != Environment.MachineName ||
            snapshot.PathStructSize != Marshal.SizeOf<PathInfo>() || snapshot.ModeStructSize != Marshal.SizeOf<ModeInfo>() ||
            snapshot.Paths is null || snapshot.Modes is null || snapshot.Paths.Length == 0 ||
            snapshot.Paths.Length > 256 * snapshot.PathStructSize || snapshot.Modes.Length > 4096 * snapshot.ModeStructSize ||
            snapshot.Paths.Length % snapshot.PathStructSize != 0 || snapshot.Modes.Length % snapshot.ModeStructSize != 0 ||
            (snapshot.QueryFlags & ~(QueryActivePaths | QueryVirtualModeAware | QueryVirtualRefreshRateAware)) != 0 ||
            (snapshot.QueryFlags & QueryActivePaths) == 0)
            throw new InvalidOperationException("Файл восстановления повреждён или создан для другой системы.");
        var configuration = new Configuration(FromBytes<PathInfo>(snapshot.Paths), FromBytes<ModeInfo>(snapshot.Modes), snapshot.QueryFlags);
        foreach (var path in configuration.Paths)
        {
            _ = RequireIndex(configuration, path.SourceIndex, 1);
            _ = RequireIndex(configuration, path.TargetIndex, 2);
            if (path.IsVirtual && path.DesktopIndex != 0xffff) _ = RequireIndex(configuration, path.DesktopIndex, 3);
        }
        return configuration;
    }

    private static void ValidateConnectedIdentities(DisplaySnapshot snapshot, Configuration current)
    {
        var active = Describe(current, false);
        if (snapshot.Displays is null || snapshot.Displays.Count != active.Count ||
            snapshot.Displays.Any(saved => !active.Any(d => d.AdapterId == saved.AdapterId &&
                d.SourceId == saved.SourceId && d.TargetId == saved.TargetId &&
                string.Equals(d.MonitorDevicePath, saved.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Состав мониторов изменился после сохранения. Автоматическое восстановление отменено: проверьте разрешения в настройках экрана Windows.");
    }

    private static DisplayApplyResult RollBack(Configuration before, string message, int error, string diagnostics)
    {
        try
        {
            var rollbackError = Set(before, Apply);
            var difference = rollbackError == 0 ? Difference(before, Query(IsVirtualAware(before))) : Win32Message(rollbackError);
            return difference is null
                ? Result(false, message + RestoredSuffix, error: error, rolledBack: true, diagnostics: diagnostics)
                : Result(false, message + " Восстановление не подтверждено: " + difference + ". Используйте аварийное восстановление или настройки экрана Windows.",
                    error: error, needsRecovery: true, diagnostics: diagnostics);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Result(false, message + " Не удалось подтвердить восстановление: " + exception.Message,
                error: error, needsRecovery: true, diagnostics: diagnostics);
        }
    }

    private static bool IsVirtualAware(Configuration configuration) => (configuration.QueryFlags & QueryVirtualModeAware) != 0;

    private static string? Difference(Configuration expected, Configuration actual) => Compare(expected, actual, false);

    /// <param name="ignoreScaling">Leave the driver's reported scaling mode out of the comparison; everything
    /// else, including size, signal and frequency, is still compared.</param>
    private static string? Compare(Configuration expected, Configuration actual, bool ignoreScaling)
    {
        if (expected.Paths.Length != actual.Paths.Length) return "изменилось число активных мониторов";
        foreach (var wanted in expected.Paths)
        {
            var found = Array.FindIndex(actual.Paths, p => p.SourceInfo.AdapterId.Value == wanted.SourceInfo.AdapterId.Value &&
                p.SourceInfo.Id == wanted.SourceInfo.Id && p.TargetInfo.AdapterId.Value == wanted.TargetInfo.AdapterId.Value && p.TargetInfo.Id == wanted.TargetInfo.Id);
            if (found < 0) return "изменился состав или подключение мониторов";
            var observed = actual.Paths[found];
            if (found != Array.IndexOf(expected.Paths, wanted)) return "изменился порядок приоритета мониторов";
            if ((wanted.Flags & 0x19) != (observed.Flags & 0x19)) return "изменились флаги активного или виртуального режима";
            if (wanted.TargetInfo.OutputTechnology != observed.TargetInfo.OutputTechnology) return "изменилось подключение монитора";
            if (!ignoreScaling && wanted.TargetInfo.Scaling != observed.TargetInfo.Scaling)
                return $"{ScalingReplaced} «{ScalingName(observed.TargetInfo.Scaling)}»";
            if (wanted.TargetInfo.Rotation != observed.TargetInfo.Rotation) return "изменилась ориентация";
            if (!StructEqual(wanted.TargetInfo.RefreshRate, observed.TargetInfo.RefreshRate) ||
                wanted.TargetInfo.ScanLineOrdering != observed.TargetInfo.ScanLineOrdering)
                return "изменилась частота или развёртка пути дисплея";
            var wantedSource = expected.Modes[RequireIndex(expected, wanted.SourceIndex, 1)].Source;
            var actualSource = actual.Modes[RequireIndex(actual, observed.SourceIndex, 1)].Source;
            if (!StructEqual(wantedSource, actualSource)) return "изменились размер или положение рабочего стола";
            var wantedSignal = expected.Modes[RequireIndex(expected, wanted.TargetIndex, 2)].Target.Signal;
            var actualSignal = actual.Modes[RequireIndex(actual, observed.TargetIndex, 2)].Target.Signal;
            if (!StructEqual(wantedSignal, actualSignal)) return $"изменился физический сигнал или частота ({actualSignal.VSyncFreq.Hertz:0.###} Гц)";
            if (wanted.IsVirtual != observed.IsVirtual) return "изменился виртуальный режим";
            if (wanted.IsVirtual)
            {
                if ((wanted.DesktopIndex == 0xffff) != (observed.DesktopIndex == 0xffff)) return "изменилась виртуальная область";
                if (wanted.DesktopIndex != 0xffff && !StructEqual(
                    expected.Modes[RequireIndex(expected, wanted.DesktopIndex, 3)].Desktop,
                    actual.Modes[RequireIndex(actual, observed.DesktopIndex, 3)].Desktop)) return "изменилась область изображения рабочего стола";
            }
        }
        return null;
    }

    /// <summary>Checks a result that was not produced from a supplied configuration (the classic
    /// ChangeDisplaySettings route): only the selected screen may differ, it must have exactly the
    /// requested size, centering, orientation and the unchanged physical signal and frequency.</summary>
    private static string? VerifyCentered(Configuration before, Configuration after, DisplayMonitor monitor, DisplaySize size) =>
        VerifyApplied(before, after, monitor, size, false);

    private static string? VerifyApplied(Configuration before, Configuration after, DisplayMonitor monitor, DisplaySize size, bool ignoreScaling)
    {
        if (before.Paths.Length != after.Paths.Length) return "изменилось число активных мониторов";
        var wantedIndex = Array.FindIndex(before.Paths, p => Matches(p, monitor));
        var observedIndex = Array.FindIndex(after.Paths, p => Matches(p, monitor));
        if (wantedIndex < 0 || observedIndex < 0) return "выбранный монитор не найден";
        var wanted = before.Paths[wantedIndex];
        var observed = after.Paths[observedIndex];
        var source = after.Modes[RequireIndex(after, observed.SourceIndex, 1)].Source;
        if (source.Width != (uint)size.Width || source.Height != (uint)size.Height)
            return $"драйвер выбрал размер {source.Width} × {source.Height}";
        var oldSignal = before.Modes[RequireIndex(before, wanted.TargetIndex, 2)].Target.Signal;
        var signal = after.Modes[RequireIndex(after, observed.TargetIndex, 2)].Target.Signal;
        if (signal.ActiveSize.Width != oldSignal.ActiveSize.Width || signal.ActiveSize.Height != oldSignal.ActiveSize.Height)
            return "изменился физический сигнал монитора";
        if (Math.Abs(signal.VSyncFreq.Hertz - oldSignal.VSyncFreq.Hertz) > 0.05)
            return $"изменилась частота ({signal.VSyncFreq.Hertz:0.###} Гц)";
        if (!ignoreScaling && observed.TargetInfo.Scaling != Centered)
            return $"{ScalingReplaced} «{ScalingName(observed.TargetInfo.Scaling)}»";
        if (observed.TargetInfo.Rotation != wanted.TargetInfo.Rotation)
            return "изменилась ориентация";
        return null;
    }

    private static string DescribeRequest(Configuration before, Configuration after, DisplayMonitor monitor)
    {
        var path = after.Paths.First(p => Matches(p, monitor));
        var source = after.Modes[RequireIndex(after, path.SourceIndex, 1)].Source;
        var signal = after.Modes[RequireIndex(after, path.TargetIndex, 2)].Target.Signal;
        return $"CCD: source={source.Width}x{source.Height}@({source.Position.X},{source.Position.Y}); " +
            $"target={signal.ActiveSize.Width}x{signal.ActiveSize.Height}; physicalHz={signal.VSyncFreq.Numerator}/{signal.VSyncFreq.Denominator}; " +
            $"pixelClock={signal.PixelRate}; scaling={path.TargetInfo.Scaling}; virtual={path.IsVirtual}; paths={before.Paths.Length}. " +
            "Применение выполняется без изменения исходных таймингов и без разрешения подбора другого режима.";
    }

    private static bool Matches(PathInfo path, DisplayMonitor monitor) => path.SourceInfo.AdapterId.Value == monitor.AdapterId &&
        path.SourceInfo.Id == monitor.SourceId && path.TargetInfo.Id == monitor.TargetId;
    private static bool SameSource(PathInfo a, PathInfo b) => a.SourceInfo.AdapterId.Value == b.SourceInfo.AdapterId.Value && a.SourceInfo.Id == b.SourceInfo.Id;
    private static int RequireIndex(Configuration configuration, uint index, uint type)
    {
        if (index >= configuration.Modes.Length || configuration.Modes[index].InfoType != type)
            throw new InvalidOperationException("Windows вернула неполные сведения о режиме монитора.");
        return (int)index;
    }
    private static DeviceInfoHeader Header<T>(uint type, Luid adapter, uint id) where T : struct =>
        new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), AdapterId = adapter, Id = id };
    private static DisplayApplyResult Result(bool success, string message, DisplayMonitor? monitor = null, int error = 0,
        bool rolledBack = false, bool needsRecovery = false, string diagnostics = "", bool unchanged = false) => new()
        {
            Success = success, Message = message, Monitor = monitor, NativeErrorCode = error,
            ActualWidth = monitor?.CurrentWidth ?? 0, ActualHeight = monitor?.CurrentHeight ?? 0,
            ActualRefreshRateHz = monitor?.RefreshRateHz ?? 0, RolledBack = rolledBack,
            NeedsRecovery = needsRecovery, Unchanged = unchanged, Diagnostics = diagnostics
        };
    private static bool IsExpected(Exception exception) => exception is Win32Exception or InvalidOperationException or ArgumentException or OverflowException;
    private static string Win32Message(int code) => $"Код Windows {code}: {new Win32Exception(code).Message}";

    /// <summary>Plain wording for the codes SetDisplayConfig returns. 1610 reads "configuration data
    /// is corrupt" in Windows, but here it only means the driver cannot build the requested mode.</summary>
    private static string Explain(int code) => code switch
    {
        1610 => "Код Windows 1610: драйвер не может собрать такой режим с центрированием (это не повреждение данных).",
        87 => "Код Windows 87: драйвер отклонил параметры режима.",
        50 => "Код Windows 50: драйвер не поддерживает эту операцию.",
        31 => "Код Windows 31: драйвер не смог выполнить переключение.",
        5 => "Код Windows 5: Windows сейчас не разрешает менять режим (экран заблокирован или занят полноэкранной игрой).",
        _ => Win32Message(code)
    };

    // DISPLAYCONFIG_SCALING values as reported by the driver after a mode change.
    private static string ScalingName(uint scaling) => scaling switch
    {
        1 => "без масштабирования 1:1",
        2 => "центрирование",
        3 => "растягивание на весь экран",
        4 => "растягивание с сохранением пропорций",
        5 => "особое масштабирование",
        128 => "режим по умолчанию",
        _ => $"режим {scaling}"
    };

    private static DisplayApplyResult Annotate(DisplayApplyResult result, DisplayMonitor applied, bool acceptScaling) =>
        acceptScaling && applied.Scaling != Centered
            ? result with { ScalingUnconfirmed = true, ReportedScaling = ScalingName(applied.Scaling) }
            : result;

    private static string Sentence(string text)
    {
        text = text.Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    private static string DescribeDispChange(int code) => code switch
    {
        0 => "принято",
        1 => "Windows просит перезагрузку для этого режима.",
        -1 => "Драйвер не смог переключить режим.",
        -2 => "Драйвер не поддерживает такой режим.",
        -3 => "Не удалось сохранить режим.",
        -4 or -5 => "Недопустимые параметры режима.",
        -6 => "Режим несовместим с текущей схемой нескольких экранов.",
        _ => $"Код {code}."
    };

    private static byte[] ToBytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static T[] FromBytes<T>(byte[] bytes) where T : struct => MemoryMarshal.Cast<byte, T>(bytes.AsSpan()).ToArray();
    private static bool StructEqual<T>(T left, T right) where T : struct =>
        ToBytes(new[] { left }).AsSpan().SequenceEqual(ToBytes(new[] { right }));

    private static void AssertNativeLayouts()
    {
        if (Marshal.SizeOf<PathInfo>() != 72 || Marshal.SizeOf<ModeInfo>() != 64 ||
            Marshal.SizeOf<DevMode>() != 220 || Marshal.SizeOf<SourceDeviceName>() != 84 ||
            Marshal.SizeOf<TargetDeviceName>() != 420 || Marshal.SizeOf<TargetPreferredMode>() != 80 ||
            Marshal.SizeOf<DisplayDevice>() != 840)
            throw new InvalidOperationException("Сборка приложения использует несовместимые структуры Win32.");
    }

    private sealed record Configuration(PathInfo[] Paths, ModeInfo[] Modes, uint QueryFlags)
    {
        public Configuration Copy() => new((PathInfo[])Paths.Clone(), (ModeInfo[])Modes.Clone(), QueryFlags);
    }

    /// <summary>One size tried with one method. Final is set when the search must stop: success, or a
    /// screen that could not be confirmed as restored. Otherwise the screen is known to be unchanged.</summary>
    private sealed record Attempt(string Label, DisplayApplyResult? Final, string? Failure, int Code, bool RolledBack = false,
        bool ScalingOnly = false, DisplaySize Size = default, Method Via = Method.CcdVirtual)
    {
        public static Attempt Done(string label, DisplayApplyResult result) => new(label, result, null, 0);
        public static Attempt Rejected(string label, string reason, int code = 0) => new(label, null, reason, code);
        public static Attempt FromRollBack(string label, DisplayApplyResult result) => result.NeedsRecovery
            ? new Attempt(label, result, null, result.NativeErrorCode)
            : new Attempt(label, null, result.Message.Replace(RestoredSuffix, string.Empty), result.NativeErrorCode, true);

        public string Describe() => $"{Label}: {(Final is { Success: true } ? "применено" : Final?.Message ?? Failure)}";
    }
}
