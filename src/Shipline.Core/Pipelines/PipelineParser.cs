using System.Globalization;
using System.Text.RegularExpressions;
using Shipline.Core.Pipelines.Conditions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Shipline.Core.Pipelines;

/// <summary>The outcome of reading a pipeline file: either a pipeline, or the list of errors.</summary>
public sealed class PipelineParseResult
{
    internal PipelineParseResult(PipelineDefinition? pipeline, IReadOnlyList<PipelineDiagnostic> diagnostics)
    {
        Pipeline = pipeline;
        Diagnostics = diagnostics;
    }

    /// <summary>The pipeline, or null when the file has errors.</summary>
    public PipelineDefinition? Pipeline { get; }

    /// <summary>The errors, sorted by position in the file. Empty when the file is valid.</summary>
    public IReadOnlyList<PipelineDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Reads a .shipline.yml file.
/// </summary>
/// <remarks>
/// The YAML is loaded as a tree of nodes that remember their line and column, and the tree is then mapped to the
/// model by hand. A reflection-based deserializer would be shorter, but it either ignores unknown keys (so a typo such
/// as "stpes" silently does nothing) or fails on the first problem without a useful position. Here every problem in
/// the file is reported at once, with its position and, for typos, the key that was probably meant.
/// </remarks>
public sealed partial class PipelineParser
{
    private static readonly string[] s_rootKeys = { "name", "env", "defaults", "fail-fast", "max-parallel", "jobs" };
    private static readonly string[] s_defaultsKeys = { "shell" };
    private static readonly string[] s_jobKeys = { "name", "runs-on", "needs", "when", "timeout", "env", "steps" };
    private static readonly string[] s_stepKeys = { "name", "run", "when", "shell", "working-directory", "timeout", "env", "continue-on-error" };

    // Keys that later phases will support. They get a clearer message than "unknown key".
    private static readonly string[] s_plannedRootKeys = { "on" };
    private static readonly string[] s_plannedJobKeys = { "artifacts", "environment", "approval", "image" };
    private static readonly string[] s_plannedStepKeys = Array.Empty<string>();

    private const int MaxStepNameLength = 60;

    private readonly List<PipelineDiagnostic> _diagnostics = new List<PipelineDiagnostic>();
    private readonly string _defaultName;

    // "needs" can be checked only after all jobs are read, so their nodes are kept until then.
    // They are checked against every declared job id, including jobs that have errors of their own.
    private readonly List<YamlNode> _needsNodes = new List<YamlNode>();
    private readonly HashSet<string> _declaredJobIds = new HashSet<string>(StringComparer.Ordinal);

    private PipelineParser(string defaultName)
    {
        _defaultName = defaultName;
    }

    /// <summary>Reads pipeline YAML.</summary>
    /// <param name="yaml">The content of the pipeline file.</param>
    /// <param name="defaultName">The pipeline name to use when the file has no "name", usually the folder name.</param>
    public static PipelineParseResult Parse(string yaml, string defaultName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        PipelineParser parser = new PipelineParser(defaultName);
        PipelineDefinition? pipeline = parser.ReadDocument(yaml);
        List<PipelineDiagnostic> diagnostics = parser._diagnostics
            .OrderBy(diagnostic => diagnostic.Location.Line)
            .ThenBy(diagnostic => diagnostic.Location.Column)
            .ToList();

        if (diagnostics.Count > 0)
        {
            return new PipelineParseResult(null, diagnostics);
        }

        return new PipelineParseResult(pipeline, diagnostics);
    }

    private PipelineDefinition? ReadDocument(string yaml)
    {
        YamlStream stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            AddError(ToLocation(exception.Start), exception.Message);
            return null;
        }

        if (stream.Documents.Count == 0)
        {
            AddError(new SourceLocation(1, 1), "The file is empty. A pipeline needs at least a \"jobs\" section.");
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            AddError(stream.Documents[1].RootNode, "A pipeline file holds one YAML document; remove the second \"---\" section.");
        }

        YamlMappingNode? root = AsMapping(stream.Documents[0].RootNode, "The pipeline");
        if (root is null)
        {
            return null;
        }

        return ReadPipeline(root);
    }

