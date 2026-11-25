using Shipline.Core.Pipelines;

namespace Shipline.Core.Tests.Pipelines;

public sealed class DurationAndSpellingTests
{
    [Theory]
    [InlineData("45s", 0, 0, 45)]
    [InlineData("30m", 0, 30, 0)]
    [InlineData("2h", 2, 0, 0)]
    [InlineData("1h30m", 1, 30, 0)]
    [InlineData("1h0m5s", 1, 0, 5)]
    [InlineData("90m", 1, 30, 0)]
    public void TryParse_ValidDuration_ReturnsTimeSpan(string text, int hours, int minutes, int seconds)
    {
        bool parsed = Duration.TryParse(text, out TimeSpan duration);

        Assert.True(parsed);
        Assert.Equal(new TimeSpan(hours, minutes, seconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("0s")]
    [InlineData("10 m")]
    [InlineData("30m1h")]
    [InlineData("1d")]
    [InlineData("-5m")]
    [InlineData("99999999999h")]
    [InlineData("2147483647h")]
    [InlineData("1000h")]
    public void TryParse_InvalidDuration_ReturnsFalse(string text)
    {
        Assert.False(Duration.TryParse(text, out TimeSpan duration));
    }

    [Theory]
    [InlineData(850, "850ms")]
    [InlineData(12_400, "12.4s")]
    [InlineData(185_000, "3m 05s")]
    [InlineData(3_720_000, "1h 02m")]
    public void Format_PicksReadableUnit(int milliseconds, string expected)
    {
        Assert.Equal(expected, Duration.Format(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData("stpes", "steps")]
    [InlineData("step", "steps")]
    [InlineData("neds", "needs")]
    [InlineData("runs_on", "runs-on")]
    public void Suggest_CloseWord_ReturnsCandidate(string word, string expected)
    {
        string[] keys = { "name", "runs-on", "needs", "timeout", "env", "steps" };

        Assert.Equal(expected, Spelling.Suggest(word, keys));
    }

    [Theory]
    [InlineData("artifacts")]
    [InlineData("x")]
    public void Suggest_FarWord_ReturnsNull(string word)
    {
        string[] keys = { "name", "runs-on", "needs", "timeout", "env", "steps" };

        Assert.Null(Spelling.Suggest(word, keys));
    }
}
