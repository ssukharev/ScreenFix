using System.Diagnostics;
using ScreenFix.Core;

namespace ScreenFix.Windows.Services;

public sealed class ProfileController
{
    private readonly DisplayService _displays = new();
    private readonly LocalStore _store = new();
    private string? _token;
    private DisplayMonitor? _monitor;
    private DisplaySizePlan? _plan;
    private double _expectedHz;
    private DateTimeOffset _ignoreChangesUntil;
    private DateTimeOffset _nextVerification;
    public bool IsActive => _token is not null;
    public bool IsPending => IsActive && ConfirmByUtc is not null;
    public DateTimeOffset? ConfirmByUtc { get; private set; }
    public string Status { get; private set; } = "Выберите монитор и желаемую диагональ.";
    public bool HasError { get; private set; }
    /// <summary>Attempts of the latest apply or check: which size and method the driver accepted or rejected.</summary>
    public string LastAttemptLog { get; private set; } = string.Empty;
    public event Action? Changed;
    public event Action<string>? Notification;

    public IReadOnlyList<DisplayMonitor> GetDisplays() => _displays.GetDisplays();

    /// <summary>Asks the driver, without changing the screen, which of the sizes it accepts.</summary>
    public (string Summary, bool AnyAccepted) Probe(DisplayMonitor monitor, IReadOnlyList<DisplaySize> sizes, bool reconnectNeighbors)
    {
        var results = _displays.Probe(monitor, sizes, reconnectNeighbors);
        if (results.Count == 1 && results[0].Size == default)
        {
            LastAttemptLog = results[0].Note;
            return (results[0].Note, false);
        }
        var lines = new List<string>();
        foreach (var group in results.GroupBy(r => r.Size))
        {
            var accepted = group.Where(r => r.Accepted).Select(r => r.Method).ToList();
            lines.Add(accepted.Count > 0
                ? $"✓ {group.Key.Width} × {group.Key.Height} — принято ({string.Join(", ", accepted)})"
                : $"✗ {group.Key.Width} × {group.Key.Height} — отклонено: " +
                  string.Join("; ", group.Select(r => $"{r.Method}: {r.Note}")));
        }
        LastAttemptLog = string.Join(Environment.NewLine, lines);
        LocalStore.Log("Probe: " + LastAttemptLog);
        if (results.Any(r => r.Accepted))
            lines.Add("✓ значит, что драйвер принял режим при проверке. Оставит ли он именно центрирование, видно только при включении.");
        return (string.Join(Environment.NewLine, lines), results.Any(r => r.Accepted));
    }

    public bool Apply(DisplayMonitor monitor, DisplaySizePlan plan, IReadOnlyList<DisplaySize> sizes, bool reconnectNeighbors)
    {
        if (IsActive && !Restore()) return false;
        try
        {
            if (File.Exists(LocalStore.RecoveryPath))
                throw new InvalidOperationException("Сначала восстановите сохранённые настройки кнопкой «Вернуть экран».");
            if (plan.IsFullSize)
            {
                SetStatus("Выбран полный размер монитора. Уменьшение не требуется.");
                return true;
            }
            var token = Guid.NewGuid().ToString("N");
            var record = LocalStore.WithRecoveryLock(() =>
            {
                var saved = new RecoveryRecord
                {
                    Token = token,
                    Snapshot = _displays.CaptureSnapshot(),
                    // Also enforced by the separate watchdog if the settings window hangs.
                    // Longer than the driver search in ApplyCentered, which stops after about 40 seconds.
                    ConfirmByUtc = DateTimeOffset.UtcNow.AddSeconds(90)
                };
                _store.SaveRecovery(saved);
                return saved;
            });
            _token = token;
            RecoveryWatchdog.Start(Environment.ProcessId, token);
            var result = LocalStore.WithRecoveryLock(() =>
            {
                // A concurrent --restore or watchdog may have consumed the backup while
                // the protection process was starting. Never mutate without that backup.
                if (_store.ReadRecovery()?.Token != token)
                    throw new InvalidOperationException("Экран уже восстановлен другой командой. Включите профиль повторно.");
                var applied = _displays.ApplyCentered(monitor, sizes, reconnectNeighbors);
                if (applied.Success)
                {
                    ConfirmByUtc = DateTimeOffset.UtcNow.AddSeconds(15);
                    _store.SaveRecovery(record with { ConfirmByUtc = ConfirmByUtc });
                }
                return applied;
            });
            LastAttemptLog = result.Diagnostics;
            LocalStore.Log("Apply: " + result.Message + " " + result.Diagnostics);
            if (!result.Success)
            {
                if (result.Unchanged)
                {
                    // Nothing was changed, so there is nothing to restore: drop the backup instead of
                    // re-applying the original configuration, which could blank the screen for no reason.
                    DiscardBackup(token);
                    ResetState();
                    SetStatus(result.Message, true);
                    return false;
                }
                var originalError = result.Message;
                var recovered = Restore();
                SetStatus(originalError + (recovered ? " Исходные настройки восстановлены." : " Требуется восстановление экрана."), true);
                return false;
            }
            var diagonal = ScreenSizeCalculator.DiagonalForResolution(plan.NativeWidth, plan.NativeHeight,
                plan.ActualDiagonalInches, result.ActualWidth, result.ActualHeight);
            _monitor = result.Monitor ?? monitor;
            _plan = plan with
            {
                VisibleWidth = result.ActualWidth,
                VisibleHeight = result.ActualHeight,
                EffectiveDiagonalInches = diagonal
            };
            _expectedHz = result.ActualRefreshRateHz;
            _ignoreChangesUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            _nextVerification = _ignoreChangesUntil;
            var differs = result.ActualWidth != plan.VisibleWidth || result.ActualHeight != plan.VisibleHeight;
            SetStatus($"Применено {result.ActualWidth} × {result.ActualHeight} (≈ {diagonal:0.##}″), {result.ActualRefreshRateHz:0.##} Гц. " +
                (differs ? $"Точный размер {plan.VisibleWidth} × {plan.VisibleHeight} драйвер не принял, включён ближайший доступный. " : string.Empty) +
                (result.ScalingUnconfirmed
                    ? $"Драйвер сообщил «{result.ReportedScaling}» вместо центрирования, поэтому приложение не может проверить результат само. " +
                      "Посмотрите на экран: если вокруг картинки есть чёрные поля, нажмите «Оставить этот размер». " +
                      "Если картинка растянута на весь экран, ничего не нажимайте: через 15 секунд экран вернётся сам."
                    : "Подтвердите, что изображение видно целиком."));
            return true;
        }
        catch (Exception ex)
        {
            LocalStore.Log("Apply failed", ex);
            if (_token is not null) Restore();
            SetStatus("Не удалось применить профиль: " + ex.Message, true);
            return false;
        }
    }

