using K4os.Compression.LZ4.Engine;
using K4os.Compression.LZ4.Internal;

namespace K4os.Compression.LZ4.Encoders;

// fast encoder context
using LZ4Context = LL.LZ4_stream_t;

/// <summary>
/// LZ4 encoder using dependent blocks with fast compression.
/// </summary>
public unsafe class LZ4FastChainEncoder: LZ4EncoderBase
{
	private PinnedMemory _contextPin;
    private readonly int _acceleration;

    private LZ4Context* Context => _contextPin.Reference<LZ4Context>();

    /// <summary>Creates new instance of <see cref="LZ4FastChainEncoder"/></summary>
    /// <param name="blockSize">Block size.</param>
    /// <param name="extraBlocks">Number of extra blocks.</param>
    /// <param name="acceleration">Acceleration parameter (1 for L00/L01, 2 for L02).</param>
    /// <param name="dictionary">Optional dictionary bytes preloaded into the encoder.</param>
    public LZ4FastChainEncoder(int blockSize, int extraBlocks = 0, int acceleration = 1, byte[]? dictionary = null) :
        base(true, blockSize, extraBlocks)
	{
		PinnedMemory.Alloc<LZ4Context>(out _contextPin);
        _acceleration = acceleration;

        if (dictionary is { Length: > 0 })
        {
            fixed (byte* dictPtr = dictionary)
            {
                var actual = PrepareInputBufferWithDict(dictPtr, dictionary.Length);
                LLxx.LZ4_loadDict(Context, InputBuffer, actual);
            }
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
        LLxx.LZ4_compress_fast_continue(Context, source, target, sourceLength, targetLength, _acceleration);

    /// <inheritdoc />
    protected override int CopyDict(byte* target, int length) =>
		LL.LZ4_saveDict(Context, target, length);
}