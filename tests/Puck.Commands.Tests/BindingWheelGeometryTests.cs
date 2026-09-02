using System.Numerics;

using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingWheelGeometryTests {
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [Theory]
    public void EverySectorCentreAndBothOfItsInteriorEdgesSelectThatSector(int sectorCount) {
        var span = (Math.Tau / sectorCount);

        foreach (var style in Styles()) {
            for (var sector = 0; (sector < sectorCount); sector++) {
                var centre = (sector * span);

                foreach (var offset in new[] { -0.49d, -0.25d, 0d, 0.25d, 0.49d, }) {
                    var selection = BindingWheelGeometry.SelectDirection(
                        sectorCount: sectorCount,
                        style: style,
                        vector: VectorFor(
                            relative: (centre + (offset * span)),
                            sectorCount: sectorCount,
                            style: style
                        )
                    );

                    Assert.Equal(expected: BindingWheelSelectionOutcome.Sector, actual: selection.Outcome);
                    Assert.Equal(expected: sector, actual: selection.Sector);
                }
            }
        }
    }
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [Theory]
    public void CrossingASectorBoundaryAdvancesExactlyOneSector(int sectorCount) {
        var span = (Math.Tau / sectorCount);

        foreach (var style in Styles()) {
            for (var sector = 0; (sector < sectorCount); sector++) {
                var boundary = ((sector * span) + (span * 0.5d));
                var below = BindingWheelGeometry.SelectDirection(
                    sectorCount: sectorCount,
                    style: style,
                    vector: VectorFor(
                        relative: (boundary - (span * 0.01d)),
                        sectorCount: sectorCount,
                        style: style
                    )
                );
                var above = BindingWheelGeometry.SelectDirection(
                    sectorCount: sectorCount,
                    style: style,
                    vector: VectorFor(
                        relative: (boundary + (span * 0.01d)),
                        sectorCount: sectorCount,
                        style: style
                    )
                );

                Assert.Equal(expected: sector, actual: below.Sector);
                Assert.Equal(expected: ((sector + 1) % sectorCount), actual: above.Sector);
            }
        }
    }
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [Theory]
    public void OneFullTurnVisitsEverySectorOnceWithExactlyOneBoundaryEach(int sectorCount) {
        const int SampleCount = 3_600;

        foreach (var style in Styles()) {
            var seen = new HashSet<int>();
            var sectors = new int[SampleCount];
            var transitions = 0;

            for (var sample = 0; (sample < SampleCount); sample++) {
                var selection = BindingWheelGeometry.SelectDirection(
                    sectorCount: sectorCount,
                    style: style,
                    vector: VectorFor(
                        relative: ((sample * Math.Tau) / SampleCount),
                        sectorCount: sectorCount,
                        style: style
                    )
                );

                Assert.Equal(expected: BindingWheelSelectionOutcome.Sector, actual: selection.Outcome);
                Assert.InRange(actual: selection.Sector, high: (sectorCount - 1), low: 0);
                _ = seen.Add(item: selection.Sector);
                sectors[sample] = selection.Sector;
            }

            for (var sample = 0; (sample < SampleCount); sample++) {
                if (sectors[sample] != sectors[((sample + 1) % SampleCount)]) {
                    transitions++;
                }
            }

            Assert.Equal(expected: sectorCount, actual: seen.Count);
            Assert.Equal(actual: transitions, expected: sectorCount);
        }
    }
    [Fact]
    public void SelectionAgreesWithTheFloatAtan2ReadingAwayFromBoundaries() {
        // The reproducible implementation is not a behavior change away from the ties it exists to pin down: the
        // decision it makes at a sector's centre and quarter-offsets is the one the previous MathF.Atan2 reading
        // made, so a re-aimed radial still means what its author drew.
        for (var sectorCount = 2; (sectorCount <= 8); sectorCount++) {
            var span = (Math.Tau / sectorCount);

            foreach (var style in Styles()) {
                for (var sector = 0; (sector < sectorCount); sector++) {
                    foreach (var offset in new[] { -0.25d, 0d, 0.25d, }) {
                        var vector = VectorFor(
                            relative: ((sector * span) + (offset * span)),
                            sectorCount: sectorCount,
                            style: style
                        );

                        Assert.Equal(
                            actual: BindingWheelGeometry.SelectDirection(
                                sectorCount: sectorCount,
                                style: style,
                                vector: vector
                            ).Sector,
                            expected: ReferenceSector(
                                sectorCount: sectorCount,
                                style: style,
                                vector: vector
                            )
                        );
                    }
                }
            }
        }
    }
    [Fact]
    public void ScalingAVectorByAPowerOfTwoNeverMovesItsSector() {
        var style = new BindingWheelStyleDefinition(SectorOffset: 0.1833f);

        for (var sample = 0; (sample < 360); sample++) {
            var vector = VectorFor(
                relative: ((sample * Math.Tau) / 360d),
                sectorCount: 6,
                style: style
            );
            var expected = BindingWheelGeometry.SelectDirection(
                sectorCount: 6,
                style: style,
                vector: vector
            ).Sector;

            foreach (var exponent in new[] { -60, -12, 12, 60, }) {
                Assert.Equal(
                    actual: BindingWheelGeometry.SelectDirection(
                        sectorCount: 6,
                        style: style,
                        vector: new Vector2(
                            x: MathF.ScaleB(
                                n: exponent,
                                x: vector.X
                            ),
                            y: MathF.ScaleB(
                                n: exponent,
                                x: vector.Y
                            )
                        )
                    ).Sector,
                    expected: expected
                );
            }
        }
    }
    [Fact]
    public void ADirectionlessVectorIsTheDeadZoneAndNeverSectorZero() {
        var style = new BindingWheelStyleDefinition();

        foreach (var vector in new[] {
            Vector2.Zero,
            new Vector2(x: 0f, y: -0f),
            new Vector2(x: float.NaN, y: 1f),
            new Vector2(x: 1f, y: float.NaN),
            new Vector2(x: float.PositiveInfinity, y: 0f),
            new Vector2(x: 0f, y: float.NegativeInfinity),
        }) {
            var direction = BindingWheelGeometry.SelectDirection(
                sectorCount: 4,
                style: style,
                vector: vector
            );

            Assert.Equal(expected: BindingWheelSelectionOutcome.DeadZone, actual: direction.Outcome);
            Assert.Equal(expected: -1, actual: direction.Sector);
        }
    }
    [Fact]
    public void ANonFiniteAxisOrSpatialVectorFallsToTheDeadZoneRatherThanASector() {
        var style = new BindingWheelStyleDefinition();
        var vector = new Vector2(
            x: float.NaN,
            y: 1f
        );

        Assert.Equal(
            actual: BindingWheelGeometry.SelectAxis(
                sectorCount: 4,
                style: style,
                vector: vector
            ).Outcome,
            expected: BindingWheelSelectionOutcome.DeadZone
        );
        Assert.Equal(
            actual: BindingWheelGeometry.SelectSpatial(
                mode: BindingWheelSpatialSelectionMode.Angle,
                ringCount: 1,
                sectorCount: 4,
                style: style,
                unit: 100f,
                vector: vector
            ).Outcome,
            expected: BindingWheelSelectionOutcome.DeadZone
        );
    }
    [Fact]
    public void ADisabledSpatialPolicyRefusesBeforeAnyGeometryIsConsidered() {
        var selection = BindingWheelGeometry.SelectSpatial(
            mode: BindingWheelSpatialSelectionMode.Disabled,
            ringCount: 1,
            sectorCount: 4,
            style: new BindingWheelStyleDefinition(PointerSelection: BindingWheelSpatialSelectionMode.Disabled),
            unit: 100f,
            vector: new Vector2(
                x: 500f,
                y: 0f
            )
        );

        Assert.Equal(expected: BindingWheelSelectionOutcome.Disabled, actual: selection.Outcome);
        Assert.Equal(expected: -1, actual: selection.Sector);
        Assert.Equal(expected: "disabled", actual: selection.Reason);
    }
    [Fact]
    public void TheSelectionGraceWindowConvertsToWholeEngineTicksAndDisablesHonestly() {
        Assert.Equal(expected: 25_200UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: 0.5f,
            ticksPerSecond: 50_400UL
        ));
        Assert.Equal(expected: 50_400UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: 1f,
            ticksPerSecond: 50_400UL
        ));
        // Truncated toward zero: a window shorter than one tick is no window at all.
        Assert.Equal(expected: 0UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: 0.000_001f,
            ticksPerSecond: 50_400UL
        ));
        Assert.Equal(expected: 0UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: 0f,
            ticksPerSecond: 50_400UL
        ));
        Assert.Equal(expected: 0UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: -1f,
            ticksPerSecond: 50_400UL
        ));
        Assert.Equal(expected: 0UL, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: float.NaN,
            ticksPerSecond: 50_400UL
        ));
        Assert.Equal(expected: ulong.MaxValue, actual: BindingWheelGeometry.SelectionGraceTicks(
            seconds: float.MaxValue,
            ticksPerSecond: 50_400UL
        ));
    }

    // The previous MathF.Atan2 reading of the SAME sector rule, kept here as the oracle the reproducible
    // implementation must still agree with everywhere except the boundary ties it exists to pin down.
    private static int ReferenceSector(Vector2 vector, int sectorCount, BindingWheelStyleDefinition style) {
        var clockwiseAngle = MathF.Atan2(
            x: -vector.Y,
            y: vector.X
        );

        if (clockwiseAngle < 0f) {
            clockwiseAngle += MathF.Tau;
        }

        var span = (MathF.Tau / sectorCount);
        var relative = ((clockwiseAngle - (style.SectorOffset * span)) + (span * 0.5f));

        relative = (((relative % MathF.Tau) + MathF.Tau) % MathF.Tau);

        return (((int)(relative / span)) % sectorCount);
    }
    // The authored seam placements a wheel may carry: SectorOffset is where sector zero's CENTRE sits, measured in
    // whole sectors clockwise from north, on [0, 1).
    private static BindingWheelStyleDefinition[] Styles() {
        return [
            new BindingWheelStyleDefinition(),
            new BindingWheelStyleDefinition(SectorOffset: 0.125f),
            new BindingWheelStyleDefinition(SectorOffset: 0.25f),
            new BindingWheelStyleDefinition(SectorOffset: 0.5f),
            new BindingWheelStyleDefinition(SectorOffset: 0.75f),
            new BindingWheelStyleDefinition(SectorOffset: 0.9f),
        ];
    }
    // The screen-space vector whose OFFSET-RELATIVE clockwise angle is `relative`: sector zero's centre sits
    // `SectorOffset` sectors clockwise of twelve o'clock (0, -1), and the angle advances clockwise from there.
    private static Vector2 VectorFor(double relative, int sectorCount, BindingWheelStyleDefinition style) {
        var clockwise = ((style.SectorOffset * (Math.Tau / sectorCount)) + relative);

        return new Vector2(
            x: ((float)Math.Sin(a: clockwise)),
            y: ((float)(-Math.Cos(d: clockwise)))
        );
    }
}
