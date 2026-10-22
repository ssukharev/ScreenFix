using System.Diagnostics;

namespace ScreenFix.Windows.Services;

internal static class RecoveryWatchdog
{
    public static void Start(int parentPid, string token)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token));
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь к приложению для защиты восстановления.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--watchdog");
        start.ArgumentList.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(token);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить защиту восстановления.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(8)) || process.HasExited)
            throw new InvalidOperationException("Защита восстановления не запустилась. Настройки экрана не будут изменены.");
    }

    public static int Run(int parentPid, string token)
    {
        try
        {
            using var parent = FindParent(parentPid);
            var store = new LocalStore();
            _ = new DisplayService(); // Verify Windows/ABI before acknowledging readiness.
            using var ready = EventWaitHandle.OpenExisting(EventName(token));
            ready.Set();
            var failureCount = 0;
            while (true)
            {
                try
                {
                    var outcome = LocalStore.WithRecoveryLock(() =>
                    {
                        var record = store.ReadRecovery();
                        if (record is null || record.Token != token) return 1;
                        var deadlineExpired = record.ConfirmByUtc is { } deadline && DateTimeOffset.UtcNow >= deadline;
                        if (parent is null || parent.HasExited || deadlineExpired)
                            return LocalStore.RestoreRecordLocked(record).Success ? 1 : -1;
                        return 0;
                    });
                    if (outcome == 1) return 0;
                    if (outcome == -1)
                    {
                        failureCount++;
                        // Leave the durable backup for --restore / next launch if the monitor
                        // topology stays unavailable. A live parent keeps its protection.
                        if ((parent is null || parent.HasExited) && failureCount >= 60) return 1;
                        Thread.Sleep(5000);
                    }
                    else { failureCount = 0; Thread.Sleep(500); }
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    if (failureCount++ % 10 == 0) LocalStore.Log("Recovery watchdog will retry", ex);
                    if ((parent is null || parent.HasExited) && failureCount >= 60) return 1;
                    Thread.Sleep(5000);
                }
            }
        }
        catch (Exception ex)
        {
            LocalStore.Log("Recovery watchdog failed", ex);
            return 1;
        }
    }

    private static string EventName(string token) => @"Local\ScreenFix.Watchdog." + token;

    private static Process? FindParent(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.SessionId != Process.GetCurrentProcess().SessionId) { process.Dispose(); return null; }
            return process;
        }
        catch (ArgumentException) { return null; }
    }
}
