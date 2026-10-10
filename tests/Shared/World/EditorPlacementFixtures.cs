using System.Numerics;




namespace Puck.World.Testing;

/// <summary>The editor placement world: one crate prototype and a seat to place from.</summary>
internal static class EditorPlacementFixtures {
    // One crate, standing a tenth off the half-unit lattice on x.
    internal static HostRow Build() {
        var document = Fixtures.BuildDocument();

        return HostRow.Build(
            definition: (document with {
                CreationsRaw = [Crate],
                PlacementsRaw = (document.PlacementsRaw! with {
                    Rows = [new WorldPlacement(
                        Id: "crate1",
                        Position: new Vector3(x: 1.1f, y: 3f, z: -1f),
                        PrototypeId: Crate.Id,
                        Scale: 1f,
                        YawDegrees: 0f
                    )],
                }),
            }),
            name: "boot"
        );
    }

    internal static WorldPrototype Crate { get; } = CreationFixtures.UnitSphere(id: "crate");
}
