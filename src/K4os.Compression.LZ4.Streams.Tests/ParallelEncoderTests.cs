using K4os.Compression.LZ4.Encoders;
using K4os.Compression.LZ4.Internal;
using K4os.Compression.LZ4.Streams.Tests.Internal;
using TestHelpers;
using Xunit;

namespace K4os.Compression.LZ4.Streams.Tests;

public class ParallelEncoderTests
{
    [Theory]
    [InlineData(".corpus/reymont", 1337)]
    [InlineData(".corpus/mozilla", 7331)]
    public async Task ParallelEncoderProducesByteIdenticalOutput(string filename, int seed)
    {
        var source = File.ReadAllBytes(Tools.FindFile(filename));

        var sequential = new LZ4EncoderSettings {
            ChainBlocks = false,
            BlockChecksum = true,
            ContentChecksum = true,
            BlockSize = Mem.K64,
            CompressionLevel = LZ4Level.L09_HC,
            MaxDegreeOfParallelism = 1,
        };

        var parallel = new LZ4EncoderSettings {
            ChainBlocks = false,
            BlockChecksum = true,
            ContentChecksum = true,
            BlockSize = Mem.K64,
            CompressionLevel = LZ4Level.L09_HC,
            MaxDegreeOfParallelism = 4,
        };

        using var memoryA = new MemoryStream();
        using var memoryB = new MemoryStream();

        using (var streamA = LZ4Stream.Encode(memoryA, sequential, leaveOpen: true))
        using (var streamB = LZ4Stream.Encode(memoryB, parallel, leaveOpen: true))
        {
            var random = new Random(seed);
            var offset = 0;

            while (offset < source.Length)
            {
                var chunk = Math.Min(
                    random.Next(1, Mem.K256 + 1),
                    source.Length - offset);

                streamA.Write(source, offset, chunk);
                await streamB.WriteAsync(source, offset, chunk);
                offset += chunk;
            }
        }

        Tools.SameBytes(memoryA.ToArray(), memoryB.ToArray());
    }

    [Fact]
    public void ParallelCompressionPreservesBlockOrderUnderStress()
    {
        var source = new byte[Mem.M1 * 32 + 1337];
        new Random(1337).NextBytes(source);

        var settings = new LZ4EncoderSettings {
            ChainBlocks = false,
            BlockSize = Mem.K64,
            CompressionLevel = LZ4Level.L09_HC,
            MaxDegreeOfParallelism = 8,
        };

        using var encoded = new MemoryStream();
        using (var stream = LZ4Stream.Encode(encoded, settings, leaveOpen: true))
        {
            var random = new Random(7331);
            var offset = 0;

            while (offset < source.Length)
            {
                var chunk = Math.Min(random.Next(1, Mem.K32), source.Length - offset);
                stream.Write(source, offset, chunk);
                offset += chunk;
            }
        }

        encoded.Position = 0;

        using var decoded = new MemoryStream();
        using (var stream = LZ4Stream.Decode(encoded, leaveOpen: true))
            stream.CopyTo(decoded);

        Tools.SameBytes(source, decoded.ToArray());
    }

    [Fact]
    public void ParallelCompressionIsDecodedByReferenceLz4()
    {
        var executable = Tools.FindFile(".tools/lz4.exe");
        if (!File.Exists(executable))
            return;

        var original = Tools.FindFile(".corpus/reymont");
        var encoded = Path.GetTempFileName();
        var decoded = Path.GetTempFileName();

        try
        {
            using (var input = File.OpenRead(original))
            using (var output = File.Create(encoded))
            using (var stream = LZ4Stream.Encode(
                       output,
                       new LZ4EncoderSettings {
                           ChainBlocks = false,
                           BlockSize = Mem.K64,
                           CompressionLevel = LZ4Level.L09_HC,
                           MaxDegreeOfParallelism = 4,
                       }))
                input.CopyTo(stream);

            ReferenceLZ4.Decode(encoded, decoded);
            Tools.SameFiles(original, decoded);
        }
        finally
        {
            File.Delete(encoded);
            File.Delete(decoded);
        }
    }

    [Fact]
    public void ParallelCompressionRequiresIndependentBlocks()
    {
        var settings = new LZ4EncoderSettings {
            ChainBlocks = true,
            MaxDegreeOfParallelism = 2,
        };

        var error = Assert.Throws<ArgumentException>(() => settings.CreateDescriptor());
        Assert.Contains("ChainBlocks", error.Message);
    }
}
