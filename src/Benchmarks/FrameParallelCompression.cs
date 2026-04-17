using BenchmarkDotNet.Attributes;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Encoders;
using K4os.Compression.LZ4.Internal;
using K4os.Compression.LZ4.Streams;
using TestHelpers;

namespace Benchmarks;

[MemoryDiagnoser]
public class FrameParallelCompression
{
	private byte[] _source = null!;

	[Params(1, 4, 8)]
	public int Threads { get; set; }

	[Params(LZ4Level.L09_HC, LZ4Level.L11_OPT, LZ4Level.L12_MAX)]
	public LZ4Level Level { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_source = File.ReadAllBytes(ResolveSourcePath());
	}

	[Benchmark]
	public long Encode()
	{
		var settings = new LZ4EncoderSettings {
			ChainBlocks = false,
			BlockSize = Mem.M4,
			CompressionLevel = Level,
			MaxDegreeOfParallelism = Threads,
		};

		using var encoder = LZ4Stream.Encode(Stream.Null, settings, leaveOpen: true);
		encoder.Write(_source, 0, _source.Length);
		return encoder.Position;
	}

	private static string ResolveSourcePath()
	{
		var envPath = Environment.GetEnvironmentVariable("LZ4_BENCH_SOURCE");
		if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
			return envPath;

		var silesia = Tools.FindFile(".corpus/silesia.tar");
		if (File.Exists(silesia))
			return silesia;

		// Repository corpus does not always include silesia; keep harness runnable.
		return Tools.FindFile(".corpus/mozilla");
	}
}
