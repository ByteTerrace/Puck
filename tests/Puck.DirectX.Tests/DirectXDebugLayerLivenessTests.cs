using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds the process's console error stream to one test at a time.</summary>
[CollectionDefinition(name: nameof(ConsoleErrorCollection), DisableParallelization = true)]
public sealed class ConsoleErrorCollection {
}
/// <summary>
/// Proves the Direct3D 12 debug drain is live, so a World run with <c>--debug-layers</c> that prints no
/// <c>[d3d12-debug]</c> line is evidence rather than silence. A device context created with the debug layer on, as the
/// presenter registration creates one under <c>GpuDeviceOptions.DebugLayers</c>, receives one deliberate violation: a committed texture that is neither a render target
/// nor a depth stencil, created with an optimized clear value. The runtime refuses the creation, so no resource exists,
/// and the debug layer stores a message that <see cref="DirectXDeviceContext.DrainDebugMessages"/> prints.
/// <para>The live-object report at teardown is held the same way: an object still alive when the context releases the
/// device is written as a <c>[d3d12-debug] live</c> line, which fails a debug-layer run like any other debug message,
/// and a teardown that leaks nothing writes no <c>[d3d12-debug]</c> line at all.</para>
/// Each test skips when the host has no Direct3D 12 device or the debug layer is not installed.
/// </summary>
[Collection(name: nameof(ConsoleErrorCollection))]
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXDebugLayerLivenessTests {
    private const string DebugPrefix = "[d3d12-debug] ";
    private const string LivePrefix = "[d3d12-debug] live ";

    [Fact]
    public void ALeakedObjectIsReportedLiveWhenTheDeviceIsTornDown() {
        var output = new StringWriter();
        var context = DebugContext(output: output);
        var leaked = new DirectXGpuBufferFactory(deviceContext: context).CreateHostVisible(
            sizeBytes: 256,
            usage: GpuBufferUsage.Storage
        );

        try {
            context.Dispose();
        } finally {
            leaked.Dispose();
        }

        var lines = DebugLines(output: output);

        Assert.Contains(
            collection: lines,
            filter: static line => (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: LivePrefix
            ) && line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "ID3D12Resource"
            ))
        );
        Assert.DoesNotContain(
            collection: lines,
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "ID3D12Device"
            )
        );
    }
    [Fact]
    public void ATeardownThatLeaksNothingWritesNoDebugLine() {
        var output = new StringWriter();
        var context = DebugContext(output: output);

        new DirectXGpuBufferFactory(deviceContext: context).CreateHostVisible(
            sizeBytes: 256,
            usage: GpuBufferUsage.Storage
        ).Dispose();
        context.Dispose();

        Assert.Empty(collection: DebugLines(output: output));
    }
    [Fact]
    public void ADeliberateViolationReachesTheDebugDrain() {
        var captured = new StringWriter();

        using var context = DebugContext(output: captured);
        var device = ((ID3D12Device*)context.Device.Handle);
        var clearValue = new D3D12_CLEAR_VALUE {
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        };

        try {
            var texture = DirectXTextures.CreateCommitted(
                clearValue: clearValue,
                device: device,
                flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                format: DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                height: 4,
                initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                width: 4
            );

            if (texture is not null) {
                _ = ((IUnknown*)texture)->Release();
            }
        } catch (Exception exception) when ((exception is ArgumentException or System.Runtime.InteropServices.COMException)) {
            // The refusal is the expected outcome; the stored message is what the test reads.
        }

        context.DrainDebugMessages();

        var lines = captured.ToString();

        // The layer's own words, for the record of a run.
        Console.Error.Write(value: lines);

        Assert.Contains(
            actualString: lines,
            expectedSubstring: "[d3d12-debug] "
        );
        Assert.Contains(
            actualString: lines,
            expectedSubstring: "CreateCommittedResource"
        );
    }

    // A context on the default adapter with the debug layer on, its device created; skips when either is unavailable.
    private static DirectXDeviceContext DebugContext(StringWriter output) {
        var context = new DirectXDeviceContext(
            adapterLuid: 0,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        ) {
            DebugOutput = output,
            EnableDebugLayer = true,
        };

        try {
            _ = context.Device;
        } catch (GpuDeviceUnavailableException exception) {
            context.Dispose();
            Assert.Skip(reason: $"no Direct3D 12 device with the debug layer on this host: {exception.Message}");
        }

        if (!context.HasDebugLayer) {
            context.Dispose();
            Assert.Skip(reason: "the Direct3D 12 debug layer did not load on this host");
        }

        return context;
    }
    private static List<string> DebugLines(StringWriter output) {
        using var reader = new StringReader(s: output.ToString());
        var lines = new List<string>();

        while (reader.ReadLine() is { } line) {
            if (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: DebugPrefix
            )) {
                lines.Add(item: line);
            }
        }

        return lines;
    }
}
