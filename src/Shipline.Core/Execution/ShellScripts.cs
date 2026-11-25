using System.Diagnostics.CodeAnalysis;
using System.Text;
using Shipline.Core.Pipelines;

namespace Shipline.Core.Execution;

/// <summary>The program and arguments that run one step script.</summary>
public sealed class ShellInvocation
{
    public required string FileName { get; init; }

    /// <summary>The command line arguments as one string, already quoted.</summary>
    public required string Arguments { get; init; }

    public required string ScriptPath { get; init; }
}

/// <summary>
/// Writes a step script to a file and builds the command that runs it.
/// </summary>
/// <remarks>
/// The script goes to a file instead of the command line because command lines have length limits and complex quoting
/// rules (cmd.exe and PowerShell quote differently). Each shell gets a short header that makes failures stop the script.
/// </remarks>
public static class ShellScripts
{
    private static readonly UTF8Encoding s_utf8WithBom = new UTF8Encoding(true);
    private static readonly UTF8Encoding s_utf8WithoutBom = new UTF8Encoding(false);

    /// <summary>
    /// Writes the script next to <paramref name="scriptPathWithoutExtension"/> and returns how to run it.
    /// Returns false with an error message when the shell is not installed.
    /// </summary>
    public static bool TryPrepare(
        ShellKind shell,
        string script,
        string scriptPathWithoutExtension,
        [NotNullWhen(true)] out ShellInvocation? invocation,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(script);
        invocation = null;
        error = null;

        string? program = FindProgram(shell);
        if (program is null)
        {
            error = "The shell \"" + ShellName(shell) + "\" is not installed or not on the PATH.";
            return false;
        }

        switch (shell)
        {
            case ShellKind.PowerShell:
            case ShellKind.Pwsh:
                invocation = PreparePowerShell(program, script, scriptPathWithoutExtension + ".ps1");
                break;
            case ShellKind.Cmd:
                invocation = PrepareCmd(program, script, scriptPathWithoutExtension + ".cmd");
                break;
            case ShellKind.Bash:
                invocation = PrepareBash(program, script, scriptPathWithoutExtension + ".sh");
                break;
            default:
                invocation = PrepareSh(program, script, scriptPathWithoutExtension + ".sh");
                break;
        }

        return true;
    }

    private static ShellInvocation PreparePowerShell(string program, string script, string scriptPath)
    {
        // $ErrorActionPreference = 'Stop' turns PowerShell errors into script failures.
        // $PSNativeCommandUseErrorActionPreference does the same for failing programs in PowerShell 7.4+; older
        // versions ignore it. There, only the exit code of the last program counts, checked by the last line.
        // Windows PowerShell 5.1 reads a script file without a BOM as ANSI, so the file is written with a BOM.
        StringBuilder content = new StringBuilder();
        content.AppendLine("$ErrorActionPreference = 'Stop'");
        content.AppendLine("$PSNativeCommandUseErrorActionPreference = $true");
        content.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        content.AppendLine(NormalizeLineEndings(script, "\r\n"));
        content.AppendLine("if (Test-Path -LiteralPath variable:\\LASTEXITCODE) { exit $LASTEXITCODE }");
        File.WriteAllText(scriptPath, content.ToString(), s_utf8WithBom);

        return new ShellInvocation
        {
            FileName = program,
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(scriptPath),
            ScriptPath = scriptPath,
        };
    }

    private static ShellInvocation PrepareCmd(string program, string script, string scriptPath)
    {
        // "chcp 65001" switches the console to UTF-8, so the lines after it and the program output are read as UTF-8.
        // cmd.exe needs CRLF line endings; with LF alone, labels and GOTO can break.
        string content = "@echo off\r\nchcp 65001 >nul\r\n" + NormalizeLineEndings(script, "\r\n") + "\r\n";
        File.WriteAllText(scriptPath, content, s_utf8WithoutBom);

        // The same command line GitHub Actions uses: /D skips AutoRun commands, CALL keeps the script's exit code.
        return new ShellInvocation
        {
            FileName = program,
            Arguments = "/D /E:ON /V:OFF /S /C \"CALL " + Quote(scriptPath) + "\"",
            ScriptPath = scriptPath,
        };
    }

    private static ShellInvocation PrepareBash(string program, string script, string scriptPath)
    {
        File.WriteAllText(scriptPath, NormalizeLineEndings(script, "\n") + "\n", s_utf8WithoutBom);

        // -e stops at the first failing command; -o pipefail makes "a | b" fail when "a" fails.
        return new ShellInvocation
        {
            FileName = program,
            Arguments = "--noprofile --norc -eo pipefail " + Quote(ToForwardSlashes(scriptPath)),
            ScriptPath = scriptPath,
        };
    }

    private static ShellInvocation PrepareSh(string program, string script, string scriptPath)
    {
        File.WriteAllText(scriptPath, NormalizeLineEndings(script, "\n") + "\n", s_utf8WithoutBom);
        return new ShellInvocation
        {
            FileName = program,
            Arguments = "-e " + Quote(ToForwardSlashes(scriptPath)),
            ScriptPath = scriptPath,
        };
    }

    private static string? FindProgram(ShellKind shell)
    {
        switch (shell)
        {
            case ShellKind.PowerShell:
                return ExecutableLocator.Find("powershell");
            case ShellKind.Pwsh:
                return ExecutableLocator.Find("pwsh");
            case ShellKind.Cmd:
                return ExecutableLocator.Find("cmd");
            case ShellKind.Bash:
                return ExecutableLocator.FindBash();
            default:
                return ExecutableLocator.Find("sh");
        }
    }

    private static string ShellName(ShellKind shell)
    {
        foreach (string name in ShellNames.All)
        {
            if (ShellNames.TryParse(name, out ShellKind candidate) && candidate == shell)
            {
                return name;
            }
        }

        return shell.ToString();
    }

    private static string NormalizeLineEndings(string text, string lineEnding)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    // Git Bash on Windows accepts C:/path/script.sh; backslashes would be read as escape characters.
    private static string ToForwardSlashes(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        return path.Replace('\\', '/');
    }

    // Script paths are under the temp folder that Shipline creates, so they never contain a double quote.
    private static string Quote(string path)
    {
        return "\"" + path + "\"";
    }
}
