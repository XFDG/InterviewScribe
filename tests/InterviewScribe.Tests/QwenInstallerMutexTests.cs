using System.Diagnostics;

namespace InterviewScribe.Tests;

public sealed class QwenInstallerMutexTests
{
    private const string InstallerMutexName = @"Global\InterviewScribe.QwenRuntimeInstaller.v1";
    private const int InstallerAlreadyRunningExitCode = 1618;

    [Fact]
    public void Installer_ResolvesRegisteredPython311Or312ByExecutablePath()
    {
        var script = File.ReadAllText(FindInstallerScript());

        Assert.Contains("Get-CompatibleBasePython", script);
        Assert.Contains("Test-CompatiblePython", script);
        Assert.Contains("py.exe", script);
        Assert.Contains("-0p", script);
        Assert.Contains("sys.implementation.name", script);
        Assert.Contains("sys.version_info[:2] in ((3, 11), (3, 12))", script);
        Assert.Contains("sys.maxsize > 2**32", script);
        Assert.Contains("elseif (-not (Test-CompatiblePython -PythonPath $PythonExe))", script);
        Assert.DoesNotContain("@(\"-3.11\", \"-m\", \"venv\"", script);
    }

    [Fact]
    public async Task Installer_WhenAnotherProcessOwnsMutex_ExitsWithClearConflict()
    {
        var installerPath = FindInstallerScript();
        using var mutexHolder = new InstallerMutexHolder();
        Assert.True(mutexHolder.OwnsMutex, "The test could not acquire the Qwen installer mutex.");

        var result = await RunInstallerAsync(installerPath);

        Assert.Equal(InstallerAlreadyRunningExitCode, result.ExitCode);
        Assert.Contains("INTERVIEWSCRIBE_INSTALL_ALREADY_RUNNING", result.CombinedOutput);
        Assert.Contains("already running", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Installer_WhenStartupFails_ReleasesMutexInFinally()
    {
        using var directory = new TemporaryDirectory();
        var copiedInstallerDirectory = Path.Combine(directory.Path, "tools", "qwen");
        Directory.CreateDirectory(copiedInstallerDirectory);
        var copiedInstallerPath = Path.Combine(copiedInstallerDirectory, "Install-QwenRuntime.ps1");
        File.Copy(FindInstallerScript(), copiedInstallerPath);

        var result = await RunInstallerAsync(copiedInstallerPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dependencies.lock.json was not found", result.CombinedOutput);

        using var mutex = new Mutex(initiallyOwned: false, InstallerMutexName);
        var ownsMutex = AcquireMutex(mutex);
        try
        {
            Assert.True(ownsMutex, "The failed installer did not release its system-wide mutex.");
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool AcquireMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static async Task<InstallerResult> RunInstallerAsync(string installerPath)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(powershell), $"Windows PowerShell was not found: {powershell}");

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(installerPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell test process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Qwen installer mutex test did not finish within 20 seconds.");
        }

        return new InstallerResult(
            process.ExitCode,
            (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private static string FindInstallerScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "qwen", "Install-QwenRuntime.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate tools/qwen/Install-QwenRuntime.ps1 from the test output directory.");
    }

    private sealed record InstallerResult(int ExitCode, string CombinedOutput);

    private sealed class InstallerMutexHolder : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;
        private Exception? _failure;

        public InstallerMutexHolder()
        {
            _thread = new Thread(HoldMutex)
            {
                IsBackground = true,
                Name = "InterviewScribe installer mutex test holder"
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test mutex holder did not start within five seconds.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException("The test mutex holder failed to start.", _failure);
            }
        }

        public bool OwnsMutex { get; private set; }

        public void Dispose()
        {
            _release.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test mutex holder did not stop within five seconds.");
            }

            _ready.Dispose();
            _release.Dispose();
            if (_failure is not null)
            {
                throw new InvalidOperationException("The test mutex holder failed while releasing the mutex.", _failure);
            }
        }

        private void HoldMutex()
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, InstallerMutexName);
                OwnsMutex = AcquireMutex(mutex);
                _ready.Set();
                if (!OwnsMutex)
                {
                    return;
                }

                _release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterviewScribe.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
