using System.ComponentModel;
using System.Diagnostics;

namespace Cmux.Core;

public sealed record GitProjectContext(string Branch, bool IsDirty)
{
    public static async Task<GitProjectContext?> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                ArgumentList = { "-C", directory, "status", "--short", "--branch" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        try { process.Start(); }
        catch (Win32Exception) { return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) return null;
            var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].StartsWith("## ", StringComparison.Ordinal)) return null;
            var branch = lines[0][3..].Trim().Split("...", 2)[0];
            const string unbornPrefix = "No commits yet on ";
            if (branch.StartsWith(unbornPrefix, StringComparison.Ordinal))
                branch = branch[unbornPrefix.Length..];
            return new GitProjectContext(branch, lines.Length > 1);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            return null;
        }
    }
}
