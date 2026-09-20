namespace InterviewScribe.Infrastructure;

/// <summary>Locked CTranslate2 checkpoint used by the offline fast mode.</summary>
internal static class FasterWhisperModelManifest
{
    internal const string RevisionMarkerFileName = ".mediascribe-revision";
    internal const string Repository = "dropbox-dash/faster-whisper-large-v3-turbo";
    internal const string Revision = "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf";
    internal const string ModelFileName = "model.bin";
    internal const long ModelFileSizeBytes = 1_617_884_929;
    internal const string ModelFileSha256 = "e76620f83d5f5b69efd3d87e3dc180c1bd21df9fbebacfd4335e5e1efcc018da";
}
