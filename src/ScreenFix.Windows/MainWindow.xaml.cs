using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenFix.Core;
using ScreenFix.Windows.Services;

namespace ScreenFix.Windows;

public partial class MainWindow : Window
{
    private readonly LocalStore _store = new();
    private readonly ProfileController _profile = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _displayDebounce = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private ScreenFixSettings _settings;
    private TrayService? _tray;
    private HotKeyService? _keys;
    private bool _ready;
    private bool _busy;
    private bool _exiting;
    private bool _hideNoticeShown;
    private bool _updatingSizes;
    private string _sizeSignature = string.Empty;
    private IReadOnlyList<SizeCandidate> _candidates = [];

    /// <summary>An entry of the size list. A null candidate is "Auto": the nearest size the driver
    /// lists first, then the other nearby ones if the driver refuses it.</summary>
    private sealed record SizeOption(string Label, SizeCandidate? Candidate)
    {
        public override string ToString() => Label;
    }

    public MainWindow(string? recoveryMessage)
    {
        _settings = _store.LoadSettings();
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(900, MaxHeight);
        PhysicalInput.Text = _settings.PhysicalDiagonalInches.ToString("0.##", CultureInfo.CurrentCulture);
        TargetInput.Text = _settings.TargetDiagonalInches.ToString("0.##", CultureInfo.CurrentCulture);
        ReconnectInput.IsChecked = _settings.EnableCursorPortals;
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · работает локально · без прав администратора";
        _profile.Changed += RenderState;
        _profile.Notification += message => Dispatcher.BeginInvoke(() => _tray?.Notify(message, true));
        _clock.Tick += (_, _) =>
        {
            if (!_busy) _profile.Tick();
            if (_profile.ConfirmByUtc is { } deadline)
                CountdownText.Text = $"Автовозврат через {Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))} сек.";
        };
        _displayDebounce.Tick += (_, _) =>
        {
            if (_busy) return;
            _displayDebounce.Stop();
            _profile.VerifyAfterDisplayChange();
            RefreshMonitors();
        };
        SourceInitialized += WindowSourceInitialized;
        Closing += WindowClosing;
        Closed += WindowClosed;
        _ready = true;
        RefreshMonitors();
        if (!string.IsNullOrWhiteSpace(recoveryMessage)) _profile.SetStatus(recoveryMessage, File.Exists(LocalStore.RecoveryPath));
        RenderState();
        _clock.Start();
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _tray = new TrayService();
        _tray.Open += ShowSettings;
        _tray.Toggle += async () => { if (_profile.IsActive) await RestoreProfile(); else await ApplyProfile(); };
        _tray.Restore += async () => await RestoreProfile();
        _tray.Exit += async () => await ExitApplication();
        _keys = new HotKeyService(new WindowInteropHelper(this).Handle);
        _keys.Toggle += async () => { if (_profile.IsActive) await RestoreProfile(); else await ApplyProfile(); };
        _keys.Restore += async () => await RestoreProfile();
        if (!_keys.ToggleRegistered || !_keys.RestoreRegistered)
            _profile.SetStatus("Одна из горячих клавиш занята другим приложением. Управление через окно и трей доступно.", true);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.SessionEnding += SessionEnding;
        RenderState();
    }

    private void RefreshMonitors()
    {
        if (!_ready || _busy) return;
        try
        {
            var old = MonitorPicker.SelectedItem as DisplayMonitor;
            var displays = _profile.GetDisplays();
            MonitorPicker.ItemsSource = displays;
            MonitorPicker.SelectedItem = displays.FirstOrDefault(d => old is not null && d.MonitorDevicePath == old.MonitorDevicePath && d.DeviceName == old.DeviceName)
                ?? displays.FirstOrDefault(d => d.DeviceName == _settings.MonitorDeviceName)
                ?? displays.FirstOrDefault(d => d.IsPrimary)
                ?? displays.FirstOrDefault();
            if (displays.Count == 0) _profile.SetStatus("Windows не сообщила доступные мониторы.", true);
            UpdatePreview();
        }
        catch (Exception ex) { LocalStore.Log("Monitor list", ex); _profile.SetStatus("Не удалось прочитать мониторы: " + ex.Message, true); }
    }

