namespace Shipline.Core.Pipelines.Conditions;

/// <summary>
/// A recursive descent parser: one method per grammar rule, from the loosest operator to the tightest.
/// </summary>
/// <remarks>
/// <code>
/// condition  := or
/// or         := and ( "||" and )*
/// and        := unary ( "&amp;&amp;" unary )*
/// unary      := "!" unary | comparison
/// comparison := primary ( ( "==" | "!=" ) primary )?
/// primary    := 'text' | true | false | branch | event | env.NAME | name "(" arguments ")" | "(" condition ")"
/// </code>
/// Because "and" calls "unary" and "or" calls "and", <c>a || b &amp;&amp; c</c> is read as <c>a || (b &amp;&amp; c)</c>.
/// Each method returns null after an error; the first error is kept.
/// </remarks>
internal sealed class ConditionParser
{
    private static readonly string[] s_variableNames = { VariableNode.Branch, VariableNode.Event, "env" };
    private static readonly string[] s_statusFunctionNames = { StatusFunctionNode.Success, StatusFunctionNode.Failure, StatusFunctionNode.Always };
    private static readonly string[] s_textFunctionNames = { TextFunctionNode.StartsWith, TextFunctionNode.EndsWith, TextFunctionNode.Contains };

    private readonly List<ConditionToken> _tokens;
    private int _index;

    public ConditionParser(List<ConditionToken> tokens)
    {
        _tokens = tokens;
    }

    public string? Error { get; private set; }

    public int ErrorPosition { get; private set; }

    /// <summary>True when the condition calls success(), failure() or always().</summary>
    public bool UsesStatusFunction { get; private set; }

    public ConditionNode? ParseCondition()
    {
        ConditionNode? root = ParseOr();
        if (root is null)
        {
            return null;
        }

        if (Current.Kind != ConditionTokenKind.End)
        {
            return Fail("Unexpected text here. Join conditions with && or ||.", Current);
        }

        if (root.Type != ConditionType.Boolean)
        {
            return Fail("The condition must be true or false, for example branch == 'main'.", _tokens[0]);
        }

        return root;
    }

    private ConditionToken Current => _tokens[_index];

    private ConditionNode? ParseOr()
    {
        ConditionNode? left = ParseAnd();
        while (left is not null && Current.Kind == ConditionTokenKind.Or)
        {
            ConditionToken operatorToken = Next();
            ConditionNode? right = ParseAnd();
            if (right is null)
            {
                return null;
            }

            if (!RequireBooleans(left, right, operatorToken, "||"))
            {
                return null;
            }

            left = new OrNode(left, right);
        }

        return left;
    }

    private ConditionNode? ParseAnd()
    {
        ConditionNode? left = ParseUnary();
        while (left is not null && Current.Kind == ConditionTokenKind.And)
        {
            ConditionToken operatorToken = Next();
            ConditionNode? right = ParseUnary();
            if (right is null)
            {
                return null;
            }

            if (!RequireBooleans(left, right, operatorToken, "&&"))
            {
                return null;
            }

            left = new AndNode(left, right);
        }

        return left;
    }

    private ConditionNode? ParseUnary()
    {
        if (Current.Kind != ConditionTokenKind.Not)
        {
            return ParseComparison();
        }

        ConditionToken notToken = Next();
        ConditionNode? operand = ParseUnary();
        if (operand is null)
        {
            return null;
        }

        if (operand.Type != ConditionType.Boolean)
        {
            return Fail("! needs true or false after it, not text.", notToken);
        }

        return new NotNode(operand);
    }

    private ConditionNode? ParseComparison()
    {
        ConditionNode? left = ParsePrimary();
        if (left is null)
        {
            return null;
        }

        bool isEqual = Current.Kind == ConditionTokenKind.Equal;
        bool isNotEqual = Current.Kind == ConditionTokenKind.NotEqual;
        if (!isEqual && !isNotEqual)
        {
            return left;
        }

        ConditionToken operatorToken = Next();
        ConditionNode? right = ParsePrimary();
        if (right is null)
        {
            return null;
        }

        if (left.Type != right.Type)
        {
            return Fail("Cannot compare text with true/false.", operatorToken);
        }

        return new EqualsNode(left, right, isNotEqual);
    }

    private ConditionNode? ParsePrimary()
    {
        ConditionToken token = Current;
        switch (token.Kind)
        {
            case ConditionTokenKind.String:
                Next();
                return new TextLiteralNode(token.Text);
            case ConditionTokenKind.True:
                Next();
                return new BooleanLiteralNode(true);
            case ConditionTokenKind.False:
                Next();
                return new BooleanLiteralNode(false);
            case ConditionTokenKind.LeftParenthesis:
                return ParseParentheses();
            case ConditionTokenKind.Identifier:
                return ParseName();
            case ConditionTokenKind.End:
                return Fail("The condition ends too early; a value is missing.", token);
            default:
                return Fail("Expected a value such as 'text', branch, env.NAME or success().", token);
        }
    }

