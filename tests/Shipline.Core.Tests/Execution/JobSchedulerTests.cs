using System.Diagnostics;
using Shipline.Core.Execution;
using Shipline.Core.Pipelines;
using static Shipline.Core.Tests.PipelineBuilder;

namespace Shipline.Core.Tests.Execution;

public sealed class JobSchedulerTests : IDisposable
{
    private readonly TempFolder _workspace = new TempFolder();
    private readonly RecordingReporter _reporter = new RecordingReporter();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    [Fact]
    public async Task IndependentJobs_RunAtTheSameTime()
    {
        PipelineDefinition pipeline = Pipeline(
            new[]
            {
                Job("a", Step(Scripts.Handshake("a.txt", "b.txt", 20))),
                Job("b", Step(Scripts.Handshake("b.txt", "a.txt", 20))),
            },
            maxParallel: 2);

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "met partner", "met partner" }, _reporter.Output);
    }

    [Fact]
    public async Task MaxParallelOne_RunsJobsOneAtATime()
    {
        PipelineDefinition pipeline = Pipeline(
            new[]
            {
                Job("a", Step(Scripts.Handshake("a.txt", "b.txt", 3))),
                Job("b", Step(Scripts.Handshake("b.txt", "a.txt", 3))),
            },
            maxParallel: 1);

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Failed, FindJob(result, "a").Outcome);
        Assert.Equal(Outcome.Succeeded, FindJob(result, "b").Outcome);
    }

    [Fact]
    public async Task Job_StartsOnlyAfterTheJobsItNeedsFinish()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("echo build")),
            Job("test", new[] { "build" }, Step("echo test")),
            Job("deploy", new[] { "test" }, Step("echo deploy")));

        await RunAsync(pipeline);

        List<string> events = _reporter.Events.ToList();
        Assert.True(events.IndexOf("job finished build Succeeded") < events.IndexOf("job started test"));
        Assert.True(events.IndexOf("job finished test Succeeded") < events.IndexOf("job started deploy"));
    }

    [Theory]
    [InlineData("main", Outcome.Succeeded)]
    [InlineData("develop", Outcome.Skipped)]
    public async Task When_DecidesWhetherTheJobRuns(string branch, Outcome expected)
    {
        PipelineDefinition pipeline = Pipeline(Job("deploy", new[] { Step("echo deploy") }, when: "branch == 'main'"));

        RunResult result = await RunAsync(pipeline, branch: branch);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        JobResult deploy = FindJob(result, "deploy");
        Assert.Equal(expected, deploy.Outcome);
        if (expected == Outcome.Skipped)
        {
            Assert.Equal("The condition \"branch == 'main'\" is false.", deploy.Message);
        }
    }

    [Fact]
    public async Task FailureJob_RunsAfterAFailure_AndNormalJobsAreSkipped()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("exit 1")),
            Job("notify", new[] { Step("echo notify") }, needs: new[] { "build" }, when: "failure()"),
            Job("deploy", new[] { "build" }, Step("echo deploy")));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal(Outcome.Succeeded, FindJob(result, "notify").Outcome);
        JobResult deploy = FindJob(result, "deploy");
        Assert.Equal(Outcome.Skipped, deploy.Outcome);
        Assert.Equal("Needs \"build\", which did not succeed.", deploy.Message);
        Assert.Equal(new[] { "notify" }, _reporter.Output);
    }

    [Fact]
    public async Task FailureJob_IsSkippedWhenNothingFailed()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("echo build")),
            Job("notify", new[] { Step("echo notify") }, needs: new[] { "build" }, when: "failure()"));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(Outcome.Skipped, FindJob(result, "notify").Outcome);
    }

    [Theory]
    [InlineData("exit 0")]
    [InlineData("exit 1")]
    public async Task AlwaysJob_RunsWhateverHappenedBefore(string buildScript)
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step(buildScript)),
            Job("cleanup", new[] { Step("echo cleanup") }, needs: new[] { "build" }, when: "always()"));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, FindJob(result, "cleanup").Outcome);
    }

    [Fact]
    public async Task Failure_SeesAFailedJobFurtherUpTheChain()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("exit 1")),
            Job("test", new[] { "build" }, Step("echo test")),
            Job("report", new[] { Step("echo report") }, needs: new[] { "test" }, when: "failure()"));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Skipped, FindJob(result, "test").Outcome);
        Assert.Equal(Outcome.Succeeded, FindJob(result, "report").Outcome);
    }

    [Fact]
    public async Task FailFast_StopsOtherJobs_ButStillRunsJobsThatReactToFailure()
    {
        PipelineDefinition pipeline = Pipeline(
            new[]
            {
                Job("slow", Step(Scripts.Sleep(60))),
                Job("broken", Step(Scripts.Sleep(1) + "\nexit 1")),
                Job("after-slow", new[] { "slow" }, Step("echo never")),
                Job("report", new[] { Step("echo report") }, needs: new[] { "slow", "broken" }, when: "always()"),
            },
            failFast: true,
            maxParallel: 4);

        Stopwatch stopwatch = Stopwatch.StartNew();
        RunResult result = await RunAsync(pipeline);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "fail-fast did not stop the slow job.");
        Assert.Equal(Outcome.Failed, result.Outcome);
        JobResult slow = FindJob(result, "slow");
        Assert.Equal(Outcome.Cancelled, slow.Outcome);
        Assert.Equal("Stopped by fail-fast: job \"broken\" failed.", slow.Steps[0].Message);
        JobResult afterSlow = FindJob(result, "after-slow");
        Assert.Equal(Outcome.Cancelled, afterSlow.Outcome);
        Assert.Equal("Not started: fail-fast stopped the run after job \"broken\" failed.", afterSlow.Message);
        Assert.Equal(Outcome.Succeeded, FindJob(result, "report").Outcome);
        Assert.Equal(new[] { "report" }, _reporter.Output);
    }

    [Fact]
    public async Task StepWhen_RunsFailureAndAlwaysStepsAfterAFailedStep()
    {
        JobDefinition job = Job(
            "build",
            Step("exit 1"),
            Step("echo normal"),
            Step("echo 'on failure'", when: "failure()"),
            Step("echo always", when: "always()"),
            Step("echo 'on success'", when: "success()"));

        RunResult result = await RunAsync(Pipeline(job));

        JobResult build = result.Jobs[0];
        Assert.Equal(Outcome.Failed, build.Outcome);
        Assert.Equal(new[] { "on failure", "always" }, _reporter.Output);
        Assert.Equal("An earlier step failed.", build.Steps[1].Message);
        Assert.Equal("The condition \"success()\" is false.", build.Steps[4].Message);
    }

    [Theory]
    [InlineData("yes", Outcome.Succeeded)]
    [InlineData("no", Outcome.Skipped)]
    public async Task StepWhen_ReadsTheEnvironment(string deploy, Outcome expected)
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step("echo deploying", when: "env.DEPLOY == 'yes'")));

        RunResult result = await RunAsync(pipeline, env: Env("DEPLOY", deploy));

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Jobs[0].Steps[0].Outcome);
    }

    [Fact]
    public async Task TwoRunsAtTheSameTime_KeepTheirStepScriptsApart()
    {
        // Same job id, same step number, started in the same second: the scripts must not overwrite each other.
        RecordingReporter firstReporter = new RecordingReporter();
        RecordingReporter secondReporter = new RecordingReporter();
        Task<RunResult> first = new LocalRunner(firstReporter).RunAsync(Options(Job("build", Step(Scripts.Sleep(2) + "\necho first"))), CancellationToken.None);
        Task<RunResult> second = new LocalRunner(secondReporter).RunAsync(Options(Job("build", Step(Scripts.Sleep(2) + "\necho second"))), CancellationToken.None);

        RunResult[] results = await Task.WhenAll(first, second);

        Assert.NotEqual(results[0].RunId, results[1].RunId);
        Assert.Equal(new[] { "first" }, firstReporter.Output);
        Assert.Equal(new[] { "second" }, secondReporter.Output);
    }

    private LocalRunOptions Options(JobDefinition job)
    {
        return new LocalRunOptions { Pipeline = Pipeline(job), Workspace = _workspace.Path };
    }

    private async Task<RunResult> RunAsync(PipelineDefinition pipeline, string branch = "", Dictionary<string, string>? env = null)
    {
        LocalRunOptions options = new LocalRunOptions
        {
            Pipeline = pipeline,
            Workspace = _workspace.Path,
            Branch = branch,
            Env = env ?? new Dictionary<string, string>(),
        };

        LocalRunner runner = new LocalRunner(_reporter);
        return await runner.RunAsync(options, CancellationToken.None);
    }

    private static JobResult FindJob(RunResult result, string id)
    {
        return result.Jobs.Single(job => job.Job.Id == id);
    }
}