    private bool TryPlan(out DisplayMonitor? monitor, out DisplaySizePlan? plan, out string error)
    {
        monitor = MonitorPicker?.SelectedItem as DisplayMonitor;
        plan = null;
        error = string.Empty;
        if (monitor is null) { error = "Выберите подключённый монитор."; return false; }
        if (!TryNumber(PhysicalInput.Text, out var physical) || !TryNumber(TargetInput.Text, out var target))
        { error = "Введите диагонали числами, например 27 и 24 или 23,8."; return false; }
        if (!double.IsFinite(physical) || !double.IsFinite(target) || physical < 1 || physical > 250 || target < 1 || target > physical)
        { error = "Диагональ должна быть от 1 до 250 дюймов, желаемый размер — не больше физического."; return false; }
        try
        {
            plan = ScreenSizeCalculator.CalculateAligned(monitor.NativeWidth, monitor.NativeHeight, physical, target);
            if (plan.VisibleWidth < 320 || plan.VisibleHeight < 200)
            { error = "Для Windows нужна область не меньше 320 × 200 пикселей. Увеличьте желаемую диагональ."; return false; }
            return true;
        }
        catch (ArgumentException) { error = "Не удалось рассчитать размер. Проверьте диагональ и разрешение монитора."; return false; }
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        var selected = MonitorPicker.SelectedItem as DisplayMonitor;
        if (selected is not null)
        {
            MonitorInfo.Text = $"Родное разрешение: {selected.NativeWidth} × {selected.NativeHeight} · текущая частота: {selected.RefreshRateHz:0.##} Гц" +
                (string.IsNullOrWhiteSpace(selected.AdapterDescription) ? string.Empty : $" · видеоадаптер: {selected.AdapterDescription}");
            HzSummary.Text = $"Сохраняем {selected.RefreshRateHz:0.##} Гц";
        }
        if (!TryPlan(out var monitor, out var plan, out var error) || plan is null || monitor is null)
        {
            PlanInfo.Text = error;
            ApplyButton.IsEnabled = false;
            ProbeButton.IsEnabled = false;
            SetSizeOptions(string.Empty, [], null);
            return;
        }
        RefreshSizeOptions(monitor, plan);
        var chosen = SelectedCandidate();
        var shown = chosen is null ? plan : plan with
        {
            VisibleWidth = chosen.Width,
            VisibleHeight = chosen.Height,
            EffectiveDiagonalInches = chosen.EffectiveDiagonalInches
        };
        SizeSummary.Text = $"{plan.ActualDiagonalInches:0.##}″ → {plan.TargetDiagonalInches:0.##}″";
        PreviewDiagonal.Text = $"≈ {shown.EffectiveDiagonalInches:0.##}″";
        PreviewResolution.Text = $"{shown.VisibleWidth} × {shown.VisibleHeight}";
        var displays = MonitorPicker.ItemsSource as IReadOnlyList<DisplayMonitor> ?? [];
        var hasRight = displays.Any(d => d.DeviceName != monitor.DeviceName && d.X >= monitor.X + monitor.Width - 8 && d.Y < monitor.Y + monitor.Height && d.Y + d.Height > monitor.Y);
        var hasLeft = displays.Any(d => d.DeviceName != monitor.DeviceName && d.X + d.Width <= monitor.X + 8 && d.Y < monitor.Y + monitor.Height && d.Y + d.Height > monitor.Y);
        var secondOnLeft = hasLeft && !hasRight;
        var monitorLeft = secondOnLeft ? 180d : 8d;
        var nativeW = 500d;
        var nativeH = nativeW * plan.NativeHeight / plan.NativeWidth;
        if (nativeH > 275) { nativeW *= 275 / nativeH; nativeH = 275; }
        PreviewMonitor.Width = nativeW;
        PreviewMonitor.Height = nativeH;
        Canvas.SetLeft(PreviewMonitor, monitorLeft);
        Canvas.SetTop(PreviewMonitor, (300 - nativeH) / 2);
        PreviewArea.Width = nativeW * shown.VisibleWidth / shown.NativeWidth;
        PreviewArea.Height = nativeH * shown.VisibleHeight / shown.NativeHeight;
        Canvas.SetLeft(PreviewArea, monitorLeft + (nativeW - PreviewArea.Width) / 2);
        Canvas.SetTop(PreviewArea, (300 - PreviewArea.Height) / 2);
        Canvas.SetLeft(PreviewSecond, secondOnLeft ? 8 : 555);
        Canvas.SetLeft(PreviewArrow, secondOnLeft ? 143 : 515);
        PreviewSecond.Visibility = PreviewArrow.Visibility = displays.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        PlanInfo.Text = DescribePlan(plan, chosen);
        var nativeSignal = monitor.SignalWidth == monitor.NativeWidth && monitor.SignalHeight == monitor.NativeHeight;
        if (!nativeSignal)
            PlanInfo.Text += " Сначала установите родное разрешение монитора в Windows — иначе физический размер нельзя подтвердить.";
        ApplyButton.IsEnabled = !_busy && !_profile.IsActive && !File.Exists(LocalStore.RecoveryPath) && nativeSignal;
        ProbeButton.IsEnabled = !_busy && !_profile.IsActive && nativeSignal && !plan.IsFullSize;
    }

