using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a malformed document whose placement names no prototype (a missing/null id that strict parse admits as
/// an absent member) is REFUSED with collected errors, never crashed — the keyed row lookups must stay as
/// null-tolerant as the linear scans they replaced.
/// </summary>
public sealed class PlacementMalformedIdValidationLawTests {
    [Fact]
    public void ANullPrototypeIdIsRefusedNotThrown() {
        var document = Fixtures.BuildDocument();

        document = (document with {
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: "row",
                    PrototypeId: null!,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f
                ),
            ],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document,
            reason: out _
        ));
    }
}
