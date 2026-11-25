using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Shipline.Core.Pipelines;

namespace Shipline.Core.Execution;

public sealed class LocalRunOptions
{
    /// <summary>The value of "event" for a run started by hand.</summary>
    public const string ManualEvent = "manual";

    public required PipelineDefinition Pipeline { get; init; }

    /// <summary>The folder the steps run in, normally the folder of the pipeline file.</summary>
    public required string Workspace { get; init; }

    /// <summary>The jobs to run, together with the jobs they need. Empty runs every job.</summary>
    public IReadOnlyList<string> JobIds { get; init; } = Array.Empty<string>();

    /// <summary>Variables from the command line. They win over the env sections of the file.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>The Git branch that "branch" reads in conditions, or empty text when unknown.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>What started the run, read by "event" in conditions.</summary>
    public string Event { get; init; } = ManualEvent;
}

/// <summary>Runs a pipeline on this machine: the jobs in parallel where "needs" allows it, the steps of a job in order.</summary>
public sealed class LocalRunner
{
    private readonly IRunReporter _reporter;

    public LocalRunner(IRunReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        _reporter = reporter;
    }

    public async Task<RunResult> RunAsync(LocalRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<JobDefinition> jobs = SelectJobs(options);
        string runId = CreateRunId();
        string scriptFolder = Path.Combine(Path.GetTempPath(), "shipline", runId);
        Directory.CreateDirectory(scriptFolder);
        RunContext context = new RunContext(options, runId, scriptFolder, cancellationToken);

        Stopwatch stopwatch = Stopwatch.StartNew();
        _reporter.RunStarted(options.Pipeline, runId, jobs);
        IReadOnlyList<JobResult> jobResults;
        try
        {
            using JobScheduler scheduler = new JobScheduler(context, _reporter, jobs);
            jobResults = await scheduler.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFolder(scriptFolder);
        }

        RunResult result = new RunResult
        {
            RunId = runId,
            Outcome = DecideRunOutcome(jobResults, cancellationToken),
            Duration = stopwatch.Elapsed,
            Jobs = jobResults,
        };
        _reporter.RunFinished(result);
        return result;
    }

    private static IReadOnlyList<JobDefinition> SelectJobs(LocalRunOptions options)
    {
        if (options.JobIds.Count == 0)
        {
            return JobGraph.OrderByNeeds(options.Pipeline.Jobs);
        }

        return JobGraph.SelectWithNeeds(options.Pipeline, options.JobIds);
    }

    /// <summary>
    /// Cancelled when the user cancelled; failed when any job failed or timed out; otherwise succeeded.
    /// Jobs skipped by a false condition do not fail the run.
    /// </summary>
    private static Outcome DecideRunOutcome(IReadOnlyList<JobResult> jobResults, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled;
        }

        bool anyFailed = jobResults.Any(result => ExecutionStates.IsFailure(result.Outcome));
        if (anyFailed)
        {
            return Outcome.Failed;
        }

        return Outcome.Succeeded;
    }

    // The time keeps ids sortable for people; the random part keeps two runs that start in the same second apart.
    // Each run writes its step scripts to a folder named after its id, so a shared id would mix the scripts of two runs.
    private static string CreateRunId()
    {
        string time = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string random = RandomNumberGenerator.GetHexString(8, lowercase: true);
        return "local-" + time + "-" + random;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A file is still in use. The folder is under %TEMP%, so it is safe to leave it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
