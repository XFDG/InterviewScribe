using InterviewScribe.Infrastructure.Processes;

namespace InterviewScribe.Tests;

public sealed class ManagedProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenOutputConsumerFails_KillsChildWithoutDeadlock()
    {
        var runner = new ManagedProcessRunner();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runner.RunAsync(
                PowerShellSpec("[Console]::Out.WriteLine('ready'); Start-Sleep -Seconds 30"),
                _ => throw new InvalidDataException("simulated reader failure"))
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("simulated reader failure", exception.Message);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_KillsChildWithoutDeadlock()
    {
        var runner = new ManagedProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(
                PowerShellSpec("Start-Sleep -Seconds 30"),
                cancellationToken: cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentOnlyThroughChildProcessSpec()
    {
        const string variableName = "INTERVIEWSCRIBE_TEST_CHILD_SECRET";
        const string variableValue = "only-in-child-67c09ac8";
        var runner = new ManagedProcessRunner();
        var spec = PowerShellSpec(
            $"[Console]::Out.WriteLine([Environment]::GetEnvironmentVariable('{variableName}'))") with
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [variableName] = variableValue,
            },
        };

        var result = await runner.RunAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([variableValue], result.StandardOutputLines);
        Assert.Null(Environment.GetEnvironmentVariable(variableName));
    }

    private static ProcessSpec PowerShellSpec(string command)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(executable), $"PowerShell test host not found: {executable}");
        return new ProcessSpec
        {
            FileName = executable,
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
        };
    }
}
