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

    public string FindQwenPython()
    {
        var explicitPath = Environment.GetEnvironmentVariable("INTERVIEWSCRIBE_QWEN_PYTHON");
        var pythonPath = string.IsNullOrWhiteSpace(explicitPath)
            ? Path.Combine(paths.QwenVirtualEnvironmentRoot, "Scripts", "python.exe")
            : Path.GetFullPath(explicitPath);

        if (!File.Exists(pythonPath) || new FileInfo(pythonPath).Length == 0)
        {
            throw new FileNotFoundException(
                "尚未安装 Qwen 高精度运行环境。请在界面中点击“安装/更新高精度组件”后重试；" +
                "如已自行安装，可用 INTERVIEWSCRIBE_QWEN_PYTHON 指定 python.exe。",
                pythonPath);
        }

        return pythonPath;
    }

    public string FindQwenSidecar()
    {
        var explicitPath = Environment.GetEnvironmentVariable("INTERVIEWSCRIBE_QWEN_SIDECAR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return ValidateQwenSidecar(Path.GetFullPath(explicitPath));
        }

        var installedPath = Path.Combine(paths.InstallRoot, "tools", "qwen", "qwen_sidecar.py");
        if (File.Exists(installedPath))
        {
            return ValidateQwenSidecar(installedPath);
        }

        // Source-tree launches do not run from the packaged root. Walk upward
        // only to find this repository's checked-in sidecar.
        var directory = new DirectoryInfo(paths.InstallRoot);
        for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
        {
            var developmentPath = Path.Combine(directory.FullName, "tools", "qwen", "qwen_sidecar.py");
            if (File.Exists(developmentPath))
            {
                return ValidateQwenSidecar(developmentPath);
            }
        }

        throw new FileNotFoundException(
            "程序安装不完整：缺少 Qwen 高精度转写组件 qwen_sidecar.py。请重新安装面试转写助手。",
            installedPath);
    }

    public string FindQwenAsrModelDirectory() => FindQwenModelDirectory(
        "INTERVIEWSCRIBE_QWEN_MODEL_PATH",
        paths.QwenAsrModelRoot,
        "Qwen3-ASR-1.7B-hf",
        QwenModelManifest.AsrRevision);

    public string FindQwenAlignerModelDirectory() => FindQwenModelDirectory(
        "INTERVIEWSCRIBE_QWEN_ALIGNER_PATH",
        paths.QwenAlignerModelRoot,
        "Qwen3-ForcedAligner-0.6B-hf",
        QwenModelManifest.AlignerRevision);

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

    private static string ValidateQwenSidecar(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new FileNotFoundException("Qwen 高精度转写组件不存在或为空。", path);
        }

        return path;
    }

    private static string FindQwenModelDirectory(
        string environmentVariable,
        string defaultPath,
        string displayName,
        string expectedRevision)
    {
        var explicitPath = Environment.GetEnvironmentVariable(environmentVariable);
        var modelDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(explicitPath) ? defaultPath : explicitPath);
        if (!Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException(
                $"本地高精度模型 {displayName} 尚未安装：{modelDirectory}。" +
                "请点击“安装/更新高精度组件”下载并固定模型权重。");
        }

        var configPath = Path.Combine(modelDirectory, "config.json");
        if (!File.Exists(configPath) || new FileInfo(configPath).Length == 0)
        {
            throw new FileNotFoundException(
                $"本地高精度模型 {displayName} 不完整：缺少 config.json。请重新运行安装脚本。",
                configPath);
        }

        ValidateQwenWeightFiles(modelDirectory, displayName);

        var markerPath = Path.Combine(modelDirectory, QwenModelManifest.RevisionMarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new FileNotFoundException(
                $"本地高精度模型 {displayName} 缺少版本标记，无法确认权重是否与程序匹配。" +
                "请重新运行高精度组件安装脚本。",
                markerPath);
        }

        string actualRevision;
        try
        {
            actualRevision = File.ReadAllText(markerPath).Trim();
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"无法读取本地高精度模型版本标记：{markerPath}", exception);
        }

        if (!string.Equals(actualRevision, expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"本地高精度模型 {displayName} 版本不匹配。" +
                $"期望 {expectedRevision}，实际 {actualRevision}。请重新运行安装脚本。");
        }

        return modelDirectory;
    }

    private static void ValidateQwenWeightFiles(string modelDirectory, string displayName)
    {
        var indexPath = Path.Combine(modelDirectory, "model.safetensors.index.json");
        if (!File.Exists(indexPath))
        {
            var hasWeights = Directory.EnumerateFiles(modelDirectory, "*.safetensors", SearchOption.TopDirectoryOnly)
                .Any(file => new FileInfo(file).Length > 0);
            if (!hasWeights)
            {
                throw new FileNotFoundException(
                    $"本地高精度模型 {displayName} 不完整：没有找到有效的 safetensors 权重。" +
                    "请重新运行安装脚本。");
            }

            return;
        }

        HashSet<string> indexedFiles = new(StringComparer.Ordinal);
        try
        {
            using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (!index.RootElement.TryGetProperty("weight_map", out var weightMap) ||
                weightMap.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"本地高精度模型 {displayName} 的权重索引缺少 weight_map：{indexPath}");
            }

            foreach (var property in weightMap.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } fileName)
                {
                    indexedFiles.Add(fileName);
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"本地高精度模型 {displayName} 的权重索引无法解析：{indexPath}",
                exception);
        }

        if (indexedFiles.Count == 0)
        {
            throw new InvalidDataException(
                $"本地高精度模型 {displayName} 的权重索引没有列出任何权重文件：{indexPath}");
        }

        var normalizedRoot = Path.GetFullPath(modelDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var missingFiles = new List<string>();
        foreach (var relativePath in indexedFiles)
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException(
                    $"本地高精度模型 {displayName} 的权重索引包含无效路径：{relativePath}");
            }

            var weightPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!weightPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"本地高精度模型 {displayName} 的权重索引包含越界路径：{relativePath}");
            }

            if (!File.Exists(weightPath) || new FileInfo(weightPath).Length == 0)
            {
                missingFiles.Add(relativePath);
            }
        }

        if (missingFiles.Count > 0)
        {
            throw new FileNotFoundException(
                $"本地高精度模型 {displayName} 缺少索引列出的权重分片：" +
                string.Join("、", missingFiles) + "。请重新运行安装脚本。");
        }
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
