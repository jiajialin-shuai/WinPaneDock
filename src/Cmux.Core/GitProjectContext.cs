using System.ComponentModel;
using System.Diagnostics;

namespace Cmux.Core;

public sealed record GitProjectContext(string Branch, bool IsDirty)
{
    public static async Task<string?> ReadWorktreeRootAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        var result = await RunGitAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Output is not { Length: > 0 } output) return null;
        try { return Path.GetFullPath(output.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    public static async Task<GitProjectContext?> ReadAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        var result = await RunGitAsync(directory, ["status", "--short", "--branch"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Output is not { } outputText) return null;
        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("## ", StringComparison.Ordinal)) return null;
        var branch = lines[0][3..].Trim().Split("...", 2)[0];
        const string unbornPrefix = "No commits yet on ";
        if (branch.StartsWith(unbornPrefix, StringComparison.Ordinal))
            branch = branch[unbornPrefix.Length..];
        return new GitProjectContext(branch, lines.Length > 1);
    }

    private static async Task<GitResult> RunGitAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Win32Exception) { return new GitResult(null, -1); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new GitResult(await output.ConfigureAwait(false), process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            return new GitResult(null, -1);
        }
    }

    private sealed record GitResult(string? Output, int ExitCode);
}
