using Shipline.Core.Pipelines.Conditions;

namespace Shipline.Core.Tests.Pipelines;

public sealed class ConditionTests
{
    [Theory]
    [InlineData("branch == 'release/1.2'", true)]
    [InlineData("branch != 'main'", true)]
    [InlineData("event == 'manual'", true)]
    [InlineData("env.DEPLOY == 'yes'", true)]
    [InlineData("env.MISSING == ''", true)]
    [InlineData("startsWith(branch, 'release/')", true)]
    [InlineData("endsWith(branch, '.2')", true)]
    [InlineData("contains(branch, 'lease')", true)]
    [InlineData("contains(branch, 'LEASE')", false)]
    [InlineData("true || false && false", true)]
    [InlineData("(true || false) && false", false)]
    [InlineData("!false && !(branch == 'main')", true)]
    [InlineData("'it''s' == \"it's\"", true)]
    [InlineData("true == true", true)]
    [InlineData("true != false", true)]
    [InlineData("success()", true)]
    [InlineData("failure()", false)]
    [InlineData("always()", true)]
    public void ShouldRun_EvaluatesExpression(string text, bool expected)
    {
        Condition condition = Parse(text);

        Assert.Equal(expected, Condition.ShouldRun(condition, Context(success: true, failure: false)));
    }

    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData(null, false, true, false)]
    [InlineData("branch == 'release/1.2'", false, true, false)]
    [InlineData("failure()", false, true, true)]
    [InlineData("failure()", true, false, false)]
    [InlineData("always()", false, true, true)]
    [InlineData("!failure()", false, false, true)]
    [InlineData("failure() && branch == 'main'", false, true, false)]
    public void ShouldRun_WithoutStatusFunction_RequiresSuccess(string? text, bool success, bool failure, bool expected)
    {
        Condition? condition = null;
        if (text is not null)
        {
            condition = Parse(text);
        }

        Assert.Equal(expected, Condition.ShouldRun(condition, Context(success, failure)));
    }

    [Theory]
    [InlineData("branch == 'main'", false)]
    [InlineData("!failure()", true)]
    [InlineData("always() || true", true)]
    [InlineData("startsWith(branch, 'x') && success()", true)]
    public void UsesStatusFunction_IsTrueWhenAStatusFunctionIsCalled(string text, bool expected)
    {
        Assert.Equal(expected, Parse(text).UsesStatusFunction);
    }

    [Theory]
    [InlineData("branch = 'main'", "Use ==", 7)]
    [InlineData("branch == 'main", "no closing '", 10)]
    [InlineData("branhc == 'main'", "Did you mean \"branch\"?", 0)]
    [InlineData("branch", "must be true or false", 0)]
    [InlineData("branch && true", "&& joins true/false values", 7)]
    [InlineData("branch == true", "Cannot compare text with true/false", 7)]
    [InlineData("succes()", "Did you mean \"success\"?", 0)]
    [InlineData("success('x')", "takes no arguments", 0)]
    [InlineData("startsWith(branch)", "takes two text arguments", 0)]
    [InlineData("(true", "Missing )", 5)]
    [InlineData("true false", "Unexpected text", 5)]
    [InlineData("", "ends too early", 0)]
    [InlineData("env.", "A variable name must follow", 4)]
    [InlineData("env", "Write env.NAME", 0)]
    [InlineData("!branch", "! needs true or false", 0)]
    [InlineData("branch == 'a' & true", "Use &&", 14)]
    [InlineData("branch == 1", "Unexpected character '1'", 10)]
    public void TryParse_InvalidCondition_ReportsMessageAndPosition(string text, string expectedMessagePart, int expectedPosition)
    {
        bool parsed = Condition.TryParse(text, out Condition? condition, out ConditionError? error);

        Assert.False(parsed);
        Assert.Null(condition);
        Assert.NotNull(error);
        Assert.Contains(expectedMessagePart, error.Message, StringComparison.Ordinal);
        Assert.Equal(expectedPosition, error.Position);
    }

    private static Condition Parse(string text)
    {
        bool parsed = Condition.TryParse(text, out Condition? condition, out ConditionError? error);
        Assert.True(parsed, error?.Message);
        Assert.NotNull(condition);
        return condition;
    }

    private static ConditionContext Context(bool success, bool failure)
    {
        return new ConditionContext
        {
            Branch = "release/1.2",
            Event = "manual",
            Env = new Dictionary<string, string> { { "DEPLOY", "yes" } },
            Success = success,
            Failure = failure,
        };
    }
}
