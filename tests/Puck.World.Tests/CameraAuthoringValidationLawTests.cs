using System.Text.Json;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Camera vendor rows cross an unsafe native control boundary, so their byte-sized wire contract and the
/// sensor vocabulary are load-time invariants of the camera producer's settings rather than values the platform layer
/// silently truncates.</summary>
public sealed class CameraAuthoringValidationLawTests {
    private static WorldDefinition WithSource(WorldScreenSource source) {
        var definition = Fixtures.BuildDocument();
        var screen = definition.Screens[0];

        return definition with {
            ScreensRaw = [screen with { Source = source }],
        };
    }
    private static WorldDefinition WithCamera(WorldCameraSensor sensor, IReadOnlyList<WorldCameraVendorControl>? vendor, int? seat = null) => WithSource(source: WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.CameraId,
        settings: new WorldCameraSettings(
            Controls: new WorldCameraControls(Vendor: vendor),
            Profile: WorldFeedProfile.Default,
            Seat: seat,
            Sensor: sensor
        )
    ));

    [Fact]
    public void AZeroSeatRefuses() {
        var definition = WithCamera(
            seat: 0,
            sensor: WorldCameraSensor.Color,
            vendor: null
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "producer.settings.seat"
        );
    }
    // The fixture document declares 4 local seats (Fixtures.BuildDocument's four seat spawns), so seat 5 is the first
    // out-of-range ordinal and seat 4 is the last in-range one.
    [Fact]
    public void AnOutOfRangeSeatRefusesWhileTheCeilingSeatPasses() {
        Laws.RefusalWithControl(
            lawId: "camera.seat-out-of-range",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithCamera(
                    seat: 5,
                    sensor: WorldCameraSensor.Color,
                    vendor: null
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithCamera(
                    seat: 4,
                    sensor: WorldCameraSensor.Color,
                    vendor: null
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void AnUnknownSensorOrSettingsMemberRefusesByName() {
        Laws.RefusalWithControl(
            lawId: "camera.sensor-unknown",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithSource(source: new WorldScreenSource.Producer(
                    Id: WorldImageProducerSettings.CameraId,
                    Settings: new Dictionary<string, JsonElement> { ["sensor"] = JsonSerializer.SerializeToElement(value: "Ultraviolet") }
                )),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithSource(source: new WorldScreenSource.Producer(
                    Id: WorldImageProducerSettings.CameraId,
                    Settings: new Dictionary<string, JsonElement> { ["sensor"] = JsonSerializer.SerializeToElement(value: "Infrared") }
                )),
                reason: out _
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: WithSource(source: new WorldScreenSource.Producer(
                Id: WorldImageProducerSettings.CameraId,
                Settings: new Dictionary<string, JsonElement> { ["lens"] = JsonSerializer.SerializeToElement(value: 3) }
            )),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "producer.settings"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "lens"
        );
    }
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
            vendor: [new WorldCameraVendorControl(
                    Id: id,
                    Value: value
                )]
        );
        var admitted = WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        );

        Assert.Equal(
            actual: admitted,
            expected: valid
        );

        if (!valid) {
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "producer.settings.controls.vendor[0]"
            );
        }
    }
}