    private void DiscardBackup(string token) => LocalStore.WithRecoveryLock(() =>
    {
        if (_store.ReadRecovery()?.Token == token) _store.DeleteRecovery();
        return true;
    });

    public void Keep()
    {
        if (!IsPending) return;
        try
        {
            var kept = LocalStore.WithRecoveryLock(() =>
            {
                var record = _store.ReadRecovery();
                if (record is null || record.Token != _token ||
                    record.ConfirmByUtc is not { } deadline || DateTimeOffset.UtcNow >= deadline) return false;
                if (_monitor is null || _plan is null ||
                    !_displays.CheckCentered(_monitor, _plan.VisibleWidth, _plan.VisibleHeight, _expectedHz).Success)
                    return false;
                _store.SaveRecovery(record with { ConfirmByUtc = null });
                return true;
            });
            if (!kept)
            {
                var restored = Restore();
                SetStatus("Время подтверждения истекло или игра изменила режим. " +
                    (restored ? "Исходные настройки восстановлены." : "Повторите восстановление экрана."), !restored);
                return;
            }
            ConfirmByUtc = null;
            SetStatus($"Профиль активен: ≈ {_plan?.EffectiveDiagonalInches:0.##}″ · {_expectedHz:0.##} Гц. Можно свернуть приложение в трей.");
        }
        catch (Exception ex) { LocalStore.Log("Confirm failed", ex); Restore(); SetStatus(ex.Message, true); }
    }

    public bool Restore()
    {
        try
        {
            _ignoreChangesUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            var result = LocalStore.Recover(_token);
            if (result.Success)
            {
                ResetState();
                SetStatus("Исходные настройки экрана восстановлены.");
            }
            else SetStatus("Не удалось восстановить экран: " + result.Message + " Сохранённая копия оставлена для повторной попытки.", true);
            return result.Success;
        }
        catch (Exception ex) { LocalStore.Log("Restore failed", ex); SetStatus("Ошибка восстановления: " + ex.Message, true); return false; }
    }

    public void Tick()
    {
        if (!IsActive) return;
        if (!File.Exists(LocalStore.RecoveryPath))
        {
            ResetState();
            SetStatus("Защита восстановила исходные настройки экрана.");
        }
        else if (ConfirmByUtc is { } deadline && DateTimeOffset.UtcNow >= deadline)
        {
            if (Restore()) SetStatus("Время подтверждения истекло. Исходные настройки восстановлены.");
        }
        else if (!IsPending && DateTimeOffset.UtcNow >= _nextVerification)
        {
            _nextVerification = DateTimeOffset.UtcNow.AddSeconds(3);
            VerifyAfterDisplayChange();
        }
    }

    public void VerifyAfterDisplayChange()
    {
        if (!IsActive || IsPending || _monitor is null || _plan is null || DateTimeOffset.UtcNow < _ignoreChangesUntil) return;
        try
        {
            var check = _displays.CheckCentered(_monitor, _plan.VisibleWidth, _plan.VisibleHeight, _expectedHz);
            if (check.Success) return;
            var detail = check.Message;
            var restored = Restore();
            SetStatus("Игра, Windows или драйвер изменили профиль экрана. " + detail +
                (restored ? " Профиль отключён, исходные настройки восстановлены. Для полноэкранной игры выберите в ней такое же разрешение и центрирование в драйвере." : " Повторите восстановление."), true);
            Notification?.Invoke(Status);
        }
        catch (Exception ex)
        {
            LocalStore.Log("Display verification failed", ex);
            var restored = Restore();
            SetStatus("Не удалось проверить профиль: " + ex.Message + (restored ? " Исходные настройки восстановлены." : " Повторите восстановление."), true);
            Notification?.Invoke(Status);
        }
    }

    public void SetStatus(string message, bool error = false)
    {
        Status = message;
        HasError = error;
        Changed?.Invoke();
    }

    public void AdoptCurrentConfiguration()
    {
        if (IsActive && !HasError)
            throw new InvalidOperationException("Сначала отключите активный профиль.");
        LocalStore.WithRecoveryLock(() =>
        {
            if (File.Exists(LocalStore.RecoveryPath))
            {
                var archive = Path.Combine(LocalStore.Folder, $"recovery-archived-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                File.Move(LocalStore.RecoveryPath, archive);
            }
            return true;
        });
        ResetState();
        SetStatus("Текущие настройки приняты как исходные. Старая копия сохранена в архиве ScreenFix. Новый профиль можно включить.");
    }

    private void ResetState()
    {
        _token = null;
        _monitor = null;
        _plan = null;
        ConfirmByUtc = null;
    }
}
