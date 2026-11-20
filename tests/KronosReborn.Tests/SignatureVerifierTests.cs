using System.Linq;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

public class SignatureVerifierTests
{
    private static readonly string[] StreamlineGames =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\PRAGMATA\sl.interposer.dll",
        @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\sl.interposer.dll",
    ];

    [Theory]
    // Exactly what AMD's FidelityFX DLLs are signed as.
    [InlineData("CN=Advanced Micro Devices, O=Advanced Micro Devices, S=California, C=US", "Advanced Micro Devices")]
    // A comma inside a quoted name is part of the name, not a separator.
    [InlineData("CN=\"Advanced Micro Devices, Inc.\", O=\"Advanced Micro Devices, Inc.\", C=US", "Advanced Micro Devices, Inc.")]
    // Organisation wins over a common name that says something else.
    [InlineData("CN=NVIDIA Corporation, O=Someone Else, C=US", "Someone Else")]
    // No organisation: the common name is used.
    [InlineData("CN=Intel Corporation, C=US", "Intel Corporation")]
    public void ThePublisherIsReadFromTheSubject(string subject, string expected)
    {
        Assert.Equal(expected, SignatureVerifier.ExtractPublisherName(subject));
    }

    [Fact]
    public void AStreamlinePluginIsTrustedDespiteNvidiasOwnSecondSignature()
    {
        // Streamline plugins carry NVIDIA's DigiCert signature and a second one from NVIDIA's private
        // "NVS Root", which Streamline checks itself and Windows does not trust. Only the first counts.
        // No such file can be made here, so this runs where a game with Streamline is installed.
        var plugin = StreamlineGames.FirstOrDefault(System.IO.File.Exists);
        if (plugin is null)
        {
            Assert.Skip("No game with Streamline is installed here.");
        }

        var result = SignatureVerifier.Check(plugin);

        Assert.True(result.HasValidSignature, result.Details);
        Assert.Equal("NVIDIA Corporation", result.PublisherName);
    }
}
