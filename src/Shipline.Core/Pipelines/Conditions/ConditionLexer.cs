using System.Text;

namespace Shipline.Core.Pipelines.Conditions;

internal enum ConditionTokenKind
{
    String,
    Identifier,
    True,
    False,
    Dot,
    Comma,
    LeftParenthesis,
    RightParenthesis,
    Equal,
    NotEqual,
    Not,
    And,
    Or,
    End,
}

internal sealed class ConditionToken
{
    public ConditionToken(ConditionTokenKind kind, string text, int position)
    {
        Kind = kind;
        Text = text;
        Position = position;
    }

    public ConditionTokenKind Kind { get; }

    /// <summary>The identifier name or the string value (without quotes). Empty for symbols.</summary>
    public string Text { get; }

    /// <summary>The 0-based position of the token in the condition text.</summary>
    public int Position { get; }
}

/// <summary>
/// Splits a condition such as <c>branch == 'main' &amp;&amp; !failure()</c> into tokens.
/// This is the first stage of every compiler and interpreter: the parser then works with tokens, not characters.
/// </summary>
internal sealed class ConditionLexer
{
    private readonly string _text;
    private int _position;

    public ConditionLexer(string text)
    {
        _text = text;
    }

    public string? Error { get; private set; }

    public int ErrorPosition { get; private set; }

    /// <summary>Returns the tokens, ending with an End token, or null when the text has an error.</summary>
    public List<ConditionToken>? Tokenize()
    {
        List<ConditionToken> tokens = new List<ConditionToken>();
        while (true)
        {
            SkipWhitespace();
            if (_position >= _text.Length)
            {
                tokens.Add(new ConditionToken(ConditionTokenKind.End, string.Empty, _position));
                return tokens;
            }

            ConditionToken? token = ReadToken();
            if (token is null)
            {
                return null;
            }

            tokens.Add(token);
        }
    }

    private ConditionToken? ReadToken()
    {
        char current = _text[_position];
        if (current == '\'' || current == '"')
        {
            return ReadString(current);
        }

        if (char.IsAsciiLetter(current) || current == '_')
        {
            return ReadIdentifier();
        }

        ConditionToken? twoCharacterToken = TryReadTwoCharacterOperator();
        if (twoCharacterToken is not null)
        {
            return twoCharacterToken;
        }

        return ReadSymbol(current);
    }

    // Inside a string the quote character is written twice to include it: 'it''s'.
    private ConditionToken? ReadString(char quote)
    {
        int start = _position;
        StringBuilder value = new StringBuilder();
        _position++;
        while (_position < _text.Length)
        {
            char current = _text[_position];
            bool isQuote = current == quote;
            bool isDoubledQuote = isQuote && _position + 1 < _text.Length && _text[_position + 1] == quote;
            if (isDoubledQuote)
            {
                value.Append(quote);
                _position += 2;
                continue;
            }

            if (isQuote)
            {
                _position++;
                return new ConditionToken(ConditionTokenKind.String, value.ToString(), start);
            }

            value.Append(current);
            _position++;
        }

        SetError("The text that starts here has no closing " + quote + ".", start);
        return null;
    }

    private ConditionToken ReadIdentifier()
    {
        int start = _position;
        while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] == '_'))
        {
            _position++;
        }

        string name = _text.Substring(start, _position - start);
        if (name == "true")
        {
            return new ConditionToken(ConditionTokenKind.True, name, start);
        }

        if (name == "false")
        {
            return new ConditionToken(ConditionTokenKind.False, name, start);
        }

        return new ConditionToken(ConditionTokenKind.Identifier, name, start);
    }

    private ConditionToken? TryReadTwoCharacterOperator()
    {
        if (_position + 1 >= _text.Length)
        {
            return null;
        }

        string pair = _text.Substring(_position, 2);
        ConditionTokenKind kind;
        switch (pair)
        {
            case "==":
                kind = ConditionTokenKind.Equal;
                break;
            case "!=":
                kind = ConditionTokenKind.NotEqual;
                break;
            case "&&":
                kind = ConditionTokenKind.And;
                break;
            case "||":
                kind = ConditionTokenKind.Or;
                break;
            default:
                return null;
        }

        ConditionToken token = new ConditionToken(kind, string.Empty, _position);
        _position += 2;
        return token;
    }

    private ConditionToken? ReadSymbol(char current)
    {
        ConditionTokenKind kind;
        switch (current)
        {
            case '!':
                kind = ConditionTokenKind.Not;
                break;
            case '(':
                kind = ConditionTokenKind.LeftParenthesis;
                break;
            case ')':
                kind = ConditionTokenKind.RightParenthesis;
                break;
            case ',':
                kind = ConditionTokenKind.Comma;
                break;
            case '.':
                kind = ConditionTokenKind.Dot;
                break;
            case '=':
                SetError("Use == to compare values.", _position);
                return null;
            case '&':
                SetError("Use && for \"and\".", _position);
                return null;
            case '|':
                SetError("Use || for \"or\".", _position);
                return null;
            default:
                SetError("Unexpected character '" + current + "'.", _position);
                return null;
        }

        ConditionToken token = new ConditionToken(kind, string.Empty, _position);
        _position++;
        return token;
    }

    private void SkipWhitespace()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private void SetError(string message, int position)
    {
        Error = message;
        ErrorPosition = position;
    }
}