    private static string DescribePlan(DisplaySizePlan plan, SizeCandidate? chosen)
    {
        var text = plan.IsFullSize
            ? "Желаемая диагональ равна диагонали монитора — уменьшение не требуется."
            : chosen is null
                ? $"Расчёт: {plan.VisibleWidth} × {plan.VisibleHeight} · ≈ {plan.EffectiveDiagonalInches:0.##}″."
                : chosen.IsRequestedSize
                    ? $"Расчёт: {chosen.Width} × {chosen.Height} · ≈ {chosen.EffectiveDiagonalInches:0.##}″. " +
                      (chosen.IsDriverListed
                          ? "Драйвер предлагает этот размер."
                          : "Драйвер не перечисляет такой размер, поэтому может его отклонить.")
                    : $"Точный расчёт {plan.VisibleWidth} × {plan.VisibleHeight} (≈ {plan.EffectiveDiagonalInches:0.##}″) драйвер не предлагает. " +
                      $"Выбран ближайший доступный размер: {chosen.Width} × {chosen.Height} (≈ {chosen.EffectiveDiagonalInches:0.##}″). Другие варианты — в списке выше.";
        return text + " Центрирование и частота проверяются при включении.";
    }

    /// <summary>Rebuilds the size list only when something it depends on has changed, so that a
    /// size picked by the user survives the many UpdatePreview calls.</summary>
    private void RefreshSizeOptions(DisplayMonitor monitor, DisplaySizePlan plan)
    {
        // While a profile is on, the driver lists modes for the reduced picture; keep the list that was shown.
        if (_busy || _profile.IsActive) return;
        var invariant = CultureInfo.InvariantCulture;
        var signature = string.Join("|", monitor.DeviceName, monitor.NativeWidth, monitor.NativeHeight,
            monitor.RefreshRateHz.ToString("0.###", invariant), monitor.AvailableModes.Count,
            plan.ActualDiagonalInches.ToString("0.###", invariant), plan.TargetDiagonalInches.ToString("0.###", invariant));
        if (signature == _sizeSignature) return;
        IReadOnlyList<SizeCandidate> candidates;
        try
        {
            var modes = monitor.AvailableModes.Select(m => new ScreenFix.Core.DisplayMode(m.Width, m.Height, m.RefreshRateHz));
            candidates = ModeSelector.RankSizes(monitor.NativeWidth, monitor.NativeHeight,
                plan.ActualDiagonalInches, plan.TargetDiagonalInches, modes, monitor.RefreshRateHz);
        }
        catch (ArgumentException) { candidates = []; }
        SetSizeOptions(signature, candidates, ModeSelector.PickAutomatic(candidates));
    }

    private void SetSizeOptions(string signature, IReadOnlyList<SizeCandidate> candidates, SizeCandidate? automatic)
    {
        _sizeSignature = signature;
        _candidates = candidates;
        var options = new List<SizeOption>();
        if (candidates.Count > 0)
        {
            options.Add(new SizeOption(automatic is null ? "Авто" :
                $"Авто — ближайший доступный: {automatic.Width} × {automatic.Height} (≈ {automatic.EffectiveDiagonalInches:0.##}″)", null));
            options.AddRange(candidates.Select(c => new SizeOption(DescribeCandidate(c), c)));
        }
        _updatingSizes = true;
        try
        {
            SizePicker.ItemsSource = options;
            SizePicker.SelectedIndex = options.Count > 0 ? 0 : -1;
        }
        finally { _updatingSizes = false; }
    }

