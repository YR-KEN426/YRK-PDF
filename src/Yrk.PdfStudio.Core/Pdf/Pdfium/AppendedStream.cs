namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>A file followed by bytes held in memory, read as one stream (see <see cref="Text.ToUnicodeRepair"/>).</summary>
internal sealed class AppendedStream(Stream file, byte[] tail) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => file.Length + tail.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            int read;
            if (_position < file.Length)
            {
                file.Position = _position;
                read = file.Read(buffer[..(int)Math.Min(buffer.Length, file.Length - _position)]);
                if (read == 0)
                {
                    break;
                }
            }
            else
            {
                var from = (int)(_position - file.Length);
                read = Math.Min(buffer.Length, tail.Length - from);
                tail.AsSpan(from, read).CopyTo(buffer);
            }

            _position += read;
            total += read;
            buffer = buffer[read..];
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            file.Dispose();
        }

        base.Dispose(disposing);
    }
}
