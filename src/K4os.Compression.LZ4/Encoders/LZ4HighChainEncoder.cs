using K4os.Compression.LZ4.Engine;
using K4os.Compression.LZ4.Internal;

namespace K4os.Compression.LZ4.Encoders;

// high encoder context
using LZ4Context = LL.LZ4_streamHC_t;

/// <summary>
/// LZ4 encoder using dependent blocks with high compression.
/// </summary>
public unsafe class LZ4HighChainEncoder: LZ4EncoderBase
{
	private PinnedMemory _contextPin;

	private LZ4Context* Context => _contextPin.Reference<LZ4Context>();

    /// <summary>Creates new instance of <see cref="LZ4HighChainEncoder"/></summary>
    /// <param name="level">Compression level.</param>
    /// <param name="blockSize">Block size.</param>
    /// <param name="extraBlocks">Number of extra blocks.</param>
    /// <param name="dictionary">Optional dictionary bytes preloaded into the encoder.</param>
    public LZ4HighChainEncoder(LZ4Level level, int blockSize, int extraBlocks = 0, byte[]? dictionary = null) :
        base(true, blockSize, extraBlocks)
	{
		if (level < LZ4Level.L03_HC) level = LZ4Level.L03_HC;
		if (level > LZ4Level.L12_MAX) level = LZ4Level.L12_MAX;
		PinnedMemory.Alloc<LZ4Context>(out _contextPin, false);
        if (dictionary is { Length: > 0 })
        {
            fixed (byte* dictPtr = dictionary)
            {
                var actual = PrepareInputBufferWithDict(dictPtr, dictionary.Length);
                LL.LZ4_loadDictHC(Context, InputBuffer, actual);
                LL.LZ4_setCompressionLevel(Context, (int)level);
            }
        }
        else
        {
            LL.LZ4_initStreamHC(Context);
            LL.LZ4_resetStreamHC_fast(Context, (int)level);
        }
    }

	/// <inheritdoc />
	protected override void ReleaseUnmanaged()
	{
		base.ReleaseUnmanaged();
		_contextPin.Free();
	}

	/// <inheritdoc />
	protected override int EncodeBlock(
		byte* source, int sourceLength, byte* target, int targetLength) =>
		LLxx.LZ4_compress_HC_continue(Context, source, target, sourceLength, targetLength);

	/// <inheritdoc />
	protected override int CopyDict(byte* target, int length) =>
		LL.LZ4_saveDictHC(Context, target, length);
}