    private static string DescribeCandidate(SizeCandidate candidate) =>
        $"{candidate.Width} × {candidate.Height} · ≈ {candidate.EffectiveDiagonalInches:0.##}″" +
        (candidate.IsRequestedSize ? " · точный расчёт" : string.Empty) +
        (candidate.IsDriverListed ? string.Empty : " · нет в списке драйвера");

    private SizeCandidate? SelectedCandidate() =>
        SizePicker.SelectedItem is SizeOption { Candidate: { } choice } ? choice : ModeSelector.PickAutomatic(_candidates);

    /// <summary>A size picked by hand is tried alone. In Auto mode the sizes the driver lists and that lie
    /// close to the request are tried nearest first, so one refusal does not end the attempt.</summary>
    private IReadOnlyList<DisplaySize> SizesToTry(DisplaySizePlan plan)
    {
        if (SizePicker.SelectedItem is SizeOption { Candidate: { } choice })
            return [new DisplaySize(choice.Width, choice.Height)];
        var listed = _candidates.Where(c => c.IsDriverListed).ToList();
        var close = listed.Where(c => Math.Abs(c.DiagonalErrorInches) <= plan.TargetDiagonalInches * 0.12).ToList();
        var pool = close.Count > 0 ? close : listed.Take(1).ToList();
        if (pool.Count == 0) pool = _candidates.Take(1).ToList();
        if (pool.Count == 0)
            return [new DisplaySize(plan.VisibleWidth, plan.VisibleHeight)];
        return pool.Select(c => new DisplaySize(c.Width, c.Height)).ToList();
    }

    private async Task ApplyProfile()
    {
        if (_busy || _profile.IsActive) return;
        if (!TryPlan(out var monitor, out var plan, out var error) || monitor is null || plan is null)
        { _profile.SetStatus(error, true); ShowSettings(); return; }
        if (monitor.SignalWidth != monitor.NativeWidth || monitor.SignalHeight != monitor.NativeHeight)
        { _profile.SetStatus("Установите родное разрешение монитора в Windows перед уменьшением. Это нужно для правильного размера в дюймах.", true); ShowSettings(); return; }
        _busy = true;
        RenderState();
        var reconnect = ReconnectInput.IsChecked == true;
        var sizes = SizesToTry(plan);
        try
        {
            SaveSettings(monitor, plan);
            _profile.SetStatus("Проверяем режимы и применяем центрирование. Исходные настройки сохранены для восстановления.");
            await Task.Run(() => _profile.Apply(monitor, plan, sizes, reconnect));
        }
        catch (Exception ex) { LocalStore.Log("Apply UI", ex); _profile.SetStatus(ex.Message, true); }
        finally { _busy = false; RenderState(); }
        if (_profile.IsPending || _profile.HasError) ShowSettings();
    }

    private async Task RestoreProfile()
    {
        if (_busy) return;
        _busy = true;
        RenderState();
        try { await Task.Run(_profile.Restore); }
        finally { _busy = false; RefreshMonitors(); RenderState(); }
    }

