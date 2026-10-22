using System.Windows;
using ScreenFix.Windows.Services;

namespace ScreenFix.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--watchdog" && int.TryParse(args[1], out var parent) && Guid.TryParseExact(args[2], "N", out _))
            return RecoveryWatchdog.Run(parent, args[2]);

        if (args is ["--restore"])
        {
            try
            {
                var result = LocalStore.Recover();
                MessageBox.Show(result.Message, "ScreenFix — восстановление", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return result.Success ? 0 : 1;
            }
            catch (Exception ex) { LocalStore.Log("Manual recovery", ex); MessageBox.Show(ex.Message, "ScreenFix"); return 1; }
        }

        if (args is ["--diagnostics"])
        {
            try
            {
                Directory.CreateDirectory(LocalStore.Folder);
                var path = Path.Combine(LocalStore.Folder, "ScreenFix-diagnostics.json");
                File.WriteAllText(path, DiagnosticReport.Create());
                MessageBox.Show("Диагностика сохранена:\n" + path, "ScreenFix");
                return 0;
            }
            catch (Exception ex) { LocalStore.Log("Diagnostics", ex); MessageBox.Show(ex.Message, "ScreenFix"); return 1; }
        }

        using var instance = new Mutex(true, @"Local\ScreenFix.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("ScreenFix уже работает. Откройте настройки через значок рядом с часами.", "ScreenFix");
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        app.DispatcherUnhandledException += (_, e) =>
        {
            LocalStore.Log("Unhandled UI error", e.Exception);
            try { LocalStore.Recover(); }
            catch (Exception recoveryError) { LocalStore.Log("Fatal error recovery", recoveryError); }
            // Leave the recovery record intact: the independent watchdog restores on exit.
            MessageBox.Show("Ошибка ScreenFix. Приложение закроется; сохранённые настройки экрана будут восстановлены.\n\n" + e.Exception.Message,
                "ScreenFix", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            app.Shutdown(1);
        };
        try
        {
            string? recoveryMessage = null;
            if (System.IO.File.Exists(LocalStore.RecoveryPath))
            {
                var restored = LocalStore.Recover();
                recoveryMessage = restored.Message;
                if (!restored.Success)
                    MessageBox.Show("Не удалось восстановить предыдущие настройки экрана. Новый профиль будет недоступен до восстановления.\n\n" + restored.Message,
                        "ScreenFix", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            var window = new MainWindow(recoveryMessage);
            app.MainWindow = window;
            window.Show();
            return app.Run();
        }
        catch (Exception ex)
        {
            LocalStore.Log("Startup error", ex);
            MessageBox.Show("Не удалось запустить ScreenFix.\n\n" + ex.Message, "ScreenFix", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
