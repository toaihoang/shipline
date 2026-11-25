using System.CommandLine;
using Shipline.Core.Execution;
using Shipline.Core.Pipelines;

namespace Shipline.Cli;

/// <summary>"shipline run": runs the pipeline on this machine, without a server.</summary>
internal static class RunCommand
{
    public static Command Create(Option<FileInfo> fileOption)
    {
        Option<string[]> jobOption = new Option<string[]>("--job", "-j")
        {
            Description = "Run only this job and the jobs it needs. Can be repeated.",
        };
        Option<string[]> envOption = new Option<string[]>("--env", "-e")
        {
            Description = "Set an environment variable as KEY=VALUE. Wins over the file. Can be repeated.",
        };
        Option<string> branchOption = new Option<string>("--branch", "-b")
        {
            Description = "The branch that \"when\" conditions see. Default: the Git branch of the pipeline folder.",
        };

        Command command = new Command("run", "Run the pipeline on this machine.");
        command.Options.Add(fileOption);
        command.Options.Add(jobOption);
        command.Options.Add(envOption);
        command.Options.Add(branchOption);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            new RunArguments
            {
                File = parseResult.GetValue(fileOption),
                JobIds = parseResult.GetValue(jobOption) ?? Array.Empty<string>(),
                EnvArguments = parseResult.GetValue(envOption) ?? Array.Empty<string>(),
                Branch = parseResult.GetValue(branchOption),
            },
            cancellationToken));
        return command;
    }

    private static async Task<int> ExecuteAsync(RunArguments arguments, CancellationToken cancellationToken)
    {
        PipelineFile? pipelineFile = PipelineFile.Load(arguments.File);
        if (pipelineFile is null)
        {
            return ExitCodes.InvalidInput;
        }

        PipelineDefinition? pipeline = pipelineFile.Result.Pipeline;
        if (pipeline is null)
        {
            pipelineFile.WriteDiagnostics(Console.Error);
            return ExitCodes.InvalidInput;
        }

        if (!CheckJobIds(pipeline, arguments.JobIds))
        {
            return ExitCodes.InvalidInput;
        }

        Dictionary<string, string>? env = ParseEnvArguments(arguments.EnvArguments);
        if (env is null)
        {
            return ExitCodes.InvalidInput;
        }

        string branch = arguments.Branch ?? GitBranch.Detect(pipelineFile.Workspace);
        LocalRunOptions options = new LocalRunOptions
        {
            Pipeline = pipeline,
            Workspace = pipelineFile.Workspace,
            JobIds = arguments.JobIds,
            Env = env,
            Branch = branch,
        };

        LocalRunner runner = new LocalRunner(new ConsoleReporter(branch));
        RunResult result = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
        return ToExitCode(result.Outcome);
    }

    private static bool CheckJobIds(PipelineDefinition pipeline, string[] jobIds)
    {
        foreach (string jobId in jobIds)
        {
            if (pipeline.FindJob(jobId) is null)
            {
                string knownJobs = string.Join(", ", pipeline.Jobs.Select(job => job.Id));
                Console.Error.WriteLine("error: Unknown job \"" + jobId + "\". Jobs in this pipeline: " + knownJobs + ".");
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string>? ParseEnvArguments(string[] envArguments)
    {
        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string argument in envArguments)
        {
            int equalsIndex = argument.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex <= 0)
            {
                Console.Error.WriteLine("error: --env \"" + argument + "\" must look like KEY=VALUE.");
                return null;
            }

            string name = argument.Substring(0, equalsIndex);
            if (BuiltInVariables.IsReserved(name))
            {
                Console.Error.WriteLine("error: --env cannot set \"" + name + "\"; it is set by Shipline.");
                return null;
            }

            env[name] = argument.Substring(equalsIndex + 1);
        }

        return env;
    }

    private static int ToExitCode(Outcome outcome)
    {
        if (outcome == Outcome.Succeeded)
        {
            return ExitCodes.Succeeded;
        }

        if (outcome == Outcome.Cancelled)
        {
            return ExitCodes.Cancelled;
        }

        return ExitCodes.Failed;
    }

    private sealed class RunArguments
    {
        public FileInfo? File { get; init; }

        public required string[] JobIds { get; init; }

        public required string[] EnvArguments { get; init; }

        /// <summary>The --branch value, or null to ask Git.</summary>
        public string? Branch { get; init; }
    }
}
