using System.Text;
using System.Text.Json;

namespace Cmux.Core;

public enum DiagnosticLevel { Debug, Info, Warning, Error, Critical }

public sealed class DiagnosticLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _component;
    private readonly DiagnosticLevel _minimumLevel;
    private int _part;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "logs");

    public DiagnosticLog(string component)
    {
        _component = component;
        _minimumLevel = Enum.TryParse<DiagnosticLevel>(
            Environment.GetEnvironmentVariable("CMUX_LOG_LEVEL"), true, out var level) ? level : DiagnosticLevel.Info;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, $"{_component}-*.jsonl"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - Retention) File.Delete(file);
        }
        catch { /* Logging must not prevent application startup. */ }
    }

    public void Write(DiagnosticLevel level, string eventName, string? detail = null, Exception? exception = null)
    {
        if (level < _minimumLevel) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = level.ToString(),
                component = _component,
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                eventName,
                detail,
                exception = exception?.ToString(),
            }, JsonOptions) + Environment.NewLine;
            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = CurrentPath();
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
                    path = CurrentPath(++_part);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch { /* Logging must not interrupt terminal or IPC work. */ }
    }

    private string CurrentPath(int? part = null) => Path.Combine(DirectoryPath,
        $"{_component}-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}{(part ?? _part) switch { 0 => "", var n => $"-{n}" }}.jsonl");
}
