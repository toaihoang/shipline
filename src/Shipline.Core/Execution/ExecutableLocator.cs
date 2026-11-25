namespace Shipline.Core.Execution;

/// <summary>Finds programs on the PATH.</summary>
public static class ExecutableLocator
{
    /// <summary>Returns the full path of a program found on the PATH, or null. On Windows ".exe" is added to the name.</summary>
    public static string? Find(string name)
    {
        return FindOnPath(name, skipFolder: null);
    }

    /// <summary>
    /// Finds bash. On Windows two traps are avoided: C:\Windows\System32\bash.exe starts WSL (a Linux VM) instead of
    /// Git Bash, and Git for Windows usually puts only its "cmd" folder (git.exe) on the PATH, not "bin" (bash.exe).
    /// </summary>
    public static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Find("bash");
        }

        string? bash = FindOnPath("bash", Environment.SystemDirectory);
        if (bash is not null)
        {
            return bash;
        }

        string? bashNextToGit = FindBashNextToGit();
        if (bashNextToGit is not null)
        {
            return bashNextToGit;
        }

        string programFilesBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(programFilesBash))
        {
            return programFilesBash;
        }

        return null;
    }

    // <Git>\cmd\git.exe -> <Git>\bin\bash.exe
    private static string? FindBashNextToGit()
    {
        string? git = Find("git");
        if (git is null)
        {
            return null;
        }

        string? gitCmdFolder = Path.GetDirectoryName(git);
        if (gitCmdFolder is null)
        {
            return null;
        }

        string? gitRoot = Path.GetDirectoryName(gitCmdFolder);
        if (gitRoot is null)
        {
            return null;
        }

        string bash = Path.Combine(gitRoot, "bin", "bash.exe");
        if (!File.Exists(bash))
        {
            return null;
        }

        return bash;
    }

    private static string? FindOnPath(string name, string? skipFolder)
    {
        string fileName = name;
        if (OperatingSystem.IsWindows())
        {
            fileName = name + ".exe";
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsSkipped(folder, skipFolder))
            {
                continue;
            }

            string candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsSkipped(string folder, string? skipFolder)
    {
        if (skipFolder is null)
        {
            return false;
        }

        string trimmedFolder = folder.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(trimmedFolder, skipFolder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // %LOCALAPPDATA%\Microsoft\WindowsApps holds a bash.exe alias that also starts WSL.
        return trimmedFolder.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase);
    }
}
