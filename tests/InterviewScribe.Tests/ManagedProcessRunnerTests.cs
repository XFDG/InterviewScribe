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
