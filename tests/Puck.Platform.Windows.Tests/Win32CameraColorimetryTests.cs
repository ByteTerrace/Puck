using System.Runtime.Versioning;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class Win32CameraColorimetryTests {
    [Fact]
    public void Unknown_metadata_uses_media_foundation_defaults() {
        var conversion = new Win32CameraColorimetry().Resolve();

        Assert.Equal(expected: Win32YuvMatrix.Bt709, actual: conversion.Matrix);
        Assert.Equal(expected: Win32YuvRange.Limited, actual: conversion.Range);
        Assert.False(condition: conversion.ChromaHorizontallyCosited);
        Assert.False(condition: conversion.ChromaVerticallyCosited);
    }
    [Fact]
    public void Explicit_metadata_selects_matrix_range_and_siting() {
        var conversion = new Win32CameraColorimetry(ChromaSiting: 0x6u, Matrix: 2u, NominalRange: 1u).Resolve();

        Assert.Equal(expected: Win32YuvMatrix.Bt601, actual: conversion.Matrix);
        Assert.Equal(expected: Win32YuvRange.Full, actual: conversion.Range);
        Assert.True(condition: conversion.ChromaHorizontallyCosited);
        Assert.True(condition: conversion.ChromaVerticallyCosited);
    }
    [InlineData(3u, 2u, 0u)]
    [InlineData(1u, 3u, 0u)]
    [InlineData(1u, 2u, 0x10u)]
    [Theory]
    public void Unsupported_metadata_refuses_gpu_conversion(uint matrix, uint range, uint siting) {
        var colorimetry = new Win32CameraColorimetry(ChromaSiting: siting, Matrix: matrix, NominalRange: range);

        _ = Assert.Throws<NotSupportedException>(testCode: () => colorimetry.Resolve());
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void Shader_selection_honors_colorimetry_and_chroma_siting() {
        var shader = Win32D3D11CameraFrameConverter.Shader(
            colorimetry: new Win32CameraColorimetry(ChromaSiting: 0x6u, Matrix: 2u, NominalRange: 1u),
            subtype: "NV12"
        );

        Assert.Contains(actualString: shader, comparisonType: StringComparison.Ordinal, expectedSubstring: "1.402 * v");
        Assert.Contains(actualString: shader, comparisonType: StringComparison.Ordinal, expectedSubstring: "float2(0.0, 0.0)");
        Assert.DoesNotContain(actualString: shader, comparisonType: StringComparison.Ordinal, expectedSubstring: "1.5748 * v");
    }
    [InlineData("YUY2", 0u, 0u, 0u)]
    [InlineData("YUY2", 2u, 1u, 0u)]
    [InlineData("NV12", 0u, 0u, 0u)]
    [InlineData("NV12", 1u, 2u, 0x6u)]
    [InlineData("L8", 0u, 0u, 0u)]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void Every_generated_kernel_compiles(string subtype, uint matrix, uint range, uint siting) {
        Win32D3D11CameraFrameConverter.ValidateShader(
            colorimetry: new Win32CameraColorimetry(ChromaSiting: siting, Matrix: matrix, NominalRange: range),
            subtype: subtype
        );
    }
}
