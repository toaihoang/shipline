namespace Shipline.Core.Pipelines;

/// <summary>The program that runs the script of a step.</summary>
public enum ShellKind
{
    /// <summary>Windows PowerShell 5.1 (powershell.exe).</summary>
    PowerShell,

    /// <summary>PowerShell 7+ (pwsh).</summary>
    Pwsh,

    /// <summary>The Windows command prompt (cmd.exe).</summary>
    Cmd,

    /// <summary>bash, with -e -o pipefail. On Windows this is Git Bash.</summary>
    Bash,

    /// <summary>POSIX sh, with -e.</summary>
    Sh,
}

/// <summary>Maps shell names in the pipeline file to <see cref="ShellKind"/>.</summary>
public static class ShellNames
{
    private static readonly Dictionary<string, ShellKind> s_shellsByName = new Dictionary<string, ShellKind>(StringComparer.Ordinal)
    {
        { "powershell", ShellKind.PowerShell },
        { "pwsh", ShellKind.Pwsh },
        { "cmd", ShellKind.Cmd },
        { "bash", ShellKind.Bash },
        { "sh", ShellKind.Sh },
    };

    public static IReadOnlyCollection<string> All => s_shellsByName.Keys;

    public static bool TryParse(string name, out ShellKind shell)
    {
        return s_shellsByName.TryGetValue(name, out shell);
    }

    /// <summary>The shell used when neither the step nor the pipeline names one.</summary>
    public static ShellKind DefaultForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return ShellKind.PowerShell;
        }

        return ShellKind.Bash;
    }
}
