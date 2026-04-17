namespace K4os.Compression.LZ4;

/// <summary>
/// Represents a pre-loaded dictionary for use with LZ4 compression and decompression.
/// Both compressor and decompressor must use the same dictionary to produce correct results.
/// </summary>
public class LZ4Dictionary
{
    /// <summary>Optional dictionary ID written into the LZ4 frame header (FLG.DictID).</summary>
    public uint? DictionaryId { get; }

    /// <summary>The raw dictionary bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>Creates a new <see cref="LZ4Dictionary"/> from raw bytes without a dictionary ID.</summary>
    /// <param name="bytes">The dictionary bytes.</param>
    public LZ4Dictionary(byte[] bytes) : this(bytes, null) { }

    /// <summary>Creates a new <see cref="LZ4Dictionary"/> from raw bytes with an optional dictionary ID.</summary>
    /// <param name="bytes">The dictionary bytes.</param>
    /// <param name="dictionaryId">Optional 32-bit dictionary identifier for frame-format interoperability.</param>
    public LZ4Dictionary(byte[] bytes, uint? dictionaryId)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        DictionaryId = dictionaryId;
    }
}