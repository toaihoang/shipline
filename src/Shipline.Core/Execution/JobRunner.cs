using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Shipline.Core.Pipelines;
using Shipline.Core.Pipelines.Conditions;

namespace Shipline.Core.Execution;

/// <summary>Runs the steps of one job, one after another.</summary>
internal sealed class JobRunner
{
    private readonly RunContext _context;
    private readonly IRunReporter _reporter;
    private readonly Func<string?> _failFastCause;

    /// <param name="context">The run this job belongs to.</param>
    /// <param name="reporter">Receives the job and step events.</param>
    /// <param name="failFastCause">Returns the id of the job that triggered fail-fast, or null.</param>
    public JobRunner(RunContext context, IRunReporter reporter, Func<string?> failFastCause)
    {
        _context = context;
        _reporter = reporter;
        _failFastCause = failFastCause;
    }

    /// <param name="job">The job to run.</param>
    /// <param name="stopToken">Cancelled when the run is cancelled or, for most jobs, when fail-fast stops the run.</param>
    public async Task<JobResult> RunAsync(JobDefinition job, CancellationToken stopToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        _reporter.JobStarted(job);
        using JobStop stop = new JobStop(job, _failFastCause, stopToken, _context.RunToken);

        List<StepResult> stepResults = new List<StepResult>();
        StepResult? failedStep = null;
        for (int stepIndex = 0; stepIndex < job.Steps.Count; stepIndex++)
        {
            StepResult stepResult = await RunOrSkipStepAsync(job, stepIndex, failedStep, stop).ConfigureAwait(false);
            _reporter.StepFinished(job, stepResult);
            stepResults.Add(stepResult);
            if (failedStep is null && StopsJob(stepResult))
            {
                failedStep = stepResult;
            }
        }

        Outcome outcome = Outcome.Succeeded;
        if (failedStep is not null)
        {
            outcome = failedStep.Outcome;
        }

        JobResult jobResult = new JobResult
        {
            Job = job,
            Outcome = outcome,
            Duration = stopwatch.Elapsed,
            Steps = stepResults,
        };
        _reporter.JobFinished(jobResult);
        return jobResult;
    }

    private async Task<StepResult> RunOrSkipStepAsync(JobDefinition job, int stepIndex, StepResult? failedStep, JobStop stop)
    {
        StepDefinition step = job.Steps[stepIndex];
        if (stop.Token.IsCancellationRequested)
        {
            return Skipped(step, "The job was stopped.");
        }

        ConditionContext conditionContext = new ConditionContext
        {
            Branch = _context.Options.Branch,
            Event = _context.Options.Event,
            Env = _context.BuildConditionEnvironment(job, step),
            Success = failedStep is null,
            Failure = failedStep is not null,
        };
        if (!Condition.ShouldRun(step.When, conditionContext))
        {
            return Skipped(step, DescribeSkip(step.When, conditionContext.Success));
        }

        return await RunStepAsync(job, stepIndex, stop).ConfigureAwait(false);
    }

    private async Task<StepResult> RunStepAsync(JobDefinition job, int stepIndex, JobStop stop)
    {
        StepDefinition step = job.Steps[stepIndex];
        _reporter.StepStarted(job, step);
        Stopwatch stopwatch = Stopwatch.StartNew();
        StepEnd end = await ExecuteStepAsync(job, stepIndex, stop).ConfigureAwait(false);

        bool failed = ExecutionStates.IsFailure(end.Outcome);
        return new StepResult
        {
            Step = step,
            Outcome = end.Outcome,
            ExitCode = end.ExitCode,
            Duration = stopwatch.Elapsed,
            FailureIgnored = failed && step.ContinueOnError,
            Message = end.Message,
        };
    }

    private async Task<StepEnd> ExecuteStepAsync(JobDefinition job, int stepIndex, JobStop stop)
    {
        StepDefinition step = job.Steps[stepIndex];
        string workingDirectory = ResolveWorkingDirectory(step);
        if (!Directory.Exists(workingDirectory))
        {
            return StepEnd.Failed("The working directory \"" + workingDirectory + "\" does not exist.");
        }

        ShellKind shell = ResolveShell(step);
        string stepNumber = (stepIndex + 1).ToString(CultureInfo.InvariantCulture);
        string scriptPath = Path.Combine(_context.ScriptFolder, job.Id + "-" + stepNumber);
        ShellInvocation? invocation;
        string? error;
        try
        {
            if (!ShellScripts.TryPrepare(shell, step.Run, scriptPath, out invocation, out error))
            {
                return StepEnd.Failed(error);
            }
        }
        catch (IOException exception)
        {
            return StepEnd.Failed("Could not write the step script: " + exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return StepEnd.Failed("Could not write the step script: " + exception.Message);
        }

        ProcessRequest request = new ProcessRequest
        {
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
            WorkingDirectory = workingDirectory,
            Environment = _context.BuildVariables(job, step),
            Timeout = step.Timeout,
        };

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(request, line => _reporter.StepOutput(job, step, line), stop.Token).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            return StepEnd.Failed("Could not start \"" + invocation.FileName + "\": " + exception.Message);
        }

        return ToStepEnd(result, step, stop);
    }

