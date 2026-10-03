using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every live retarget releases the camera view a screen was filming. The retarget verbs share
/// one release-then-bind path, so from a screen bound to a camera view, each of them leaves no view registration
/// behind. The uploaded-sources fixture boots headless with its views configured over a device-free pipeline catalog,
/// and a camera service that reports a camera present so the camera verb binds.
/// </summary>
public sealed class ScreenViewReleaseLawTests {
    private const string World = "tests/Puck.World.Canaries/uploaded-sources/fixture.world.json";

    private static readonly ulong Step = EngineTicks.PerRate(ratePerSecond: 30u);

    private static string Run(CommandRegistry registry, string line) {
        var result = registry.Submit(line: line);

        Assert.False(condition: result.IsError, userMessage: $"{line}: {result.Output}");
        return result.Output;
    }
    private static int Census(CommandRegistry registry) {
        var refresh = Run(line: "world.view-refresh", registry: registry);

        return int.Parse(s: refresh[(refresh.IndexOf(value: "; ", comparisonType: StringComparison.Ordinal) + 2)..].Split(separator: ' ')[0], provider: System.Globalization.CultureInfo.InvariantCulture);
    }

    [InlineData("qr hello")]
    [InlineData("camera")]
    [InlineData("row")]
    [Theory]
    public void ARetargetFromACameraViewLeavesNoRegistrationBehind(string retarget) {
        using var state = new TemporaryDirectory(prefix: "puck-screen-view-release-");
        var builder = WorldBootHarness.Compose(presentation: WorldHostPresentation.None, stateDirectory: state, world: World);

        builder.Services.AddSingleton<ICameraCaptureService, PresentCameraService>();

        var host = state.Own(owner: builder.Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var pipelines = new GpuPassPipelineCache();

        host.Services.GetRequiredService<IWorldViewHost>().ConfigureViews(
            displayHeight: 64,
            displayWidth: 64,
            dynamicTransformCapacity: 64,
            host: new EmptyFrameSource(),
            hostsOnDirectX: false,
            instanceCapacity: 64,
            pipelines: new SdfWorldPipelineCatalog(
                meshRaster: new SdfMeshRasterPass(bytecodeExtension: ".spv", pipelines: pipelines),
                regionCopy: new GpuRegionCopyPass(bytecodeExtension: ".spv", pipelines: pipelines)
            ),
            programWordCapacity: 1024,
            viewports: new WorldSeatViewports()
        );

        var baseline = Census(registry: registry);

        _ = Run(line: "screen.source 1 view edge", registry: registry);
        Assert.Equal(expected: (baseline + 1), actual: Census(registry: registry));
        _ = Run(line: $"screen.source 1 {retarget}", registry: registry);
        Assert.Equal(expected: baseline, actual: Census(registry: registry));
    }

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new NotSupportedException(message: "no frame is captured headless");
    }
    // A platform with a camera present and no device to open: the camera verb binds, and its feed never opens.
    private sealed class PresentCameraService : ICameraCaptureService {
        public bool IsSupported => true;

        public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() => [];
        public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) {
            graph = null;
            return false;
        }
        public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) {
            graph = null;
            return false;
        }
    }
}
