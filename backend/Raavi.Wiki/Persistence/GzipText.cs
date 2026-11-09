using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Raavi.Wiki.Persistence;

/// <summary>
/// Stores page bodies gzip-compressed (<c>varbinary(max)</c>): the JSON of a page compresses about 6–8×, and a wiki keeps every version of every page,
/// so this is where most of the database size goes. The bytes are exactly what SQL Server's <c>COMPRESS()</c> produces for an <c>nvarchar</c>
/// (gzip of UTF-16), so migrations and manual repairs can use <c>COMPRESS</c>/<c>DECOMPRESS</c>. Text that is not gzip is read as plain UTF-16
/// (never written), so a half-migrated table cannot break reads.
/// </summary>
public static class GzipText
{
    public static byte[] Compress(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.Unicode.GetBytes(value));
        }

        return output.ToArray();
    }

    public static string Decompress(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1F || bytes[1] != 0x8B)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.Unicode.GetString(output.ToArray());
    }

    public static readonly ValueConverter<string, byte[]> Converter = new(v => Compress(v), v => Decompress(v));
}
