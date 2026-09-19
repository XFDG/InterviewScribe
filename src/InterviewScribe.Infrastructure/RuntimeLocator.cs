using System.Text.Json;

namespace InterviewScribe.Infrastructure;

public sealed class RuntimeLocator(AppPaths paths)
{
    public string FindFfmpeg() => FindRequiredFile(
        "INTERVIEWSCRIBE_FFMPEG",
        "ffmpeg.exe",
        Path.Combine(paths.InstallRoot, "tools", "ffmpeg"),
        Path.Combine(paths.RuntimeRoot, "ffmpeg"));

    public string FindFfprobe() => FindRequiredFile(
        "INTERVIEWSCRIBE_FFPROBE",
        "ffprobe.exe",
        Path.Combine(paths.InstallRoot, "tools", "ffmpeg"),
        Path.Combine(paths.RuntimeRoot, "ffmpeg"));

    public string FindEngineHost() => FindRequiredFile(
        "INTERVIEWSCRIBE_ENGINE_HOST",
        "InterviewScribe.EngineHost.exe",
        Path.Combine(paths.InstallRoot, "tools", "engine"),
        paths.InstallRoot);

    public string FindTranscribeRuntimeDirectory()
    {
        var explicitPath = Environment.GetEnvironmentVariable("INTERVIEWSCRIBE_TRANSCRIBE_RUNTIME");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            ValidateTranscribeRuntime(fullPath);
            return fullPath;
        }

        var dll = FindRequiredFile(
            null,
            "transcribe.dll",
            Path.Combine(paths.InstallRoot, "tools", "transcribe"),
            Path.Combine(paths.RuntimeRoot, "transcribe"));
        var runtimeDirectory = Path.GetDirectoryName(dll)
            ?? throw new DirectoryNotFoundException("无法确定原生推理库目录。");
        ValidateTranscribeRuntime(runtimeDirectory);
        return runtimeDirectory;
    }

    private void ValidateTranscribeRuntime(string runtimeDirectory)
    {
        if (!Directory.Exists(runtimeDirectory))
        {
            throw new DirectoryNotFoundException($"原生推理库目录不存在：{runtimeDirectory}");
        }

        var lockPath = FindDependencyLockPath();
        DependencyLock? dependencyLock;
        try
        {
            using var stream = File.OpenRead(lockPath);
            dependencyLock = JsonSerializer.Deserialize<DependencyLock>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidDataException($"无法读取原生依赖锁文件：{lockPath}", exception);
        }

        var requiredFiles = dependencyLock?.NativeDependencies?.TranscribeCpp?.RequiredFiles;
        if (requiredFiles is not { Count: > 0 })
        {
            throw new InvalidDataException(
                $"原生依赖锁文件缺少 nativeDependencies.transcribeCpp.requiredFiles：{lockPath}");
        }

        var normalizedRoot = Path.GetFullPath(runtimeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var missingFiles = new List<string>();
        foreach (var relativePath in requiredFiles)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException($"原生依赖锁文件包含无效路径：{relativePath}");
            }

            var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"原生依赖锁文件中的路径越过了运行库目录：{relativePath}");
            }

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            {
                missingFiles.Add(relativePath);
            }
        }

        if (missingFiles.Count > 0)
        {
            throw new FileNotFoundException(
                "程序安装不完整，原生推理库缺少必需文件：" +
                string.Join("、", missingFiles) + "。请重新安装面试转写助手。");
        }
    }

    private string FindDependencyLockPath()
    {
        var installedLock = Path.Combine(paths.InstallRoot, "dependencies.lock.json");
        if (File.Exists(installedLock))
        {
            return installedLock;
        }

        // Test and source-tree launches do not run from the packaged root. Walk
        // upward only to locate this repository's authoritative packaging lock.
        var directory = new DirectoryInfo(paths.InstallRoot);
        for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
        {
            var developmentLock = Path.Combine(directory.FullName, "packaging", "dependencies.lock.json");
            if (File.Exists(developmentLock))
            {
                return developmentLock;
            }
        }

        throw new FileNotFoundException(
            "程序安装不完整：缺少 dependencies.lock.json。请重新安装面试转写助手。",
            installedLock);
    }

    private static string FindRequiredFile(string? environmentVariable, string fileName, params string[] roots)
    {
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var explicitPath = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var fullPath = Path.GetFullPath(explicitPath);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                throw new FileNotFoundException($"{environmentVariable} 指向的文件不存在。", fullPath);
            }
        }

        var fromInstall = FindOptionalTool(fileName, roots);
        if (fromInstall is not null)
        {
            return fromInstall;
        }

        var fromPath = FindOnPath(fileName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        throw new FileNotFoundException($"程序安装不完整：缺少 {fileName}。请重新安装面试转写助手。");
    }

    private static string? FindOptionalTool(
        string fileName,
        params string[] roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var direct = Path.Combine(root, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var nested = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var value = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var candidateRoot in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(candidateRoot.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record DependencyLock(NativeDependencies? NativeDependencies);
    private sealed record NativeDependencies(TranscribeDependency? TranscribeCpp);
    private sealed record TranscribeDependency(IReadOnlyList<string>? RequiredFiles);
}
