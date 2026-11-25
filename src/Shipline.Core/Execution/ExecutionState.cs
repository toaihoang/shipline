namespace Shipline.Core.Execution;

/// <summary>Where a run, job or step is in its life. <see cref="Outcome"/> is the subset that ends it.</summary>
public enum ExecutionState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Skipped,
}

/// <summary>
/// The state machine. Only these moves are allowed:
/// <code>
/// Pending --start--> Running --end--> Succeeded | Failed | TimedOut | Cancelled
/// Pending --never started--> Skipped | Cancelled
/// </code>
/// A final state never changes again. Making the rules explicit means a bug such as "finish a job twice" fails loudly at
/// the line that does it, instead of silently corrupting the run history.
/// </summary>
public static class ExecutionStates
{
    public static bool IsFinal(ExecutionState state)
    {
        return state is not (ExecutionState.Pending or ExecutionState.Running);
    }

    public static bool CanMove(ExecutionState from, ExecutionState to)
    {
        if (from == ExecutionState.Pending)
        {
            return to is ExecutionState.Running or ExecutionState.Skipped or ExecutionState.Cancelled;
        }

        if (from == ExecutionState.Running)
        {
            return to is ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.TimedOut or ExecutionState.Cancelled;
        }

        return false;
    }

    public static ExecutionState FromOutcome(Outcome outcome)
    {
        switch (outcome)
        {
            case Outcome.Succeeded:
                return ExecutionState.Succeeded;
            case Outcome.Failed:
                return ExecutionState.Failed;
            case Outcome.TimedOut:
                return ExecutionState.TimedOut;
            case Outcome.Cancelled:
                return ExecutionState.Cancelled;
            default:
                return ExecutionState.Skipped;
        }
    }

    /// <summary>Failed or timed out: the outcomes that fail a run and that failure() reacts to.</summary>
    public static bool IsFailure(Outcome outcome)
    {
        return outcome is Outcome.Failed or Outcome.TimedOut;
    }
}
