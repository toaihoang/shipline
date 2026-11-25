namespace Shipline.Core.Pipelines.Conditions;

internal enum ConditionType
{
    Text,
    Boolean,
}

/// <summary>
/// A node of the syntax tree. The parser checks types while it builds the tree, so evaluation never meets a value of
/// the wrong type: a Text node always returns a string and a Boolean node always returns a bool.
/// </summary>
internal abstract class ConditionNode
{
    public abstract ConditionType Type { get; }

    public abstract object Evaluate(ConditionContext context);

    public bool EvaluateBoolean(ConditionContext context)
    {
        return (bool)Evaluate(context);
    }

    public string EvaluateText(ConditionContext context)
    {
        return (string)Evaluate(context);
    }
}

internal sealed class TextLiteralNode : ConditionNode
{
    private readonly string _value;

    public TextLiteralNode(string value)
    {
        _value = value;
    }

    public override ConditionType Type => ConditionType.Text;

    public override object Evaluate(ConditionContext context)
    {
        return _value;
    }
}

internal sealed class BooleanLiteralNode : ConditionNode
{
    private readonly bool _value;

    public BooleanLiteralNode(bool value)
    {
        _value = value;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        return _value;
    }
}

/// <summary>"branch" or "event".</summary>
internal sealed class VariableNode : ConditionNode
{
    public const string Branch = "branch";
    public const string Event = "event";

    private readonly string _name;

    public VariableNode(string name)
    {
        _name = name;
    }

    public override ConditionType Type => ConditionType.Text;

    public override object Evaluate(ConditionContext context)
    {
        if (_name == Branch)
        {
            return context.Branch;
        }

        return context.Event;
    }
}

/// <summary>"env.NAME". A variable that is not set reads as empty text.</summary>
internal sealed class EnvNode : ConditionNode
{
    private readonly string _name;

    public EnvNode(string name)
    {
        _name = name;
    }

    public override ConditionType Type => ConditionType.Text;

    public override object Evaluate(ConditionContext context)
    {
        if (context.Env.TryGetValue(_name, out string? value))
        {
            return value;
        }

        return string.Empty;
    }
}

internal sealed class NotNode : ConditionNode
{
    private readonly ConditionNode _operand;

    public NotNode(ConditionNode operand)
    {
        _operand = operand;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        return !_operand.EvaluateBoolean(context);
    }
}

internal sealed class AndNode : ConditionNode
{
    private readonly ConditionNode _left;
    private readonly ConditionNode _right;

    public AndNode(ConditionNode left, ConditionNode right)
    {
        _left = left;
        _right = right;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        if (!_left.EvaluateBoolean(context))
        {
            return false;
        }

        return _right.EvaluateBoolean(context);
    }
}

internal sealed class OrNode : ConditionNode
{
    private readonly ConditionNode _left;
    private readonly ConditionNode _right;

    public OrNode(ConditionNode left, ConditionNode right)
    {
        _left = left;
        _right = right;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        if (_left.EvaluateBoolean(context))
        {
            return true;
        }

        return _right.EvaluateBoolean(context);
    }
}

/// <summary>"a == b" or "a != b". Both sides have the same type; text is compared exactly, case included.</summary>
internal sealed class EqualsNode : ConditionNode
{
    private readonly ConditionNode _left;
    private readonly ConditionNode _right;
    private readonly bool _isNotEqual;

    public EqualsNode(ConditionNode left, ConditionNode right, bool isNotEqual)
    {
        _left = left;
        _right = right;
        _isNotEqual = isNotEqual;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        bool areEqual = Equals(_left.Evaluate(context), _right.Evaluate(context));
        if (_isNotEqual)
        {
            return !areEqual;
        }

        return areEqual;
    }
}

/// <summary>success(), failure() or always().</summary>
internal sealed class StatusFunctionNode : ConditionNode
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Always = "always";

    private readonly string _name;

    public StatusFunctionNode(string name)
    {
        _name = name;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        if (_name == Success)
        {
            return context.Success;
        }

        if (_name == Failure)
        {
            return context.Failure;
        }

        return true;
    }
}

/// <summary>startsWith(text, prefix), endsWith(text, suffix) or contains(text, part). Case-sensitive, like Git branch names.</summary>
internal sealed class TextFunctionNode : ConditionNode
{
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string Contains = "contains";

    private readonly string _name;
    private readonly ConditionNode _text;
    private readonly ConditionNode _part;

    public TextFunctionNode(string name, ConditionNode text, ConditionNode part)
    {
        _name = name;
        _text = text;
        _part = part;
    }

    public override ConditionType Type => ConditionType.Boolean;

    public override object Evaluate(ConditionContext context)
    {
        string text = _text.EvaluateText(context);
        string part = _part.EvaluateText(context);
        if (_name == StartsWith)
        {
            return text.StartsWith(part, StringComparison.Ordinal);
        }

        if (_name == EndsWith)
        {
            return text.EndsWith(part, StringComparison.Ordinal);
        }

        return text.Contains(part, StringComparison.Ordinal);
    }
}
