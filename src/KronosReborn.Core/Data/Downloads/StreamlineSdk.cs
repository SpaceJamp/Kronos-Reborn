using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Downloads;

/// <summary>NVIDIA's Streamline SDK releases, from which only the production plugins are taken.</summary>
/// <remarks>
/// NVIDIA publishes Streamline only as a whole SDK zip of about 270 MB. The plugins a game uses are
/// about 2.5 MB of it, so the zip is read in pieces: its table of contents from the end, then just
/// the production plugins in <c>bin/x64</c>. Development builds and everything else are never fetched.
/// The plugins are checked afterwards like any other download: each must be signed by NVIDIA.
/// </remarks>
public static class StreamlineSdk
{
    /// <summary>The production plugins' folder inside the SDK zip.</summary>
    private const string ProductionFolder = "bin/x64/";

    /// <summary>How much of the end of the zip is read first; its table of contents is about 45 KB.</summary>
    private const int TailBytes = 256 * 1024;

    /// <summary>Larger than any Streamline plugin (the biggest is about 1.2 MB); a bigger claim is a damaged zip.</summary>
    private const long MaxPluginBytes = 64L * 1024 * 1024;

    /// <summary>The SDK zip NVIDIA publishes with a release tag.</summary>
    public static Uri ZipFor(string tag) =>
        new($"https://github.com/NVIDIA-RTX/Streamline/releases/download/{Uri.EscapeDataString(tag)}/streamline-sdk-{Uri.EscapeDataString(tag)}.zip");

    /// <summary>Writes the release's production Streamline plugins into <paramref name="folder"/>.</summary>
    /// <returns>False when the release has no SDK zip.</returns>
    /// <exception cref="InvalidDataException">
    /// The server will not send parts of the file, or the zip is not as expected. Downloading the whole
    /// 270 MB instead is refused rather than done quietly.
    /// </exception>
    public static async Task<bool> ExtractProductionPluginsAsync(
        HttpClient http, Uri zip, string folder, IProgress<double>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(zip);

        var (tail, total) = await GetTailAsync(http, zip, TailBytes, cancellation).ConfigureAwait(false);
        if (tail is null)
        {
            return false;
        }

        var tailStart = total - tail.Length;
        var directory = ReadCentralDirectory(tail, tailStart);
        if (directory.Offset < tailStart)
        {
            // A table of contents larger than the first read: fetch exactly it.
            tail = await GetRangeAsync(http, zip, directory.Offset, directory.Offset + directory.Size - 1, cancellation).ConfigureAwait(false);
            tailStart = directory.Offset;
        }

        var plugins = ReadEntries(tail, (int)(directory.Offset - tailStart), directory.Size)
            .Where(e => IsProductionPlugin(e.Name))
            .ToList();

        if (plugins.Count == 0)
        {
            throw new InvalidDataException("The Streamline SDK zip has no production plugins in bin/x64.");
        }

        Directory.CreateDirectory(folder);
        var done = 0;
        foreach (var plugin in plugins)
        {
            if (plugin.Size > MaxPluginBytes || plugin.CompressedSize > MaxPluginBytes)
            {
                throw new InvalidDataException($"{plugin.Name} claims to be larger than any Streamline plugin.");
            }

            var header = await GetRangeAsync(http, zip, plugin.HeaderOffset, plugin.HeaderOffset + 29, cancellation).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            {
                throw new InvalidDataException($"{plugin.Name} has no local header where the zip says.");
            }

            var dataStart = plugin.HeaderOffset + 30
                            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26))
                            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var data = plugin.CompressedSize == 0
                ? []
                : await GetRangeAsync(http, zip, dataStart, dataStart + plugin.CompressedSize - 1, cancellation).ConfigureAwait(false);

            var bytes = plugin.Method switch
            {
                0 => data,
                8 => Inflate(data, plugin.Size),
                _ => throw new InvalidDataException($"{plugin.Name} uses an unsupported compression method {plugin.Method}."),
            };

            if (bytes.Length != plugin.Size)
            {
                throw new InvalidDataException($"{plugin.Name} unpacked to {bytes.Length} bytes, not {plugin.Size}.");
            }

