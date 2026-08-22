using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Round-trip laws for <see cref="WorldFrameSource"/> — the frame-producing sub-vocabulary of
/// <see cref="WorldScreenSource"/> (<see cref="WorldScreenSource.Camera"/>/<see cref="WorldScreenSource.View"/>/
/// <see cref="WorldScreenSource.Probe"/>/<see cref="WorldScreenSource.Capture"/>) a probe socket plugs into.</summary>
public sealed class WorldFrameSourceSerializationTests {
    private static T RoundTrip<T>(T value, JsonTypeInfo<T> typeInfo) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            jsonTypeInfo: typeInfo,
            value: value
        );

        return JsonSerializer.Deserialize(
            jsonTypeInfo: typeInfo,
            utf8Json: bytes
        )!;
    }

    [Fact]
    public void ACameraFrameSourceRoundTripsThroughTheFrameSourceAccessor() {
        var source = new WorldScreenSource.Camera(
            Profile: WorldFeedProfile.Default,
            Sensor: WorldCameraSensor.Infrared
        );

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldFrameSource,
            value: (WorldFrameSource)source
        );

        var camera = Assert.IsType<WorldScreenSource.Camera>(roundTripped);

        Assert.Equal(expected: WorldCameraSensor.Infrared, actual: camera.Sensor);
        Assert.Equal(expected: WorldFeedProfile.Default, actual: camera.Profile);
    }
    [Fact]
    public void AViewFrameSourceRoundTripsThroughTheFrameSourceAccessor() {
        var source = new WorldScreenSource.View(CameraName: "gallery");

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldFrameSource,
            value: (WorldFrameSource)source
        );

        var view = Assert.IsType<WorldScreenSource.View>(roundTripped);

        Assert.Equal(expected: "gallery", actual: view.CameraName);
    }
    [Fact]
    public void AProbeFrameSourceRoundTripsThroughTheFrameSourceAccessor() {
        var source = new WorldScreenSource.Probe(Id: "faerie");

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldFrameSource,
            value: (WorldFrameSource)source
        );

        var probe = Assert.IsType<WorldScreenSource.Probe>(roundTripped);

        Assert.Equal(expected: "faerie", actual: probe.Id);
    }
    [Fact]
    public void ACaptureFrameSourceRoundTripsThroughTheFrameSourceAccessor() {
        var source = new WorldScreenSource.Capture(
            WindowTitle: "OBS",
            Profile: WorldFeedProfile.Default,
            MonitorIndex: null
        );

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldFrameSource,
            value: (WorldFrameSource)source
        );

        var capture = Assert.IsType<WorldScreenSource.Capture>(roundTripped);

        Assert.Equal(expected: "OBS", actual: capture.WindowTitle);
        Assert.Null(@object: capture.MonitorIndex);
    }
    // A screen row's own Source is typed WorldScreenSource, the wider union — this proves the narrower
    // WorldFrameSource discriminator set carved out above changed nothing about what a full screen row writes/reads.
    [Fact]
    public void AScreenRowCarryingACameraSourceRoundTripsUnchanged() {
        var screen = new WorldScreen(
            Index: 0,
            Origin: new Vector3(x: 0f, y: 1f, z: 0f),
            Right: new Vector3(x: 1f, y: 0f, z: 0f),
            Up: new Vector3(x: 0f, y: 1f, z: 0f),
            HalfWidth: 1f,
            HalfHeight: 1f,
            HalfDepth: 0.1f,
            Round: 0f,
            Source: new WorldScreenSource.Camera(Profile: WorldFeedProfile.Default, Sensor: WorldCameraSensor.Color),
            Route: WorldScreenRoute.Passive
        );

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldScreen,
            value: screen
        );

        Assert.Equal(expected: screen.Index, actual: roundTripped.Index);

        var camera = Assert.IsType<WorldScreenSource.Camera>(roundTripped.Source);

        Assert.Equal(expected: WorldCameraSensor.Color, actual: camera.Sensor);
    }
}
