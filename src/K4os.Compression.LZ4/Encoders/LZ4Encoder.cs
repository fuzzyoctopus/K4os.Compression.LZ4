namespace K4os.Compression.LZ4.Encoders;

/// <summary>
/// Static class with factory method to create LZ4 encoders.
/// </summary>
public static class LZ4Encoder
{
	/// <summary>Creates appropriate decoder for given parameters.</summary>
	/// <param name="chaining">Dependent blocks.</param>
	/// <param name="level">Compression level.</param>
	/// <param name="blockSize">Block size.</param>
	/// <param name="extraBlocks">Number of extra blocks.</param>
	/// <returns>LZ4 encoder.</returns>
	public static ILZ4Encoder Create(
		bool chaining, LZ4Level level, int blockSize, int extraBlocks = 0) =>
        Create(chaining, level, blockSize, extraBlocks, null);

    /// <summary>Creates appropriate encoder for given parameters with an optional preloaded dictionary.</summary>
    /// <param name="chaining">Dependent blocks.</param>
    /// <param name="level">Compression level.</param>
    /// <param name="blockSize">Block size.</param>
    /// <param name="extraBlocks">Number of extra blocks.</param>
    /// <param name="dictionary">Optional dictionary bytes preloaded into the encoder.</param>
    /// <returns>LZ4 encoder.</returns>
    public static ILZ4Encoder Create(
        bool chaining, LZ4Level level, int blockSize, int extraBlocks, byte[]? dictionary) =>
        !chaining ? CreateBlockEncoder(level, blockSize) :
            level < LZ4Level.L03_HC
                ? CreateFastEncoder(blockSize, extraBlocks, (int)level, dictionary)
                : CreateHighEncoder(level, blockSize, extraBlocks, dictionary);

    private static ILZ4Encoder CreateBlockEncoder(LZ4Level level, int blockSize) =>
		new LZ4BlockEncoder(level, blockSize);

    private static ILZ4Encoder CreateFastEncoder(int blockSize, int extraBlocks, int acceleration, byte[]? dictionary) =>
        new LZ4FastChainEncoder(blockSize, extraBlocks, acceleration, dictionary);

    private static ILZ4Encoder CreateHighEncoder(
        LZ4Level level, int blockSize, int extraBlocks, byte[]? dictionary) =>
        new LZ4HighChainEncoder(level, blockSize, extraBlocks, dictionary);
}