    private static StepEnd ToStepEnd(ProcessResult result, StepDefinition step, JobStop stop)
    {
        if (result.EndReason == ProcessEndReason.TimedOut)
        {
            return new StepEnd(Outcome.TimedOut, null, "The step timed out after " + FormatTimeout(step.Timeout) + ".");
        }

        if (result.EndReason == ProcessEndReason.Cancelled)
        {
            if (stop.JobTimedOut)
            {
                return new StepEnd(Outcome.TimedOut, null, stop.DescribeStop());
            }

            return new StepEnd(Outcome.Cancelled, null, stop.DescribeStop());
        }

        int exitCode = result.ExitCode.GetValueOrDefault();
        if (exitCode == 0)
        {
            return new StepEnd(Outcome.Succeeded, 0, null);
        }

        return new StepEnd(Outcome.Failed, exitCode, "Exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static string DescribeSkip(Condition? condition, bool success)
    {
        bool skippedByEarlierFailure = condition is null || (!condition.UsesStatusFunction && !success);
        if (skippedByEarlierFailure)
        {
            return "An earlier step failed.";
        }

        return "The condition \"" + condition + "\" is false.";
    }

    private static StepResult Skipped(StepDefinition step, string message)
    {
        return new StepResult { Step = step, Outcome = Outcome.Skipped, Message = message };
    }

    private static string FormatTimeout(TimeSpan? timeout)
    {
        if (timeout is null)
        {
            return string.Empty;
        }

        return Duration.Format(timeout.Value);
    }

    private string ResolveWorkingDirectory(StepDefinition step)
    {
        if (step.WorkingDirectory is null)
        {
            return _context.Workspace;
        }

        return Path.GetFullPath(Path.Combine(_context.Workspace, step.WorkingDirectory));
    }

    private ShellKind ResolveShell(StepDefinition step)
    {
        if (step.Shell is not null)
        {
            return step.Shell.Value;
        }

        if (_context.Pipeline.DefaultShell is not null)
        {
            return _context.Pipeline.DefaultShell.Value;
        }

        return ShellNames.DefaultForCurrentOs();
    }

    // A failed step stops the job unless it has continue-on-error. A cancelled step always stops it.
    private static bool StopsJob(StepResult result)
    {
        if (result.Outcome == Outcome.Succeeded || result.Outcome == Outcome.Skipped)
        {
            return false;
        }

        return !result.FailureIgnored;
    }

    /// <summary>Stops a job on its timeout or on the stop token, and tells the reasons apart.</summary>
    private sealed class JobStop : IDisposable
    {
        private readonly JobDefinition _job;
        private readonly CancellationToken _stopToken;
        private readonly CancellationToken _runToken;
        private readonly Func<string?> _failFastCause;
        private readonly CancellationTokenSource _timeoutSource;
        private readonly CancellationTokenSource _combinedSource;

        public JobStop(JobDefinition job, Func<string?> failFastCause, CancellationToken stopToken, CancellationToken runToken)
        {
            _job = job;
            _stopToken = stopToken;
            _runToken = runToken;
            _failFastCause = failFastCause;
            _timeoutSource = new CancellationTokenSource(job.Timeout);
            _combinedSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken, _timeoutSource.Token);
        }

        public CancellationToken Token => _combinedSource.Token;

        public bool JobTimedOut => _timeoutSource.IsCancellationRequested && !_stopToken.IsCancellationRequested;

        public string DescribeStop()
        {
            if (JobTimedOut)
            {
                return "The job timed out after " + Duration.Format(_job.Timeout) + ".";
            }

            string? failFastCause = _failFastCause();
            if (failFastCause is not null && !_runToken.IsCancellationRequested)
            {
                return "Stopped by fail-fast: job \"" + failFastCause + "\" failed.";
            }

            return "Cancelled.";
        }

        public void Dispose()
        {
            _combinedSource.Dispose();
            _timeoutSource.Dispose();
        }
    }

    private sealed class StepEnd
    {
        public StepEnd(Outcome outcome, int? exitCode, string? message)
        {
            Outcome = outcome;
            ExitCode = exitCode;
            Message = message;
        }

        public Outcome Outcome { get; }

        public int? ExitCode { get; }

        public string? Message { get; }

        public static StepEnd Failed(string message)
        {
            return new StepEnd(Outcome.Failed, null, message);
        }
    }
}
