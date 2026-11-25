namespace Shipline.Cli;

internal static class ExitCodes
{
    public const int Succeeded = 0;
    public const int Failed = 1;

    /// <summary>Bad command line or invalid pipeline file: nothing was run.</summary>
    public const int InvalidInput = 2;

    /// <summary>128 + SIGINT, the usual exit code after Ctrl+C.</summary>
    public const int Cancelled = 130;
}
