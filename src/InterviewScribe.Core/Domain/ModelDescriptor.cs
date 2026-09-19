namespace InterviewScribe.Core.Domain;

public sealed record ModelDescriptor(
    string DisplayName,
    string FileName,
    string Revision,
    Uri DownloadUri,
    long ExpectedBytes,
    string Sha256);

