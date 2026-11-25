namespace Shipline.Core.Pipelines;

/// <summary>An error found in a pipeline file, with the place it was found.</summary>
public sealed class PipelineDiagnostic
{
    public PipelineDiagnostic(SourceLocation location, string message)
    {
        Location = location;
        Message = message;
    }

    public SourceLocation Location { get; }

    public string Message { get; }

    /// <summary>Formats the error as "file:line:column: error: message", which editors can turn into a link.</summary>
    public string Format(string fileName)
    {
        return fileName + ":" + Location + ": error: " + Message;
    }
}
