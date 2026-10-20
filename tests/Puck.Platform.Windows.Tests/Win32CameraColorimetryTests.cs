using System.Runtime.Versioning;
using Puck.Abstractions.Sources;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class Win32CameraColorimetryTests {
    [InlineData("YUY2")]
    [InlineData("NV12")]
    [InlineData("L8")]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void Every_subtype_converts_through_a_kernel_compiled_at_build(string subtype) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Direct3D 11 kernels compile at build only on Windows."); return; }

        var bytecode = File.ReadAllBytes(path: Win32D3D11CameraFrameConverter.KernelPath(subtype: subtype));

        // A DXBC container opens with its four-character code.
        Assert.Equal(
            actual: System.Text.Encoding.ASCII.GetString(bytes: bytecode, count: 4, index: 0),
            expected: "DXBC"
        );
    }
    [Fact]
    public void Explicit_metadata_selects_matrix_range_and_siting() {
        var conversion = new Win32CameraColorimetry(
            ChromaSiting: 0x6u,
            Matrix: 2u,
            NominalRange: 1u
        ).Resolve();

        Assert.Equal(
            expected: ImageYuvMatrix.Bt601,
            actual: conversion.Matrix
        );
        Assert.Equal(
            expected: ImageYuvRange.Full,
            actual: conversion.Range
        );
        Assert.True(condition: conversion.ChromaHorizontallyCosited);
        Assert.True(condition: conversion.ChromaVerticallyCosited);
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void Conversion_constants_honor_colorimetry_and_chroma_siting() {
        // BT.601, full range, chroma cosited on both axes.
        AssertConstants(
            actual: Win32D3D11CameraFrameConverter.ConversionConstants(colorimetry: new Win32CameraColorimetry(
                ChromaSiting: 0x6u,
                Matrix: 2u,
                NominalRange: 1u
            )),
            expected: [0f, 255f, 255f, 0f, 1.402f, 0.344136f, 0.714136f, 1.772f, 0f, 0f, 0f, 0f]
        );
        // The Media Foundation defaults: BT.709, limited range, chroma centered.
        AssertConstants(
            actual: Win32D3D11CameraFrameConverter.ConversionConstants(colorimetry: new Win32CameraColorimetry()),
            expected: [16f, 219f, 224f, 0f, 1.5748f, 0.187324f, 0.468124f, 1.8556f, 0.5f, 0.5f, 0f, 0f]
        );
    }
    // The matrix coefficients are derived from Kr and Kb, so they agree with the published six-digit values to within
    // the digits those carry; every other constant is exact.
    private static void AssertConstants(float[] actual, float[] expected) {
        Assert.Equal(
            actual: actual.Length,
            expected: expected.Length
        );

        for (var index = 0; (index < expected.Length); index++) {
            Assert.InRange(
                actual: actual[index],
                high: (expected[index] + (((index >= 4) && (index <= 7)) ? 1e-6f : 0f)),
                low: (expected[index] - (((index >= 4) && (index <= 7)) ? 1e-6f : 0f))
            );
        }
    }
    [Fact]
    public void Unknown_metadata_uses_media_foundation_defaults() {
        var conversion = new Win32CameraColorimetry().Resolve();

        Assert.Equal(
            expected: ImageYuvMatrix.Bt709,
            actual: conversion.Matrix
        );
        Assert.Equal(
            expected: ImageYuvRange.Limited,
            actual: conversion.Range
        );
        Assert.False(condition: conversion.ChromaHorizontallyCosited);
        Assert.False(condition: conversion.ChromaVerticallyCosited);
    }
    [InlineData(3u, 2u, 0u)]
    [InlineData(1u, 3u, 0u)]
    [InlineData(1u, 2u, 0x10u)]
    [Theory]
    public void Unsupported_metadata_refuses_gpu_conversion(uint matrix, uint range, uint siting) {
        var colorimetry = new Win32CameraColorimetry(
            ChromaSiting: siting,
            Matrix: matrix,
            NominalRange: range
        );

        _ = Assert.Throws<NotSupportedException>(testCode: () => colorimetry.Resolve());
    }
}
