using System.Diagnostics;
using System.Globalization;
using Shipline.Core.Execution;
using Shipline.Core.Pipelines;
using static Shipline.Core.Tests.PipelineBuilder;

namespace Shipline.Core.Tests.Execution;

public sealed class LocalRunnerTests : IDisposable
{
    private readonly TempFolder _workspace = new TempFolder();
    private readonly RecordingReporter _reporter = new RecordingReporter();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    [Fact]
    public async Task Run_AllStepsSucceed_RunSucceeds()
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step("echo hello"), Step("echo world")));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "hello", "world" }, _reporter.Output);
        Assert.Equal("run finished Succeeded", _reporter.Events[_reporter.Events.Count - 1]);
    }

    [Fact]
    public async Task Run_StepFails_SkipsRestOfJobAndJobsThatNeedIt()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("exit 3"), Step("echo never")),
            Job("test", new[] { "build" }, Step("echo never either")),
            Job("lint", Step("echo lint")));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Failed, result.Outcome);
        JobResult build = FindJob(result, "build");
        Assert.Equal(Outcome.Failed, build.Outcome);
        Assert.Equal(3, build.Steps[0].ExitCode);
        Assert.Equal("Exit code 3.", build.Steps[0].Message);
        Assert.Equal(Outcome.Skipped, build.Steps[1].Outcome);
        Assert.Equal(Outcome.Skipped, FindJob(result, "test").Outcome);
        Assert.Equal(Outcome.Succeeded, FindJob(result, "lint").Outcome);
        Assert.Equal(new[] { "lint" }, _reporter.Output);
    }

    [Fact]
    public async Task Run_ContinueOnError_JobGoesOn()
    {
        StepDefinition failing = Step("exit 1", continueOnError: true);
        PipelineDefinition pipeline = Pipeline(Job("build", failing, Step("echo after")));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        StepResult failed = result.Jobs[0].Steps[0];
        Assert.Equal(Outcome.Failed, failed.Outcome);
        Assert.True(failed.FailureIgnored);
        Assert.Equal(new[] { "after" }, _reporter.Output);
    }

    [Fact]
    public async Task Run_Environment_NarrowestScopeWins_AndBuiltInsAreSet()
    {
        string[] names = { "A", "B", "C", "D", "CI", "SHIPLINE_JOB", "SHIPLINE_WORKSPACE" };
        string script = string.Join("\n", names.Select(Scripts.PrintVariable));
        StepDefinition step = Step(script, env: Env("C", "step"));
        JobDefinition job = Job("build", new[] { step }, env: Env("B", "job", "C", "job"));
        PipelineDefinition pipeline = Pipeline(new[] { job }, env: Env("A", "pipeline", "B", "pipeline"));

        RunResult result = await RunAsync(pipeline, commandLineEnv: Env("A", "command line", "D", "command line"));

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(
            new[]
            {
                "A=command line",
                "B=job",
                "C=step",
                "D=command line",
                "CI=true",
                "SHIPLINE_JOB=build",
                "SHIPLINE_WORKSPACE=" + _workspace.Path,
            },
            _reporter.Output);
    }

    [Fact]
    public async Task Run_WorkingDirectory_IsRelativeToWorkspace()
    {
        Directory.CreateDirectory(_workspace.Combine("src"));
        PipelineDefinition pipeline = Pipeline(Job("build", Step(Scripts.PrintWorkingDirectory(), workingDirectory: "src")));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(_workspace.Combine("src"), Assert.Single(_reporter.Output));
    }

    [Fact]
    public async Task Run_MissingWorkingDirectory_FailsStep()
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step("echo x", workingDirectory: "missing")));

        RunResult result = await RunAsync(pipeline);

        StepResult step = result.Jobs[0].Steps[0];
        Assert.Equal(Outcome.Failed, step.Outcome);
        Assert.Contains("does not exist", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_StepTimeout_StopsStep()
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step(Scripts.Sleep(60), timeout: TimeSpan.FromSeconds(2))));

        Stopwatch stopwatch = Stopwatch.StartNew();
        RunResult result = await RunAsync(pipeline);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "The step was not stopped in time.");
        Assert.Equal(Outcome.Failed, result.Outcome);
        StepResult step = result.Jobs[0].Steps[0];
        Assert.Equal(Outcome.TimedOut, step.Outcome);
        Assert.Equal("The step timed out after 2.0s.", step.Message);
    }

    [Fact]
    public async Task Run_JobTimeout_StopsJob()
    {
        JobDefinition job = Job("build", new[] { Step(Scripts.Sleep(60)), Step("echo never") }, timeout: TimeSpan.FromSeconds(2));

        RunResult result = await RunAsync(Pipeline(job));

        JobResult jobResult = result.Jobs[0];
        Assert.Equal(Outcome.TimedOut, jobResult.Outcome);
        Assert.Equal("The job timed out after 2.0s.", jobResult.Steps[0].Message);
        Assert.Equal(Outcome.Skipped, jobResult.Steps[1].Outcome);
    }

    [Fact]
    public async Task Run_Cancelled_StopsStepAndDoesNotStartNextJob()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step(Scripts.Sleep(60))),
            Job("test", new[] { "build" }, Step("echo never")));
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        RunResult result = await RunAsync(pipeline, cancellationToken: cancellation.Token);

        Assert.Equal(Outcome.Cancelled, result.Outcome);
        Assert.Equal(Outcome.Cancelled, FindJob(result, "build").Outcome);
        JobResult test = FindJob(result, "test");
        Assert.Equal(Outcome.Cancelled, test.Outcome);
        Assert.Equal("The run was cancelled.", test.Message);
        Assert.Empty(_reporter.Output);
    }

    [Fact]
    public async Task Run_StepReadsInput_GetsEndOfInputInsteadOfHanging()
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step(Scripts.ReadOneLineOrPrintEof(), timeout: TimeSpan.FromSeconds(20))));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "eof" }, _reporter.Output);
    }

    [Fact]
    public async Task Run_SelectedJob_RunsItAndItsNeedsOnly()
    {
        PipelineDefinition pipeline = Pipeline(
            Job("build", Step("echo build")),
            Job("lint", Step("echo lint")),
            Job("test", new[] { "build" }, Step("echo test")));

        RunResult result = await RunAsync(pipeline, jobIds: new[] { "test" });

        Assert.Equal(new[] { "build", "test" }, result.Jobs.Select(job => job.Job.Id));
        Assert.Equal(new[] { "build", "test" }, _reporter.Output);
    }

    [WindowsFact]
    public async Task Run_CmdShell_ReportsExitCode()
    {
        PipelineDefinition pipeline = Pipeline(Job("build", Step("echo from cmd\r\nexit /b 4", shell: ShellKind.Cmd)));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(4, result.Jobs[0].Steps[0].ExitCode);
        Assert.Equal(new[] { "from cmd" }, _reporter.Output);
    }

    [WindowsFact]
    public async Task Run_StepLeavesBackgroundProcess_ProcessIsKilled()
    {
        // The child shares the step's output pipe, so without the kill the run would also wait for it forever.
        string script = StartBackgroundChildScript() + "\nWrite-Output 'parent done'";
        PipelineDefinition pipeline = Pipeline(Job("build", Step(script)));

        Stopwatch stopwatch = Stopwatch.StartNew();
        RunResult result = await RunAsync(pipeline);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), "The run waited for the background process.");
        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.True(await HasExitedAsync(ReadChildProcessId()), "The background process is still running.");
    }

    [WindowsFact]
    public async Task Run_StepTimesOut_GrandchildIsKilled()
    {
        string script = StartBackgroundChildScript() + "\nStart-Sleep -Seconds 120";
        PipelineDefinition pipeline = Pipeline(Job("build", Step(script, timeout: TimeSpan.FromSeconds(5))));

        RunResult result = await RunAsync(pipeline);

        Assert.Equal(Outcome.TimedOut, result.Jobs[0].Steps[0].Outcome);
        Assert.True(await HasExitedAsync(ReadChildProcessId()), "The grandchild process is still running.");
    }

    private static string StartBackgroundChildScript()
    {
        return "$child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -NoNewWindow -PassThru\n"
            + "Write-Output \"child=$($child.Id)\"";
    }

    private int ReadChildProcessId()
    {
        string line = Assert.Single(_reporter.Output, text => text.StartsWith("child=", StringComparison.Ordinal));
        return int.Parse(line.AsSpan("child=".Length), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasExitedAsync(int processId)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!IsRunning(processId))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<RunResult> RunAsync(
        PipelineDefinition pipeline,
        IReadOnlyList<string>? jobIds = null,
        IReadOnlyDictionary<string, string>? commandLineEnv = null,
        CancellationToken cancellationToken = default)
    {
        LocalRunOptions options = new LocalRunOptions
        {
            Pipeline = pipeline,
            Workspace = _workspace.Path,
            JobIds = jobIds ?? Array.Empty<string>(),
            Env = commandLineEnv ?? new Dictionary<string, string>(),
        };

        LocalRunner runner = new LocalRunner(_reporter);
        return await runner.RunAsync(options, cancellationToken);
    }

    private static JobResult FindJob(RunResult result, string id)
    {
        return result.Jobs.Single(job => job.Job.Id == id);
    }
}
