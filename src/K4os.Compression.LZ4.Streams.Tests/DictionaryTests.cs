
using K4os.Compression.LZ4.Internal;
using K4os.Compression.LZ4.Streams.Abstractions;
using TestHelpers;
using Xunit;

namespace K4os.Compression.LZ4.Streams.Tests;

public class DictionaryTests
{
    private static byte[] LoremBytes(int length)
    {
        var buf = new byte[length];
        Lorem.Fill(buf, 0, length);
        return buf;
    }

    private static LZ4Dictionary MakeDict(int size = 4096, uint? id = null) =>
        new(LoremBytes(size), id);

    // ------- Frame roundtrip via LZ4Frame static API -------

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(65536)]
    [InlineData(1 << 17)]
    public void FrameRoundtripWithDict(int sourceLength)
    {
        var dict = MakeDict();
        var source = LoremBytes(sourceLength);

        var encoderSettings = new LZ4EncoderSettings { DictionaryData = dict };
        var decoderSettings = new LZ4DecoderSettings { DictionaryData = dict };

        var compressed = new BufferWriter();
        LZ4Frame.Encode(source.AsSpan(), compressed, encoderSettings);

        var decompressed = new BufferWriter();
        LZ4Frame.Decode(compressed.WrittenSpan, decompressed, decoderSettings);

        Assert.Equal(source, decompressed.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(LZ4Level.L00_FAST)]
    [InlineData(LZ4Level.L09_HC)]
    [InlineData(LZ4Level.L12_MAX)]
    public void FrameRoundtripWithDictAllLevels(LZ4Level level)
    {
        var dict = MakeDict();
        var source = LoremBytes(32768);

        var encoderSettings = new LZ4EncoderSettings
        {
            DictionaryData = dict,
            CompressionLevel = level
        };
        var decoderSettings = new LZ4DecoderSettings { DictionaryData = dict };

        var compressed = new BufferWriter();
        LZ4Frame.Encode(source.AsSpan(), compressed, encoderSettings);

        var decompressed = new BufferWriter();
        LZ4Frame.Decode(compressed.WrittenSpan, decompressed, decoderSettings);

        Assert.Equal(source, decompressed.WrittenSpan.ToArray());
    }

    [Fact]
    public void FrameHeaderContainsDictId()
    {
        const uint dictId = 0xDEADBEEF;
        var dict = MakeDict(id: dictId);
        var source = LoremBytes(1024);

        var encoderSettings = new LZ4EncoderSettings { DictionaryData = dict };
        var compressed = new BufferWriter();
        LZ4Frame.Encode(source.AsSpan(), compressed, encoderSettings);

        // FLG byte is at offset 4 (after 4-byte magic). Bit 0 = hasDictionary.
        var flg = compressed.WrittenSpan[4];
        Assert.True((flg & 0x01) != 0, "FLG.DictID bit should be set");

        // DictID is written after optional ContentSize field.
        // With ChainBlocks=true, ContentSize=null, DictID present:
        // offset 4=FLG, 5=BD, 6..9=DictID (no ContentSize since bit not set)
        var dictIdInHeader = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            compressed.WrittenSpan.Slice(6));
        Assert.Equal(dictId, dictIdInHeader);
    }

    [Fact]
    public void FrameWithDictIdRoundtripWithCorrectDict()
    {
        const uint dictId = 42u;
        var dict = MakeDict(id: dictId);
        var source = LoremBytes(8192);

        var encoderSettings = new LZ4EncoderSettings { DictionaryData = dict };
        var decoderSettings = new LZ4DecoderSettings { DictionaryData = dict };

        var compressed = new BufferWriter();
        LZ4Frame.Encode(source.AsSpan(), compressed, encoderSettings);

        var decompressed = new BufferWriter();
        LZ4Frame.Decode(compressed.WrittenSpan, decompressed, decoderSettings);

        Assert.Equal(source, decompressed.WrittenSpan.ToArray());
    }

    [Fact]
    public void FrameDictImprovesCompressionRatio()
    {
        var dict = new LZ4Dictionary(LoremBytes(4096));
        var source = LoremBytes(65536);

        var noDict = new BufferWriter();
        var withDict = new BufferWriter();

        LZ4Frame.Encode(source.AsSpan(), noDict);
        LZ4Frame.Encode(source.AsSpan(), withDict,
            new LZ4EncoderSettings { DictionaryData = dict });

        Assert.True(withDict.WrittenSpan.Length < noDict.WrittenSpan.Length,
            $"Dict frame ({withDict.WrittenSpan.Length}) should be < no-dict ({noDict.WrittenSpan.Length})");
    }

    // ------- Stream-based roundtrip -------

    [Fact]
    public void StreamRoundtripWithDict()
    {
        var dict = MakeDict();
        var source = LoremBytes(65536);

        var encoderSettings = new LZ4EncoderSettings { DictionaryData = dict };
        var decoderSettings = new LZ4DecoderSettings { DictionaryData = dict };

        using var compressed = new MemoryStream();
        using (var writer = LZ4Frame.Encode(compressed, encoderSettings, leaveOpen: true))
            writer.WriteManyBytes(source.AsSpan());

        compressed.Position = 0;
        using var reader = LZ4Frame.Decode(compressed, decoderSettings, leaveOpen: false);
        var result = ReadAll(reader);

        Assert.Equal(source, result);
    }

    [Fact]
    public void LZ4DictionaryWithoutIdRoundtrips()
    {
        var dict = new LZ4Dictionary(LoremBytes(4096)); // no ID
        var source = LoremBytes(8192);

        var encoderSettings = new LZ4EncoderSettings { DictionaryData = dict };
        var decoderSettings = new LZ4DecoderSettings { DictionaryData = dict };

        var compressed = new BufferWriter();
        LZ4Frame.Encode(source.AsSpan(), compressed, encoderSettings);

        // FLG bit 0 should be unset (no dict ID in header)
        var flg = compressed.WrittenSpan[4];
        Assert.True((flg & 0x01) == 0, "FLG.DictID bit should NOT be set when DictionaryId is null");

        var decompressed = new BufferWriter();
        LZ4Frame.Decode(compressed.WrittenSpan, decompressed, decoderSettings);
        Assert.Equal(source, decompressed.WrittenSpan.ToArray());
    }

    private static byte[] ReadAll(ILZ4FrameReader reader)
    {
        var buffer = new BufferWriter();
        var tmp = new byte[Mem.K64];
        while (true)
        {
            var n = reader.ReadManyBytes(tmp.AsSpan(), interactive: false);
            if (n == 0) break;
            var dest = buffer.GetSpan(n);
            tmp.AsSpan(0, n).CopyTo(dest);
            buffer.Advance(n);
        }
        return buffer.WrittenSpan.ToArray();
    }
}