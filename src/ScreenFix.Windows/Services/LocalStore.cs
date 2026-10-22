using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScreenFix.Core;

namespace ScreenFix.Windows.Services;

public sealed record RecoveryRecord
{
    public required string Token { get; init; }
    public required DisplaySnapshot Snapshot { get; init; }
    public DateTimeOffset? ConfirmByUtc { get; init; }
}

public sealed class LocalStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenFix");
    public static string RecoveryPath => Path.Combine(Folder, "recovery.json");
    public static string LogPath => Path.Combine(Folder, "ScreenFix.log");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ScreenFixSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(Folder, "settings.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<ScreenFixSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log("Settings load", ex);
            return new();
        }
    }

    public void SaveSettings(ScreenFixSettings settings) => WriteAtomic(Path.Combine(Folder, "settings.json"), settings);
    public RecoveryRecord? ReadRecovery() => File.Exists(RecoveryPath)
        ? JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(RecoveryPath), Json)
        : null;
    public void SaveRecovery(RecoveryRecord record) => WriteAtomic(RecoveryPath, record);
    public void DeleteRecovery() => File.Delete(RecoveryPath);

    public static T WithRecoveryLock<T>(Func<T> operation)
    {
        using var mutex = new Mutex(false, @"Local\ScreenFix.Recovery");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Восстановление экрана уже выполняется. Повторите через несколько секунд.");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public static DisplayApplyResult Recover(string? expectedToken = null)
    {
        return WithRecoveryLock(() =>
        {
            var store = new LocalStore();
            var record = store.ReadRecovery();
            if (record is null || (expectedToken is not null && record.Token != expectedToken))
                return new DisplayApplyResult { Success = true, Message = "Сохранённого профиля для восстановления нет." };
            return RestoreRecordLocked(record);
        });
    }

    // Caller owns the cross-process recovery mutex, including the decision to restore.
    internal static DisplayApplyResult RestoreRecordLocked(RecoveryRecord record)
    {
        var result = new DisplayService().Restore(record.Snapshot);
        Log("Restore: " + result.Message + " " + result.Diagnostics);
        if (result.Success) new LocalStore().DeleteRecovery();
        return result;
    }

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.Move(LogPath, LogPath + ".old", true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : "\n" + exception)}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
