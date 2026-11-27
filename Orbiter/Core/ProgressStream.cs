namespace Orbiter.Core;

/// <summary>
/// Enveloppe un flux local pour compter les octets transférés et permettre l'annulation
/// (une exception levée dans Read/Write interrompt proprement UploadFile/DownloadFile).
/// </summary>
public sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onBytes;
    private readonly CancellationToken _ct;

    public ProgressStream(Stream inner, Action<long> onBytes, CancellationToken ct)
    {
        _inner = inner; _onBytes = onBytes; _ct = ct;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) _onBytes(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        _inner.Write(buffer, offset, count);
        _onBytes(count);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
