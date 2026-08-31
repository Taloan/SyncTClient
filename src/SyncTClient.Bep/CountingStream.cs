namespace SyncTClient.Bep;

/// <summary>
/// Umschliesst einen Datenstrom und zaehlt die Bytes, die durch ihn
/// hindurchgehen.
/// </summary>
/// <remarks>
/// Gezaehlt wird auf dieser Ebene und nicht in den einzelnen Sendemethoden,
/// weil hier alles anfaellt: Rahmenkoepfe, Anfragen, Index-Nachrichten und
/// Lebenszeichen. Ein Zaehler in einer hoeheren Schicht erfasste nur die
/// Nutzdaten und liesse offen, woher die uebrige Last auf der Verbindung
/// stammt.
/// </remarks>
public sealed class CountingStream(Stream inner) : Stream
{
    private long _read;
    private long _written;

    public long BytesRead => Interlocked.Read(ref _read);
    public long BytesWritten => Interlocked.Read(ref _written);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        Interlocked.Add(ref _read, n);
        return n;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        Interlocked.Add(ref _read, n);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        Interlocked.Add(ref _read, n);
        return n;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        Interlocked.Add(ref _written, buffer.Length);
    }

    public override async Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        Interlocked.Add(ref _written, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _written, count);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
