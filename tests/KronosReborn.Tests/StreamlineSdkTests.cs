using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Downloads;
using Xunit;

namespace KronosReborn.Tests;

public class StreamlineSdkTests
{
    private static readonly Uri Zip = StreamlineSdk.ZipFor("v2.14.16");

    [Fact]
    public void TheReleaseZipIsNVIDIAsOwn()
    {
        Assert.Equal("https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.16/streamline-sdk-v2.14.16.zip", Zip.AbsoluteUri);
    }

    [Fact]
    public async Task OnlyTheProductionPluginsAreFetchedAndUnpacked()
    {
        var zip = MakeZip(
            ("bin/x64/sl.interposer.dll", "interposer"),
            ("bin/x64/sl.reflex.dll", "reflex"),
            ("bin/x64/development/sl.interposer.dll", "development build"),
            ("_artifacts/sl.dlss_g/Production_x64/sl.dlss_g.dll", "artifact"),
            ("bin/x64/sl.notaplugin.txt", "text"),
            ("docs/big.bin", new string('x', 2_000_000)));
        var server = new RangeServer(zip);
        using var http = new HttpClient(server);
        using var temp = new TemporaryDirectory();

        var found = await StreamlineSdk.ExtractProductionPluginsAsync(http, Zip, temp.Root, null, CancellationToken.None);

        Assert.True(found);
        Assert.Equal(["sl.interposer.dll", "sl.reflex.dll"], Directory.GetFiles(temp.Root).Select(Path.GetFileName).Order());
        Assert.Equal("interposer", File.ReadAllText(Path.Combine(temp.Root, "sl.interposer.dll")));
        Assert.Equal("reflex", File.ReadAllText(Path.Combine(temp.Root, "sl.reflex.dll")));

        // The point: a 270 MB SDK costs only its table of contents and the plugins.
        Assert.True(server.BytesServed < zip.Length / 4, $"{server.BytesServed} of {zip.Length} bytes fetched");
    }

    [Fact]
    public async Task AReleaseWithoutTheSdkZipIsReportedAsMissing()
    {
        using var http = new HttpClient(new RangeServer(null));
        using var temp = new TemporaryDirectory();

        Assert.False(await StreamlineSdk.ExtractProductionPluginsAsync(http, Zip, temp.Root, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task AServerThatIgnoresPartialRequestsIsRefusedRatherThanDownloadingEverything()
    {
        var zip = MakeZip(("bin/x64/sl.interposer.dll", "interposer"));
        using var http = new HttpClient(new RangeServer(zip, honourRanges: false));
        using var temp = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StreamlineSdk.ExtractProductionPluginsAsync(http, Zip, temp.Root, null, CancellationToken.None));
    }

    public enum Damage
    {
        TableOfContentsOffsetWraps,
        EntryNameRunsPastTheTable,
        PluginClaimsToBeHuge,
    }

    [Theory]
    [InlineData(Damage.TableOfContentsOffsetWraps)]
    [InlineData(Damage.EntryNameRunsPastTheTable)]
    [InlineData(Damage.PluginClaimsToBeHuge)]
    public async Task ADamagedZipIsRefusedAsDamagedRatherThanCrashing(Damage damage)
    {
        var zip = MakeZip(("bin/x64/sl.interposer.dll", "interposer interposer interposer"));
        var end = zip.Length - 22;
        var directory = (int)BitConverter.ToUInt32(zip, end + 16);
        switch (damage)
        {
            case Damage.TableOfContentsOffsetWraps:
                BitConverter.GetBytes(0xFFFFFF00u).CopyTo(zip, end + 16);
                BitConverter.GetBytes(0x150u).CopyTo(zip, end + 12);
                break;
            case Damage.EntryNameRunsPastTheTable:
                BitConverter.GetBytes((ushort)60000).CopyTo(zip, directory + 28);
                break;
            case Damage.PluginClaimsToBeHuge:
                BitConverter.GetBytes(0xFFFFFF00u).CopyTo(zip, directory + 24);
                break;
        }

        using var http = new HttpClient(new RangeServer(zip));
        using var temp = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StreamlineSdk.ExtractProductionPluginsAsync(http, Zip, temp.Root, null, CancellationToken.None));
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                // The SDK's bulk is large; stored as is here so this zip is large too.
                var level = content.Length > 100_000 ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var entry = archive.CreateEntry(name, level).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return stream.ToArray();
    }

    /// <summary>Serves one file and answers byte-range requests the way GitHub's download server does.</summary>
    private sealed class RangeServer(byte[]? file, bool honourRanges = true) : HttpMessageHandler
    {
        public long BytesServed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (file is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (!honourRanges || range is null)
            {
                BytesServed += file.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            }

            if (range.From is null)
            {
                // GitHub's download server refuses "the last N bytes" ranges.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
            }

            var from = range.From.Value;
            var to = Math.Min(range.To ?? file.Length - 1, file.Length - 1);

            var part = file[(int)from..((int)to + 1)];
            BytesServed += part.Length;
            var content = new ByteArrayContent(part);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
}
