using Shipline.Core.Pipelines.Conditions;

namespace Shipline.Core.Pipelines;

/// <summary>A pipeline file after it has been read and validated.</summary>
public sealed class PipelineDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyDictionary<string, string> Env { get; init; }

    /// <summary>The shell from "defaults.shell", or null when the file does not set one.</summary>
    public ShellKind? DefaultShell { get; init; }

    /// <summary>When true, the first failed job stops the jobs that are running and the ones not started yet.</summary>
    public bool FailFast { get; init; }

    /// <summary>The most jobs that run at the same time, or null for one per processor.</summary>
    public int? MaxParallel { get; init; }

    /// <summary>The jobs in the order they appear in the file.</summary>
    public required IReadOnlyList<JobDefinition> Jobs { get; init; }

    public JobDefinition? FindJob(string id)
    {
        foreach (JobDefinition job in Jobs)
        {
            if (string.Equals(job.Id, id, StringComparison.Ordinal))
            {
                return job;
            }
        }

        return null;
    }
}

/// <summary>A job: a list of steps that run one after another on the same machine.</summary>
public sealed class JobDefinition
{
    /// <summary>The default job timeout. Jenkins has none, so a stuck build can hold an agent for days.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    /// <summary>The key under "jobs", used by "needs".</summary>
    public required string Id { get; init; }

    /// <summary>The display name: "name" when set, otherwise the id.</summary>
    public required string Name { get; init; }

    /// <summary>Agent labels. Not used by "shipline run", which always runs on this machine.</summary>
    public required IReadOnlyList<string> RunsOn { get; init; }

    public required IReadOnlyList<string> Needs { get; init; }

    /// <summary>The "when" condition, or null to run only when every needed job succeeded.</summary>
    public Condition? When { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required IReadOnlyDictionary<string, string> Env { get; init; }

    public required IReadOnlyList<StepDefinition> Steps { get; init; }

    public required SourceLocation Location { get; init; }
}

/// <summary>A step: one script run by one shell.</summary>
public sealed class StepDefinition
{
    /// <summary>The display name: "name" when set, otherwise the first line of the script.</summary>
    public required string Name { get; init; }

    public required string Run { get; init; }

    /// <summary>The "when" condition, or null to run only when no earlier step failed.</summary>
    public Condition? When { get; init; }

    /// <summary>The shell set on the step, or null to use the pipeline default.</summary>
    public ShellKind? Shell { get; init; }

    /// <summary>The working directory relative to the workspace, or null for the workspace itself.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The step timeout, or null when only the job timeout applies.</summary>
    public TimeSpan? Timeout { get; init; }

    public required IReadOnlyDictionary<string, string> Env { get; init; }

    /// <summary>When true, a failure of this step is reported but does not fail the job.</summary>
    public bool ContinueOnError { get; init; }

    public required SourceLocation Location { get; init; }
}
