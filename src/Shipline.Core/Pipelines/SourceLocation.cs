using System.Globalization;

namespace Shipline.Core.Pipelines;

/// <summary>A 1-based line and column in the pipeline file.</summary>
public readonly struct SourceLocation : IEquatable<SourceLocation>
{
    public SourceLocation(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }

    public bool Equals(SourceLocation other)
    {
        return Line == other.Line && Column == other.Column;
    }

    public override bool Equals(object? obj)
    {
        return obj is SourceLocation other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Line, Column);
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Line}:{Column}");
    }

    public static bool operator ==(SourceLocation left, SourceLocation right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SourceLocation left, SourceLocation right)
    {
        return !left.Equals(right);
    }
}
