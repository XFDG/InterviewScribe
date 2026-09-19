using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace InterviewScribe.EngineHost;

internal static class NativeMethods
{
    private const string LibraryName = "transcribe.dll";
    private static string? _libraryPath;
    private static LogCallback? _logCallback;

    public enum Status
    {
        Ok = 0,
    }

    public enum BackendRequest
    {
        Auto = 0,
        Cpu = 1,
        Vulkan = 3,
        Cuda = 5,
    }

    public enum TranscribeTask
    {
        Transcribe = 0,
    }

    public enum TimestampKind
    {
        Segment = 2,
    }

    public enum DiarizeMode
    {
        On = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelLoadParams
    {
        public ulong StructSize;
        public BackendRequest Backend;
        public IntPtr Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunParams
    {
        public ulong StructSize;
        public TranscribeTask Task;
        public TimestampKind Timestamps;
        public int Pnc;
        public int Itn;
        public DiarizeMode Diarize;
        public IntPtr Language;
        public IntPtr TargetLanguage;

        [MarshalAs(UnmanagedType.I1)]
        public bool KeepSpecialTags;

        public IntPtr Family;
        public int SpeculativeDrafts;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Segment
    {
        public ulong StructSize;
        public long StartMs;
        public long EndMs;
        public int FirstWord;
        public int WordCount;
        public int FirstToken;
        public int TokenCount;
        public IntPtr Text;
        public int SpeakerId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(int level, IntPtr message, IntPtr userData);

    public static void Configure(string runtimeDirectory)
    {
        _libraryPath = Path.Combine(runtimeDirectory, LibraryName);
        if (!File.Exists(_libraryPath))
        {
            throw new EngineException($"缺少原生推理库：{_libraryPath}");
        }

        _ = SetDllDirectory(runtimeDirectory);
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveLibrary);
    }

    public static void InstallLogCallback()
    {
        _logCallback = (_, message, _) =>
        {
            var value = Utf8(message).Trim();
            if (value.Length > 0)
            {
                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { type = "native", message = value }));
            }
        };
        TranscribeLogSet(_logCallback, IntPtr.Zero);
    }

    public static void VerifyAbi()
    {
        VerifyStructSize(0, Unsafe.SizeOf<ModelLoadParams>(), nameof(ModelLoadParams));
        VerifyStructSize(2, Unsafe.SizeOf<RunParams>(), nameof(RunParams));
        VerifyStructSize(6, Unsafe.SizeOf<Segment>(), nameof(Segment));
    }

    public static string StatusText(Status status) => Utf8(TranscribeStatusString((int)status));

    public static string Utf8(IntPtr pointer) => pointer == IntPtr.Zero
        ? string.Empty
        : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

    private static void VerifyStructSize(int id, int managedSize, string name)
    {
        var nativeSize = checked((int)TranscribeAbiStructSize(id));
        if (nativeSize != managedSize)
        {
            throw new EngineException($"原生库 ABI 不匹配：{name} C#={managedSize}, native={nativeSize}。");
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly _, DllImportSearchPath? __)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase) || _libraryPath is null)
        {
            return IntPtr.Zero;
        }

        return NativeLibrary.Load(_libraryPath);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_status_string")]
    private static extern IntPtr TranscribeStatusString(int status);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_abi_struct_size")]
    private static extern nuint TranscribeAbiStructSize(int which);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_log_set")]
    private static extern void TranscribeLogSet(LogCallback callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_init_backends")]
    internal static extern Status TranscribeInitBackends([MarshalAs(UnmanagedType.LPUTF8Str)] string artifactDirectory);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_load_params_init")]
    internal static extern void TranscribeModelLoadParamsInit(ref ModelLoadParams parameters);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_run_params_init")]
    internal static extern void TranscribeRunParamsInit(ref RunParams parameters);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_segment_init")]
    internal static extern void TranscribeSegmentInit(ref Segment segment);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_open")]
    internal static extern Status TranscribeOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref ModelLoadParams loadParameters,
        IntPtr sessionParameters,
        out IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_session_free")]
    internal static extern void TranscribeSessionFree(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_get_model")]
    internal static extern IntPtr TranscribeGetModel(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_backend")]
    internal static extern IntPtr TranscribeModelBackend(IntPtr model);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_version")]
    internal static extern IntPtr TranscribeVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_run")]
    internal static extern Status TranscribeRun(IntPtr session, float[] pcm, int sampleCount, ref RunParams parameters);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_n_segments")]
    internal static extern int TranscribeSegmentCount(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_get_segment")]
    internal static extern Status TranscribeGetSegment(IntPtr session, int index, ref Segment segment);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_detected_language")]
    internal static extern IntPtr TranscribeDetectedLanguage(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_full_text")]
    internal static extern IntPtr TranscribeFullText(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_raw_text")]
    internal static extern IntPtr TranscribeRawText(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_was_truncated")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool TranscribeWasTruncated(IntPtr session);
}
