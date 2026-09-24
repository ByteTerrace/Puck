using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Round-trip laws for <see cref="WorldFrameSource"/> — the frame-producing sub-vocabulary of
/// <see cref="WorldScreenSource"/> (<see cref="WorldScreenSource.Producer"/>/<see cref="WorldScreenSource.View"/>/
/// <see cref="WorldScreenSource.Probe"/>) a probe socket plugs into.</summary>
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

    // Every frame-source arm, including a camera producer's optional seat and a capture producer's absent monitor,
    // round-trips through the frame-source accessor to an equal record of the same arm.
    public static TheoryData<string> Sources() => new(values: ["camera", "camera-seat", "capture", "probe", "view"]);
    [MemberData(memberName: nameof(Sources))]
    [Theory]
    public void EveryFrameSourceRoundTripsThroughTheFrameSourceAccessor(string arm) {
        WorldFrameSource source = arm switch {
            "camera" => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
                Profile: WorldFeedProfile.Default,
                Sensor: WorldCameraSensor.Infrared
            )),
            "camera-seat" => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
                Sensor: WorldCameraSensor.Color,
                Seat: 2
            )),
            "capture" => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(
                WindowTitle: "OBS",
                Profile: WorldFeedProfile.Default,
                MonitorIndex: null
            )),
            "probe" => new WorldScreenSource.Probe(Id: "faerie"),
            _ => new WorldScreenSource.View(CameraName: "gallery"),
        };
        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldFrameSource,
            value: source
        );

        Assert.IsType(
            expectedType: source.GetType(),
            @object: roundTripped
        );
        Assert.Equal(
            actual: roundTripped,
            expected: source
        );
    }
    // A screen row's own Source is typed WorldScreenSource, the wider union — this proves the narrower
    // WorldFrameSource discriminator set carved out above changed nothing about what a full screen row writes/reads.
    [Fact]
    public void AScreenRowCarryingACameraSourceRoundTripsUnchanged() {
        var screen = new WorldScreen(
            Index: 0,
            Origin: new Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            Right: new Vector3(
                x: 1f,
                y: 0f,
                z: 0f
            ),
            Up: new Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            HalfWidth: 1f,
            HalfHeight: 1f,
            HalfDepth: 0.1f,
            Round: 0f,
            Source: WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
                Profile: WorldFeedProfile.Default,
                Sensor: WorldCameraSensor.Color
            )),
            Route: WorldScreenRoute.Passive
        );

        var roundTripped = RoundTrip(
            typeInfo: WorldJsonContext.Default.WorldScreen,
            value: screen
        );

        Assert.Equal(
            expected: screen.Index,
            actual: roundTripped.Index
        );

        Assert.True(condition: WorldImageProducerSettings.TryCamera(
            camera: out var camera,
            source: Assert.IsType<WorldScreenSource.Producer>(@object: roundTripped.Source)
        ));

        Assert.Equal(
            expected: WorldCameraSensor.Color,
            actual: camera.Sensor
        );
    }
}
