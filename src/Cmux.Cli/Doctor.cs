using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

internal static class Doctor
{
    public static int Run()
    {
        var problems = 0;
        void Check(string name, bool okay, string detail, bool required = false)
        {
            Console.WriteLine($"{name,-18} {(okay ? "OK" : required ? "FAIL" : "WARN"),-4} {detail}");
            if (!okay && required) problems++;
        }

        Check("OS", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763),
            Environment.OSVersion.VersionString, required: true);
        Check(".NET runtime", Environment.Version.Major >= 8,
            Environment.Version.ToString(), required: true);
        var app = AppContext.BaseDirectory;
        Check("Terminal engine", File.Exists(Path.Combine(app, "Microsoft.Terminal.Wpf.dll")),
            "Microsoft.Terminal.Wpf.dll", required: true);
        Check("OpenConsole", File.Exists(Path.Combine(app, "OpenConsole.exe")),
            "OpenConsole.exe", required: true);
        var conpty = false;
        if (NativeLibrary.TryLoad("kernel32.dll", out var kernel))
        {
            conpty = NativeLibrary.TryGetExport(kernel, "CreatePseudoConsole", out _);
            NativeLibrary.Free(kernel);
        }
        Check("ConPTY", conpty, "CreatePseudoConsole export", required: true);

        Check("PowerShell", FindExecutable("powershell.exe") is not null, "powershell.exe");
        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git", "bin", "bash.exe");
        Check("Git Bash", File.Exists(gitBash) || FindExecutable("bash.exe") is not null, "bash.exe");
        Check("WSL", FindExecutable("wsl.exe") is not null, "wsl.exe");
        Check("Codex", FindExecutable("codex.exe") is not null || FindExecutable("codex.cmd") is not null, "codex");
        Check("Claude", FindExecutable("claude.exe") is not null || FindExecutable("claude.cmd") is not null, "claude");

        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");
        foreach (var file in new[] { "profiles.json", "terminal-settings.json", "workspace-state.json" })
        {
            var path = Path.Combine(config, file);
            var valid = false;
            if (File.Exists(path))
            {
                try { using var json = JsonDocument.Parse(File.ReadAllText(path)); valid = true; }
                catch (Exception ex) when (ex is JsonException or IOException) { }
            }
            Check(file, valid, valid ? "valid JSON" : "missing or invalid JSON");
        }

        var daemon = false;
        if (OperatingSystem.IsWindows()) try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_');
            using var pipe = new NamedPipeClientStream(".", "cmux-session-host-" + sid, PipeDirection.InOut);
            pipe.Connect(300);
            daemon = pipe.IsConnected;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        Check("Session daemon", daemon, daemon ? "connected" : "not running");

        Console.WriteLine(problems == 0 ? "Required checks passed." : $"{problems} required check(s) failed.");
        return problems == 0 ? 0 : 1;
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
