using System.Text;

namespace InterviewScribe.Infrastructure.Processes;

public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? StandardOutputLogPath { get; init; }
    public string? StandardErrorLogPath { get; init; }
    public Encoding StandardOutputEncoding { get; init; } = new UTF8Encoding(false);
    public Encoding StandardErrorEncoding { get; init; } = new UTF8Encoding(false);
    public int MaxCapturedStandardOutputChars { get; init; } = 32 * 1024 * 1024;
    public int MaxCapturedStandardErrorChars { get; init; } = 2 * 1024 * 1024;
}

public sealed record ProcessResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutputLines,
    string StandardErrorTail,
    bool WasCancelled,
    TimeSpan Elapsed);