    private ConditionNode? ParseParentheses()
    {
        Next();
        ConditionNode? inner = ParseOr();
        if (inner is null)
        {
            return null;
        }

        if (Current.Kind != ConditionTokenKind.RightParenthesis)
        {
            return Fail("Missing ).", Current);
        }

        Next();
        return inner;
    }

    private ConditionNode? ParseName()
    {
        ConditionToken nameToken = Next();
        string name = nameToken.Text;
        if (Current.Kind == ConditionTokenKind.LeftParenthesis)
        {
            return ParseFunctionCall(nameToken);
        }

        if (name == "env")
        {
            return ParseEnv(nameToken);
        }

        if (name == VariableNode.Branch || name == VariableNode.Event)
        {
            return new VariableNode(name);
        }

        string hint = DidYouMean(name, s_variableNames.Concat(s_statusFunctionNames).Concat(s_textFunctionNames));
        return Fail("Unknown name \"" + name + "\"." + hint + " Use branch, event, env.NAME, or a function such as success().", nameToken);
    }

    private ConditionNode? ParseEnv(ConditionToken envToken)
    {
        if (Current.Kind != ConditionTokenKind.Dot)
        {
            return Fail("Write env.NAME to read an environment variable.", envToken);
        }

        Next();
        if (Current.Kind != ConditionTokenKind.Identifier)
        {
            return Fail("A variable name must follow \"env.\".", Current);
        }

        return new EnvNode(Next().Text);
    }

    private ConditionNode? ParseFunctionCall(ConditionToken nameToken)
    {
        Next();
        List<ConditionNode>? arguments = ParseArguments();
        if (arguments is null)
        {
            return null;
        }

        string name = nameToken.Text;
        if (s_statusFunctionNames.Contains(name, StringComparer.Ordinal))
        {
            if (arguments.Count != 0)
            {
                return Fail(name + "() takes no arguments.", nameToken);
            }

            UsesStatusFunction = true;
            return new StatusFunctionNode(name);
        }

        if (s_textFunctionNames.Contains(name, StringComparer.Ordinal))
        {
            bool hasTwoTextArguments = arguments.Count == 2 && arguments.TrueForAll(argument => argument.Type == ConditionType.Text);
            if (!hasTwoTextArguments)
            {
                return Fail(name + "() takes two text arguments, for example " + name + "(branch, 'release/').", nameToken);
            }

            return new TextFunctionNode(name, arguments[0], arguments[1]);
        }

        string hint = DidYouMean(name, s_statusFunctionNames.Concat(s_textFunctionNames));
        return Fail("Unknown function \"" + name + "\"." + hint, nameToken);
    }

    private List<ConditionNode>? ParseArguments()
    {
        List<ConditionNode> arguments = new List<ConditionNode>();
        if (Current.Kind == ConditionTokenKind.RightParenthesis)
        {
            Next();
            return arguments;
        }

        while (true)
        {
            ConditionNode? argument = ParseOr();
            if (argument is null)
            {
                return null;
            }

            arguments.Add(argument);
            if (Current.Kind == ConditionTokenKind.Comma)
            {
                Next();
                continue;
            }

            if (Current.Kind == ConditionTokenKind.RightParenthesis)
            {
                Next();
                return arguments;
            }

            Fail("Expected , or ) in the argument list.", Current);
            return null;
        }
    }

    private bool RequireBooleans(ConditionNode left, ConditionNode right, ConditionToken operatorToken, string operatorText)
    {
        if (left.Type == ConditionType.Boolean && right.Type == ConditionType.Boolean)
        {
            return true;
        }

        Fail(operatorText + " joins true/false values. To test text, compare it: branch == 'main'.", operatorToken);
        return false;
    }

    private ConditionToken Next()
    {
        ConditionToken token = _tokens[_index];
        if (token.Kind != ConditionTokenKind.End)
        {
            _index++;
        }

        return token;
    }

    private ConditionNode? Fail(string message, ConditionToken token)
    {
        if (Error is null)
        {
            Error = message;
            ErrorPosition = token.Position;
        }

        return null;
    }

    private static string DidYouMean(string name, IEnumerable<string> candidates)
    {
        string? suggestion = Spelling.Suggest(name, candidates);
        if (suggestion is null)
        {
            return string.Empty;
        }

        return " Did you mean \"" + suggestion + "\"?";
    }
}
