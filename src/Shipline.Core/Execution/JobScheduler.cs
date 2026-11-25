using Shipline.Core.Pipelines;
using Shipline.Core.Pipelines.Conditions;

namespace Shipline.Core.Execution;

/// <summary>
/// Decides which job runs when. Jobs whose needs are all finished start right away, in parallel, up to max-parallel.
/// </summary>
/// <remarks>
/// One coordinator loop owns all job states; jobs run on other threads and only hand back a result. Because a single
/// loop makes every state change, no locks are needed around the states:
/// <code>
/// loop:
///   1. settle pending jobs: start, skip or cancel each one whose needs are finished (repeat while anything changes,
///      because a skipped job can make the jobs that need it ready)
///   2. if nothing is running, stop: every job is final
///   3. wait for any running job to finish, record its result, maybe trigger fail-fast
/// </code>
/// Jenkins runs the branches of a "parallel" block in a similar way, as separate CPS threads under one pipeline.
/// </remarks>
internal sealed class JobScheduler : IDisposable
{
    private readonly RunContext _context;
    private readonly IRunReporter _reporter;
    private readonly JobRunner _jobRunner;
    private readonly int _maxParallel;
    private readonly List<JobRun> _jobs = new List<JobRun>();
    private readonly Dictionary<string, JobRun> _jobsById = new Dictionary<string, JobRun>(StringComparer.Ordinal);
    private readonly Dictionary<Task<JobResult>, JobRun> _running = new Dictionary<Task<JobResult>, JobRun>();
    private readonly CancellationTokenSource _failFastSource = new CancellationTokenSource();
    private string? _failFastCause;

    public JobScheduler(RunContext context, IRunReporter reporter, IReadOnlyList<JobDefinition> jobs)
    {
        _context = context;
        _reporter = reporter;
        _jobRunner = new JobRunner(context, reporter, () => _failFastCause);
        _maxParallel = context.Pipeline.MaxParallel ?? Environment.ProcessorCount;
        foreach (JobDefinition job in jobs)
        {
            JobRun jobRun = new JobRun(job);
            _jobs.Add(jobRun);
            _jobsById[job.Id] = jobRun;
        }
    }

    public async Task<IReadOnlyList<JobResult>> RunAsync()
    {
        while (true)
        {
            SettlePendingJobs();
            if (_running.Count == 0)
            {
                break;
            }

            Task<JobResult> finishedTask = await Task.WhenAny(_running.Keys).ConfigureAwait(false);
            JobRun finishedJob = _running[finishedTask];
            _running.Remove(finishedTask);
            JobResult result = await finishedTask.ConfigureAwait(false);
            finishedJob.Finish(result);
            TriggerFailFastIfNeeded(result);
        }

        return CollectResults();
    }

    public void Dispose()
    {
        _failFastSource.Dispose();
    }

