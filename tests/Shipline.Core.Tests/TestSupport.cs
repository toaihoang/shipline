using Shipline.Core.Execution;
using Shipline.Core.Pipelines;

namespace Shipline.Core.Tests;

/// <summary>A fact that runs only on Windows, for behavior that depends on Job Objects or PowerShell.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
        }
    }
}

/// <summary>A new empty folder under %TEMP%, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shipline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relativePath)
    {
        return System.IO.Path.Combine(Path, relativePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A killed process may still hold a file for a moment. The folder is under %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>Short scripts that work in the default shell of each OS (PowerShell on Windows, bash elsewhere).</summary>
internal static class Scripts
{
    public static string PrintVariable(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return "Write-Output \"" + name + "=$env:" + name + "\"";
        }

        return "echo \"" + name + "=$" + name + "\"";
    }

    public static string Sleep(int seconds)
    {
        string text = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            return "Start-Sleep -Seconds " + text;
        }

        return "sleep " + text;
    }

    public static string ReadOneLineOrPrintEof()
    {
        if (OperatingSystem.IsWindows())
        {
            return "$line = [Console]::In.ReadLine(); if ($null -eq $line) { Write-Output 'eof' }";
        }

        return "read line || echo eof";
    }

    /// <summary>
    /// Creates <paramref name="myFile"/>, then waits for <paramref name="otherFile"/>. Two jobs that each wait for the
    /// other's file both succeed only when they run at the same time; run one after the other, the first one gives up.
    /// </summary>
    public static string Handshake(string myFile, string otherFile, int waitSeconds)
    {
        string seconds = waitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            return "Set-Content -Path '" + myFile + "' -Value x\n"
                + "$deadline = (Get-Date).AddSeconds(" + seconds + ")\n"
                + "while (-not (Test-Path '" + otherFile + "')) {\n"
                + "  if ((Get-Date) -gt $deadline) { Write-Output 'no partner'; exit 1 }\n"
                + "  Start-Sleep -Milliseconds 100\n"
                + "}\n"
                + "Write-Output 'met partner'";
        }

        string tenths = (waitSeconds * 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "touch '" + myFile + "'\n"
            + "for i in $(seq 1 " + tenths + "); do\n"
            + "  if [ -f '" + otherFile + "' ]; then echo 'met partner'; exit 0; fi\n"
            + "  sleep 0.1\n"
            + "done\n"
            + "echo 'no partner'\n"
            + "exit 1";
    }

    public static string PrintWorkingDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Write-Output (Get-Location).Path";
        }

        return "pwd";
    }
}

/// <summary>Records every reporter event, for assertions.</summary>
internal sealed class RecordingReporter : IRunReporter
{
    private readonly Lock _lock = new Lock();
    private readonly List<string> _output = new List<string>();
    private readonly List<string> _events = new List<string>();

    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_lock)
            {
                return _output.ToList();
            }
        }
    }

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }

    public void RunStarted(PipelineDefinition pipeline, string runId, IReadOnlyList<JobDefinition> jobs)
    {
        Record("run started");
    }

    public void JobStarted(JobDefinition job)
    {
        Record("job started " + job.Id);
    }

    public void StepStarted(JobDefinition job, StepDefinition step)
    {
        Record("step started " + job.Id + "/" + step.Name);
    }

    public void StepOutput(JobDefinition job, StepDefinition step, OutputLine line)
    {
        lock (_lock)
        {
            _output.Add(line.Text);
        }
    }

    public void StepFinished(JobDefinition job, StepResult result)
    {
        Record("step finished " + job.Id + "/" + result.Step.Name + " " + result.Outcome);
    }

    public void JobFinished(JobResult result)
    {
        Record("job finished " + result.Job.Id + " " + result.Outcome);
    }

    public void RunFinished(RunResult result)
    {
        Record("run finished " + result.Outcome);
    }

    private void Record(string text)
    {
        lock (_lock)
        {
            _events.Add(text);
        }
    }
}
