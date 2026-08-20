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

    private static WorldDefinition WithCamera(WorldCameraSensor sensor, IReadOnlyList<WorldCameraVendorControl>? vendor) {
        var definition = Fixtures.BuildDocument();
        var screen = definition.Screens[0];

        return definition with {
            ScreensRaw = [screen with {
                Source = new WorldScreenSource.Camera(
                    Profile: WorldFeedProfile.Default,
                    Controls: new WorldCameraControls(Vendor: vendor),
                    Sensor: sensor
                ),
            }],
        };
    }
}
