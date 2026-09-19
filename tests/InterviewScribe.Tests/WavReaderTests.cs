using System.Text;
using InterviewScribe.EngineHost;

namespace InterviewScribe.Tests;

public sealed class WavReaderTests
{
    [Fact]
    public void ReadMono16Khz_ReadsPcm16SamplesAndDuration()
    {
        short[] pattern = [short.MinValue, 0, short.MaxValue];
        var samples = new short[160];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = pattern[index % pattern.Length];
        }
        using var file = TemporaryFile.FromBytes(CreatePcm16Wav(samples));

        var audio = WavReader.ReadMono16Khz(file.Path);

        Assert.Equal(160, audio.Samples.Length);
        Assert.Equal(10, audio.DurationMs);
        Assert.Equal(-1f, audio.Samples[0]);
        Assert.Equal(0f, audio.Samples[1]);
        Assert.InRange(audio.Samples[2], 0.9999f, 1f);
    }

    [Fact]
    public void ReadMono16Khz_WhenLastSampleIsTruncated_ThrowsEndOfStreamException()
    {
        var complete = CreatePcm16Wav([short.MinValue, short.MaxValue]);
        var truncated = complete[..^1];
        using var file = TemporaryFile.FromBytes(truncated);

        var exception = Assert.Throws<EndOfStreamException>(() => WavReader.ReadMono16Khz(file.Path));

        Assert.Contains("意外截断", exception.Message);
    }

    [Fact]
    public void ReadMono16Khz_ReadsOneMillionSamplesInBlocks()
    {
        const int sampleCount = 1_000_000;
        var samples = new short[sampleCount];
        samples[0] = short.MinValue;
        samples[543_210] = 12_345;
        samples[^1] = short.MaxValue;
        using var stream = new CountingReadStream(CreatePcm16Wav(samples));

        var audio = WavReader.ReadMono16Khz(stream);

        Assert.Equal(sampleCount, audio.Samples.Length);
        Assert.Equal(62_500, audio.DurationMs);
        Assert.Equal(-1f, audio.Samples[0]);
        Assert.Equal(12_345 / 32768f, audio.Samples[543_210]);
        Assert.InRange(audio.Samples[^1], 0.9999f, 1f);
        Assert.InRange(stream.ReadCallCount, 1, 128);
    }

    [Fact]
    public void ReadMono16Khz_RejectsWrongSampleRate()
    {
        using var file = TemporaryFile.FromBytes(CreatePcm16Wav([0, 1], sampleRate: 44_100));

        var exception = Assert.Throws<EngineException>(() => WavReader.ReadMono16Khz(file.Path));

        Assert.Contains("16 kHz 单声道", exception.Message);
        Assert.Contains("44100 Hz", exception.Message);
    }

    [Fact]
    public void ReadMono16Khz_SkipsOddSizedChunkAndItsPadding()
    {
        using var stream = new MemoryStream(CreatePcm16Wav([short.MinValue, short.MaxValue], includeOddJunkChunk: true));

        var audio = WavReader.ReadMono16Khz(stream);

        Assert.Equal([-1f, short.MaxValue / 32768f], audio.Samples);
    }

    private static byte[] CreatePcm16Wav(
        IReadOnlyList<short> samples,
        uint sampleRate = 16_000,
        bool includeOddJunkChunk = false)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * (bitsPerSample / 8);
        var dataLength = checked((uint)samples.Count * blockAlign);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36u + dataLength + (includeOddJunkChunk ? 10u : 0u)));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        if (includeOddJunkChunk)
        {
            writer.Write(Encoding.ASCII.GetBytes("JUNK"));
            writer.Write(1u);
            writer.Write((byte)0x7f);
            writer.Write((byte)0); // RIFF chunks are padded to an even boundary.
        }

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * blockAlign));
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryFile FromBytes(byte[] bytes)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"InterviewScribe-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, bytes);
            return new TemporaryFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class CountingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int ReadCallCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCallCount++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCallCount++;
            return base.Read(buffer);
        }
    }
}
