using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cmux.Core;

public sealed class LayoutStore(string path)
{
    private readonly string _path = path;
    private string BackupPath => _path + ".bak";

    public LayoutSnapshot? Load()
    {
        if (!File.Exists(_path)) return File.Exists(BackupPath) ? Read(BackupPath) : null;
        try { return Read(_path); }
        catch (Exception) when (File.Exists(BackupPath)) { return Read(BackupPath); }
    }

    public void Save(LayoutSnapshot snapshot)
    {
        if (snapshot.Version != 1) throw new ArgumentException("Unsupported layout version.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var backupTemporary = BackupPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        try
        {
            if (File.Exists(_path))
            {
                try
                {
                    Read(_path); // only replace the backup with a valid previous snapshot
                    File.Copy(_path, backupTemporary, true);
                    File.Move(backupTemporary, BackupPath, true);
                }
                catch (JsonException) { }
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backupTemporary)) File.Delete(backupTemporary);
        }
    }

    private static LayoutSnapshot Read(string path)
    {
        var snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Empty layout snapshot.");
        if (snapshot.Version != 1 || snapshot.Workspaces is null)
            throw new JsonException("Unsupported or invalid layout snapshot.");
        return snapshot;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
