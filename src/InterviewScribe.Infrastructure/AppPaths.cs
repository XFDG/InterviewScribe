namespace InterviewScribe.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? localRoot = null, string? installRoot = null)
    {
        LocalRoot = localRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewScribe");
        InstallRoot = installRoot ?? AppContext.BaseDirectory;
    }

    public string LocalRoot { get; }
    public string InstallRoot { get; }
    public string ModelsRoot => Path.Combine(LocalRoot, "models");
    public string JobsRoot => Path.Combine(LocalRoot, "jobs");
    public string LogsRoot => Path.Combine(LocalRoot, "logs");
    public string RuntimeRoot => Path.Combine(LocalRoot, "runtime");
    public string QwenRuntimeRoot => Path.Combine(LocalRoot, "qwen-runtime");
    public string QwenVirtualEnvironmentRoot => Path.Combine(QwenRuntimeRoot, ".venv");
    public string WhisperRuntimeRoot => Path.Combine(LocalRoot, "whisper-runtime");
    public string WhisperVirtualEnvironmentRoot => Path.Combine(WhisperRuntimeRoot, ".venv");
    public string WhisperModelRoot => Path.Combine(ModelsRoot, "Whisper-large-v3-turbo-ct2");
    // Qwen officially recommends ModelScope for downloads in Mainland China.
    // Keeping its snapshots separate from the MOSS GGUF store also lets the
    // installer adopt a manually completed official ModelScope download without
    // copying several additional gigabytes.
    public string QwenModelScopeRoot => Path.Combine(LocalRoot, "ModelScope-Qwen3");
    public string QwenAsrModelRoot => Path.Combine(QwenModelScopeRoot, "Qwen3-ASR-1.7B");
    public string QwenAlignerModelRoot => Path.Combine(QwenModelScopeRoot, "Qwen3-ForcedAligner-0.6B");
    public string SettingsPath => Path.Combine(LocalRoot, "settings.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(ModelsRoot);
        Directory.CreateDirectory(JobsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(QwenRuntimeRoot);
        Directory.CreateDirectory(WhisperRuntimeRoot);
    }

    public string CreateJobDirectory()
    {
        EnsureCreated();
        var path = Path.Combine(JobsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
