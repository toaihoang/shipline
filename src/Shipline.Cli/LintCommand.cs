using System.CommandLine;
using System.Globalization;
using Shipline.Core.Pipelines;

namespace Shipline.Cli;

/// <summary>"shipline lint": checks a pipeline file without running anything.</summary>
internal static class LintCommand
{
    public static Command Create(Option<FileInfo> fileOption)
    {
        Command command = new Command("lint", "Check the pipeline file and list every error with its line and column.");
        command.Options.Add(fileOption);
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileOption)));
        return command;
    }

    private static int Execute(FileInfo? file)
    {
        PipelineFile? pipelineFile = PipelineFile.Load(file);
        if (pipelineFile is null)
        {
            return ExitCodes.InvalidInput;
        }

        PipelineDefinition? pipeline = pipelineFile.Result.Pipeline;
        if (pipeline is null)
        {
            pipelineFile.WriteDiagnostics(Console.Error);
            string errorCount = pipelineFile.Result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture);
            Console.Error.WriteLine(errorCount + " error(s).");
            return ExitCodes.InvalidInput;
        }

        int stepCount = pipeline.Jobs.Sum(job => job.Steps.Count);
        string summary = string.Create(CultureInfo.InvariantCulture, $"{pipeline.Jobs.Count} job(s), {stepCount} step(s)");
        Console.WriteLine(pipelineFile.DisplayPath + ": OK, " + summary + ".");
        return ExitCodes.Succeeded;
    }
}
