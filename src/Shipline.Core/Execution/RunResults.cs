using Shipline.Core.Pipelines;

namespace Shipline.Core.Execution;

/// <summary>How a step, a job or a whole run ended.</summary>
public enum Outcome
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,

    /// <summary>Did not run: an earlier step failed, or a needed job did not succeed.</summary>
    Skipped,
}

public sealed class StepResult
{
    public required StepDefinition Step { get; init; }

    public required Outcome Outcome { get; init; }

    public int? ExitCode { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>True when the step failed but has "continue-on-error: true", so the job goes on.</summary>
    public bool FailureIgnored { get; init; }

    /// <summary>Why the step ended this way, such as "exit code 1", or null when it succeeded.</summary>
    public string? Message { get; init; }
}

public sealed class JobResult
{
    public required JobDefinition Job { get; init; }

    public required Outcome Outcome { get; init; }

    public TimeSpan Duration { get; init; }

    public required IReadOnlyList<StepResult> Steps { get; init; }

    public string? Message { get; init; }
}

public sealed class RunResult
{
    public required string RunId { get; init; }

    public required Outcome Outcome { get; init; }

    public TimeSpan Duration { get; init; }

    public required IReadOnlyList<JobResult> Jobs { get; init; }
}

/// <summary>
/// Receives the events of a run as they happen. The console prints them; the server (a later phase) will store them
/// and stream them to the browser. Output events arrive from several threads at once.
/// </summary>
public interface IRunReporter
{
    void RunStarted(PipelineDefinition pipeline, string runId, IReadOnlyList<JobDefinition> jobs);

    void JobStarted(JobDefinition job);

    void StepStarted(JobDefinition job, StepDefinition step);

    void StepOutput(JobDefinition job, StepDefinition step, OutputLine line);

    void StepFinished(JobDefinition job, StepResult result);

    void JobFinished(JobResult result);

    void RunFinished(RunResult result);
}