    private PipelineDefinition? ReadPipeline(YamlMappingNode root)
    {
        string name = _defaultName;
        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
        ShellKind? defaultShell = null;
        bool failFast = false;
        int? maxParallel = null;
        List<JobDefinition>? jobs = null;
        YamlNode? jobsNode = null;

        foreach (KeyValuePair<string, YamlNode> entry in ReadKeys(root, s_rootKeys, s_plannedRootKeys, "the pipeline"))
        {
            switch (entry.Key)
            {
                case "name":
                    name = ReadRequiredText(entry.Value, "\"name\"") ?? name;
                    break;
                case "env":
                    env = ReadEnv(entry.Value);
                    break;
                case "defaults":
                    defaultShell = ReadDefaults(entry.Value);
                    break;
                case "fail-fast":
                    failFast = ReadBoolean(entry.Value, "\"fail-fast\"");
                    break;
                case "max-parallel":
                    maxParallel = ReadPositiveNumber(entry.Value, "\"max-parallel\"");
                    break;
                case "jobs":
                    jobsNode = entry.Value;
                    jobs = ReadJobs(entry.Value);
                    break;
            }
        }

        if (jobsNode is null)
        {
            AddError(root, "The pipeline has no \"jobs\" section.");
            return null;
        }

        if (jobs is null)
        {
            return null;
        }

        CheckNeeds(jobs);
        return new PipelineDefinition
        {
            Name = name,
            Env = env,
            DefaultShell = defaultShell,
            FailFast = failFast,
            MaxParallel = maxParallel,
            Jobs = jobs,
        };
    }

    private ShellKind? ReadDefaults(YamlNode node)
    {
        YamlMappingNode? mapping = AsMapping(node, "\"defaults\"");
        if (mapping is null)
        {
            return null;
        }

        ShellKind? shell = null;
        foreach (KeyValuePair<string, YamlNode> entry in ReadKeys(mapping, s_defaultsKeys, Array.Empty<string>(), "\"defaults\""))
        {
            shell = ReadShell(entry.Value);
        }

        return shell;
    }

    private List<JobDefinition>? ReadJobs(YamlNode node)
    {
        YamlMappingNode? mapping = AsMapping(node, "\"jobs\"");
        if (mapping is null)
        {
            return null;
        }

        if (mapping.Children.Count == 0)
        {
            AddError(node, "\"jobs\" is empty. Add at least one job.");
            return null;
        }

        List<JobDefinition> jobs = new List<JobDefinition>();
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            string? id = ReadJobId(entry.Key);
            if (id is null)
            {
                continue;
            }

            _declaredJobIds.Add(id);
            JobDefinition? job = ReadJob(id, entry.Key, entry.Value);
            if (job is not null)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    private string? ReadJobId(YamlNode keyNode)
    {
        if (keyNode is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value))
        {
            AddError(keyNode, "A job id must be a plain name.");
            return null;
        }

        string id = scalar.Value;
        if (!JobIdPattern().IsMatch(id))
        {
            AddError(keyNode, "Invalid job id \"" + id + "\". Use letters, digits, \"-\" and \"_\", starting with a letter or \"_\".");
            return null;
        }

        return id;
    }

    private JobDefinition? ReadJob(string id, YamlNode keyNode, YamlNode valueNode)
    {
        string where = "job \"" + id + "\"";
        YamlMappingNode? mapping = AsMapping(valueNode, "The " + where);
        if (mapping is null)
        {
            return null;
        }

        string name = id;
        IReadOnlyList<string> runsOn = Array.Empty<string>();
        IReadOnlyList<string> needs = Array.Empty<string>();
        Condition? when = null;
        TimeSpan timeout = JobDefinition.DefaultTimeout;
        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
        List<StepDefinition>? steps = null;
        bool hasSteps = false;

        foreach (KeyValuePair<string, YamlNode> entry in ReadKeys(mapping, s_jobKeys, s_plannedJobKeys, "a job"))
        {
            switch (entry.Key)
            {
                case "name":
                    name = ReadRequiredText(entry.Value, "\"name\"") ?? name;
                    break;
                case "runs-on":
                    runsOn = ReadTextOrList(entry.Value, "\"runs-on\"");
                    break;
                case "needs":
                    needs = ReadNeeds(entry.Value);
                    break;
                case "when":
                    when = ReadCondition(entry.Value);
                    break;
                case "timeout":
                    timeout = ReadDuration(entry.Value, "\"timeout\"") ?? timeout;
                    break;
                case "env":
                    env = ReadEnv(entry.Value);
                    break;
                case "steps":
                    hasSteps = true;
                    steps = ReadSteps(entry.Value, where);
                    break;
            }
        }

        if (!hasSteps)
        {
            AddError(keyNode, "The " + where + " has no \"steps\".");
            return null;
        }

        if (steps is null)
        {
            return null;
        }

        return new JobDefinition
        {
            Id = id,
            Name = name,
            RunsOn = runsOn,
            Needs = needs,
            When = when,
            Timeout = timeout,
            Env = env,
            Steps = steps,
            Location = ToLocation(keyNode),
        };
    }

