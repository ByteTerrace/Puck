using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>The wallpaper fold's soundness contract: a program folds only through a group whose fold is continuous
/// (<see cref="SdfWallpaperFold.IsContinuous"/>), and such a fold's field never reads past the geometry it renders. The
/// rendered set is every point whose folded image lies in the prototype; a brute-force grid search measures its true
/// distance, against which the folded field must never read long (no skip), and must read exactly when the prototype
/// lies in the fold's fixed region. A sphere-traced ray over the field stops at or before the first rendered point. A
/// group whose fold jumps fails the same sweep, and a program refuses it by name.</summary>
public sealed class SdfWallpaperFoldLawTests {
    private const float Grid = 0.01f;
    private const float Radius = 0.12f;
    private const float Tolerance = 0.001f;
    private const float Window = 1.2f;

    [Fact]
    public void TheMirrorGroupsAreTheContinuousOnes() => Assert.Equal(
        actual: Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous).ToHashSet(),
        expected: [SdfWallpaperGroup.Pmm, SdfWallpaperGroup.P4M, SdfWallpaperGroup.P3M1, SdfWallpaperGroup.P6M]
    );
    [Fact]
    public void EveryAcceptedGroupReadsNoLongerThanItsGeometryAndExactlyWhenContained() {
        var failures = new List<string>();

        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            failures.AddRange(collection: Sweep(group: group));
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    [Fact]
    public void TheSweepCatchesAGroupWhoseFoldJumps() => Assert.NotEmpty(collection: Sweep(group: SdfWallpaperGroup.P2));
    [Fact]
    public void AProgramRefusesEveryGroupWhoseFoldJumpsByName() {
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: static group => !SdfWallpaperFold.IsContinuous(group: group))) {
            var refusal = Assert.Throws<ArgumentException>(testCode: () => Program(cell: new Vector2(value: 4), group: group, offset: Vector3.Zero, radius: 0.25f));

            Assert.Contains(expectedSubstring: $"wallpaper group {group},", actualString: refusal.Message);
        }
    }
    [Fact]
    public void AProgramRefusesTheOffCentreP2Lattice() {
        // Four-unit cells whose sphere sits at the cell's x = 1: from x = -1.75 the folded field reads cell 0's copy 2.5
        // away while cell -1's lies 1.0 away.
        var refusal = Assert.Throws<ArgumentException>(testCode: () => Program(cell: new Vector2(value: 4), group: SdfWallpaperGroup.P2, offset: Vector3.UnitX, radius: 0.25f));

        Assert.Contains(expectedSubstring: "P2", actualString: refusal.Message);
    }
    [Fact]
    public void AProgramAcceptsEveryMirrorGroupWhateverItsContent() {
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            Assert.Equal(expected: 1f, actual: Program(cell: new Vector2(value: 1.7f), group: group, offset: new Vector3(x: 0.8f, y: 0, z: 0.7f), radius: 0.5f).StepScale);
        }
    }

    // Nine prototype offsets over the cell, six points each against the brute-force distance, and twelve rays each
    // marched over the folded field.
    private static List<string> Sweep(SdfWallpaperGroup group) {
        var failures = new List<string>();
        var cell = ((group is SdfWallpaperGroup.Pmm or SdfWallpaperGroup.P2) ? new Vector2(x: 1f, y: 0.8f) : Vector2.One);
        var limit = new Vector2(value: 50);

        for (var offsetX = -1; (offsetX <= 1); offsetX++) {
            for (var offsetY = -1; (offsetY <= 1); offsetY++) {
                var center = new Vector3(x: ((offsetX * 0.3f) * cell.X), y: 0.05f, z: ((offsetY * 0.3f) * cell.Y));
                var contained = Contained(cell: cell, center: new Vector2(x: center.X, y: center.Z), group: group, limit: limit);

                for (var index = 1; (index <= 6); index++) {
                    var point = new Vector3(x: Spread(index: index, multiplier: 0.7548777f), y: 0.1f, z: Spread(index: index, multiplier: 0.5698403f));
                    var field = Field(cell: cell, center: center, group: group, limit: limit, point: point);
                    var truth = Brute(cell: cell, center: center, group: group, limit: limit, point: point);

                    if (truth >= (0.5f * Window)) {
                        continue;
                    }
                    if (field > (truth + (2 * Grid))) {
                        failures.Add(item: $"{group} prototype {center}: the field reads {field} at {point}, past its geometry at {truth}");
                    } else if (contained && (MathF.Max(x: field, y: 0f) < (truth - (2 * Grid)))) {
                        failures.Add(item: $"{group} prototype {center}, contained: the field reads {field} at {point}, short of {truth}");
                    }
                }
                for (var index = 1; (index <= 12); index++) {
                    var origin = new Vector3(x: Spread(index: (index + 50), multiplier: 0.7548777f), y: 0.05f, z: Spread(index: (index + 50), multiplier: 0.5698403f));
                    var angle = (index * 2.399963f);
                    var direction = new Vector3(x: MathF.Cos(x: angle), y: 0, z: MathF.Sin(x: angle));
                    var first = FirstSolid(cell: cell, center: center, direction: direction, group: group, limit: limit, origin: origin);
                    var stop = March(cell: cell, center: center, direction: direction, group: group, limit: limit, origin: origin);

                    if ((first is { } hit) && (!(stop is { } found) || (found > ((hit + Tolerance) + 0.002f)))) {
                        failures.Add(item: $"{group} prototype {center}: the ray from {origin} along {direction} first meets its geometry at {hit}, the march stopped at {stop}");
                    }
                }
            }
        }

        return failures;

        static float Spread(int index, float multiplier) => ((((index * multiplier) % 1f) * 3f) - 1.5f);
    }
    // The folded field of a sphere prototype: the distance from the folded point to the sphere.
    private static float Field(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, Vector3 center, Vector3 point) {
        var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: new Vector2(x: point.X, y: point.Z));

        return (Vector3.Distance(value1: new Vector3(x: folded.X, y: point.Y, z: folded.Y), value2: center) - Radius);
    }
    // The distance to the rendered set, searched over a grid in the fold plane: at each grid point the prototype's
    // slice through the folded point is an interval along the plane's normal.
    private static float Brute(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, Vector3 center, Vector3 point) {
        var best = float.PositiveInfinity;
        var steps = ((int)(Window / Grid));

        for (var i = -steps; (i <= steps); i++) {
            for (var j = -steps; (j <= steps); j++) {
                var plane = new Vector2(x: (point.X + (i * Grid)), y: (point.Z + (j * Grid)));
                var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: plane);
                var inPlane = Vector2.DistanceSquared(value1: folded, value2: new Vector2(x: center.X, y: center.Z));

                if (inPlane > (Radius * Radius)) {
                    continue;
                }
                var halfHeight = MathF.Sqrt(x: ((Radius * Radius) - inPlane));
                var normal = MathF.Max(x: 0f, y: (MathF.Abs(x: (point.Y - center.Y)) - halfHeight));
                var across = Vector2.DistanceSquared(value1: plane, value2: new Vector2(x: point.X, y: point.Z));

                best = MathF.Min(x: best, y: MathF.Sqrt(x: (across + (normal * normal))));
            }
        }

        return best;
    }
    // Whether the prototype's disk in the fold plane lies where the fold leaves every point fixed, tested on a circle a
    // little wider than the disk.
    private static bool Contained(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, Vector2 center) {
        for (var index = 0; (index < 64); index++) {
            var angle = (index * (MathF.Tau / 64));
            var point = (center + ((Radius * 1.002f) * new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle))));

            if (Vector2.Distance(value1: SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: point), value2: point) > 1.0e-5f) {
                return false;
            }
        }

        return true;
    }
    private static float? March(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, Vector3 center, Vector3 origin, Vector3 direction) {
        var traveled = 0f;

        for (var step = 0; (step < 512); step++) {
            var field = Field(cell: cell, center: center, group: group, limit: limit, point: (origin + (direction * traveled)));

            if (field < Tolerance) {
                return traveled;
            }
            traveled += field;
            if (traveled > 4f) {
                break;
            }
        }

        return null;
    }
    // The first ray parameter within four units at which the ray stands in the rendered set, sampled every 0.002.
    private static float? FirstSolid(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, Vector3 center, Vector3 origin, Vector3 direction) {
        for (var t = 0f; (t <= 4f); t += 0.002f) {
            var point = (origin + (direction * t));
            var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: new Vector2(x: point.X, y: point.Z));

            if (Vector3.Distance(value1: new Vector3(x: folded.X, y: point.Y, z: folded.Y), value2: center) <= Radius) {
                return t;
            }
        }

        return null;
    }
    private static SdfProgram Program(SdfWallpaperGroup group, Vector2 cell, Vector3 offset, float radius) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.WallpaperFold(cell: cell, group: group, limit: new Vector2(value: 2)).Translate(offset: offset).Sphere(material: material, radius: radius);

        return builder.Build();
    }
}
