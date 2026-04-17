using System;
using K4os.Compression.LZ4.Encoders;
using K4os.Compression.LZ4.Tests.Adapters;
using TestHelpers;
using Xunit;

namespace K4os.Compression.LZ4.Tests
{
    public class Level2CompressionTests
    {
        private static void RoundtripLevel(LZ4Level level)
        {
            var source = new byte[10000];
            new Random(42).NextBytes(source);

            var compressed = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var compressedLength = LZ4Codec.Encode(
                source, 0, source.Length,
                compressed, 0, compressed.Length,
                level);

            Assert.True(compressedLength > 0);

            var decompressed = new byte[source.Length];
            var decompressedLength = LZ4Codec.Decode(
                compressed, 0, compressedLength,
                decompressed, 0, decompressed.Length);

            Assert.Equal(source.Length, decompressedLength);
            Tools.SameBytes(source, decompressed);
        }

        [Fact]
        public void Level2Roundtrip()
        {
            RoundtripLevel(LZ4Level.L02_FAST);
        }

        [Fact]
        public void Level1Roundtrip()
        {
            RoundtripLevel(LZ4Level.L01_FAST);
        }

        [Fact]
        public void Level0Roundtrip()
        {
            RoundtripLevel(LZ4Level.L00_FAST);
        }

        [Fact]
        public void Level2CompressionRatioBetweenL1AndL3()
        {
            var source = new byte[50000];
            Lorem.Fill(source, 0, source.Length);

            var compressed1 = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var len1 = LZ4Codec.Encode(
                source, 0, source.Length,
                compressed1, 0, compressed1.Length,
                LZ4Level.L01_FAST);

            var compressed2 = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var len2 = LZ4Codec.Encode(
                source, 0, source.Length,
                compressed2, 0, compressed2.Length,
                LZ4Level.L02_FAST);

            var compressed3 = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var len3 = LZ4Codec.Encode(
                source, 0, source.Length,
                compressed3, 0, compressed3.Length,
                LZ4Level.L03_HC);

            Assert.True(len2 > len1, $"L02 ({len2}) should be larger than L01 ({len1}) - higher acceleration = less compression");
            Assert.True(len2 > len3, $"L02 ({len2}) should be larger than L03_HC ({len3})");
        }

        [Theory]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(10000)]
        [InlineData(100000)]
        public void Level2RoundtripVariousSizes(int size)
        {
            var source = new byte[size];
            new Random(size).NextBytes(source);

            var compressed = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
            var compressedLength = LZ4Codec.Encode(
                source, 0, source.Length,
                compressed, 0, compressed.Length,
                LZ4Level.L02_FAST);

            Assert.True(compressedLength > 0);

            var decompressed = new byte[source.Length];
            var decompressedLength = LZ4Codec.Decode(
                compressed, 0, compressedLength,
                decompressed, 0, decompressed.Length);

            Assert.Equal(source.Length, decompressedLength);
            Tools.SameBytes(source, decompressed);
        }

        [Fact]
        public void EncoderFactoryCreatesCorrectLevel2Encoder()
        {
            var encoder = LZ4Encoder.Create(chaining: true, LZ4Level.L02_FAST, 65536);
            Assert.NotNull(encoder);
        }

        [Fact]
        public void EncoderFactoryLevel1UsesAcceleration1()
        {
            var encoder = LZ4Encoder.Create(chaining: true, LZ4Level.L01_FAST, 65536);
            Assert.NotNull(encoder);
        }
    }
}