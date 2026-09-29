using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cmux.Core;

public sealed class LayoutStore(string path)
{
    private readonly string _path = path;
    private string BackupPath => _path + ".bak";

    public string? LastRecoveryMessage { get; private set; }

    public LayoutSnapshot? Load()
    {
        LastRecoveryMessage = null;
        if (!File.Exists(_path))
        {
            if (!File.Exists(BackupPath)) return null;
            try
            {
                var recovered = Read(BackupPath);
                LastRecoveryMessage = "Primary layout was missing; recovered the last valid backup.";
                return recovered;
            }
            catch (Exception backup)
            {
                throw RecoveryException(
                    new FileNotFoundException($"Layout file was not found: {_path}"), backup);
            }
        }

        try { return Read(_path); }
        catch (Exception primary)
        {
            if (!File.Exists(BackupPath)) throw RecoveryException(primary, null);
            try
            {
                var recovered = Read(BackupPath);
                LastRecoveryMessage = $"Primary layout was invalid; recovered the last valid backup. {primary.Message}";
                return recovered;
            }
            catch (Exception backup)
            {
                throw RecoveryException(primary, backup);
            }
        }
    }

    public void Save(LayoutSnapshot snapshot)
    {
        LayoutValidator.Validate(snapshot);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        var backupTemporary = BackupPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        try
        {
            if (File.Exists(_path) && IsValidSnapshot(_path))
            {
                File.Copy(_path, backupTemporary, true);
                File.Move(backupTemporary, BackupPath, true);
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backupTemporary)) File.Delete(backupTemporary);
        }
    }

    private LayoutRecoveryException RecoveryException(Exception primary, Exception? backup) =>
        new(_path, File.Exists(BackupPath) ? BackupPath : null, primary, backup);

    private static bool IsValidSnapshot(string path)
    {
        try { Read(path); return true; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static LayoutSnapshot Read(string path)
    {
        var snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Empty layout snapshot.");
        LayoutValidator.Validate(snapshot);
        return snapshot;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