    private void RenderState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RenderState); return; }
        if (!_ready) return;
        StatusText.Text = _profile.Status;
        StatusPanel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_profile.HasError ? "#423324" : "#1C2B36"));
        StateBadge.Text = _busy ? "ПЕРЕКЛЮЧЕНИЕ" : _profile.IsPending ? "ПОДТВЕРДИТЕ" : _profile.IsActive ? "ПРОФИЛЬ АКТИВЕН" : "ПОЛНЫЙ ЭКРАН";
        ConfirmationPanel.Visibility = _profile.IsPending ? Visibility.Visible : Visibility.Collapsed;
        MonitorPicker.IsEnabled = SizePicker.IsEnabled = PhysicalInput.IsEnabled = TargetInput.IsEnabled = ReconnectInput.IsEnabled = !_busy && !_profile.IsActive;
        RestoreButton.IsEnabled = !_busy && (_profile.IsActive || File.Exists(LocalStore.RecoveryPath));
        UseCurrentButton.Visibility = !_busy && (!_profile.IsActive || _profile.HasError) && File.Exists(LocalStore.RecoveryPath) ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
        _tray?.Update(_profile.IsActive, _profile.IsPending || _busy);
    }

    private void SaveSettings(DisplayMonitor monitor, DisplaySizePlan plan)
    {
        _settings.MonitorDeviceName = monitor.DeviceName;
        _settings.PhysicalDiagonalInches = plan.ActualDiagonalInches;
        _settings.TargetDiagonalInches = plan.TargetDiagonalInches;
        _settings.PreferredRefreshRateHz = monitor.RefreshRateHz;
        _settings.EnableCursorPortals = ReconnectInput.IsChecked == true;
        _store.SaveSettings(_settings);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _displayDebounce.Stop();
        _displayDebounce.Start();
    });

    private void SessionEnding(object sender, SessionEndingEventArgs e)
    {
        try { LocalStore.Recover(); }
        catch (Exception ex) { LocalStore.Log("Session ending", ex); }
    }

    private void ShowSettings()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // Display changes can leave the previous window origin off-screen.
        var bounds = SystemParameters.WorkArea;
        MaxHeight = bounds.Height;
        Height = Math.Min(Height, MaxHeight);
        Left = Math.Clamp(Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - ActualWidth));
        Top = Math.Clamp(Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Math.Min(ActualHeight, bounds.Height)));
        Activate();
    }

    private async Task ExitApplication()
    {
        if (_busy) { _tray?.Notify("Дождитесь завершения переключения экрана."); return; }
        _busy = true;
        RenderState();
        var restored = await Task.Run(_profile.Restore);
        _busy = false;
        if (!restored) { RenderState(); ShowSettings(); return; }
        _exiting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        if (!_hideNoticeShown)
        {
            _hideNoticeShown = true;
            _tray?.Notify("ScreenFix работает в трее рядом с часами. Двойной щелчок открывает настройки.");
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.SessionEnding -= SessionEnding;
        _clock.Stop();
        _displayDebounce.Stop();
        _keys?.Dispose();
        _tray?.Dispose();
    }

    private static bool TryNumber(string text, out double value) => double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out value);
    private void MonitorChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void SizeChosen(object sender, SelectionChangedEventArgs e) { if (!_updatingSizes) UpdatePreview(); }
    private void SizeChangedInput(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshMonitors();
    private async void ApplyClick(object sender, RoutedEventArgs e) => await ApplyProfile();
    private async void RestoreClick(object sender, RoutedEventArgs e) => await RestoreProfile();

    /// <summary>Dry run: asks Windows whether the driver would accept each size. The screen is not touched.</summary>
    private async void ProbeClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _profile.IsActive) return;
        if (!TryPlan(out var monitor, out var plan, out var error) || monitor is null || plan is null)
        { _profile.SetStatus(error, true); return; }
        var sizes = _candidates.Select(c => new DisplaySize(c.Width, c.Height)).ToList();
        if (sizes.Count == 0) sizes.Add(new DisplaySize(plan.VisibleWidth, plan.VisibleHeight));
        var reconnect = ReconnectInput.IsChecked == true;
        _busy = true;
        RenderState();
        try
        {
            _profile.SetStatus("Спрашиваем у драйвера, какие размеры он принимает. Экран не меняется…");
            var (summary, anyAccepted) = await Task.Run(() => _profile.Probe(monitor, sizes, reconnect));
            _profile.SetStatus("Ответ драйвера (экран не менялся):" + Environment.NewLine + summary +
                (anyAccepted ? string.Empty : Environment.NewLine + "Ни один размер не принят. Сохраните диагностику и отправьте её разработчику."), !anyAccepted);
        }
        catch (Exception ex) { LocalStore.Log("Probe UI", ex); _profile.SetStatus("Не удалось проверить режимы: " + ex.Message, true); }
        finally { _busy = false; RenderState(); }
    }
    private void KeepClick(object sender, RoutedEventArgs e) { if (!_busy) _profile.Keep(); }
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Сохранить диагностику ScreenFix", FileName = "ScreenFix-diagnostics.json", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, DiagnosticReport.Create(_profile.LastAttemptLog)); _profile.SetStatus("Диагностика сохранена: " + dialog.FileName); }
        catch (Exception ex) { _profile.SetStatus("Не удалось сохранить диагностику: " + ex.Message, true); }
    }

    private void UseCurrentClick(object sender, RoutedEventArgs e)
    {
        if (_busy || (_profile.IsActive && !_profile.HasError)) return;
        try
        {
            _profile.AdoptCurrentConfiguration();
            RefreshMonitors();
        }
        catch (Exception ex) { _profile.SetStatus("Не удалось архивировать старую копию: " + ex.Message, true); }
    }
}
