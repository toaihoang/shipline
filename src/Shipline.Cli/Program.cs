using System.CommandLine;
using System.Text;

namespace Shipline.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Steps write UTF-8 (see ShellScripts), so the console must show UTF-8 too.
        Console.OutputEncoding = new UTF8Encoding(false);

        Option<FileInfo> fileOption = new Option<FileInfo>("--file", "-f")
        {
            Description = "The pipeline file. Default: .shipline.yml in the current folder.",
        };

        RootCommand root = new RootCommand("Shipline: a simple CI/CD tool. Run \"shipline run\" in a folder that has a .shipline.yml.");
        root.Subcommands.Add(RunCommand.Create(fileOption));
        root.Subcommands.Add(LintCommand.Create(fileOption));

        // On Ctrl+C the run is cancelled and its processes are killed; this is how long that may take before
        // the program exits anyway. The Job Object kills any processes left at that point.
        InvocationConfiguration configuration = new InvocationConfiguration
        {
            ProcessTerminationTimeout = TimeSpan.FromSeconds(15),
        };

        ParseResult parseResult = root.Parse(args);
        return await parseResult.InvokeAsync(configuration).ConfigureAwait(false);
    }
}
