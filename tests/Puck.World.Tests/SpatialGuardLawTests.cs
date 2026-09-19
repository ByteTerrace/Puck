using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Locks bounded spatial fingerprints to the actual compiled volume envelope and explicit input selection.</summary>
public sealed class SpatialGuardLawTests {
    private static WorldPlacement Box(
        string id,
        float x,
        float y = 0,
        float z = 0,
        float halfX = 0.5f,
        float halfY = 0.5f,
        float halfZ = 0.5f,
        float yaw = 0f
    ) => new(
        Id: id,
        PrototypeId: "marker",
        Position: new DocumentVector3(
            x: x,
            y: y,
            z: z
        ),
        YawDegrees: 0f,
        Scale: 1f,
        Spatial: [new WorldPlacementSpatialVolume(
                Name: "occupation",
                Role: WorldPlacementSpatialRole.Occupation,
                Shape: new WorldSpatialShape(
                    Kind: WorldSpatialShapeKind.Box,
                    Center: new DocumentVector3(
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    HalfExtents: new DocumentVector3(
                        x: halfX,
                        y: halfY,
                        z: halfZ
                    ),
                    YawDegrees: yaw
                )
            )]
    );
    private static WorldDefinition Definition(params WorldPlacement[] placements) => new(PlacementsRaw: new WorldPlacementsSection(placements));
    private static long Raw(int value) => FixedQ4816.FromInteger(value: value).Value;
    private static WorldSpatialReadRegion Region(int minX = -1, int minZ = -1, int maxX = 1, int maxZ = 1, int minY = -1, int maxY = 1) =>
        new(
            Raw(value: minX),
            Raw(value: minZ),
            Raw(value: maxX),
            Raw(value: maxZ),
            Raw(value: minY),
            Raw(value: maxY)
        );

    [Fact]
    public void ExplicitInputHashTracksSelectedPlacementAndStateRowsOnly() {
        var selected = Box(
            id: "selected",
            x: 0
        );
        var unrelated = Box(
            id: "unrelated",
            x: 20
        );
        var selectedState = new WorldStateRow(
            CellName.Parse(candidate: "selected"),
            CellKind.Int,
            Max: 10
        );
        var unrelatedState = new WorldStateRow(
            CellName.Parse(candidate: "unrelated"),
            CellKind.Int,
            Max: 10
        );
        var definition = new WorldDefinition(
            PlacementsRaw: new WorldPlacementsSection([selected, unrelated]),
            StateRaw: new WorldStateSection([selectedState, unrelatedState])
        );
        var selectedHash = WorldDefinitionFingerprint.ComputeInputs(
            definition: definition,
            placementIds: ["selected"],
            stateRows: ["selected"]
        );
        var selectedChanged = definition with {
            PlacementsRaw = new WorldPlacementsSection([selected with { PrototypeId = "other-template" }, unrelated]),
            StateRaw = new WorldStateSection([selectedState with { Max = 11 }, unrelatedState]),
        };
        var unrelatedChanged = definition with {
            PlacementsRaw = new WorldPlacementsSection([selected, unrelated with { Position = new DocumentVector3(
                x: 30,
                y: 0,
                z: 0
            ) }]),
            StateRaw = new WorldStateSection([selectedState, unrelatedState with { Max = 11 }]),
        };

        Assert.NotEqual(
            selectedHash,
            WorldDefinitionFingerprint.ComputeInputs(
                definition: selectedChanged,
                placementIds: ["selected"],
                stateRows: ["selected"]
            )
        );
        Assert.Equal(
            selectedHash,
            WorldDefinitionFingerprint.ComputeInputs(
                definition: unrelatedChanged,
                placementIds: ["selected"],
                stateRows: ["selected"]
            )
        );
    }
    [Fact]
    public void SpatialHashDetectsInsertedAndEnlargedLocalBlockersButIgnoresDistantRows() {
        var region = Region();
        var empty = Definition();
        var inserted = Definition(Box(
            id: "local",
            x: 0
        ));
        var enlarged = Definition(Box(
            id: "local",
            x: 0,
            halfX: 2,
            halfZ: 2
        ));
        var distant = Definition(Box(
            id: "distant",
            x: 100
        ));
        var movedDistant = Definition(Box(
            id: "distant",
            x: 200,
            halfX: 4,
            halfZ: 4
        ));

        Assert.NotEqual(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: empty,
                region: region
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: inserted,
                region: region
            )
        );
        Assert.NotEqual(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: inserted,
                region: region
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: enlarged,
                region: region
            )
        );
        Assert.Equal(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: distant,
                region: region
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: movedDistant,
                region: region
            )
        );
    }
    [Fact]
    public void SpatialHashIncludesRotationAndFiniteVerticalExtent() {
        var region = Region();
        var unrotated = Definition(Box(
            id: "rotated",
            x: 2.5f,
            halfX: 0.5f,
            halfZ: 2f,
            yaw: 0f
        ));
        var rotated = Definition(Box(
            id: "rotated",
            x: 2.5f,
            halfX: 0.5f,
            halfZ: 2f,
            yaw: 45f
        ));
        var above = Definition(Box(
            id: "height",
            x: 0,
            y: 5
        ));
        var lowered = Definition(Box(
            id: "height",
            x: 0,
            y: 0
        ));

        Assert.NotEqual(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: unrotated,
                region: region
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: rotated,
                region: region
            )
        );
        Assert.NotEqual(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: above,
                region: region
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: lowered,
                region: region
            )
        );
    }
    [Fact]
    public void SpatialHashTracksBoundExtentsEvenWhenTheCensusAndReferenceStayTheSame() {
        static WorldDefinition BoundBox(string extent) {
            var box = Box(
                "bound",
                0
            );
            var volume = box.Spatial![0];
            var bound = System.Text.Json.JsonSerializer.Deserialize<DocumentVector3>("\"state.extent\"")!;
            var definition = Definition(box with { Spatial = [volume with { Shape = volume.Shape with { HalfExtents = bound } }] }) with {
                StateRaw = new WorldStateSection([new WorldStateRow(
                    CellName.Parse(candidate: "extent"),
                    CellKind.Text,
                    Cells: [new StateCell(
                            WorldStateRow.SlotKey,
                            CellValue.Text(value: extent)
                        )]
                )]),
            };

            Assert.True(
                condition: WorldStateDocumentValues.TryResolve(
                    definition,
                    out var reason
                ),
                userMessage: reason
            );
            return definition;
        }
        var before = BoundBox(extent: "[0.5, 0.5, 0.5]");
        var after = BoundBox(extent: "[0.75, 0.5, 0.5]");

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(
                before.Placements[0],
                WorldJsonContext.Default.WorldPlacement
            ),
            System.Text.Json.JsonSerializer.Serialize(
                after.Placements[0],
                WorldJsonContext.Default.WorldPlacement
            )
        );
        Assert.NotEqual(
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: before,
                region: Region()
            ),
            WorldDefinitionFingerprint.ComputeSpatial(
                definition: after,
                region: Region()
            )
        );
    }
}
