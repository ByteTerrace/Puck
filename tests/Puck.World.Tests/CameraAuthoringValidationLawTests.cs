using Xunit;

namespace Puck.World.Tests;

/// <summary>Camera vendor rows cross an unsafe native control boundary, so their byte-sized wire contract and the
/// sensor enum are load-time invariants rather than values the platform layer silently truncates.</summary>
public sealed class CameraAuthoringValidationLawTests {
    public static IEnumerable<object[]> VendorByteCases() {
        yield return [-1, 0, false];
        yield return [0, 0, true];
        yield return [255, 255, true];
        yield return [256, 0, false];
        yield return [1, -1, false];
        yield return [1, 256, false];
    }

    [MemberData(nameof(VendorByteCases))]
    [Theory]
    public void VendorSelectorsAndValuesAreBytes(int id, int value, bool valid) {
        var definition = WithCamera(
            sensor: WorldCameraSensor.Color,
            vendor: [new WorldCameraVendorControl(Id: id, Value: value)]
        );
        var admitted = WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason);

        Assert.Equal(expected: valid, actual: admitted);

        if (!valid) {
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "camera.controls.vendor[0]");
        }
    }

    [Fact]
    public void UndefinedSensorRefusesBeforeBinderIndexing() {
        var definition = WithCamera(sensor: ((WorldCameraSensor)byte.MaxValue), vendor: null);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "camera.sensor");
    }

    // The fixture document declares 4 local seats (Fixtures.BuildDocument's four seat spawns), so seat 5 is the first
    // out-of-range ordinal and seat 4 is the last in-range one.
    [Fact]
    public void AnOutOfRangeSeatRefusesWhileTheCeilingSeatPasses() {
        Laws.RefusalWithControl(
            lawId: "camera.seat-out-of-range",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithCamera(sensor: WorldCameraSensor.Color, vendor: null, seat: 5),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithCamera(sensor: WorldCameraSensor.Color, vendor: null, seat: 4),
                reason: out _
            ));
    }
    [Fact]
    public void AZeroSeatRefuses() {
        var definition = WithCamera(sensor: WorldCameraSensor.Color, vendor: null, seat: 0);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "camera.seat");
    }

    private static WorldDefinition WithCamera(WorldCameraSensor sensor, IReadOnlyList<WorldCameraVendorControl>? vendor, int? seat = null) {
        var definition = Fixtures.BuildDocument();
        var screen = definition.Screens[0];

        return definition with {
            ScreensRaw = [screen with {
                Source = new WorldScreenSource.Camera(
                    Profile: WorldFeedProfile.Default,
                    Controls: new WorldCameraControls(Vendor: vendor),
                    Sensor: sensor,
                    Seat: seat
                ),
            }],
        };
    }
}
