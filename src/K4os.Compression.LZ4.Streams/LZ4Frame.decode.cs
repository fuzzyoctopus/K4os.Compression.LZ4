using System.Buffers;
using System.IO.Pipelines;
using K4os.Compression.LZ4.Streams.Abstractions;
using K4os.Compression.LZ4.Streams.Adapters;
using K4os.Compression.LZ4.Streams.Frames;

namespace K4os.Compression.LZ4.Streams;

/// <summary>
/// LZ4 factory methods to encode/decode anything which can be represented as a stream-like object.
/// Please note, to avoid all the complexity of dealing with streams, it uses
/// <see cref="ILZ4FrameReader"/> and <see cref="ILZ4FrameWriter"/> as stream abstractions.
/// </summary>
public static partial class LZ4Frame
{
    private static LZ4DecoderSettings ToDecoderSettings(int extraMemory) => new() { ExtraMemory = extraMemory };

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="source">Span to read from.</param>
    /// <param name="target">Buffer to write to.</param>
    /// <param name="settings">Decoder settings.</param>
    public static unsafe TBufferWriter Decode<TBufferWriter>(
        ReadOnlySpan<byte> source, TBufferWriter target, LZ4DecoderSettings? settings = null)
        where TBufferWriter: IBufferWriter<byte>
    {
        settings ??= LZ4DecoderSettings.Default;
        fixed (byte* source0 = source)
        {
            var decoder = new ByteSpanLZ4FrameReader(
                UnsafeByteSpan.Create(source0, source.Length),
                i => i.CreateDecoder(settings), settings.DictionaryData);
            using (decoder) decoder.CopyTo(target);
            return target;
        }
    }

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="source">Span to read from.</param>
    /// <param name="target">Buffer to write to.</param>
    /// <param name="extraMemory">Extra memory used for decompression.</param>
    public static TBufferWriter Decode<TBufferWriter>(
        ReadOnlySpan<byte> source, TBufferWriter target, int extraMemory = 0)
        where TBufferWriter : IBufferWriter<byte> =>
        Decode(source, target, ToDecoderSettings(extraMemory));

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="memory">Stream to be decoded.</param>
    /// <param name="settings">Decoder settings.</param>
    /// <returns>Decompression stream.</returns>
    public static ByteMemoryLZ4FrameReader Decode(
        ReadOnlyMemory<byte> memory, LZ4DecoderSettings? settings = default)
    {
        settings ??= LZ4DecoderSettings.Default;
        return new(memory, i => i.CreateDecoder(settings), settings.DictionaryData);
    }


    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="memory">Stream to be decoded.</param>
    /// <param name="extraMemory">Extra memory used for decompression.</param>
    /// <returns>Decompression stream.</returns>
    public static ByteMemoryLZ4FrameReader Decode(
        ReadOnlyMemory<byte> memory, int extraMemory) =>
        Decode(memory, ToDecoderSettings(extraMemory));

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="sequence">Stream to be decoded.</param>
    /// <param name="settings">Decoder settings.</param>
    /// <returns>Decompression stream.</returns>
    public static ByteSequenceLZ4FrameReader Decode(
        ReadOnlySequence<byte> sequence, LZ4DecoderSettings? settings = default)
    {
        settings ??= LZ4DecoderSettings.Default;
        return new(sequence, i => i.CreateDecoder(settings), settings.DictionaryData);
    }

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="sequence">Stream to be decoded.</param>
    /// <param name="extraMemory">Extra memory used for decompression.</param>
    /// <returns>Decompression stream.</returns>
    public static ByteSequenceLZ4FrameReader Decode(
        ReadOnlySequence<byte> sequence, int extraMemory) =>
        Decode(sequence, ToDecoderSettings(extraMemory));

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="stream">Stream to be decoded.</param>
    /// <param name="settings">Decoder settings.</param>
    /// <param name="leaveOpen">Indicates if stream should stay open after disposing decoder.</param>
    /// <returns>Decompression stream.</returns>
    public static StreamLZ4FrameReader Decode(
        Stream stream, LZ4DecoderSettings? settings = default, bool leaveOpen = false)
    {
        settings ??= LZ4DecoderSettings.Default;
        return new(stream, leaveOpen, i => i.CreateDecoder(settings), settings.DictionaryData);
    }

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="stream">Stream to be decoded.</param>
    /// <param name="extraMemory">Extra memory used for decompression.</param>
    /// <param name="leaveOpen">Indicates if stream should stay open after disposing decoder.</param>
    /// <returns>Decompression stream.</returns>
    public static StreamLZ4FrameReader Decode(
        Stream stream, int extraMemory, bool leaveOpen = false) =>
        Decode(stream, ToDecoderSettings(extraMemory), leaveOpen);

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="reader">Stream to be decoded.</param>
    /// <param name="settings">Decoder settings.</param>
    /// <param name="leaveOpen">Indicates if stream should stay open after disposing decoder.</param>
    /// <returns>Decompression stream.</returns>
    public static PipeLZ4FrameReader Decode(
        PipeReader reader, LZ4DecoderSettings? settings = default, bool leaveOpen = false)
    {
        settings ??= LZ4DecoderSettings.Default;
        return new(reader, leaveOpen, i => i.CreateDecoder(settings), settings.DictionaryData);
    }

    /// <summary>Creates decompression stream on top of inner stream.</summary>
    /// <param name="reader">Stream to be decoded.</param>
    /// <param name="extraMemory">Extra memory used for decompression.</param>
    /// <param name="leaveOpen">Indicates if stream should stay open after disposing decoder.</param>
    /// <returns>Decompression stream.</returns>
    public static PipeLZ4FrameReader Decode(
        PipeReader reader, int extraMemory, bool leaveOpen = false) =>
        Decode(reader, ToDecoderSettings(extraMemory), leaveOpen);
}
