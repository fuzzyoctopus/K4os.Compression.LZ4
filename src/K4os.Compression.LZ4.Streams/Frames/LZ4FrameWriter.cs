using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using K4os.Compression.LZ4.Encoders;
using K4os.Compression.LZ4.Internal;
using K4os.Compression.LZ4.Streams.Abstractions;
using K4os.Compression.LZ4.Streams.Internal;
using K4os.Hash.xxHash;

namespace K4os.Compression.LZ4.Streams.Frames;

/// <summary>
/// LZ4 stream encoder. 
/// </summary>
public partial class LZ4FrameWriter<TStreamWriter, TStreamState>:
    ILZ4FrameWriter
    where TStreamWriter: IStreamWriter<TStreamState>
{
    private readonly TStreamWriter _writer;
    private TStreamState _stream;
    private Stash _stash = new();

    private readonly Func<ILZ4Descriptor, ILZ4Encoder> _encoderFactory;
    private readonly int _maxDegreeOfParallelism;
    private ILZ4Descriptor? _descriptor;
    private ILZ4Encoder? _encoder;
    private bool _frameOpened;

    private byte[]? _buffer;

    private long _bytesWritten;
    private XXH32.State _contentChecksum;

    private Channel<ParallelWorkItem>? _parallelWorkChannel;
    private Channel<ParallelResult>? _parallelResultChannel;
    private Task[]? _parallelWorkers;
    private Task? _parallelCompletionTask;
    private SemaphoreSlim? _parallelSlots;
    private Exception? _parallelFault;
    private readonly Dictionary<long, ParallelResult> _parallelPending = [];
    private byte[]? _parallelInputBuffer;
    private int _parallelInputLength;
    private int _parallelMaxOutputSize;
    private long _parallelProducedBlocks;
    private long _parallelNextBlockToWrite;

    private bool UseParallelCompression => _parallelWorkChannel is not null;

    private readonly struct ParallelWorkItem
    {
        public long Sequence { get; }
        public byte[] Buffer { get; }
        public int Length { get; }

        public ParallelWorkItem(long sequence, byte[] buffer, int length)
        {
            Sequence = sequence;
            Buffer = buffer;
            Length = length;
        }
    }

    private readonly struct ParallelResult
    {
        public long Sequence { get; }
        public BlockInfo Block { get; }

        public ParallelResult(long sequence, BlockInfo block)
        {
            Sequence = sequence;
            Block = block;
        }
    }

    /// <summary>Creates new instance of <see cref="LZ4EncoderStream"/>.</summary>
    /// <param name="writer">Inner stream.</param>
    /// <param name="stream">Inner stream initial state.</param>
    /// <param name="encoderFactory">LZ4 Encoder factory.</param>
    /// <param name="descriptor">LZ4 settings.</param>
    /// <param name="maxDegreeOfParallelism">Maximum degree of block compression parallelism.</param>
    public LZ4FrameWriter(
        TStreamWriter writer, TStreamState stream,
        Func<ILZ4Descriptor, ILZ4Encoder> encoderFactory,
        ILZ4Descriptor descriptor,
        int? maxDegreeOfParallelism = null)
    {
        _writer = writer;
        _stream = stream;
        _descriptor = descriptor;
        _encoderFactory = encoderFactory;
        _maxDegreeOfParallelism = Math.Max(maxDegreeOfParallelism.GetValueOrDefault(1), 1);
        _bytesWritten = 0;
    }

    /// <summary>
    /// Exposes internal stream state. Existence of this field is a hack,
    /// and it really shouldn't be here but it is needed for relatively low
    /// level operations (like writing directly to unmanaged memory).
    /// Please, do not use it directly, if don't know what you are doing. 
    /// </summary>
    protected TStreamState StreamState => _stream;

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private bool TryStashFrame()
    {
        if (_frameOpened)
            return false;

        _descriptor.AssertIsNotNull();

        _stash.Poke4(0x184D2204);

        var headerOffset = _stash.Head;

        const int versionCode = 0x01;
        var blockChaining = _descriptor.Chaining;
        var blockChecksum = _descriptor.BlockChecksum;
        var contentChecksum = _descriptor.ContentChecksum;
        var hasContentSize = _descriptor.ContentLength.HasValue;
        var hasDictionary = _descriptor.Dictionary.HasValue;

        var FLG =
            (versionCode << 6) |
            ((blockChaining ? 0 : 1) << 5) |
            ((blockChecksum ? 1 : 0) << 4) |
            ((hasContentSize ? 1 : 0) << 3) |
            ((contentChecksum ? 1 : 0) << 2) |
            (hasDictionary ? 1 : 0);

        var blockSize = _descriptor.BlockSize;

        var BD = MaxBlockSizeCode(blockSize) << 4;

        _stash.Poke2((ushort)((FLG & 0xFF) | (BD & 0xFF) << 8));

        if (hasContentSize)
            throw NotImplemented(
                "ContentSize feature is not implemented"); // Stash8(contentSize);

        if (hasDictionary)
            _stash.Poke4(_descriptor.Dictionary!.Value);

        if (contentChecksum)
            InitializeContentChecksum();

        var HC = (byte)(_stash.Digest(headerOffset) >> 8);

        _stash.Poke1(HC);

        if (_maxDegreeOfParallelism > 1)
        {
            if (blockChaining)
                throw InvalidValue("Parallel compression requires ChainBlocks = false");

            InitializeParallelCompression(blockSize);
        }
        else
        {
            _encoder = CreateEncoder();
            _buffer = AllocateBuffer(LZ4Codec.MaximumOutputSize(blockSize));
        }

        _frameOpened = true;

        return true;
    }

    private void InitializeParallelCompression(int blockSize)
    {
        var capacity = Math.Max(_maxDegreeOfParallelism * 2, 2);

        _parallelWorkChannel = Channel.CreateBounded<ParallelWorkItem>(
            new BoundedChannelOptions(capacity) {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });

        _parallelResultChannel = Channel.CreateUnbounded<ParallelResult>(
            new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
            });

        _parallelSlots = new SemaphoreSlim(capacity, capacity);
        _parallelPending.Clear();
        _parallelFault = null;
        _parallelProducedBlocks = 0;
        _parallelNextBlockToWrite = 0;
        _parallelInputBuffer = null;
        _parallelInputLength = 0;
        _parallelMaxOutputSize = LZ4Codec.MaximumOutputSize(blockSize);

        _parallelWorkers = Enumerable.Range(0, _maxDegreeOfParallelism).Select(unknown => Task.Run(ParallelWorkerLoop)).ToArray();

        _parallelCompletionTask = CompleteParallelWorkers();
    }

    private async Task CompleteParallelWorkers()
    {
        _parallelWorkers.AssertIsNotNull();

        try
        {
            await Task.WhenAll(_parallelWorkers).ConfigureAwait(false);
            _parallelResultChannel?.Writer.TryComplete();
        }
        catch (Exception error)
        {
            TryFailParallel(error);
        }
    }

    private async Task ParallelWorkerLoop()
    {
        ILZ4Encoder? encoder = null;

        try
        {
            encoder = CreateEncoder();
            _parallelWorkChannel.AssertIsNotNull();
            _parallelResultChannel.AssertIsNotNull();

            var reader = _parallelWorkChannel.Reader;
            var writer = _parallelResultChannel.Writer;

            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var work))
                {
                    byte[]? outputBuffer = null;

                    try
                    {
                        outputBuffer = AllocateBuffer(_parallelMaxOutputSize);

                        var action = encoder.TopupAndEncode(
                            work.Buffer.AsSpan(0, work.Length),
                            outputBuffer.AsSpan(),
                            true, true,
                            out var loaded,
                            out var encoded);

                        if (loaded != work.Length ||
                            (action != EncoderAction.Encoded && action != EncoderAction.Copied))
                            throw new InvalidOperationException(
                                "Parallel compression failed to encode a full block.");

                        await writer.WriteAsync(
                            new ParallelResult(work.Sequence, new BlockInfo(outputBuffer, action, encoded)))
                            .ConfigureAwait(false);

                        outputBuffer = null;
                    }
                    finally
                    {
                        ReleaseBuffer(work.Buffer);
                        if (outputBuffer is not null)
                            ReleaseBuffer(outputBuffer);
                    }
                }
            }
        }
        catch (Exception error)
        {
            TryFailParallel(error);
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private void TryFailParallel(Exception error)
    {
        if (Interlocked.CompareExchange(ref _parallelFault, error, null) is not null)
            return;

        _parallelWorkChannel?.Writer.TryComplete(error);
        _parallelResultChannel?.Writer.TryComplete(error);
    }

    private void ThrowIfParallelFaulted()
    {
        if (_parallelFault is not null)
            throw new InvalidOperationException("Parallel compression failed", _parallelFault);
    }

    private void EnsureParallelInputBuffer()
    {
        if (_parallelInputBuffer is not null)
            return;

        _descriptor.AssertIsNotNull();
        _parallelInputBuffer = AllocateBuffer(_descriptor.BlockSize);
        _parallelInputLength = 0;
    }

    private void QueueParallelBlock(EmptyToken token, byte[] buffer, int length)
    {
        _parallelSlots.AssertIsNotNull();

        while (!_parallelSlots.Wait(0))
            DrainAtLeastOneParallelBlock(token);

        try
        {
            WriteParallelWork(token, new ParallelWorkItem(_parallelProducedBlocks++, buffer, length));
        }
        catch
        {
            _parallelSlots.Release();
            ReleaseBuffer(buffer);
            throw;
        }
    }

    private async Task QueueParallelBlock(CancellationToken token, byte[] buffer, int length)
    {
        _parallelSlots.AssertIsNotNull();

        while (!_parallelSlots.Wait(0))
            await DrainAtLeastOneParallelBlock(token).Weave();

        try
        {
            await WriteParallelWork(token, new ParallelWorkItem(_parallelProducedBlocks++, buffer, length))
                .Weave();
        }
        catch
        {
            _parallelSlots.Release();
            ReleaseBuffer(buffer);
            throw;
        }
    }

    private void WriteParallelWork(EmptyToken _, ParallelWorkItem block)
    {
        _parallelWorkChannel.AssertIsNotNull();
        _parallelWorkChannel.Writer.WriteAsync(block).AsTask().GetAwaiter().GetResult();
    }

    private Task WriteParallelWork(CancellationToken token, ParallelWorkItem block)
    {
        _parallelWorkChannel.AssertIsNotNull();
        return _parallelWorkChannel.Writer.WriteAsync(block, token).AsTask();
    }

    private ParallelResult ReadParallelResult(EmptyToken _)
    {
        _parallelResultChannel.AssertIsNotNull();

        while (true)
        {
            ThrowIfParallelFaulted();

            if (_parallelResultChannel.Reader.TryRead(out var result))
                return result;

            if (!_parallelResultChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                throw InvalidOperation("Parallel compression pipeline terminated unexpectedly");
        }
    }

    private async Task<ParallelResult> ReadParallelResult(CancellationToken token)
    {
        _parallelResultChannel.AssertIsNotNull();

        while (true)
        {
            ThrowIfParallelFaulted();

            if (_parallelResultChannel.Reader.TryRead(out var result))
                return result;

            if (!await _parallelResultChannel.Reader.WaitToReadAsync(token).Weave())
                throw InvalidOperation("Parallel compression pipeline terminated unexpectedly");
        }
    }

    private void PullAvailableParallelResults()
    {
        _parallelResultChannel.AssertIsNotNull();

        while (_parallelResultChannel.Reader.TryRead(out var result))
            _parallelPending[result.Sequence] = result;
    }

    private void DrainAvailableParallelBlocks(EmptyToken token)
    {
        PullAvailableParallelResults();
        WritePendingParallelBlocks(token);
    }

    private async Task DrainAvailableParallelBlocks(CancellationToken token)
    {
        PullAvailableParallelResults();
        await WritePendingParallelBlocks(token).Weave();
    }

    private void DrainAtLeastOneParallelBlock(EmptyToken token)
    {
        var initial = _parallelNextBlockToWrite;

        while (_parallelNextBlockToWrite == initial)
        {
            DrainAvailableParallelBlocks(token);
            if (_parallelNextBlockToWrite > initial)
                return;

            var result = ReadParallelResult(token);
            _parallelPending[result.Sequence] = result;
        }

        WritePendingParallelBlocks(token);
    }

    private async Task DrainAtLeastOneParallelBlock(CancellationToken token)
    {
        var initial = _parallelNextBlockToWrite;

        while (_parallelNextBlockToWrite == initial)
        {
            await DrainAvailableParallelBlocks(token).Weave();
            if (_parallelNextBlockToWrite > initial)
                return;

            var result = await ReadParallelResult(token).Weave();
            _parallelPending[result.Sequence] = result;
        }

        await WritePendingParallelBlocks(token).Weave();
    }

    private void WritePendingParallelBlocks(EmptyToken token)
    {
        _parallelSlots.AssertIsNotNull();

        while (_parallelPending.TryGetValue(_parallelNextBlockToWrite, out var result))
        {
            _parallelPending.Remove(_parallelNextBlockToWrite);
            WriteBlock(token, result.Block);
            ReleaseBuffer(result.Block.Buffer);
            _parallelNextBlockToWrite++;
            _parallelSlots.Release();
        }
    }

    private async Task WritePendingParallelBlocks(CancellationToken token)
    {
        _parallelSlots.AssertIsNotNull();

        while (_parallelPending.TryGetValue(_parallelNextBlockToWrite, out var result))
        {
            _parallelPending.Remove(_parallelNextBlockToWrite);
            await WriteBlock(token, result.Block).Weave();
            ReleaseBuffer(result.Block.Buffer);
            _parallelNextBlockToWrite++;
            _parallelSlots.Release();
        }
    }

    private void WriteManyBytesParallel(EmptyToken token, ReadOnlySpan<byte> buffer)
    {
        _descriptor.AssertIsNotNull();

        _bytesWritten += buffer.Length;

        var offset = 0;
        while (offset < buffer.Length)
        {
            EnsureParallelInputBuffer();
            _parallelInputBuffer.AssertIsNotNull();

            var chunk = Math.Min(
                _descriptor.BlockSize - _parallelInputLength,
                buffer.Length - offset);

            buffer
                .Slice(offset, chunk)
                .CopyTo(_parallelInputBuffer.AsSpan(_parallelInputLength));

            _parallelInputLength += chunk;
            offset += chunk;

            if (_parallelInputLength != _descriptor.BlockSize)
                continue;

            var block = _parallelInputBuffer;
            _parallelInputBuffer = null;
            _parallelInputLength = 0;
            QueueParallelBlock(token, block, _descriptor.BlockSize);
        }

        DrainAvailableParallelBlocks(token);
    }

    private async Task WriteManyBytesParallel(CancellationToken token, ReadOnlyMemory<byte> buffer)
    {
        _descriptor.AssertIsNotNull();

        _bytesWritten += buffer.Length;

        var offset = 0;
        while (offset < buffer.Length)
        {
            EnsureParallelInputBuffer();
            _parallelInputBuffer.AssertIsNotNull();

            var chunk = Math.Min(
                _descriptor.BlockSize - _parallelInputLength,
                buffer.Length - offset);

            buffer
                .Slice(offset, chunk)
                .Span
                .CopyTo(_parallelInputBuffer.AsSpan(_parallelInputLength));

            _parallelInputLength += chunk;
            offset += chunk;

            if (_parallelInputLength != _descriptor.BlockSize)
                continue;

            var block = _parallelInputBuffer;
            _parallelInputBuffer = null;
            _parallelInputLength = 0;
            await QueueParallelBlock(token, block, _descriptor.BlockSize).Weave();
        }

        await DrainAvailableParallelBlocks(token).Weave();
    }

    private void WriteParallelFrameTail(EmptyToken token)
    {
        if (_parallelInputLength > 0)
        {
            _parallelInputBuffer.AssertIsNotNull();
            var block = _parallelInputBuffer;
            var length = _parallelInputLength;
            _parallelInputBuffer = null;
            _parallelInputLength = 0;
            QueueParallelBlock(token, block, length);
        }

        _parallelWorkChannel.AssertIsNotNull();
        _parallelWorkChannel.Writer.TryComplete();

        while (_parallelNextBlockToWrite < _parallelProducedBlocks)
            DrainAtLeastOneParallelBlock(token);

        WaitParallelCompletion(token);
        ThrowIfParallelFaulted();

        _stash.Poke4(0);
        _stash.TryPoke4(ContentChecksum());
        FlushMeta(token, true);
    }

    private async Task WriteParallelFrameTail(CancellationToken token)
    {
        if (_parallelInputLength > 0)
        {
            _parallelInputBuffer.AssertIsNotNull();
            var block = _parallelInputBuffer;
            var length = _parallelInputLength;
            _parallelInputBuffer = null;
            _parallelInputLength = 0;
            await QueueParallelBlock(token, block, length).Weave();
        }

        _parallelWorkChannel.AssertIsNotNull();
        _parallelWorkChannel.Writer.TryComplete();

        while (_parallelNextBlockToWrite < _parallelProducedBlocks)
            await DrainAtLeastOneParallelBlock(token).Weave();

        await WaitParallelCompletion(token).Weave();
        ThrowIfParallelFaulted();

        _stash.Poke4(0);
        _stash.TryPoke4(ContentChecksum());
        await FlushMeta(token, true).Weave();
    }

    private void WaitParallelCompletion(EmptyToken _)
    {
        _parallelCompletionTask?.GetAwaiter().GetResult();
    }

    private Task WaitParallelCompletion(CancellationToken _) =>
        _parallelCompletionTask ?? Task.CompletedTask;

    private void StopParallelCompression(EmptyToken token)
    {
        if (!UseParallelCompression)
            return;

        try
        {
            _parallelWorkChannel?.Writer.TryComplete();
            WaitParallelCompletion(token);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private async Task StopParallelCompression(CancellationToken token)
    {
        if (!UseParallelCompression)
            return;

        try
        {
            _parallelWorkChannel?.Writer.TryComplete();
            await WaitParallelCompletion(token).Weave();
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private void DisposeParallelCompression()
    {
        if (_parallelInputBuffer is not null)
        {
            ReleaseBuffer(_parallelInputBuffer);
            _parallelInputBuffer = null;
        }

        _parallelInputLength = 0;

        if (_parallelWorkChannel is not null)
        {
            while (_parallelWorkChannel.Reader.TryRead(out var work))
                ReleaseBuffer(work.Buffer);

            _parallelWorkChannel.Writer.TryComplete();
        }

        if (_parallelResultChannel is not null)
        {
            while (_parallelResultChannel.Reader.TryRead(out var result))
                ReleaseBuffer(result.Block.Buffer);

            _parallelResultChannel.Writer.TryComplete();
        }

        foreach (var result in _parallelPending.Values)
            ReleaseBuffer(result.Block.Buffer);

        _parallelPending.Clear();

        _parallelSlots?.Dispose();
        _parallelSlots = null;
        _parallelWorkChannel = null;
        _parallelResultChannel = null;
        _parallelWorkers = null;
        _parallelCompletionTask = null;
        _parallelFault = null;
        _parallelProducedBlocks = 0;
        _parallelNextBlockToWrite = 0;
        _parallelMaxOutputSize = 0;
    }

    /// <summary>Allocate temporary buffer to store decompressed data.</summary>
    /// <param name="size">Minimum size of the buffer.</param>
    /// <returns>Allocated buffer.</returns>
    protected virtual byte[] AllocateBuffer(int size) => BufferPool.Alloc(size);

    /// <summary>Releases allocated buffer. <see cref="AllocateBuffer"/></summary>
    /// <param name="buffer">Previously allocated buffer.</param>
    protected virtual void ReleaseBuffer(byte[] buffer) => BufferPool.Free(buffer);

    private ILZ4Encoder CreateEncoder()
    {
        _descriptor.AssertIsNotNull();

        var encoder = _encoderFactory(_descriptor);
        if (encoder.BlockSize > _descriptor.BlockSize)
            throw InvalidValue("BlockSize is greater than declared");

        return encoder;
    }

    private BlockInfo TopupAndEncode(
        ReadOnlySpan<byte> buffer, ref int offset, ref int count)
    {
        _buffer.AssertIsNotNull();

        var action = _encoder.TopupAndEncode(
            buffer.Slice(offset, count),
            _buffer.AsSpan(),
            false, true,
            out var loaded,
            out var encoded);

        _bytesWritten += loaded;
        offset += loaded;
        count -= loaded;

        return new BlockInfo(_buffer, action, encoded);
    }

    private BlockInfo FlushAndEncode()
    {
        _buffer.AssertIsNotNull();

        var action = _encoder.FlushAndEncode(
            _buffer.AsSpan(), true, out var encoded);

        return new BlockInfo(_buffer, action, encoded);
    }

    private static uint BlockLengthCode(in BlockInfo block) =>
        (uint)block.Length | (block.Compressed ? 0 : 0x80000000);

    private void InitializeContentChecksum() =>
        XXH32.Reset(ref _contentChecksum);

    private void UpdateContentChecksum(ReadOnlySpan<byte> buffer) =>
        XXH32.Update(ref _contentChecksum, buffer);

    private uint? BlockChecksum(BlockInfo block)
    {
        _descriptor.AssertIsNotNull();
        return _descriptor.BlockChecksum
            ? XXH32.DigestOf(block.Buffer, block.Offset, block.Length)
            : null;
    }

    private uint? ContentChecksum()
    {
        _descriptor.AssertIsNotNull();
        return _descriptor.ContentChecksum
            ? XXH32.Digest(_contentChecksum)
            : null;
    }

    private int MaxBlockSizeCode(int blockSize) =>
        blockSize <= Mem.K64 ? 4 :
        blockSize <= Mem.K256 ? 5 :
        blockSize <= Mem.M1 ? 6 :
        blockSize <= Mem.M4 ? 7 :
        throw InvalidBlockSize(blockSize);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetBytesWritten() => _bytesWritten;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteOneByte(byte value) =>
        WriteOneByte(EmptyToken.Value, value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteOneByteAsync(CancellationToken token, byte value) =>
        WriteOneByte(token, value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteManyBytes(ReadOnlySpan<byte> buffer) =>
        WriteManyBytes(EmptyToken.Value, buffer);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteManyBytesAsync(CancellationToken token, ReadOnlyMemory<byte> buffer) =>
        WriteManyBytes(token, buffer);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool OpenFrame() => OpenFrame(EmptyToken.Value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<bool> OpenFrameAsync(CancellationToken token = default) => OpenFrame(token);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CloseFrame() => CloseFrame(EmptyToken.Value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task CloseFrameAsync(CancellationToken token = default) =>
        CloseFrame(token);

    /// <summary>
    /// Disposes the stream and releases all resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> if called by user; <c>false</c> when called by garbage collector.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        try
        {
            CloseFrame();
        }
        finally
        {
            _stash.Dispose();
            ReleaseResources();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
    }

    /// <summary>
    /// Releases all unmanaged resources.
    /// </summary>
    protected virtual void ReleaseResources() { }

    /// <summary>
    /// Releases all unmanaged resources.
    /// </summary>
    /// <returns>Task indicating completion of the operation.</returns>
    protected virtual Task ReleaseResourcesAsync() => Task.CompletedTask;

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
	/// <inheritdoc />
	public virtual async ValueTask DisposeAsync()
	{
		try
		{
			await CloseFrameAsync().Weave();
		}
		finally
		{
			_stash.Dispose();
			await ReleaseResourcesAsync().Weave();
		}
	}

#endif

    // ReSharper disable once UnusedParameter.Local
    private void FlushMeta(EmptyToken _, bool eof = false)
    {
        var length = _stash.Flush();

        if (length > 0)
            _writer.Write(ref _stream, _stash.Data, 0, length);

        if (eof && _writer.CanFlush)
            _writer.Flush(ref _stream);
    }

    private async Task FlushMeta(CancellationToken token, bool eof = false)
    {
        var length = _stash.Flush();

        if (length > 0)
            _stream = await _writer.WriteAsync(_stream, _stash.Data, 0, length, token).Weave();

        if (eof && _writer.CanFlush)
            _stream = await _writer.FlushAsync(_stream, token).Weave();
    }

    // ReSharper disable once UnusedParameter.Local
    private void WriteData(EmptyToken _, BlockInfo block)
    {
        _writer.Write(ref _stream, block.Buffer, block.Offset, block.Length);
    }

    private async Task WriteData(CancellationToken token, BlockInfo block)
    {
        _stream = await _writer
            .WriteAsync(_stream, block.Buffer, block.Offset, block.Length, token)
            .Weave();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // ReSharper disable once UnusedParameter.Local
    private Span<byte> OneByteBuffer(in EmptyToken _, byte value) =>
        _stash.OneByteSpan(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // ReSharper disable once UnusedParameter.Local
    private Memory<byte> OneByteBuffer(in CancellationToken _, byte value) =>
        _stash.OneByteMemory(value);

    private NotImplementedException NotImplemented(string operation) =>
        new($"Feature {operation} has not been implemented in {GetType().Name}");

    private static ArgumentException InvalidValue(string description) =>
        new(description);

    private static InvalidOperationException InvalidOperation(string description) =>
        new(description);

    private protected ArgumentException InvalidBlockSize(int blockSize) =>
        InvalidValue($"Invalid block size ${blockSize} for {GetType().Name}");
}