            await File.WriteAllBytesAsync(Path.Combine(folder, Path.GetFileName(plugin.Name)), bytes, cancellation).ConfigureAwait(false);
            progress?.Report(100.0 * ++done / plugins.Count);
        }

        return true;
    }

    /// <summary>Whether a zip entry is one of the production plugins a game can carry.</summary>
    private static bool IsProductionPlugin(string name) =>
        name.StartsWith(ProductionFolder, StringComparison.OrdinalIgnoreCase)
        && !name[ProductionFolder.Length..].Contains('/', StringComparison.Ordinal)
        && RuntimeDlls.ByKind[RuntimeKind.Streamline].Contains(name[ProductionFolder.Length..], StringComparer.OrdinalIgnoreCase);

    private static byte[] Inflate(byte[] data, long size)
    {
        // Read one byte past the declared size at most, so data that inflates further is caught
        // without unpacking all of it.
        using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        var buffer = new byte[size + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = input.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += n;
        }

        return buffer[..read];
    }

    private sealed record Entry(string Name, int Method, long CompressedSize, long Size, long HeaderOffset);

    /// <summary>Where the table of contents is, from the end-of-central-directory record.</summary>
    private static (long Offset, int Size) ReadCentralDirectory(byte[] tail, long tailStart)
    {
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
                if (size == uint.MaxValue || offset == uint.MaxValue)
                {
                    throw new InvalidDataException("The Streamline SDK zip uses the 64-bit zip format, which is not read here.");
                }

                if ((long)offset + size > tailStart + i)
                {
                    throw new InvalidDataException("The Streamline SDK zip's table of contents is out of place.");
                }

                return (offset, (int)size);
            }
        }

        throw new InvalidDataException("The Streamline SDK zip has no table of contents at its end.");
    }

    private static IEnumerable<Entry> ReadEntries(byte[] buffer, int start, int size)
    {
        var p = start;
        var end = start + size;
        while (p + 46 <= end)
        {
            var span = buffer.AsSpan(p);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x02014b50)
            {
                throw new InvalidDataException("The Streamline SDK zip's table of contents is damaged.");
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            if (p + 46 + nameLength + extraLength + commentLength > end)
            {
                throw new InvalidDataException("The Streamline SDK zip's table of contents is damaged.");
            }

            yield return new Entry(
                System.Text.Encoding.UTF8.GetString(buffer, p + 46, nameLength).Replace('\\', '/'),
                BinaryPrimitives.ReadUInt16LittleEndian(span[10..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[42..]));
            p += 46 + nameLength + extraLength + commentLength;
        }
    }

    /// <summary>The last bytes of the file and its total length, or null when there is no such file.</summary>
    private static async Task<(byte[]? Tail, long Total)> GetTailAsync(HttpClient http, Uri url, int bytes, CancellationToken cancellation)
    {
        // GitHub's download server refuses "the last N bytes" (501), so the size is learned from the
        // first byte and the end is then asked for by position.
        long total;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, 0);
            }

            await ReadPartAsync(response, cancellation).ConfigureAwait(false);
            total = response.Content.Headers.ContentRange?.Length
                    ?? throw new InvalidDataException("The download server did not say how large the SDK is.");
        }

        var from = Math.Max(0, total - bytes);
        return (await GetRangeAsync(http, url, from, total - 1, cancellation).ConfigureAwait(false), total);
    }

    private static async Task<byte[]> GetRangeAsync(HttpClient http, Uri url, long from, long to, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        var part = await ReadPartAsync(response, cancellation).ConfigureAwait(false);
        return part.Length == to - from + 1
            ? part
            : throw new InvalidDataException($"The download server sent {part.Length} bytes where {to - from + 1} were asked for.");
    }

    /// <summary>A partial response's body; a whole-file answer is refused rather than read.</summary>
    private static async Task<byte[]> ReadPartAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException(
                "The download server sent the whole Streamline SDK (about 270 MB) instead of the parts asked for, so it was stopped.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
    }
}
