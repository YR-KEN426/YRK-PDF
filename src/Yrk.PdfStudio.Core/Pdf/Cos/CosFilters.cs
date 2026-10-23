using System.IO.Compression;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>Stream decoding for the structural streams we read (object and cross-reference streams).</summary>
internal static class CosFilters
{
    private const int MaxDecodedLength = 256 * 1024 * 1024;

    public static byte[] Decode(CosStream stream, Func<CosObject?, CosObject?> resolve)
    {
        var filters = resolve(stream.Dictionary["Filter"]) switch
        {
            CosName name => [name],
            CosArray array => array.Items.Select(resolve).OfType<CosName>().ToList(),
            _ => new List<CosName>(),
        };

        var parameters = resolve(stream.Dictionary["DecodeParms"]) switch
        {
            CosDictionary d => [d],
            CosArray array => array.Items.Select(i => resolve(i) as CosDictionary).ToList(),
            _ => new List<CosDictionary?>(),
        };

        var data = stream.Data.ToArray();
        for (var i = 0; i < filters.Count; i++)
        {
            data = filters[i].Value switch
            {
                "FlateDecode" or "Fl" => Unpredict(Inflate(data), i < parameters.Count ? parameters[i] : null),
                "LZWDecode" or "LZW" => Unpredict(Lzw(data, (i < parameters.Count ? parameters[i]?["EarlyChange"] as CosInteger : null)?.Value ?? 1), i < parameters.Count ? parameters[i] : null),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                _ => throw new PdfException($"The /{filters[i].Value} filter is not supported here."),
            };
        }

        return data;
    }

    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxDecodedLength)
                {
                    throw new PdfException("A stream is too large to decode.");
                }
            }
        }
        catch (InvalidDataException)
        {
            // Keep what decoded before the damage, as other readers do.
        }

        return output.ToArray();
    }

    /// <summary>Reverses PNG predictors (10–15), which cross-reference streams normally use.</summary>
    private static byte[] Unpredict(byte[] data, CosDictionary? parameters)
    {
        var predictor = (parameters?["Predictor"] as CosInteger)?.Value ?? 1;
        if (predictor < 10)
        {
            return predictor == 1 ? data : throw new PdfException("TIFF predictors are not supported here.");
        }

        var colors = (int)((parameters!["Colors"] as CosInteger)?.Value ?? 1);
        var bitsPerComponent = (int)((parameters["BitsPerComponent"] as CosInteger)?.Value ?? 8);
        var columns = (int)((parameters["Columns"] as CosInteger)?.Value ?? 1);
        var bytesPerPixel = Math.Max(1, colors * bitsPerComponent / 8);
        var rowLength = ((colors * bitsPerComponent * columns) + 7) / 8;
        if (rowLength <= 0)
        {
            throw new PdfException("Invalid predictor parameters.");
        }

        var rows = data.Length / (rowLength + 1);
        var output = new byte[rows * rowLength];
        var previous = new byte[rowLength];
        for (var row = 0; row < rows; row++)
        {
            var filter = data[row * (rowLength + 1)];
            var source = data.AsSpan((row * (rowLength + 1)) + 1, rowLength);
            var current = output.AsSpan(row * rowLength, rowLength);
            for (var i = 0; i < rowLength; i++)
            {
                var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                var up = previous[i];
                var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                current[i] = (byte)(source[i] + filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                });
            }

            current.CopyTo(previous);
        }

        return output;
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int? high = null;
        foreach (var b in data)
        {
            if (b == (byte)'>')
            {
                break;
            }

            var value = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (value < 0)
            {
                continue;
            }

            if (high is null)
            {
                high = value;
            }
            else
            {
                output.Add((byte)((high.Value << 4) | value));
                high = null;
            }
        }

        if (high is not null)
        {
            output.Add((byte)(high.Value << 4));
        }

        return output.ToArray();
    }

    private static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var group = new int[5];
        var count = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == (byte)'~')
            {
                break;
            }

            if (b == (byte)'z' && count == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
            {
                continue;
            }

            group[count++] = b - '!';
            if (count == 5)
            {
                Flush(5);
                count = 0;
            }
        }

        if (count > 1)
        {
            for (var j = count; j < 5; j++)
            {
                group[j] = 84;
            }

            Flush(count);
        }

        return output.ToArray();

        void Flush(int n)
        {
            uint value = 0;
            for (var j = 0; j < 5; j++)
            {
                value = (value * 85) + (uint)group[j];
            }

            for (var j = 0; j < n - 1; j++)
            {
                output.Add((byte)(value >> (24 - (8 * j))));
            }
        }
    }

    private static byte[] Lzw(byte[] data, long earlyChange)
    {
        var output = new List<byte>(data.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
            {
                table.Add([(byte)i]);
            }

            table.Add([]); // 256: clear
            table.Add([]); // 257: end
        }

        Reset();
        var codeLength = 9;
        byte[]? previous = null;
        long bitBuffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= codeLength)
            {
                var code = (int)((bitBuffer >> (bits - codeLength)) & ((1 << codeLength) - 1));
                bits -= codeLength;
                if (code == 256)
                {
                    Reset();
                    codeLength = 9;
                    previous = null;
                    continue;
                }

                if (code == 257)
                {
                    return output.ToArray();
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                    if (previous is not null)
                    {
                        table.Add([.. previous, entry[0]]);
                    }
                }
                else if (previous is not null)
                {
                    entry = [.. previous, previous[0]];
                    table.Add(entry);
                }
                else
                {
                    return output.ToArray();
                }

                output.AddRange(entry);
                previous = entry;
                var limit = (1 << codeLength) - (int)earlyChange;
                if (table.Count >= limit && codeLength < 12)
                {
                    codeLength++;
                }
            }
        }

        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
