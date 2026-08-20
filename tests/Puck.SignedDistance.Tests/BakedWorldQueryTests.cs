using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class BakedWorldQueryTests {
    private const long CellSizeRaw = 16384L;

    private static BakedWorldQuery BlockedColumnQuery() =>
        Query(
            blocked: [(2, 0),],
            height: 1,
            width: 4
        );
    private static WorldQueryArtifact Artifact(int width, int height, params (int Column, int Row)[] blocked) {
        var cellCount = (width * height);
        var words = new ulong[WorldQueryArtifact.BlockedWordCount(cellCount: cellCount)];
        var heights = new long[cellCount];

        Array.Fill(
            array: heights,
            value: WorldQueryArtifact.NoHeightSentinel
        );

        foreach (var (column, row) in blocked) {
            var cellIndex = ((row * width) + column);

            words[(cellIndex >> 6)] |= (1UL << (cellIndex & 63));
        }

        return new WorldQueryArtifact(
            blocked: words,
            cellSizeRaw: CellSizeRaw,
            height: height,
            heightRaw: heights,
            originXRaw: 0L,
            originZRaw: 0L,
            width: width
        );
    }
    private static FixedVector3 Direction(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    private static FixedQ4816 Fixed(double value) =>
        FixedQ4816.FromDouble(value: value);
    private static WorldQueryArtifact GroundPlane(float topY) =>
        WorldQueryBaker.Bake(
            blockers: [],
            maxX: 2f,
            maxZ: 2f,
            minX: -2f,
            minZ: -2f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 2f,
                MaxZ: 2f,
                MinX: -2f,
                MinZ: -2f,
                TopY: topY
            ),]
        );
    private static FixedPosition CellPosition(long cellX, long cellZ, double x, double y, double z) {
        Assert.True(condition: FixedPosition.TryCreate(
            cellX: cellX,
            cellY: 0L,
            cellZ: cellZ,
            local: new FixedVector3(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y),
                Z: FixedQ4816.FromDouble(value: z)
            ),
            result: out var position
        ));

        return position;
    }
    private static FixedPosition Position(double x, double z) =>
        Position(
            x: x,
            y: 0.0,
            z: z
        );
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static BakedWorldQuery Query(int width, int height, params (int Column, int Row)[] blocked) =>
        new(artifact: Artifact(
            blocked: blocked,
            height: height,
            width: width
        ));
    private static BakedWorldQuery TwoCellQuery() =>
        new(artifact: new WorldQueryArtifact(
            blocked: [2UL,],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [WorldQueryArtifact.NoHeightSentinel, WorldQueryArtifact.NoHeightSentinel,],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 2
        ));

    [Fact]
    public void AnAllSentinelHeightLayerDoesNotAdvertiseAHeightfield() {
        var absent = new WorldQueryArtifact(
            blocked: [],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [WorldQueryArtifact.NoHeightSentinel, WorldQueryArtifact.NoHeightSentinel,],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 2
        );
        var present = new WorldQueryArtifact(
            blocked: [],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [WorldQueryArtifact.NoHeightSentinel, 0L,],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 2
        );

        Assert.False(condition: absent.HasHeightfield);
        Assert.True(condition: present.HasHeightfield);
    }

    [Fact]
    public void AnAllZeroBlockedBitmapDoesNotAdvertiseABlockedLayer() {
        var absent = new WorldQueryArtifact(
            blocked: [0UL,],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 2
        );
        var present = new WorldQueryArtifact(
            blocked: [2UL,],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 2
        );

        Assert.False(condition: absent.HasBlocked);
        Assert.True(condition: present.HasBlocked);
    }

    [Fact]
    public void AnEmptyBakeAdvertisesNeitherLayer() {
        var empty = WorldQueryBaker.Bake(
            blockers: [],
            maxX: 2f,
            maxZ: 2f,
            minX: 0f,
            minZ: 0f,
            terrain: []
        );
        var authored = WorldQueryBaker.Bake(
            blockers: [new WorldQueryBlockerInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f
            ),],
            maxX: 2f,
            maxZ: 2f,
            minX: 0f,
            minZ: 0f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f,
                TopY: 0f
            ),]
        );

        Assert.False(condition: empty.HasBlocked);
        Assert.False(condition: empty.HasHeightfield);
        Assert.True(condition: authored.HasBlocked);
        Assert.True(condition: authored.HasHeightfield);
    }

    [Fact]
    public void BakeCoversPartialCellsAtMaximumEdges() {
        var artifact = WorldQueryBaker.Bake(
            blockers: [],
            maxX: 0.3f,
            maxZ: 0.3f,
            minX: 0f,
            minZ: 0f,
            terrain: []
        );

        Assert.Equal(
            expected: 2,
            actual: artifact.Width
        );
        Assert.Equal(
            expected: 2,
            actual: artifact.Height
        );
    }

    [Fact]
    public void BakeRefusesAGridSpanningMoreCellsThanACellIndexAddresses() {
        Assert.Throws<ArgumentException>(
            paramName: "maxX",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: 1e14f,
                maxZ: 1f,
                minX: -1e14f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Throws<ArgumentException>(
            paramName: "maxX",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: 200000f,
                maxZ: 200000f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Equal(
            expected: 40000,
            actual: WorldQueryBaker.Bake(
                blockers: [],
                maxX: 10000f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            ).Width
        );
    }

    [Fact]
    public void BakeRefusesItsAllocationBudgetBeforeAllocatingLayers() {
        Assert.Throws<ArgumentException>(
            paramName: "maxCellCount",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxCellCount: 15,
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Throws<ArgumentException>(
            paramName: "maxCellCount",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: 10_000f,
                maxZ: 10_000f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "maxCellCount",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxCellCount: 0,
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );

        Assert.Equal(
            expected: 16,
            actual: WorldQueryBaker.Bake(
                blockers: [],
                maxCellCount: 16,
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            ).CellCount
        );
    }

    [Fact]
    public void BakeRefusesAGridBoundTheCoordinateCarrierCanOnlySaturate() {
        Assert.Throws<ArgumentException>(
            paramName: "maxX",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: float.MaxValue,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Throws<ArgumentException>(
            paramName: "minZ",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: float.MinValue,
                terrain: []
            )
        );
        Assert.Equal(
            expected: 4,
            actual: WorldQueryBaker.Bake(
                blockers: [],
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            ).Width
        );
    }

    [Fact]
    public void BakeRefusesATerrainHeightThatQuantizesToTheNoGroundSentinel() {
        Assert.Throws<ArgumentException>(testCode: () => WorldQueryBaker.Bake(
            blockers: [],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f,
                TopY: float.MinValue
            ),]
        ));

        var deep = WorldQueryBaker.Bake(
            blockers: [],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f,
                TopY: -1e9f
            ),]
        );

        Assert.True(condition: deep.HasHeightfield);
        Assert.True(condition: deep.TryHeightRaw(
            cellIndex: 0,
            heightRaw: out var heightRaw
        ));
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: -1e9).Value,
            actual: heightRaw
        );
    }

    [Fact]
    public void BakeRefusesAnInvertedGridBound() {
        Assert.Throws<ArgumentException>(
            paramName: "maxX",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: 0f,
                maxZ: 1f,
                minX: 1f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Equal(
            expected: 4,
            actual: WorldQueryBaker.Bake(
                blockers: [],
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            ).Width
        );
    }

    [Fact]
    public void BakeRefusesAnInvertedRectangle() {
        Assert.Throws<ArgumentException>(testCode: () => WorldQueryBaker.Bake(
            blockers: [new WorldQueryBlockerInput(
                MaxX: 0f,
                MaxZ: 1f,
                MinX: 1f,
                MinZ: 0f
            ),],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: []
        ));
        Assert.True(condition: WorldQueryBaker.Bake(
            blockers: [new WorldQueryBlockerInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f
            ),],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: []
        ).HasBlocked);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void BakeRefusesANonFiniteTerrainHeight(float topY) {
        Assert.Throws<ArgumentException>(testCode: () => WorldQueryBaker.Bake(
            blockers: [],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f,
                TopY: topY
            ),]
        ));
        Assert.True(condition: WorldQueryBaker.Bake(
            blockers: [],
            maxX: 1f,
            maxZ: 1f,
            minX: 0f,
            minZ: 0f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 1f,
                MaxZ: 1f,
                MinX: 0f,
                MinZ: 0f,
                TopY: 3f
            ),]
        ).HasHeightfield);
    }

    [Fact]
    public void BakeRefusesANonFiniteGridBound() {
        Assert.Throws<ArgumentException>(
            paramName: "maxX",
            testCode: () => WorldQueryBaker.Bake(
                blockers: [],
                maxX: float.NaN,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            )
        );
        Assert.Equal(
            expected: 4,
            actual: WorldQueryBaker.Bake(
                blockers: [],
                maxX: 1f,
                maxZ: 1f,
                minX: 0f,
                minZ: 0f,
                terrain: []
            ).Width
        );
    }

    [Fact]
    public void CastsAgainstTheSameArtifactAreBitIdentical() {
        var artifact = Artifact(
            blocked: [(3, 2), (5, 5), (1, 6),],
            height: 8,
            width: 8
        );
        var first = new BakedWorldQuery(artifact: artifact);
        var second = new BakedWorldQuery(artifact: artifact);
        var random = new Random(Seed: 4816);

        for (var index = 0; (index < 512); index++) {
            var origin = Position(
                x: ((random.NextDouble() * 2.0) - 0.25),
                y: ((random.NextDouble() * 2.0) - 1.0),
                z: ((random.NextDouble() * 2.0) - 0.25)
            );
            var direction = Direction(
                x: ((random.NextDouble() * 2.0) - 1.0),
                y: ((random.NextDouble() * 2.0) - 1.0),
                z: ((random.NextDouble() * 2.0) - 1.0)
            );
            var radius = Fixed(value: (random.NextDouble() * 0.5));
            var found = first.SphereCast(
                dir: direction,
                hit: out var expected,
                maxDist: Fixed(value: 3.0),
                origin: origin,
                radius: radius
            );
            var repeated = second.SphereCast(
                dir: direction,
                hit: out var actual,
                maxDist: Fixed(value: 3.0),
                origin: origin,
                radius: radius
            );

            Assert.Equal(
                actual: repeated,
                expected: found
            );
            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }

    [Fact]
    public void LineOfSightBlocksADegenerateSegmentInsideABlockedCell() {
        var query = BlockedColumnQuery();

        Assert.False(condition: query.LineOfSight(
            from: Position(
                x: 0.6,
                z: 0.125
            ),
            to: Position(
                x: 0.6,
                z: 0.125
            )
        ));
        Assert.True(condition: query.LineOfSight(
            from: Position(
                x: 0.1,
                z: 0.125
            ),
            to: Position(
                x: 0.1,
                z: 0.125
            )
        ));
    }

    [Fact]
    public void LineOfSightBlocksASegmentShorterThanOneCell() {
        var query = BlockedColumnQuery();

        Assert.False(condition: query.LineOfSight(
            from: Position(
                x: 0.55,
                z: 0.125
            ),
            to: Position(
                x: 0.70,
                z: 0.125
            )
        ));
        Assert.True(condition: query.LineOfSight(
            from: Position(
                x: 0.05,
                z: 0.125
            ),
            to: Position(
                x: 0.20,
                z: 0.125
            )
        ));
    }

    [Fact]
    public void LineOfSightBlocksWhenOnlyItsEndpointLandsInABlocker() {
        var query = BlockedColumnQuery();

        Assert.False(condition: query.LineOfSight(
            from: Position(
                x: 0.10,
                z: 0.125
            ),
            to: Position(
                x: 0.55,
                z: 0.125
            )
        ));
        Assert.True(condition: query.LineOfSight(
            from: Position(
                x: 0.10,
                z: 0.125
            ),
            to: Position(
                x: 0.45,
                z: 0.125
            )
        ));
    }

    [Fact]
    public void LineOfSightUsesTheHeightfieldWhenTheBlockedLayerIsAbsent() {
        var artifact = WorldQueryBaker.Bake(
            blockers: [],
            maxX: 5f,
            maxZ: 5f,
            minX: -5f,
            minZ: -5f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 0.5f,
                MaxZ: 5f,
                MinX: 0f,
                MinZ: -5f,
                TopY: 1f
            ),]
        );
        var query = new BakedWorldQuery(artifact: artifact);

        Assert.False(condition: query.Capabilities.HasBlocked);
        Assert.True(condition: query.Capabilities.HasHeightfield);
        Assert.False(condition: query.LineOfSight(
            from: Position(
                x: -2.0,
                y: 0.5,
                z: 0.0
            ),
            to: Position(
                x: 2.0,
                y: 0.5,
                z: 0.0
            )
        ));
        Assert.True(condition: query.LineOfSight(
            from: Position(
                x: -2.0,
                y: 2.0,
                z: 0.0
            ),
            to: Position(
                x: 2.0,
                y: 2.0,
                z: 0.0
            )
        ));
    }

    [Fact]
    public void LineOfSightReadsBothEndpointsInWorldSpace() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));

        Assert.False(condition: query.LineOfSight(
            from: Position(
                x: -1.0,
                y: -0.5,
                z: 0.0
            ),
            to: Position(
                x: 1.0,
                y: -0.5,
                z: 0.0
            )
        ));
        // The same pair of local offsets one hierarchy cell out is a segment a million units from the grid.
        Assert.True(condition: query.LineOfSight(
            from: CellPosition(
                cellX: 1L,
                cellZ: 0L,
                x: -1.0,
                y: -0.5,
                z: 0.0
            ),
            to: CellPosition(
                cellX: 1L,
                cellZ: 0L,
                x: 1.0,
                y: -0.5,
                z: 0.0
            )
        ));
    }

    [Fact]
    public void OverlapClampsTheDiscToTheArtifactRatherThanBailingOnAnOutsideCenter() {
        var query = Query(
            blocked: [(0, 0),],
            height: 1,
            width: 2
        );

        Assert.True(condition: query.Overlap(
            center: Position(
                x: -0.05,
                z: 0.125
            ),
            radius: Fixed(value: 0.5)
        ));
        Assert.False(condition: query.Overlap(
            center: Position(
                x: -2.0,
                z: 0.125
            ),
            radius: Fixed(value: 0.5)
        ));
    }

    [Fact]
    public void OverlapTreatsABlockedCellAsAreaRatherThanAsItsCenterPoint() {
        var query = TwoCellQuery();

        var overlaps = query.Overlap(
            center: Position(
                x: 0.30,
                z: 0.125
            ),
            radius: FixedQ4816.Zero
        );

        Assert.True(condition: overlaps);
    }

    [Fact]
    public void OverlapConsultsTheHeightfieldNotOnlyTheBlockedLayer() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));

        Assert.False(condition: query.Capabilities.HasBlocked);
        Assert.True(condition: query.Overlap(
            center: Position(
                x: 0.0,
                y: -0.5,
                z: 0.0
            ),
            radius: Fixed(value: 0.1)
        ));
        Assert.True(condition: query.Overlap(
            center: Position(
                x: 0.0,
                y: 0.05,
                z: 0.0
            ),
            radius: Fixed(value: 0.1)
        ));
        Assert.False(condition: query.Overlap(
            center: Position(
                x: 0.0,
                y: 0.5,
                z: 0.0
            ),
            radius: Fixed(value: 0.1)
        ));
    }

    [Fact]
    public void OverlapIsNeverLooserThanTheSweepThatSharesItsRadius() {
        var artifact = WorldQueryBaker.Bake(
            blockers: [new WorldQueryBlockerInput(
                MaxX: 1.25f,
                MaxZ: 0.75f,
                MinX: 0.5f,
                MinZ: 0.25f
            ),],
            maxX: 2f,
            maxZ: 2f,
            minX: -2f,
            minZ: -2f,
            terrain: [new WorldQueryTerrainInput(
                MaxX: 0f,
                MaxZ: 2f,
                MinX: -2f,
                MinZ: -2f,
                TopY: 0.25f
            ),]
        );
        var query = new BakedWorldQuery(artifact: artifact);
        var random = new Random(Seed: 1607);
        var overlaps = 0;

        for (var index = 0; (index < 4096); index++) {
            var center = Position(
                x: ((random.NextDouble() * 5.0) - 2.5),
                y: ((random.NextDouble() * 2.0) - 1.0),
                z: ((random.NextDouble() * 5.0) - 2.5)
            );
            var radius = Fixed(value: (random.NextDouble() * 0.5));

            if (!query.Overlap(
                center: center,
                radius: radius
            )) {
                continue;
            }

            overlaps++;

            Assert.True(condition: query.SphereCast(
                dir: Direction(
                    x: 1.0,
                    y: 0.0,
                    z: 0.0
                ),
                hit: out var hit,
                maxDist: FixedQ4816.Epsilon,
                origin: center,
                radius: radius
            ));
            Assert.Equal(
                expected: FixedQ4816.Zero,
                actual: hit.Distance
            );
        }

        Assert.InRange(
            actual: overlaps,
            high: 4096,
            low: 1
        );
    }

    [Fact]
    public void OverlapMeasuresTheCornerExactlyWhereTheSweepDilatesIt() {
        var query = Query(
            blocked: [(2, 1),],
            height: 2,
            width: 4
        );
        // 0.1 from the blocked cell's corner on each axis: inside the sweep's axis-aligned dilation, outside the
        // radius-0.12 ball whose true corner distance is 0.1414.
        var center = Position(
            x: 0.4,
            y: 0.0,
            z: 0.15
        );
        var radius = Fixed(value: 0.12);

        Assert.False(condition: query.Overlap(
            center: center,
            radius: radius
        ));
        Assert.True(condition: query.SphereCast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out _,
            maxDist: FixedQ4816.Epsilon,
            origin: center,
            radius: radius
        ));
        Assert.True(condition: query.Overlap(
            center: center,
            radius: Fixed(value: 0.15)
        ));
    }

    [Fact]
    public void OverlapRefusesARadiusPastTheCellCeiling() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var ceiling = query.MaxRadius;

        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: (BakedWorldQuery.MaxRadiusCells * 0.25)),
            actual: ceiling
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "radius",
            testCode: () => query.Overlap(
                center: Position(
                    x: 0.0,
                    z: 0.0
                ),
                radius: FixedQ4816.FromRawBits(value: (ceiling.Value + 1L))
            )
        );
        Assert.True(condition: query.Overlap(
            center: Position(
                x: 0.0,
                y: -0.5,
                z: 0.0
            ),
            radius: ceiling
        ));
    }

    [Fact]
    public void QueriesAnswerAgainstAnArtifactCarryingNeitherLayer() {
        var query = new BakedWorldQuery(artifact: new WorldQueryArtifact(
            blocked: [],
            cellSizeRaw: CellSizeRaw,
            height: 4,
            heightRaw: [],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 4
        ));

        Assert.False(condition: query.TryGroundHeight(
            groundY: out _,
            position: Position(
                x: 0.5,
                z: 0.5
            ),
            probeDown: Fixed(value: 9.0),
            probeUp: Fixed(value: 9.0)
        ));
        Assert.False(condition: query.Raycast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out _,
            maxDist: Fixed(value: 1.0),
            origin: Position(
                x: 0.1,
                z: 0.1
            )
        ));
        Assert.True(condition: query.LineOfSight(
            from: Position(
                x: 0.1,
                z: 0.1
            ),
            to: Position(
                x: 0.9,
                z: 0.9
            )
        ));
    }

    [Fact]
    public void QueriesReadWorldSpaceNotTheCellLocalOffset() {
        var ground = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var blocked = Query(
            blocked: [(1, 0),],
            height: 1,
            width: 2
        );
        var down = Direction(
            x: 0.0,
            y: -1.0,
            z: 0.0
        );

        Assert.True(condition: ground.TryGroundHeight(
            groundY: out _,
            position: Position(
                x: 0.0,
                z: 0.0
            ),
            probeDown: Fixed(value: 9.0),
            probeUp: Fixed(value: 9.0)
        ));
        Assert.False(condition: ground.TryGroundHeight(
            groundY: out _,
            position: CellPosition(
                cellX: 1L,
                cellZ: 0L,
                x: 0.0,
                y: 0.0,
                z: 0.0
            ),
            probeDown: Fixed(value: 9.0),
            probeUp: Fixed(value: 9.0)
        ));
        Assert.True(condition: ground.Raycast(
            dir: down,
            hit: out _,
            maxDist: Fixed(value: 2.0),
            origin: Position(
                x: 0.0,
                y: 1.0,
                z: 0.0
            )
        ));
        Assert.False(condition: ground.Raycast(
            dir: down,
            hit: out _,
            maxDist: Fixed(value: 2.0),
            origin: CellPosition(
                cellX: 0L,
                cellZ: 1L,
                x: 0.0,
                y: 1.0,
                z: 0.0
            )
        ));
        Assert.True(condition: blocked.Overlap(
            center: Position(
                x: 0.3,
                z: 0.125
            ),
            radius: FixedQ4816.Zero
        ));
        Assert.False(condition: blocked.Overlap(
            center: CellPosition(
                cellX: 1L,
                cellZ: 1L,
                x: 0.3,
                y: 0.0,
                z: 0.125
            ),
            radius: FixedQ4816.Zero
        ));
    }

    [Fact]
    public void QueriesRefuseAPositionTheWorldCarrierCannotHold() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var unreachable = CellPosition(
            cellX: (1L << 40),
            cellZ: 0L,
            x: 0.0,
            y: 0.0,
            z: 0.0
        );
        var reachable = CellPosition(
            cellX: (1L << 20),
            cellZ: 0L,
            x: 0.0,
            y: 0.0,
            z: 0.0
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "center",
            testCode: () => query.Overlap(
                center: unreachable,
                radius: FixedQ4816.Zero
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "origin",
            testCode: () => query.Raycast(
                dir: Direction(
                    x: 0.0,
                    y: -1.0,
                    z: 0.0
                ),
                hit: out _,
                maxDist: Fixed(value: 1.0),
                origin: unreachable
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "to",
            testCode: () => query.LineOfSight(
                from: Position(
                    x: 0.0,
                    z: 0.0
                ),
                to: unreachable
            )
        );
        Assert.False(condition: query.Overlap(
            center: reachable,
            radius: FixedQ4816.Zero
        ));
    }

    [Fact]
    public void RaycastCrossesEveryCellTheSegmentTouchesNotOnlyItsSamples() {
        var query = Query(
            blocked: [(3, 2),],
            height: 8,
            width: 8
        );

        // The segment occupies the blocked cell for a 0.0112-unit corner sliver strictly between two consecutive
        // cell-size samples, so any point-sampled march reports it clear.
        var found = query.Raycast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.5
            ),
            hit: out var hit,
            maxDist: Fixed(value: 2.0),
            origin: Position(
                x: 0.01,
                z: 0.01
            )
        );

        Assert.True(condition: found);
        Assert.InRange(
            actual: ((double)hit.Distance),
            high: 1.096,
            low: 1.094
        );
        Assert.False(condition: query.Raycast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.05
            ),
            hit: out _,
            maxDist: Fixed(value: 2.0),
            origin: Position(
                x: 0.01,
                z: 0.01
            )
        ));
    }

    [Fact]
    public void RaycastDoesNotReportACellBeyondMaximumDistance() {
        var query = TwoCellQuery();

        var hit = query.Raycast(
            origin: Position(
                x: 0.0,
                z: 0.125
            ),
            dir: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            maxDist: FixedQ4816.FromDouble(value: 0.02),
            hit: out _
        );

        Assert.False(condition: hit);
    }

    [Fact]
    public void RaycastStillReportsAHitExactlyAtMaximumDistance() {
        var query = TwoCellQuery();

        var found = query.Raycast(
            origin: Position(
                x: 0.0,
                z: 0.125
            ),
            dir: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            maxDist: FixedQ4816.FromDouble(value: 0.25),
            hit: out var hit
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: 0.25),
            actual: hit.Distance
        );
    }

    [Fact]
    public void SphereCastGroundsAtTheSphereSurfaceNotAtItsCenter() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var down = Direction(
            x: 0.0,
            y: -1.0,
            z: 0.0
        );
        var origin = Position(
            x: 0.0,
            y: 1.0,
            z: 0.0
        );

        var swept = query.SphereCast(
            dir: down,
            hit: out var sphere,
            maxDist: Fixed(value: 2.0),
            origin: origin,
            radius: Fixed(value: 0.5)
        );
        var cast = query.Raycast(
            dir: down,
            hit: out var ray,
            maxDist: Fixed(value: 2.0),
            origin: origin
        );

        Assert.True(condition: swept);
        Assert.Equal(
            expected: Fixed(value: 0.5),
            actual: sphere.Distance
        );
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: sphere.Point.Local.Y
        );
        Assert.True(condition: cast);
        Assert.Equal(
            expected: Fixed(value: 1.0),
            actual: ray.Distance
        );
    }

    [Fact]
    public void SphereCastRefusesARadiusPastTheCellCeiling() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var down = Direction(
            x: 0.0,
            y: -1.0,
            z: 0.0
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "radius",
            testCode: () => query.SphereCast(
                dir: down,
                hit: out _,
                maxDist: Fixed(value: 2.0),
                origin: Position(
                    x: 0.0,
                    y: 1.0,
                    z: 0.0
                ),
                radius: FixedQ4816.FromRawBits(value: (query.MaxRadius.Value + 1L))
            )
        );
        Assert.True(condition: query.SphereCast(
            dir: down,
            hit: out _,
            maxDist: Fixed(value: 2.0),
            origin: Position(
                x: 0.0,
                y: 1.0,
                z: 0.0
            ),
            radius: query.MaxRadius
        ));
    }

    [Fact]
    public void SphereCastRefusesGroundContactBeyondMaximumDistance() {
        var query = new BakedWorldQuery(artifact: GroundPlane(topY: 0f));
        var down = Direction(
            x: 0.0,
            y: -1.0,
            z: 0.0
        );
        var origin = Position(
            x: 0.0,
            y: 1.0,
            z: 0.0
        );

        Assert.False(condition: query.SphereCast(
            dir: down,
            hit: out _,
            maxDist: Fixed(value: 0.4),
            origin: origin,
            radius: Fixed(value: 0.5)
        ));
        Assert.True(condition: query.SphereCast(
            dir: down,
            hit: out _,
            maxDist: Fixed(value: 0.6),
            origin: origin,
            radius: Fixed(value: 0.5)
        ));
    }

    [Fact]
    public void SphereCastReportsContactWhenTheSweepStartsAlreadyOverlapping() {
        var query = Query(
            blocked: [(0, 0),],
            height: 1,
            width: 2
        );

        var found = query.SphereCast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out var hit,
            maxDist: Fixed(value: 0.20),
            origin: Position(
                x: -0.30,
                z: 0.125
            ),
            radius: Fixed(value: 0.5)
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: hit.Distance
        );
        Assert.False(condition: query.SphereCast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out _,
            maxDist: Fixed(value: 0.20),
            origin: Position(
                x: -2.0,
                z: 0.125
            ),
            radius: Fixed(value: 0.5)
        ));
    }

    [Fact]
    public void SphereCastReportsTheSweptSpheresFirstContactNotItsMarchingCenter() {
        var query = BlockedColumnQuery();

        // The blocker spans x in [0.5, 0.75]; a radius-0.25 sphere starting at x = 0.1 first touches it after
        // 0.15 units of sweep, at the box face, not after a whole cell-size step at the sphere's own center.
        var found = query.SphereCast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out var hit,
            maxDist: Fixed(value: 1.0),
            origin: Position(
                x: 0.10,
                z: 0.125
            ),
            radius: Fixed(value: 0.25)
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: Fixed(value: 0.15),
            actual: hit.Distance
        );
        Assert.Equal(
            expected: Fixed(value: 0.5),
            actual: hit.Point.Local.X
        );
        Assert.False(condition: query.SphereCast(
            dir: Direction(
                x: 1.0,
                y: 0.0,
                z: 0.0
            ),
            hit: out _,
            maxDist: Fixed(value: 0.14),
            origin: Position(
                x: 0.10,
                z: 0.125
            ),
            radius: Fixed(value: 0.25)
        ));
    }

    [Fact]
    public void TheBlockedWordCountCoversTheWidestGridAndRefusesANegativeCount() {
        Assert.Equal(
            expected: 33554432,
            actual: WorldQueryArtifact.BlockedWordCount(cellCount: int.MaxValue)
        );
        Assert.Equal(
            expected: 1,
            actual: WorldQueryArtifact.BlockedWordCount(cellCount: 1)
        );
        Assert.Equal(
            expected: 0,
            actual: WorldQueryArtifact.BlockedWordCount(cellCount: 0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "cellCount",
            testCode: () => WorldQueryArtifact.BlockedWordCount(cellCount: -1)
        );
    }

    [Fact]
    public void TheConstructorRefusesABlockedLayerSettingAPaddingBit() {
        Assert.Throws<ArgumentException>(
            paramName: "blocked",
            testCode: () => new WorldQueryArtifact(
                blocked: [(1UL << 63),],
                cellSizeRaw: CellSizeRaw,
                height: 1,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 1
            )
        );

        var padded = new WorldQueryArtifact(
            blocked: [1UL,],
            cellSizeRaw: CellSizeRaw,
            height: 1,
            heightRaw: [],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 1
        );

        Assert.True(condition: padded.HasBlocked);
        Assert.True(condition: padded.IsBlockedCell(cellIndex: 0));
        Assert.False(condition: padded.IsBlockedCell(cellIndex: 63));
        Assert.Equal(
            expected: 1,
            actual: padded.CellCount
        );
    }

    [Fact]
    public void TheConstructorRefusesABlockedLayerThatContradictsTheGrid() {
        Assert.Throws<ArgumentException>(
            paramName: "blocked",
            testCode: () => new WorldQueryArtifact(
                blocked: [0UL, 0UL,],
                cellSizeRaw: CellSizeRaw,
                height: 2,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 2
            )
        );
        Assert.Equal(
            expected: 1,
            actual: new WorldQueryArtifact(
                blocked: [0UL,],
                cellSizeRaw: CellSizeRaw,
                height: 2,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 2
            ).Blocked.Length
        );
    }

    [Fact]
    public void TheConstructorRefusesAHeightLayerThatContradictsTheGrid() {
        Assert.Throws<ArgumentException>(
            paramName: "heightRaw",
            testCode: () => new WorldQueryArtifact(
                blocked: [],
                cellSizeRaw: CellSizeRaw,
                height: 1000,
                heightRaw: new long[2],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 1000
            )
        );
        Assert.Equal(
            expected: 4,
            actual: new WorldQueryArtifact(
                blocked: [],
                cellSizeRaw: CellSizeRaw,
                height: 2,
                heightRaw: new long[4],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 2
            ).HeightRaw.Length
        );
    }

    [Fact]
    public void TheConstructorRefusesANonPositiveCellSize() {
        Assert.Throws<ArgumentOutOfRangeException>(
            paramName: "cellSizeRaw",
            testCode: () => new WorldQueryArtifact(
                blocked: [],
                cellSizeRaw: 0L,
                height: 1,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 1
            )
        );
        Assert.Equal(
            expected: CellSizeRaw,
            actual: new WorldQueryArtifact(
                blocked: [],
                cellSizeRaw: CellSizeRaw,
                height: 1,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 1
            ).CellSizeRaw
        );
    }

    [Fact]
    public void TheConstructorRefusesAnAxisWhoseFarEdgeOverflowsTheCarrier() {
        Assert.Throws<ArgumentException>(
            paramName: "width",
            testCode: () => new WorldQueryArtifact(
                blocked: [0UL,],
                cellSizeRaw: long.MaxValue,
                height: 1,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 2
            )
        );

        Assert.Equal(
            expected: long.MaxValue,
            actual: new WorldQueryArtifact(
                blocked: [0UL,],
                cellSizeRaw: long.MaxValue,
                height: 1,
                heightRaw: [],
                originXRaw: 0L,
                originZRaw: 0L,
                width: 1
            ).CellSizeRaw
        );
    }

    [Fact]
    public void ExtremeButRepresentableArtifactsDoNotOverflowQueryArithmetic() {
        var widestCell = new BakedWorldQuery(artifact: new WorldQueryArtifact(
            blocked: [1UL,],
            cellSizeRaw: long.MaxValue,
            height: 1,
            heightRaw: [],
            originXRaw: 0L,
            originZRaw: 0L,
            width: 1
        ));

        Assert.True(condition: widestCell.SphereCast(
            dir: Direction(x: 1.0, y: 0.0, z: 0.0),
            hit: out var hit,
            maxDist: FixedQ4816.Epsilon,
            origin: FixedPosition.Zero,
            radius: FixedQ4816.FromRawBits(value: long.MaxValue)
        ));
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: hit.Distance
        );

        const long largeCell = 4_000_000_000_000_000_000L;
        var compensatedOrigin = long.MinValue;
        var lastCellCenter = ((long)(((Int128)compensatedOrigin) + (3 * ((Int128)largeCell)) + (largeCell / 2)));
        var compensated = new BakedWorldQuery(artifact: new WorldQueryArtifact(
            blocked: [(1UL << 3),],
            cellSizeRaw: largeCell,
            height: 1,
            heightRaw: [],
            originXRaw: compensatedOrigin,
            originZRaw: 0L,
            width: 4
        ));

        Assert.True(condition: compensated.Overlap(
            center: FixedPosition.FromLocal(local: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: lastCellCenter),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.FromRawBits(value: 1L)
            )),
            radius: FixedQ4816.Zero
        ));
    }
}
