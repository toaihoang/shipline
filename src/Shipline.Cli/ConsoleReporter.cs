using System.Globalization;
using Shipline.Core.Execution;
using Shipline.Core.Pipelines;

namespace Shipline.Cli;

/// <summary>
/// Prints a run to the console: a header per job and step, the raw output, and a summary at the end.
/// When more than one job runs, every line starts with "[job]", because parallel jobs write at the same time.
/// </summary>
internal sealed class ConsoleReporter : IRunReporter
{
    // Events come from several job threads and from the stdout and stderr reader threads at the same time.
    private readonly Lock _lock = new Lock();
    private readonly bool _useColors = !Console.IsOutputRedirected;
    private readonly string _branch;
    private bool _showJobPrefix;
    private int _jobIdWidth;

    public ConsoleReporter(string branch)
    {
        _branch = branch;
    }

    public void RunStarted(PipelineDefinition pipeline, string runId, IReadOnlyList<JobDefinition> jobs)
    {
        _showJobPrefix = jobs.Count > 1;
        _jobIdWidth = jobs.Max(job => job.Id.Length);

        string jobCount = jobs.Count.ToString(CultureInfo.InvariantCulture);
        string branch = _branch.Length == 0 ? "no branch" : "branch " + _branch;
        WriteLine("Pipeline " + pipeline.Name + ", " + jobCount + " job(s), " + branch + ", run " + runId, ConsoleColor.Gray);
    }

    public void JobStarted(JobDefinition job)
    {
        WriteBlankLineIfSequential();
        WriteLine(Prefix(job) + "== " + job.Name, ConsoleColor.Cyan);
    }

    public void StepStarted(JobDefinition job, StepDefinition step)
    {
        WriteLine(Prefix(job) + "-- " + step.Name, ConsoleColor.White);
    }

    public void StepOutput(JobDefinition job, StepDefinition step, OutputLine line)
    {
        WriteLine(Prefix(job) + line.Text, null);
    }

    public void StepFinished(JobDefinition job, StepResult result)
    {
        if (result.Outcome == Outcome.Skipped)
        {
            WriteLine(Prefix(job) + "-- " + result.Step.Name + ": skipped. " + result.Message, ConsoleColor.DarkGray);
            return;
        }

        string text = Prefix(job) + "   " + DescribeOutcome(result.Outcome) + " in " + Duration.Format(result.Duration);
        if (result.Message is not null)
        {
            text = text + ". " + result.Message;
        }

        if (result.FailureIgnored)
        {
            WriteLine(text + " Ignored: continue-on-error is set.", ConsoleColor.Yellow);
            return;
        }

        WriteLine(text, ColorFor(result.Outcome));
    }

    public void JobFinished(JobResult result)
    {
        bool notStarted = result.Steps.Count == 0;
        if (notStarted)
        {
            WriteBlankLineIfSequential();
            WriteLine(Prefix(result.Job) + "== " + result.Job.Name + ": " + DescribeOutcome(result.Outcome) + ". " + result.Message, ConsoleColor.DarkGray);
            return;
        }

        if (_showJobPrefix)
        {
            string text = Prefix(result.Job) + "== " + result.Job.Name + ": " + DescribeOutcome(result.Outcome) + " in " + Duration.Format(result.Duration);
            WriteLine(text, ColorFor(result.Outcome));
        }
    }

    public void RunFinished(RunResult result)
    {
        WriteLine(string.Empty, null);
        WriteLine("Summary", ConsoleColor.White);
        int nameWidth = result.Jobs.Max(job => job.Job.Name.Length) + 2;
        foreach (JobResult job in result.Jobs)
        {
            string duration = string.Empty;
            if (job.Steps.Count > 0)
            {
                duration = Duration.Format(job.Duration);
            }

            string line = "  " + job.Job.Name.PadRight(nameWidth) + DescribeOutcome(job.Outcome).PadRight(12) + duration;
            WriteLine(line, ColorFor(job.Outcome));
        }

        WriteLine(string.Empty, null);
        WriteLine("Run " + DescribeOutcome(result.Outcome) + " in " + Duration.Format(result.Duration) + ".", ColorFor(result.Outcome));
    }

    private string Prefix(JobDefinition job)
    {
        if (!_showJobPrefix)
        {
            return string.Empty;
        }

        return "[" + job.Id.PadRight(_jobIdWidth) + "] ";
    }

    // With one job the output reads as blocks separated by blank lines; with prefixes the lines interleave anyway.
    private void WriteBlankLineIfSequential()
    {
        if (!_showJobPrefix)
        {
            WriteLine(string.Empty, null);
        }
    }

    private void WriteLine(string text, ConsoleColor? color)
    {
        lock (_lock)
        {
            if (color is null || !_useColors)
            {
                Console.WriteLine(text);
                return;
            }

            Console.ForegroundColor = color.Value;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    private static string DescribeOutcome(Outcome outcome)
    {
        switch (outcome)
        {
            case Outcome.Succeeded:
                return "succeeded";
            case Outcome.Failed:
                return "failed";
            case Outcome.TimedOut:
                return "timed out";
            case Outcome.Cancelled:
                return "cancelled";
            default:
                return "skipped";
        }
    }

    private static ConsoleColor ColorFor(Outcome outcome)
    {
        switch (outcome)
        {
            case Outcome.Succeeded:
                return ConsoleColor.Green;
            case Outcome.Failed:
            case Outcome.TimedOut:
                return ConsoleColor.Red;
            case Outcome.Cancelled:
                return ConsoleColor.Yellow;
            default:
                return ConsoleColor.DarkGray;
        }
    }
}