    private IReadOnlyList<string> ReadNeeds(YamlNode node)
    {
        IReadOnlyList<string> needs = ReadTextOrList(node, "\"needs\"");
        _needsNodes.Add(node);
        return needs;
    }

    private List<StepDefinition>? ReadSteps(YamlNode node, string jobWhere)
    {
        if (node is not YamlSequenceNode sequence)
        {
            AddError(node, "\"steps\" must be a list. Start each step with \"- \".");
            return null;
        }

        if (sequence.Children.Count == 0)
        {
            AddError(node, "The " + jobWhere + " has an empty \"steps\" list.");
            return null;
        }

        List<StepDefinition> steps = new List<StepDefinition>();
        foreach (YamlNode stepNode in sequence.Children)
        {
            StepDefinition? step = ReadStep(stepNode);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private StepDefinition? ReadStep(YamlNode node)
    {
        YamlMappingNode? mapping = AsMapping(node, "A step");
        if (mapping is null)
        {
            return null;
        }

        string? name = null;
        string? run = null;
        Condition? when = null;
        ShellKind? shell = null;
        string? workingDirectory = null;
        TimeSpan? timeout = null;
        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
        bool continueOnError = false;
        bool hasRun = false;

        foreach (KeyValuePair<string, YamlNode> entry in ReadKeys(mapping, s_stepKeys, s_plannedStepKeys, "a step"))
        {
            switch (entry.Key)
            {
                case "name":
                    name = ReadRequiredText(entry.Value, "\"name\"");
                    break;
                case "run":
                    hasRun = true;
                    run = ReadRequiredText(entry.Value, "\"run\"");
                    break;
                case "when":
                    when = ReadCondition(entry.Value);
                    break;
                case "shell":
                    shell = ReadShell(entry.Value);
                    break;
                case "working-directory":
                    workingDirectory = ReadRequiredText(entry.Value, "\"working-directory\"");
                    break;
                case "timeout":
                    timeout = ReadDuration(entry.Value, "\"timeout\"");
                    break;
                case "env":
                    env = ReadEnv(entry.Value);
                    break;
                case "continue-on-error":
                    continueOnError = ReadBoolean(entry.Value, "\"continue-on-error\"");
                    break;
            }
        }

        if (!hasRun)
        {
            AddError(node, "A step needs a \"run\" script.");
            return null;
        }

        if (run is null)
        {
            return null;
        }

        return new StepDefinition
        {
            Name = name ?? NameFromScript(run),
            Run = run,
            When = when,
            Shell = shell,
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            Env = env,
            ContinueOnError = continueOnError,
            Location = ToLocation(node),
        };
    }

    private void CheckNeeds(List<JobDefinition> jobs)
    {
        foreach (YamlNode needsNode in _needsNodes)
        {
            CheckNeedsNode(needsNode, _declaredJobIds);
        }

        foreach (JobDefinition job in jobs)
        {
            if (job.Needs.Contains(job.Id, StringComparer.Ordinal))
            {
                AddError(job.Location, "The job \"" + job.Id + "\" needs itself.");
            }
        }

        IReadOnlyList<string>? cycle = JobGraph.FindCycle(jobs);
        if (cycle is not null)
        {
            JobDefinition first = jobs.First(job => job.Id == cycle[0]);
            AddError(first.Location, "The jobs need each other in a cycle: " + string.Join(" -> ", cycle) + ". Each job needs the next one, so none can start.");
        }
    }

    private void CheckNeedsNode(YamlNode needsNode, HashSet<string> jobIds)
    {
        List<YamlScalarNode> items = new List<YamlScalarNode>();
        if (needsNode is YamlScalarNode single)
        {
            items.Add(single);
        }

        if (needsNode is YamlSequenceNode sequence)
        {
            items.AddRange(sequence.Children.OfType<YamlScalarNode>());
        }

        foreach (YamlScalarNode item in items)
        {
            string id = item.Value ?? string.Empty;
            if (jobIds.Contains(id))
            {
                continue;
            }

            AddError(item, "Unknown job \"" + id + "\" in \"needs\"." + DidYouMean(id, jobIds));
        }
    }

    private List<KeyValuePair<string, YamlNode>> ReadKeys(
        YamlMappingNode mapping,
        string[] allowedKeys,
        string[] plannedKeys,
        string where)
    {
        List<KeyValuePair<string, YamlNode>> entries = new List<KeyValuePair<string, YamlNode>>();
        foreach (KeyValuePair<YamlNode, YamlNode> child in mapping.Children)
        {
            if (child.Key is not YamlScalarNode keyScalar || keyScalar.Value is null)
            {
                AddError(child.Key, "Keys must be plain names.");
                continue;
            }

            string key = keyScalar.Value;
            if (allowedKeys.Contains(key, StringComparer.Ordinal))
            {
                entries.Add(new KeyValuePair<string, YamlNode>(key, child.Value));
                continue;
            }

            if (plannedKeys.Contains(key, StringComparer.Ordinal))
            {
                AddError(child.Key, "\"" + key + "\" is not supported yet.");
                continue;
            }

            AddError(child.Key, "Unknown key \"" + key + "\" in " + where + "." + DidYouMean(key, allowedKeys)
                + " Valid keys: " + string.Join(", ", allowedKeys) + ".");
        }

        return entries;
    }

    private Dictionary<string, string> ReadEnv(YamlNode node)
    {
        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
        YamlMappingNode? mapping = AsMapping(node, "\"env\"");
        if (mapping is null)
        {
            return env;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            string? name = ReadEnvName(entry.Key);
            if (name is null)
            {
                continue;
            }

            if (entry.Value is not YamlScalarNode value)
            {
                AddError(entry.Value, "The value of \"" + name + "\" must be a single value, not a list or mapping.");
                continue;
            }

            env[name] = value.Value ?? string.Empty;
        }

        return env;
    }

    private string? ReadEnvName(YamlNode keyNode)
    {
        if (keyNode is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value))
        {
            AddError(keyNode, "An environment variable name must be a plain name.");
            return null;
        }

        string name = scalar.Value;
        if (!EnvNamePattern().IsMatch(name))
        {
            AddError(keyNode, "Invalid environment variable name \"" + name + "\". Use letters, digits and \"_\", starting with a letter or \"_\".");
            return null;
        }

        if (BuiltInVariables.IsReserved(name))
        {
            AddError(keyNode, "\"" + name + "\" is set by Shipline and cannot be changed. Names starting with SHIPLINE_ and the name CI are reserved.");
            return null;
        }

        return name;
    }

    private IReadOnlyList<string> ReadTextOrList(YamlNode node, string what)
    {
        if (node is YamlScalarNode scalar)
        {
            string? text = ReadRequiredText(scalar, what);
            if (text is null)
            {
                return Array.Empty<string>();
            }

            return new string[] { text };
        }

        if (node is not YamlSequenceNode sequence)
        {
            AddError(node, what + " must be a name or a list of names.");
            return Array.Empty<string>();
        }

        List<string> items = new List<string>();
        foreach (YamlNode item in sequence.Children)
        {
            string? text = ReadRequiredText(item, what);
            if (text is not null)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private string? ReadRequiredText(YamlNode node, string what)
    {
        if (node is not YamlScalarNode scalar)
        {
            AddError(node, what + " must be text, not a list or mapping.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            AddError(node, what + " is empty.");
            return null;
        }

        return scalar.Value;
    }

    private ShellKind? ReadShell(YamlNode node)
    {
        string? name = ReadRequiredText(node, "\"shell\"");
        if (name is null)
        {
            return null;
        }

        if (ShellNames.TryParse(name, out ShellKind shell))
        {
            return shell;
        }

        AddError(node, "Unknown shell \"" + name + "\"." + DidYouMean(name, ShellNames.All) + " Valid shells: " + string.Join(", ", ShellNames.All) + ".");
        return null;
    }

    private TimeSpan? ReadDuration(YamlNode node, string what)
    {
        string? text = ReadRequiredText(node, what);
        if (text is null)
        {
            return null;
        }

        if (Duration.TryParse(text, out TimeSpan duration))
        {
            return duration;
        }

        AddError(node, "Invalid " + what + " \"" + text + "\". Use hours, minutes and seconds such as 45s, 30m or 1h30m, up to 720h.");
        return null;
    }

    private Condition? ReadCondition(YamlNode node)
    {
        // In YAML, a value that starts with ! is a tag, so "when: !failure()" reads as an empty value with a tag.
        if (node is YamlScalarNode tagged && !tagged.Tag.IsEmpty)
        {
            AddError(node, "Put quotes around a condition that starts with \"!\", for example when: \"!failure()\". In YAML a leading ! starts a tag.");
            return null;
        }

        string? text = ReadRequiredText(node, "\"when\"");
        if (text is null)
        {
            return null;
        }

        if (Condition.TryParse(text, out Condition? condition, out ConditionError? error))
        {
            return condition;
        }

        AddError(ConditionErrorLocation((YamlScalarNode)node, error.Position), "Invalid \"when\": " + error.Message);
        return null;
    }

    // Points at the character inside the condition when the value is on one line; quoted values start one column later.
    private static SourceLocation ConditionErrorLocation(YamlScalarNode scalar, int position)
    {
        SourceLocation start = ToLocation(scalar);
        string value = scalar.Value ?? string.Empty;
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            return start;
        }

        if (scalar.Style == ScalarStyle.Plain)
        {
            return new SourceLocation(start.Line, start.Column + position);
        }

        if (scalar.Style == ScalarStyle.SingleQuoted || scalar.Style == ScalarStyle.DoubleQuoted)
        {
            return new SourceLocation(start.Line, start.Column + 1 + position);
        }

        return start;
    }

    private int? ReadPositiveNumber(YamlNode node, string what)
    {
        string? text = ReadRequiredText(node, what);
        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
        {
            return number;
        }

        AddError(node, what + " must be a whole number of 1 or more.");
        return null;
    }

    private bool ReadBoolean(YamlNode node, string what)
    {
        string? text = ReadRequiredText(node, what);
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text is not null)
        {
            AddError(node, what + " must be true or false.");
        }

        return false;
    }

    private YamlMappingNode? AsMapping(YamlNode node, string what)
    {
        if (node is YamlMappingNode mapping)
        {
            return mapping;
        }

        AddError(node, what + " must be a mapping of \"key: value\" lines.");
        return null;
    }

    private static string NameFromScript(string script)
    {
        string firstLine = script
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.Length > 0);

        if (firstLine.Length <= MaxStepNameLength)
        {
            return firstLine;
        }

        return string.Concat(firstLine.AsSpan(0, MaxStepNameLength - 3), "...");
    }

    private static string DidYouMean(string word, IEnumerable<string> candidates)
    {
        string? suggestion = Spelling.Suggest(word, candidates);
        if (suggestion is null)
        {
            return string.Empty;
        }

        return " Did you mean \"" + suggestion + "\"?";
    }

    private void AddError(YamlNode node, string message)
    {
        AddError(ToLocation(node), message);
    }

    private void AddError(SourceLocation location, string message)
    {
        _diagnostics.Add(new PipelineDiagnostic(location, message));
    }

    private static SourceLocation ToLocation(YamlNode node)
    {
        return ToLocation(node.Start);
    }

    private static SourceLocation ToLocation(Mark mark)
    {
        return new SourceLocation((int)mark.Line, (int)mark.Column);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNamePattern();
}
