using Shipline.Core.Execution;

namespace Shipline.Core.Tests.Execution;

public sealed class ExecutionStateTests
{
    // The only allowed moves, copied from the diagram on ExecutionStates. Every other pair must be refused.
    private static readonly HashSet<string> s_allowedMoves = new HashSet<string>(StringComparer.Ordinal)
    {
        "Pending -> Running",
        "Pending -> Skipped",
        "Pending -> Cancelled",
        "Running -> Succeeded",
        "Running -> Failed",
        "Running -> TimedOut",
        "Running -> Cancelled",
    };

    public static TheoryData<ExecutionState, ExecutionState> AllPairs()
    {
        TheoryData<ExecutionState, ExecutionState> pairs = new TheoryData<ExecutionState, ExecutionState>();
        foreach (ExecutionState from in Enum.GetValues<ExecutionState>())
        {
            foreach (ExecutionState to in Enum.GetValues<ExecutionState>())
            {
                pairs.Add(from, to);
            }
        }

        return pairs;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void CanMove_AllowsOnlyTheMovesInTheDiagram(ExecutionState from, ExecutionState to)
    {
        bool expected = s_allowedMoves.Contains(from + " -> " + to);

        Assert.Equal(expected, ExecutionStates.CanMove(from, to));
    }

    [Theory]
    [InlineData(ExecutionState.Pending, false)]
    [InlineData(ExecutionState.Running, false)]
    [InlineData(ExecutionState.Succeeded, true)]
    [InlineData(ExecutionState.Failed, true)]
    [InlineData(ExecutionState.TimedOut, true)]
    [InlineData(ExecutionState.Cancelled, true)]
    [InlineData(ExecutionState.Skipped, true)]
    public void IsFinal_IsTrueOnlyForEndStates(ExecutionState state, bool expected)
    {
        Assert.Equal(expected, ExecutionStates.IsFinal(state));
    }

    [Theory]
    [InlineData(Outcome.Succeeded, ExecutionState.Succeeded)]
    [InlineData(Outcome.Failed, ExecutionState.Failed)]
    [InlineData(Outcome.TimedOut, ExecutionState.TimedOut)]
    [InlineData(Outcome.Cancelled, ExecutionState.Cancelled)]
    [InlineData(Outcome.Skipped, ExecutionState.Skipped)]
    public void FromOutcome_MapsToTheSameName(Outcome outcome, ExecutionState expected)
    {
        Assert.Equal(expected, ExecutionStates.FromOutcome(outcome));
    }
}
