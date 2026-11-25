using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Shipline.Core.Execution;

/// <summary>One line written by a step, to standard output or standard error.</summary>
public sealed class OutputLine
{
    public OutputLine(string text, bool isError)
    {
        Text = text;
        IsError = isError;
    }

    public string Text { get; }

    public bool IsError { get; }
}

public sealed class ProcessRequest
{
    public required string FileName { get; init; }

    public required string Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Variables added to (or replacing) the environment that Shipline itself runs with.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public TimeSpan? Timeout { get; init; }
}

public enum ProcessEndReason
{
    Exited,
    TimedOut,
    Cancelled,
}

public sealed class ProcessResult
{
    public required ProcessEndReason EndReason { get; init; }

    /// <summary>The exit code, or null when the process was killed.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// Runs one process to the end and streams its output line by line.
/// </summary>
/// <remarks>
/// Three rules keep a build from hanging or leaking processes:
/// 1. Standard input is closed at once, so a command that asks a question reads "end of input" instead of waiting
///    forever for an answer nobody will type.
/// 2. When the process exits, times out or is cancelled, the whole process tree is killed. A step cannot leave
///    background processes behind; they would hold files open and change the next build.
/// 3. The wait for the process is separate from the wait for its output: a leftover grandchild can keep the output
///    pipe open after the main process has exited, and waiting for that pipe would wait forever.
/// </remarks>
public static class ProcessRunner
{
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

    public static async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onOutput);

        using Process process = new Process();
        process.StartInfo = CreateStartInfo(request);
        process.EnableRaisingEvents = true;

        TaskCompletionSource exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (sender, args) => exited.TrySetResult();
        process.OutputDataReceived += (sender, args) => HandleLine(args.Data, false, onOutput, outputClosed);
        process.ErrorDataReceived += (sender, args) => HandleLine(args.Data, true, onOutput, errorClosed);

        using ProcessTree tree = new ProcessTree();
        process.Start();
        tree.Add(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        ProcessEndReason endReason = await WaitForExitAsync(exited.Task, request.Timeout, cancellationToken).ConfigureAwait(false);

        tree.KillAll(process);
        await WaitAtMostAsync(exited.Task, s_drainTimeout).ConfigureAwait(false);
        await WaitAtMostAsync(Task.WhenAll(outputClosed.Task, errorClosed.Task), s_drainTimeout).ConfigureAwait(false);

        if (endReason != ProcessEndReason.Exited)
        {
            return new ProcessResult { EndReason = endReason };
        }

        return new ProcessResult { EndReason = endReason, ExitCode = process.ExitCode };
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,

            // The process gets its own hidden console, so Ctrl+C in Shipline's console does not reach it directly.
            // Shipline decides when and how to stop it.
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = s_utf8,
            StandardErrorEncoding = s_utf8,
        };

        foreach (KeyValuePair<string, string> variable in request.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    private static async Task<ProcessEndReason> WaitForExitAsync(Task exited, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = new CancellationTokenSource();
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        using CancellationTokenSource stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await exited.WaitAsync(stopSource.Token).ConfigureAwait(false);
            return ProcessEndReason.Exited;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ProcessEndReason.Cancelled;
            }

            return ProcessEndReason.TimedOut;
        }
    }

    private static void HandleLine(string? text, bool isError, Action<OutputLine> onOutput, TaskCompletionSource closed)
    {
        // A null line means the stream has reached its end.
        if (text is null)
        {
            closed.TrySetResult();
            return;
        }

        onOutput(new OutputLine(text, isError));
    }

    private static async Task WaitAtMostAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Something outside the process tree still holds the pipe. Stop waiting; the result is already known.
        }
    }

    /// <summary>Kills a process and everything it started: a Job Object on Windows, Process.Kill(true) elsewhere.</summary>
    private sealed class ProcessTree : IDisposable
    {
        private readonly WindowsJobObject? _jobObject;
        private bool _isInJobObject;

        public ProcessTree()
        {
            if (OperatingSystem.IsWindows())
            {
                _jobObject = new WindowsJobObject();
            }
        }

        public void Add(Process process)
        {
            if (_jobObject is null || !OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                _jobObject.AddProcess(process);
                _isInJobObject = true;
            }
            catch (Win32Exception)
            {
                // Rare (a parent job that forbids nested jobs). KillAll then falls back to Process.Kill(true).
            }
        }

        public void KillAll(Process process)
        {
            if (_isInJobObject && _jobObject is not null && OperatingSystem.IsWindows())
            {
                _jobObject.KillAll();
                return;
            }

            // Without a job object, only processes still linked to a living parent can be found.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process has already exited.
            }
            catch (Win32Exception)
            {
                // A process in the tree exited while the tree was being killed.
            }
        }

        public void Dispose()
        {
            if (_jobObject is not null && OperatingSystem.IsWindows())
            {
                _jobObject.Dispose();
            }
        }
    }
}
