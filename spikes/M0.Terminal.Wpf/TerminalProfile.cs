using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cmux.Spike.Terminal;

public sealed record TerminalProfile(string Name, string Command, string[] Args, string StartingDirectory)
{
    [JsonIgnore]
    public string CommandLine => string.Join(" ", new[] { Quote(Command) }.Concat((Args ?? []).Select(Quote)));

    [JsonIgnore]
    public string WorkingDirectory => string.IsNullOrWhiteSpace(StartingDirectory)
        ? Environment.CurrentDirectory
        : Environment.ExpandEnvironmentVariables(StartingDirectory);

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}

public static class ProfileStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "profiles.json");

    public static IReadOnlyList<TerminalProfile> Load()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Defaults(), JsonOptions));
        }

        var profiles = JsonSerializer.Deserialize<TerminalProfile[]>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("profiles.json must contain a profile array.");
        if (profiles.Length == 0 || profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Args is null))
            throw new InvalidDataException("Every profile needs a name and args array.");
        return profiles;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static TerminalProfile[] Defaults() =>
    [
        new("PowerShell", "powershell.exe", ["-NoLogo"], ""),
        new("PowerShell 7", PowerShell7(), ["-NoLogo"], ""),
        new("CMD", "cmd.exe", [], ""),
        new("Git Bash", GitBash(), ["--login", "-i"], ""),
        new("WSL", "wsl.exe", [], ""),
        new("Custom", "", [], ""),
    ];

    private static string PowerShell7()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed)) return installed;
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe");
        return File.Exists(alias) ? alias : "pwsh.exe";
    }

    private static string GitBash()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var bash = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(bash)) return bash;
        }
        return @"C:\Program Files\Git\bin\bash.exe";
    }
}
