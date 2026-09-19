using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace InterviewScribe.Infrastructure.Processes;

public sealed class ManagedProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!File.Exists(spec.FileName))
        {
            throw new FileNotFoundException($"找不到运行组件：{Path.GetFileName(spec.FileName)}", spec.FileName);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = spec.StandardOutputEncoding,
            StandardErrorEncoding = spec.StandardErrorEncoding
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('='))
            {
                throw new ArgumentException("子进程环境变量名称无效。", nameof(spec));
            }

            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var job = new WindowsJobObject();
        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(spec.FileName)}。");
        }

        _ = job.TryAssign(process);
        var stdoutLines = new List<string>();
        var stderrTail = new StringBuilder();
        var stdoutChars = 0;
        StreamWriter? stdoutLog = null;
        StreamWriter? stderrLog = null;

        try
        {
            stdoutLog = CreateLogWriter(spec.StandardOutputLogPath);
            stderrLog = CreateLogWriter(spec.StandardErrorLogPath);

            var stdoutTask = ReadStandardOutputAsync();
            var stderrTask = ReadStandardErrorAsync();
            var readersTask = Task.WhenAll(stdoutTask, stderrTask);
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancellationSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationRequested = 0;
            using var registration = cancellationToken.Register(() =>
            {
                Interlocked.Exchange(ref cancellationRequested, 1);
                cancellationSignal.TrySetResult();
                TryKill(process);
            });

            // Do not wait for process exit before observing the pipe readers. If a
            // callback, decoder or log write fails and a reader stops draining its
            // pipe, a chatty child can otherwise block forever on a full pipe.
            var firstCompleted = await Task.WhenAny(
                exitTask,
                stdoutTask,
                stderrTask,
                cancellationSignal.Task).ConfigureAwait(false);

            if ((firstCompleted == stdoutTask && stdoutTask.IsFaulted) ||
                (firstCompleted == stderrTask && stderrTask.IsFaulted))
            {
                TryKill(process);
            }

            if (firstCompleted == cancellationSignal.Task)
            {
                TryKill(process);
            }

            await exitTask.ConfigureAwait(false);

            if (Volatile.Read(ref cancellationRequested) != 0)
            {
                // Observe reader failures so their exceptions are not left
                // unobserved, but cancellation remains the public outcome.
                try
                {
                    await readersTask.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                }

                throw new OperationCanceledException(cancellationToken);
            }

            await readersTask.ConfigureAwait(false);
            stopwatch.Stop();

            return new ProcessResult(
                process.ExitCode,
                stdoutLines,
                stderrTail.ToString(),
                false,
                stopwatch.Elapsed);

            async Task ReadStandardOutputAsync()
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    stdoutChars += line.Length;
                    if (stdoutChars > spec.MaxCapturedStandardOutputChars)
                    {
                        throw new InvalidDataException("运行组件输出异常过大，已停止以保护内存。");
                    }

                    stdoutLines.Add(line);
                    onStandardOutput?.Invoke(line);
                    if (stdoutLog is not null)
                    {
                        await stdoutLog.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
            }

            async Task ReadStandardErrorAsync()
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    AppendTail(stderrTail, line, spec.MaxCapturedStandardErrorChars);
                    onStandardError?.Invoke(line);
                    if (stderrLog is not null)
                    {
                        await stderrLog.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            if (stdoutLog is not null)
            {
                await stdoutLog.DisposeAsync().ConfigureAwait(false);
            }

            if (stderrLog is not null)
            {
                await stderrLog.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process may exit between HasExited and Kill. This is expected
            // during cancellation and pipe-reader failure handling.
        }
    }

    private static StreamWriter? CreateLogWriter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        return new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
    }

    private static void AppendTail(StringBuilder builder, string line, int maximumCharacters)
    {
        builder.AppendLine(line);
        if (builder.Length <= maximumCharacters)
        {
            return;
        }

        builder.Remove(0, builder.Length - maximumCharacters);
    }
}
