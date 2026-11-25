using Shipline.Core.Pipelines;
using Shipline.Core.Pipelines.Conditions;

namespace Shipline.Core.Tests;

/// <summary>Builds pipeline models in code, for tests that run them.</summary>
internal static class PipelineBuilder
{
    public static PipelineDefinition Pipeline(params JobDefinition[] jobs)
    {
        return Pipeline(jobs, env: null);
    }

    public static PipelineDefinition Pipeline(
        JobDefinition[] jobs,
        Dictionary<string, string>? env = null,
        bool failFast = false,
        int? maxParallel = null)
    {
        return new PipelineDefinition
        {
            Name = "test",
            Env = env ?? new Dictionary<string, string>(),
            FailFast = failFast,
            MaxParallel = maxParallel,
            Jobs = jobs,
        };
    }

    public static JobDefinition Job(string id, params StepDefinition[] steps)
    {
        return Job(id, Array.Empty<string>(), steps);
    }

    public static JobDefinition Job(string id, string[] needs, params StepDefinition[] steps)
    {
        return Job(id, steps, needs: needs);
    }

    public static JobDefinition Job(
        string id,
        StepDefinition[] steps,
        string[]? needs = null,
        string? when = null,
        TimeSpan? timeout = null,
        Dictionary<string, string>? env = null)
    {
        return new JobDefinition
        {
            Id = id,
            Name = id,
            RunsOn = Array.Empty<string>(),
            Needs = needs ?? Array.Empty<string>(),
            When = ParseCondition(when),
            Timeout = timeout ?? JobDefinition.DefaultTimeout,
            Env = env ?? new Dictionary<string, string>(),
            Steps = steps,
            Location = new SourceLocation(1, 1),
        };
    }

    public static StepDefinition Step(
        string run,
        string? when = null,
        ShellKind? shell = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        Dictionary<string, string>? env = null,
        bool continueOnError = false)
    {
        return new StepDefinition
        {
            Name = run,
            Run = run,
            When = ParseCondition(when),
            Shell = shell,
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            Env = env ?? new Dictionary<string, string>(),
            ContinueOnError = continueOnError,
            Location = new SourceLocation(1, 1),
        };
    }

    public static Dictionary<string, string> Env(params string[] namesAndValues)
    {
        Dictionary<string, string> env = new Dictionary<string, string>();
        for (int index = 0; index < namesAndValues.Length; index += 2)
        {
            env[namesAndValues[index]] = namesAndValues[index + 1];
        }

        return env;
    }

    private static Condition? ParseCondition(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (!Condition.TryParse(text, out Condition? condition, out ConditionError? error))
        {
            throw new ArgumentException("Invalid test condition: " + error.Message, nameof(text));
        }

        return condition;
    }
}
