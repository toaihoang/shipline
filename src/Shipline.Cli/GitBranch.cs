using System.Diagnostics;
using Shipline.Core.Execution;

namespace Shipline.Cli;

internal static class GitBranch
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the branch checked out in the folder, or empty text when the folder is not a Git repository,
    /// Git is not installed, or HEAD is detached (no branch).
    /// </summary>
    public static string Detect(string folder)
    {
        string? git = ExecutableLocator.Find("git");
        if (git is null)
        {
            return string.Empty;
        }

        // "symbolic-ref" also works in a new repository that has no commit yet, unlike "rev-parse --abbrev-ref".
        ProcessStartInfo startInfo = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(folder);
        startInfo.ArgumentList.Add("symbolic-ref");
        startInfo.ArgumentList.Add("--short");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("HEAD");

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        // Both pipes are read so that neither can fill up and block git.
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        error.Wait(s_timeout);
        if (!process.WaitForExit(s_timeout) || process.ExitCode != 0)
        {
            return string.Empty;
        }

        return output.Trim();
    }
}
