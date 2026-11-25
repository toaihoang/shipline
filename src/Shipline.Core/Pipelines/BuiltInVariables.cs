namespace Shipline.Core.Pipelines;

/// <summary>Environment variables that Shipline sets for every step. A pipeline cannot override them.</summary>
public static class BuiltInVariables
{
    public const string Ci = "CI";
    public const string Shipline = "SHIPLINE";
    public const string RunId = "SHIPLINE_RUN_ID";
    public const string PipelineName = "SHIPLINE_PIPELINE";
    public const string JobId = "SHIPLINE_JOB";
    public const string StepName = "SHIPLINE_STEP";
    public const string Workspace = "SHIPLINE_WORKSPACE";

    private const string ReservedPrefix = "SHIPLINE_";

    /// <summary>
    /// True for CI, SHIPLINE and every SHIPLINE_ name. The check ignores case because Windows treats
    /// environment variable names that way.
    /// </summary>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.Equals(name, Ci, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(name, Shipline, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
