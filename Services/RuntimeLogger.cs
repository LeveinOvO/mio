using System.Text.Json;

namespace PortForwarder.Services;

internal static class RuntimeLogger
{
    private static readonly object Gate = new();
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data", "logs");

    public static void Info(string eventName, string message = "") => Write("Info", eventName, message);
    public static void Warning(string eventName, string message = "") => Write("Warning", eventName, message);
    public static void Error(string eventName, Exception error, string message = "") =>
        Write("Error", eventName, string.IsNullOrEmpty(message) ? error.Message : message, error);

    private static void Write(string level, string eventName, string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = Path.Combine(DirectoryPath, $"Mio-WinUI-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length > 10 * 1024 * 1024)
                {
                    var rotated = Path.Combine(DirectoryPath, $"Mio-WinUI-{DateTime.UtcNow:yyyy-MM-dd}.1.jsonl");
                    File.Delete(rotated);
                    File.Move(file, rotated);
                }

                var entry = new Dictionary<string, object?>
                {
                    ["time"] = DateTime.UtcNow.ToString("O"),
                    ["level"] = level,
                    ["source"] = "WinUI",
                    ["event"] = eventName
                };
                if (!string.IsNullOrWhiteSpace(message)) entry["message"] = message;
                if (error is not null)
                {
                    entry["exception"] = error.GetType().FullName;
                    entry["stack"] = error.StackTrace;
                }
                File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never prevent the UI from continuing or closing.
        }
    }
}
