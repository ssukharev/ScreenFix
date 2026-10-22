using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScreenFix.Windows.Services;

internal static class DiagnosticReport
{
    /// <param name="lastAttempts">Result of the latest apply or driver check, if any.</param>
    public static string Create(string? lastAttempts = null) => JsonSerializer.Serialize(new
    {
        App = "ScreenFix",
        Version = typeof(DiagnosticReport).Assembly.GetName().Version?.ToString(),
        Timestamp = DateTimeOffset.Now,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Displays = new DisplayService().GetDisplays(),
        Settings = new LocalStore().LoadSettings(),
        RecoveryPending = File.Exists(LocalStore.RecoveryPath),
        LastAttempts = string.IsNullOrWhiteSpace(lastAttempts) ? null : lastAttempts.Split(Environment.NewLine)
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
