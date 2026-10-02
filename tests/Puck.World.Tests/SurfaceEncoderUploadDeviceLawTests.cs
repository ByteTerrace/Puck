using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of a capture of CPU float pixels. A CPU-pixel surface in a float working format reaches a capture sink
/// only through <see cref="SurfaceEncoder.ReadSurface"/>, which uploads it through the device's one image upload
/// (<see cref="IGpuSurfaceUpload"/>), draws the display encode (<c>Assets/Shaders/Runtime/display-encode.frag.hlsl</c>) over it
/// in SDR into its RGBA8 target and reads that back. Every working value the law hands in sits on an 8-bit code, so the
/// encode's half code of dither either side leaves each channel within one code of it; headroom above SDR white saturates
/// to 255, a negative value to 0, and the encode writes an opaque alpha whatever the source's. The pattern differs per
/// pixel and channel, so an upload that drops, transposes, swaps or reinterprets the pixels misses by many codes. It runs
/// in both float formats on the first Vulkan device with a graphics queue, on the first Direct3D 12 hardware adapter with
/// the debug layer on, where the upload and the encode must agree on the image's state (no <c>[d3d12-debug]</c> line),
/// and on the software (WARP) renderer, and skips by name where the host has none. It runs alone, because the debug layer
/// removes every device the process already holds.
/// </summary>
[Collection(name: DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class SurfaceEncoderUploadDeviceLawTests {
    private const uint Height = 8U;
    private const int HeadroomRow = 7;
    private const uint Width = 16U;
    private const float Headroom = 3.0f;
    private const float SourceAlpha = 0.25f;
    private const float Undershoot = -1.0f;

    [InlineData(GpuPixelFormat.R16G16B16A16Float)]
    [InlineData(GpuPixelFormat.R32G32B32A32Float)]
    [Theory]
    public void CpuFloatPixelsEncodeToTheirSdrCodesOnAVulkanDevice(GpuPixelFormat format) {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SurfaceEncoderUploadDeviceLawTests));

        Encode(
            backend: $"vulkan ({device.Name})",
            device: device,
            directX: false,
            format: format
        );
    }
    [InlineData(GpuPixelFormat.R16G16B16A16Float, false)]
    [InlineData(GpuPixelFormat.R16G16B16A16Float, true)]
    [InlineData(GpuPixelFormat.R32G32B32A32Float, false)]
    [InlineData(GpuPixelFormat.R32G32B32A32Float, true)]
    [Theory]
    public void CpuFloatPixelsEncodeToTheirSdrCodesOnADirect3D12Device(GpuPixelFormat format, bool warp) {
        var output = new StringWriter();
        var context = (warp ? DirectXTestDevices.Warp() : DirectXTestDevices.Debug(output: output));

        try {
            Encode(
                backend: (warp ? "directx (WARP)" : "directx"),
                device: context,
                directX: true,
                format: format
            );
            context.DrainDebugMessages();
        } finally {
            context.Dispose();
        }

        Assert.DoesNotContain(
            actualString: output.ToString(),
            expectedSubstring: "[d3d12-debug]"
        );
    }

    // The 8-bit code each channel of a pixel encodes to: red and green run against each other along the row, blue down
    // the column, and the last row carries headroom in red and a negative value in blue.
    private static (int Red, int Green, int Blue) Codes(int x, int y) {
        var red = ((x * 15) + (y * 2));
        var blue = ((y * 30) + x);

        return ((y == HeadroomRow)
            ? (255, (255 - red), 0)
            : (red, (255 - red), blue));
    }
    // The working values handed in: each code's value, headroom and the negative value as they are.
    private static (float Red, float Green, float Blue) Values(int x, int y) {
        var (red, green, blue) = Codes(x: x, y: y);

        return ((y == HeadroomRow)
            ? (Headroom, (green / 255.0f), Undershoot)
            : ((red / 255.0f), (green / 255.0f), (blue / 255.0f)));
    }
    private static byte[] Pixels(GpuPixelFormat format) {
        var values = new float[checked(((int)((Width * Height) * 4U)))];

        for (var y = 0; (y < Height); y++) {
            for (var x = 0; (x < Width); x++) {
                var (red, green, blue) = Values(x: x, y: y);
                var pixel = (((y * ((int)Width)) + x) * 4);

                values[pixel] = red;
                values[(pixel + 1)] = green;
                values[(pixel + 2)] = blue;
                values[(pixel + 3)] = SourceAlpha;
            }
        }

        return ((format == GpuPixelFormat.R16G16B16A16Float)
            ? MemoryMarshal.AsBytes(span: values.Select(selector: static value => ((Half)value)).ToArray().AsSpan()).ToArray()
            : MemoryMarshal.AsBytes(span: values.AsSpan()).ToArray());
    }
    private static void Encode(string backend, IGpuDeviceContext device, bool directX, GpuPixelFormat format) {
        Surface encoded;
        byte[] codes;

        using (var encoder = new SurfaceEncoder(
            device: device,
            directX: directX,
            owner: nameof(SurfaceEncoderUploadDeviceLawTests),
            pipelines: new GpuPassPipelineCache()
        )) {
            encoded = encoder.ReadSurface(surface: Surface.CpuPixels(
                format: format,
                height: Height,
                pixels: Pixels(format: format),
                width: Width
            ));
            // The returned pixels are the encoder's readback, valid until its next read or its disposal.
            codes = encoded.Pixels.ToArray();
        }

        Assert.True(condition: encoded.IsCpuPixels);
        Assert.Equal(
            actual: (encoded.Format, encoded.Width, encoded.Height),
            expected: (SurfaceEncoder.CaptureFormat, Width, Height)
        );

        var failures = new List<string>();

        for (var y = 0; (y < Height); y++) {
            for (var x = 0; (x < Width); x++) {
                var (red, green, blue) = Codes(x: x, y: y);
                var pixel = (((y * ((int)Width)) + x) * 4);
                int[] expected = [red, green, blue, 255];
                int[] actual = [codes[pixel], codes[(pixel + 1)], codes[(pixel + 2)], codes[(pixel + 3)]];

                for (var channel = 0; (channel < 4); channel++) {
                    if (Math.Abs(value: (actual[channel] - expected[channel])) > 1) {
                        failures.Add(item: $"{backend}: {format} pixel ({x}, {y}) encoded [{string.Join(separator: ", ", values: actual)}], expected [{string.Join(separator: ", ", values: expected)}] within one code");
                        break;
                    }
                }
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: failures)
        );
    }
}
