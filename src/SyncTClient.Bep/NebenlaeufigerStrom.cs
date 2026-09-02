namespace SyncTClient.Bep;

/// <summary>
/// Laesst Lesen und Schreiben nebeneinander laufen.
/// </summary>
/// <remarks>
/// <see cref="Stream"/> bringt fuer <c>ReadAsync</c> und <c>WriteAsync</c>
/// eine Standard-Umsetzung mit, die beide Richtungen ueber <em>dieselbe</em>
/// Sperre fuehrt. Ein Lesen, das auf die Gegenstelle wartet, haelt damit
/// jedes Schreiben auf, bis es selbst fertig ist. <see cref="System.Net.Security.SslStream"/>
/// ueberschreibt das; der Strom von Bouncy Castle nicht.
///
/// Gemessen an einer laufenden Verbindung: das ClusterConfig brauchte
/// 44 Sekunden zum Senden und ein Lebenszeichen 90 -- jeweils genau so
/// lange, bis von der Gegenstelle etwas ankam.
///
/// Erlaubt ist ein Leser und ein Schreiber zugleich, mehr nicht. Das ist
/// dieselbe Zusage, die auch <c>SslStream</c> gibt, und die Schicht darueber
/// haelt sich daran: sie liest in einer einzigen Schleife und schreibt unter
/// einer eigenen Sperre.
/// </remarks>
internal sealed class NebenlaeufigerStrom(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Flush() => inner.Flush();

    // Der Strom darunter kennt nur die blockierende Form. Sie bekommt einen
    // eigenen Thread, und zwar je Richtung einen -- der Sinn der Uebung.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() => inner.Read(buffer.Span), ct));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() => inner.Write(buffer.Span), ct));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task FlushAsync(CancellationToken ct) => Task.Run(inner.Flush, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
