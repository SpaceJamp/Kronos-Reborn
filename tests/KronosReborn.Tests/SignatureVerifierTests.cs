using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

public class SignatureVerifierTests
{
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
}
