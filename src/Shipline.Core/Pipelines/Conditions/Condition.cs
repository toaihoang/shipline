using System.Diagnostics.CodeAnalysis;

namespace Shipline.Core.Pipelines.Conditions;

/// <summary>What a condition can read when it is evaluated.</summary>
public sealed class ConditionContext
{
    /// <summary>The Git branch, or empty text when it is not known.</summary>
    public required string Branch { get; init; }

    /// <summary>What started the run: "manual" for "shipline run"; "push" and "pull_request" come with webhooks.</summary>
    public required string Event { get; init; }

    /// <summary>The environment the job or step would run with.</summary>
    public required IReadOnlyDictionary<string, string> Env { get; init; }

    /// <summary>
    /// For a job: every job it needs succeeded. For a step: no earlier step of the job failed.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// For a job: a job it needs, directly or indirectly, failed or timed out. For a step: an earlier step failed.
    /// </summary>
    public required bool Failure { get; init; }
}

public sealed class ConditionError
{
    public ConditionError(string message, int position)
    {
        Message = message;
        Position = position;
    }

    public string Message { get; }

    /// <summary>The 0-based position in the condition text.</summary>
    public int Position { get; }
}

/// <summary>A parsed "when" condition, such as <c>branch == 'main' &amp;&amp; success()</c>.</summary>
public sealed class Condition
{
    private readonly ConditionNode _root;

    private Condition(string text, ConditionNode root, bool usesStatusFunction)
    {
        Text = text;
        _root = root;
        UsesStatusFunction = usesStatusFunction;
    }

    public string Text { get; }

    /// <summary>
    /// True when the condition calls success(), failure() or always(). Such a condition decides by itself whether earlier
    /// failures matter; any other condition is joined with an implicit success().
    /// </summary>
    public bool UsesStatusFunction { get; }

    public static bool TryParse(string text, [NotNullWhen(true)] out Condition? condition, [NotNullWhen(false)] out ConditionError? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        condition = null;
        error = null;

        ConditionLexer lexer = new ConditionLexer(text);
        List<ConditionToken>? tokens = lexer.Tokenize();
        if (tokens is null)
        {
            error = new ConditionError(lexer.Error ?? "Invalid condition.", lexer.ErrorPosition);
            return false;
        }

        ConditionParser parser = new ConditionParser(tokens);
        ConditionNode? root = parser.ParseCondition();
        if (root is null)
        {
            error = new ConditionError(parser.Error ?? "Invalid condition.", parser.ErrorPosition);
            return false;
        }

        condition = new Condition(text, root, parser.UsesStatusFunction);
        return true;
    }

    /// <summary>
    /// Decides whether a job or step runs. No condition means success(). A condition without a status function
    /// means success() &amp;&amp; condition, so by default nothing runs after a failure.
    /// </summary>
    public static bool ShouldRun(Condition? condition, ConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (condition is null)
        {
            return context.Success;
        }

        if (!condition.UsesStatusFunction && !context.Success)
        {
            return false;
        }

        return condition._root.EvaluateBoolean(context);
    }

    public override string ToString()
    {
        return Text;
    }
}
