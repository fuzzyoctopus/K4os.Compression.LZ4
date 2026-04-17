namespace K4os.Compression.LZ4.Streams;

/// <summary>
/// Decoder settings.
/// </summary>
public class LZ4DecoderSettings
{
    internal static LZ4DecoderSettings Default { get; } = new();

    /// <summary>Extra memory for decompression.</summary>
    public int ExtraMemory { get; set; }
    /// <summary>Optional dictionary for decompression. Must match the dictionary used during compression.</summary>
    public LZ4Dictionary? DictionaryData { get; set; } = null;
}
