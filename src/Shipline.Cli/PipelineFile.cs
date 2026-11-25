using Shipline.Core.Pipelines;

namespace Shipline.Cli;

/// <summary>A pipeline file found on disk and parsed.</summary>
internal sealed class PipelineFile
{
    private static readonly string[] s_defaultFileNames = { ".shipline.yml", ".shipline.yaml" };

    private PipelineFile(string path, PipelineParseResult result)
    {
        Path = path;
        Result = result;
    }

    public string Path { get; }

    /// <summary>The folder that holds the pipeline file. Steps run here.</summary>
    public string Workspace => System.IO.Path.GetDirectoryName(Path) ?? Directory.GetCurrentDirectory();

    /// <summary>The path as the user sees it: relative to the current folder when possible.</summary>
    public string DisplayPath => System.IO.Path.GetRelativePath(Directory.GetCurrentDirectory(), Path);

    public PipelineParseResult Result { get; }

    /// <summary>Loads the given file, or .shipline.yml / .shipline.yaml in the current folder. Writes an error and returns null when none is found.</summary>
    public static PipelineFile? Load(FileInfo? file)
    {
        string? path = FindPath(file);
        if (path is null)
        {
            return null;
        }

        string yaml = File.ReadAllText(path);
        string folderName = new DirectoryInfo(System.IO.Path.GetDirectoryName(path) ?? ".").Name;
        PipelineParseResult result = PipelineParser.Parse(yaml, folderName);
        return new PipelineFile(path, result);
    }

    public void WriteDiagnostics(TextWriter writer)
    {
        foreach (PipelineDiagnostic diagnostic in Result.Diagnostics)
        {
            writer.WriteLine(diagnostic.Format(DisplayPath));
        }
    }

    private static string? FindPath(FileInfo? file)
    {
        if (file is not null)
        {
            if (!file.Exists)
            {
                Console.Error.WriteLine("error: The pipeline file \"" + file.FullName + "\" does not exist.");
                return null;
            }

            return file.FullName;
        }

        string folder = Directory.GetCurrentDirectory();
        foreach (string fileName in s_defaultFileNames)
        {
            string candidate = System.IO.Path.Combine(folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Console.Error.WriteLine("error: No .shipline.yml in \"" + folder + "\". Create one, or pass its path with --file.");
        return null;
    }
}
