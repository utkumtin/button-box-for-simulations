namespace ButtonBox.Output.Scs.Protocol;

/// <summary>Result of <see cref="FrameReader.TryRead"/>.</summary>
public enum FrameReadResult
{
    /// <summary>No complete frame yet; push more bytes.</summary>
    NeedMore,

    /// <summary>One payload was returned.</summary>
    Frame,

    /// <summary>The length prefix is 0 or above 256. The connection must be closed; the reader stays failed.</summary>
    Error,
}

/// <summary>
/// Incremental reader for the length-prefixed frames of PROTOCOL.md: bytes in, payloads out. A bad length is reported as soon
/// as the two length bytes are read, so a hostile peer cannot make the reader wait for or buffer more than one frame.
/// </summary>
public sealed class FrameReader
{
    /// <summary>The largest chunk <see cref="Push"/> accepts; callers read from the pipe in chunks of at most this size.</summary>
    public const int MaxChunk = 512;

    private readonly byte[] _buffer = new byte[MaxChunk * 2];
    private int _count;
    private bool _failed;

    /// <summary>Bytes received but not yet returned as a frame.</summary>
    public int BufferedBytes => _count;

    /// <summary>Adds received bytes. Drain with <see cref="TryRead"/> until it returns NeedMore before the next push.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Push at most MaxChunk bytes at a time.");
        }

        if (_count + data.Length > _buffer.Length)
        {
            throw new InvalidOperationException("Frames were not drained before the next push.");
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>Takes the next complete frame's payload, if any.</summary>
    public FrameReadResult TryRead(out byte[] payload)
    {
        payload = [];
        if (_failed)
        {
            return FrameReadResult.Error;
        }

        if (_count < 2)
        {
            return FrameReadResult.NeedMore;
        }

        var length = _buffer[0] | (_buffer[1] << 8);
        if (length is < 1 or > ScsProtocol.MaxPayload)
        {
            _failed = true;
            return FrameReadResult.Error;
        }

        if (_count < 2 + length)
        {
            return FrameReadResult.NeedMore;
        }

        payload = _buffer.AsSpan(2, length).ToArray();
        _count -= 2 + length;
        Buffer.BlockCopy(_buffer, 2 + length, _buffer, 0, _count);
        return FrameReadResult.Frame;
    }
}
