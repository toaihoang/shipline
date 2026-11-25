using System.Collections;
using Shipline.Core.Pipelines;

namespace Shipline.Core.Execution;

/// <summary>Everything about one run that the scheduler and the job runner share.</summary>
internal sealed class RunContext
{
    public RunContext(LocalRunOptions options, string runId, string scriptFolder, CancellationToken runToken)
    {
        Options = options;
        RunId = runId;
        Workspace = Path.GetFullPath(options.Workspace);
        ScriptFolder = scriptFolder;
        RunToken = runToken;
        ProcessEnvironment = ReadProcessEnvironment();
    }

    public LocalRunOptions Options { get; }

    public PipelineDefinition Pipeline => Options.Pipeline;

    public string RunId { get; }

    public string Workspace { get; }

    public string ScriptFolder { get; }

    /// <summary>Cancelled when the user cancels the whole run (Ctrl+C).</summary>
    public CancellationToken RunToken { get; }

    /// <summary>The environment Shipline started with, read once so every condition in the run sees the same values.</summary>
    public IReadOnlyDictionary<string, string> ProcessEnvironment { get; }

    /// <summary>
    /// The variables a step adds to the process environment, merged from the widest scope to the narrowest:
    /// pipeline, job, step, then the command line. The built-in variables are set last so nothing can change them.
    /// </summary>
    public Dictionary<string, string> BuildVariables(JobDefinition job, StepDefinition? step)
    {
        Dictionary<string, string> variables = new Dictionary<string, string>(StringComparer.Ordinal);
        CopyInto(variables, Pipeline.Env);
        CopyInto(variables, job.Env);
        if (step is not null)
        {
            CopyInto(variables, step.Env);
        }

        CopyInto(variables, Options.Env);

        variables[BuiltInVariables.Ci] = "true";
        variables[BuiltInVariables.Shipline] = "true";
        variables[BuiltInVariables.RunId] = RunId;
        variables[BuiltInVariables.PipelineName] = Pipeline.Name;
        variables[BuiltInVariables.JobId] = job.Id;
        variables[BuiltInVariables.Workspace] = Workspace;
        if (step is not null)
        {
            variables[BuiltInVariables.StepName] = step.Name;
        }

        return variables;
    }

    /// <summary>The full environment a job or step would see, for env.NAME in conditions.</summary>
    public Dictionary<string, string> BuildConditionEnvironment(JobDefinition job, StepDefinition? step)
    {
        Dictionary<string, string> environment = new Dictionary<string, string>(ProcessEnvironment, EnvironmentNameComparer());
        CopyInto(environment, BuildVariables(job, step));
        return environment;
    }

    private static Dictionary<string, string> ReadProcessEnvironment()
    {
        Dictionary<string, string> environment = new Dictionary<string, string>(EnvironmentNameComparer());
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string? name = entry.Key as string;
            string? value = entry.Value as string;
            if (name is not null && value is not null)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    // Windows treats "Path" and "PATH" as the same variable; Linux and macOS do not.
    private static StringComparer EnvironmentNameComparer()
    {
        if (OperatingSystem.IsWindows())
        {
            return StringComparer.OrdinalIgnoreCase;
        }

        return StringComparer.Ordinal;
    }

    private static void CopyInto(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (KeyValuePair<string, string> variable in source)
        {
            target[variable.Key] = variable.Value;
        }
    }
}