    private void SettlePendingJobs()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (JobRun jobRun in _jobs)
            {
                if (jobRun.State == ExecutionState.Pending && TrySettle(jobRun))
                {
                    changed = true;
                }
            }
        }
    }

    /// <summary>Starts, skips or cancels a pending job. Returns false when the job has to keep waiting.</summary>
    private bool TrySettle(JobRun jobRun)
    {
        JobDefinition job = jobRun.Job;
        if (_context.RunToken.IsCancellationRequested)
        {
            FinishWithoutStarting(jobRun, Outcome.Cancelled, "The run was cancelled.");
            return true;
        }

        if (!AllNeedsFinal(job))
        {
            return false;
        }

        bool reactsToFailures = job.When is not null && job.When.UsesStatusFunction;
        if (_failFastCause is not null && !reactsToFailures)
        {
            FinishWithoutStarting(jobRun, Outcome.Cancelled, "Not started: fail-fast stopped the run after job \"" + _failFastCause + "\" failed.");
            return true;
        }

        ConditionContext conditionContext = BuildConditionContext(job);
        if (!Condition.ShouldRun(job.When, conditionContext))
        {
            FinishWithoutStarting(jobRun, Outcome.Skipped, DescribeSkip(job, conditionContext.Success));
            return true;
        }

        if (_running.Count >= _maxParallel)
        {
            return false;
        }

        Start(jobRun, reactsToFailures);
        return true;
    }

    private void Start(JobRun jobRun, bool reactsToFailures)
    {
        jobRun.MoveTo(ExecutionState.Running);

        // A job that reacts to failures (failure(), always()) is a clean-up or report job: fail-fast must not stop it.
        CancellationToken stopToken = _context.RunToken;
        if (!reactsToFailures)
        {
            jobRun.StopSource = CancellationTokenSource.CreateLinkedTokenSource(_context.RunToken, _failFastSource.Token);
            stopToken = jobRun.StopSource.Token;
        }

        // Task.Run so that starting the job's first process does not hold up the coordinator loop.
        Task<JobResult> task = Task.Run(() => _jobRunner.RunAsync(jobRun.Job, stopToken), CancellationToken.None);
        _running[task] = jobRun;
    }

    private void TriggerFailFastIfNeeded(JobResult result)
    {
        if (!_context.Pipeline.FailFast || _failFastCause is not null)
        {
            return;
        }

        if (!ExecutionStates.IsFailure(result.Outcome))
        {
            return;
        }

        _failFastCause = result.Job.Id;
        _failFastSource.Cancel();
    }

    private bool AllNeedsFinal(JobDefinition job)
    {
        foreach (string need in job.Needs)
        {
            if (!ExecutionStates.IsFinal(_jobsById[need].State))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// success(): every job this job needs succeeded.
    /// failure(): a job it needs, directly or through other jobs, failed or timed out.
    /// </summary>
    private ConditionContext BuildConditionContext(JobDefinition job)
    {
        bool allNeedsSucceeded = job.Needs.All(need => _jobsById[need].State == ExecutionState.Succeeded);
        return new ConditionContext
        {
            Branch = _context.Options.Branch,
            Event = _context.Options.Event,
            Env = _context.BuildConditionEnvironment(job, null),
            Success = allNeedsSucceeded,
            Failure = AnyAncestorFailed(job),
        };
    }

    private bool AnyAncestorFailed(JobDefinition job)
    {
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        Stack<string> toVisit = new Stack<string>(job.Needs);
        while (toVisit.Count > 0)
        {
            string id = toVisit.Pop();
            if (!visited.Add(id))
            {
                continue;
            }

            JobRun ancestor = _jobsById[id];
            if (ancestor.State == ExecutionState.Failed || ancestor.State == ExecutionState.TimedOut)
            {
                return true;
            }

            foreach (string need in ancestor.Job.Needs)
            {
                toVisit.Push(need);
            }
        }

        return false;
    }

    private string DescribeSkip(JobDefinition job, bool success)
    {
        bool skippedByNeeds = job.When is null || (!job.When.UsesStatusFunction && !success);
        if (!skippedByNeeds)
        {
            return "The condition \"" + job.When + "\" is false.";
        }

        string failedNeed = job.Needs.First(need => _jobsById[need].State != ExecutionState.Succeeded);
        return "Needs \"" + failedNeed + "\", which did not succeed.";
    }

    private void FinishWithoutStarting(JobRun jobRun, Outcome outcome, string message)
    {
        JobResult result = new JobResult
        {
            Job = jobRun.Job,
            Outcome = outcome,
            Steps = Array.Empty<StepResult>(),
            Message = message,
        };
        jobRun.FinishWithoutStarting(result);
        _reporter.JobFinished(result);
    }

    private List<JobResult> CollectResults()
    {
        List<JobResult> results = new List<JobResult>();
        foreach (JobRun jobRun in _jobs)
        {
            if (jobRun.Result is null)
            {
                throw new InvalidOperationException("Job \"" + jobRun.Job.Id + "\" never finished. This is a bug in the scheduler.");
            }

            results.Add(jobRun.Result);
        }

        return results;
    }

    /// <summary>The live state of one job in this run.</summary>
    private sealed class JobRun
    {
        public JobRun(JobDefinition job)
        {
            Job = job;
        }

        public JobDefinition Job { get; }

        public ExecutionState State { get; private set; } = ExecutionState.Pending;

        public JobResult? Result { get; private set; }

        /// <summary>Links the run token and the fail-fast token for this job; disposed when the job finishes.</summary>
        public CancellationTokenSource? StopSource { get; set; }

        public void MoveTo(ExecutionState next)
        {
            if (!ExecutionStates.CanMove(State, next))
            {
                throw new InvalidOperationException("Job \"" + Job.Id + "\" cannot move from " + State + " to " + next + ".");
            }

            State = next;
        }

        public void Finish(JobResult result)
        {
            MoveTo(ExecutionStates.FromOutcome(result.Outcome));
            Result = result;
            if (StopSource is not null)
            {
                StopSource.Dispose();
                StopSource = null;
            }
        }

        public void FinishWithoutStarting(JobResult result)
        {
            MoveTo(ExecutionStates.FromOutcome(result.Outcome));
            Result = result;
        }
    }
}
