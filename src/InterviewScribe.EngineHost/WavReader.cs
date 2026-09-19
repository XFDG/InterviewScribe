using System.Buffers.Binary;

namespace InterviewScribe.EngineHost;

internal sealed record WavAudio(float[] Samples, long DurationMs);

internal static class WavReader
{
    private const ushort Pcm = 1;
    private const ushort IeeeFloat = 3;
    private const int AudioReadBufferSize = 64 * 1024;

    public static WavAudio ReadMono16Khz(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMono16Khz(stream);
    }

    internal static WavAudio ReadMono16Khz(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (ReadFourCc(reader) != "RIFF" || reader.ReadUInt32() < 4 || ReadFourCc(reader) != "WAVE")
        {
            throw new EngineException("音频不是有效的 WAV 文件。");
        }

        ushort format = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        long dataOffset = -1;
        uint dataLength = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkLength = reader.ReadUInt32();
            var nextChunk = checked(stream.Position + chunkLength + (chunkLength & 1));
            if (nextChunk > stream.Length + 1)
            {
                throw new EngineException("WAV 文件的数据块已损坏。");
            }

            if (chunkId == "fmt ")
            {
                if (chunkLength < 16)
                {
                    throw new EngineException("WAV 格式块过短。");
                }

                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                dataOffset = stream.Position;
                dataLength = chunkLength;
            }

            stream.Position = Math.Min(nextChunk, stream.Length);
        }

        if (dataOffset < 0)
        {
            throw new EngineException("WAV 文件没有音频数据。");
        }

        if (channels != 1 || sampleRate != 16_000)
        {
            throw new EngineException($"音频必须是 16 kHz 单声道，当前为 {sampleRate} Hz / {channels} 声道。");
        }

        var bytesPerSample = bitsPerSample / 8;
        if (!((format == Pcm && bitsPerSample == 16) || (format == IeeeFloat && bitsPerSample == 32)))
        {
            throw new EngineException($"不支持的 WAV 格式：format={format}, bits={bitsPerSample}。");
        }

        if (dataLength % bytesPerSample != 0)
        {
            throw new EngineException("WAV 音频数据长度不合法。");
        }

        var sampleCountLong = dataLength / bytesPerSample;
        if (sampleCountLong == 0 || sampleCountLong > int.MaxValue)
        {
            throw new EngineException("音频为空或时长超出当前版本限制。");
        }

        var samples = new float[(int)sampleCountLong];
        stream.Position = dataOffset;
        var buffer = new byte[AudioReadBufferSize];
        var bytesRemaining = (long)dataLength;
        var sampleIndex = 0;
        while (bytesRemaining > 0)
        {
            var byteCount = (int)Math.Min(buffer.Length, bytesRemaining);
            try
            {
                stream.ReadExactly(buffer.AsSpan(0, byteCount));
            }
            catch (EndOfStreamException exception)
            {
                throw new EndOfStreamException("WAV 音频数据被意外截断。", exception);
            }

            var bytes = buffer.AsSpan(0, byteCount);
            for (var offset = 0; offset < byteCount; offset += bytesPerSample)
            {
                samples[sampleIndex++] = format == Pcm
                    ? BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) / 32768f
                    : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
            }

            bytesRemaining -= byteCount;
        }

        return new WavAudio(samples, samples.LongLength * 1000L / 16_000L);
    }

    private static string ReadFourCc(BinaryReader reader) => new(reader.ReadChars(4));
}
