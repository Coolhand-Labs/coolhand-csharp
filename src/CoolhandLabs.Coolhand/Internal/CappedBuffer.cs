namespace CoolhandLabs.Internal;

/// <summary>
/// Accumulates bytes up to a cap, dropping (or partially truncating) anything beyond it instead of
/// growing without bound. Thread-safe: a capture can be finalized by a timer while the host reads.
/// </summary>
internal sealed class CappedBuffer
{
    /// <summary>Upper bound on captured or decompressed bodies, as coolhand-node's <c>MAX_DECOMPRESSED_BYTES</c>.</summary>
    public const int MaxBytes = 50 * 1024 * 1024;

    private readonly int _maxBytes;
    private readonly Action? _onTruncate;
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private bool _truncated;

    public CappedBuffer(int maxBytes, Action? onTruncate = null)
    {
        _maxBytes = maxBytes;
        _onTruncate = onTruncate;
    }

    public void Push(ReadOnlySpan<byte> chunk)
    {
        // An empty chunk carries no data, so it can't overflow the cap.
        if (chunk.Length == 0) return;
        var truncatedNow = false;
        lock (_lock)
        {
            var remaining = _maxBytes - (int)_buffer.Length;
            if (remaining <= 0)
            {
                truncatedNow = MarkTruncated();
            }
            else if (chunk.Length > remaining)
            {
                _buffer.Write(chunk.Slice(0, remaining));
                truncatedNow = MarkTruncated();
            }
            else
            {
                _buffer.Write(chunk);
            }
        }
        if (truncatedNow && _onTruncate is not null)
        {
            try
            {
                _onTruncate();
            }
            catch
            {
                // a logging callback must never break capture
            }
        }
    }

    public void Push(byte[] chunk, int offset, int length) => Push(new ReadOnlySpan<byte>(chunk, offset, length));

    private bool MarkTruncated()
    {
        if (_truncated) return false;
        _truncated = true;
        return true;
    }

    /// <summary>True once a chunk overflowed the cap (there was more data than fit).</summary>
    public bool IsTruncated
    {
        get
        {
            lock (_lock) return _truncated;
        }
    }

    public int Size
    {
        get
        {
            lock (_lock) return (int)_buffer.Length;
        }
    }

    public byte[] ToArray()
    {
        lock (_lock) return _buffer.ToArray();
    }
}
