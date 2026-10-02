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
    // Every cell and limit a builder accepts, whatever the group: the folded point moves no farther than the point does.
    // Finite and fractional limits, and cells from ten micro-units to three thousand, are in the table, so a clamp that
    // collapses cells off their lattice, or a reciprocal that disagrees with the cell it divides, shows as a jump.
    [Fact]
    public void EveryCellAndLimitTheBuilderAcceptsFoldsContinuously() {
        var failures = new List<string>();
        var accepted = 0;

        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            foreach (var (cell, limit) in Configurations(group: group)) {
                if (!Accepts(cell: cell, group: group, limit: limit)) {
                    continue;
                }
                accepted++;
                var stretch = Stretch(cell: cell, group: group, limit: limit);

                if (stretch > 1.01f) {
                    failures.Add(item: $"{group} cell {cell} limit {limit} is accepted, and folds a pair {stretch} times farther apart than it was");
                }
            }
        }
        Assert.True(condition: (accepted > 20), userMessage: $"only {accepted} configurations were accepted");
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    // The refusal is not conservatism: a fractional square limit (a quarter cell or more off the lattice) is one that
    // makes its fold jump. A hex lattice's clamp cannot be measured, since no clamp is left in its fold to measure.
    [Fact]
    public void EveryFractionalLimitTheBuilderRefusesBreaksContinuity() {
        var failures = new List<string>();

        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: static group => (SdfWallpaperFold.IsContinuous(group: group) && (group < SdfWallpaperGroup.P3)))) {
            var cell = ((group == SdfWallpaperGroup.Pmm) ? new Vector2(x: 1f, y: 0.8f) : Vector2.One);

            foreach (var limit in new[] { new Vector2(value: 0.25f), new Vector2(x: 0.5f, y: 2f), new Vector2(x: 1.5f, y: 1.5f) }) {
                Assert.False(condition: Accepts(cell: cell, group: group, limit: limit));
                if (Stretch(cell: cell, group: group, limit: limit) <= 1.01f) {
                    failures.Add(item: $"{group} cell {cell} limit {limit} is refused, and folds continuously");
                }
            }
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures));
    }
    // Unit cells, limits (1, 1), then a sphere at (0.25, 0, 0.433): the points (1.75005, 0.4329261) and (1.74995, 0.4330993)
    // are 0.0002 apart, and clamping each rounded axial index apart folded them 0.5 apart.
    [Fact]
    public void AProgramRefusesAFiniteHexLimitAndNamesTheBound() {
        foreach (var group in new[] { SdfWallpaperGroup.P3M1, SdfWallpaperGroup.P6M }) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().WallpaperFold(cell: Vector2.One, group: group, limit: Vector2.One));

            Assert.Contains(expectedSubstring: "intersecting it with a bounding shape, not by limits", actualString: refusal.Message);
            Assert.Equal(expected: "limit", actual: refusal.ParamName);
        }
    }
    // PMM, unit cells, limits (0.25, 0.25): at x = 0.4999 the clamped index is 0, and at 0.5001 it is 0.25, which folds x to 0.2501.
    [Fact]
    public void AProgramRefusesAFractionalLimit() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().WallpaperFold(cell: Vector2.One, group: SdfWallpaperGroup.Pmm, limit: new Vector2(value: 0.25f)));

        Assert.Contains(expectedSubstring: "whole number", actualString: refusal.Message);
        Assert.Equal(expected: "limit", actual: refusal.ParamName);
        // The two points a ten-thousandth apart fold 0.25 apart under the fractional clamp.
        var below = SdfWallpaperFold.Fold(cell: Vector2.One, cellIndex: out _, group: SdfWallpaperGroup.Pmm, limit: new Vector2(value: 0.25f), point: new Vector2(x: 0.4999f, y: 0f));
        var above = SdfWallpaperFold.Fold(cell: Vector2.One, cellIndex: out _, group: SdfWallpaperGroup.Pmm, limit: new Vector2(value: 0.25f), point: new Vector2(x: 0.5001f, y: 0f));

        Assert.True(condition: (Vector2.Distance(value1: below, value2: above) > 0.1f), userMessage: $"{below} against {above}");
    }
    // A program admits the same limits whatever built it: a stream whose limit is fractional, or a hex lattice's finite,
    // is refused where the packed words are read.
    [Fact]
    public void AdmissionRefusesALimitTheBuilderWouldRefuse() {
        foreach (var (group, limit) in new[] { (SdfWallpaperGroup.Pmm, new Vector2(value: 0.25f)), (SdfWallpaperGroup.P6M, new Vector2(value: 2f)), (SdfWallpaperGroup.P4M, new Vector2(x: -1f, y: 1f)) }) {
            var refusal = Assert.Throws<ArgumentException>(testCode: () => Rebuilt(group: group, cell: Vector2.One, tamper: instruction => instruction with { Data1 = new Vector4(w: 0f, x: limit.X, y: limit.Y, z: 0f) }));

            Assert.Contains(expectedSubstring: $"wallpaper group {group} through a limit", actualString: refusal.Message);
        }
    }
    // Scale(1e6) then PMM with cells (1e-5, 1e-5) and limits (2, 2), a sphere at (4e-5, 0, 0) of radius 1e-6: the reciprocal
    // was floored at 1e4, so the lattice round read cells of 1e-4 while the fold subtracted cells of 1e-5, and the field at
    // world x = 49 read 8 while x = 50.1 was inside the sphere.
    [Fact]
    public void ATinyCellFoldsByItsOwnReciprocal() {
        var cell = new Vector2(value: 1.0e-5f);
        var limit = new Vector2(value: 2f);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Scale(scale: new Vector3(value: 1.0e6f)).WallpaperFold(cell: cell, group: SdfWallpaperGroup.Pmm, limit: limit).Translate(offset: new Vector3(x: 4.0e-5f, y: 0f, z: 0f)).Sphere(material: material, radius: 1.0e-6f).Build();
        var fold = Assert.Single(collection: program.Instructions, predicate: static instruction => (instruction.Op == SdfOp.WallpaperFold));

        Assert.Equal(expected: 1.0e5f, actual: fold.Data0.Z);
        Assert.Equal(expected: 1.0e5f, actual: fold.Data0.W);
        Assert.True(condition: (Stretch(cell: cell, group: SdfWallpaperGroup.Pmm, limit: limit) <= 1.01f));
        // Either side of x = 5e-5, 1.1e-6 apart: the folded points stay no farther apart than the points.
        var below = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: SdfWallpaperGroup.Pmm, limit: limit, point: new Vector2(x: 4.9e-5f, y: 0f));
        var above = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: SdfWallpaperGroup.Pmm, limit: limit, point: new Vector2(x: 5.01e-5f, y: 0f));

        Assert.True(condition: (Vector2.Distance(value1: below, value2: above) < 1.2e-6f), userMessage: $"{below} against {above}");
    }
    // The floor that guarded a vanishing cell is gone, so a cell is refused by what it must satisfy: positive, finite, and a
    // reciprocal the lattice round keeps finite at any point a float resolves, outer scale included.
    [Fact]
    public void AProgramRefusesACellItCannotInvert() {
        foreach (var extent in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 1.0e-39f, 1.0e-19f }) {
            foreach (var group in new[] { SdfWallpaperGroup.Pmm, SdfWallpaperGroup.P6M }) {
                var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().WallpaperFold(cell: new Vector2(value: extent), group: group, limit: new Vector2(value: ((group == SdfWallpaperGroup.P6M) ? SdfWallpaperFold.UnboundedLimit : 1f))));

                Assert.Equal(expected: "cell", actual: refusal.ParamName);
            }
        }
        Assert.NotNull(@object: SdfWallpaperFold.CellRefusal(group: SdfWallpaperGroup.Pmm, cell: new Vector2(value: 1.0e-19f)));
        Assert.Null(@object: SdfWallpaperFold.CellRefusal(group: SdfWallpaperGroup.Pmm, cell: new Vector2(value: 1.0e-17f)));
        // The largest reciprocal times the farthest point a float resolves a cell at stays finite.
        Assert.True(condition: float.IsFinite(f: (SdfWallpaperFold.MaximumInverseCell * 3.0e20f)));
        // A stream packed with a vanishing or non-reciprocal cell is refused where it is read.
        Assert.Throws<ArgumentException>(testCode: () => Rebuilt(group: SdfWallpaperGroup.Pmm, cell: Vector2.One, tamper: static instruction => instruction with { Data0 = new Vector4(w: 1f, x: 0f, y: 1f, z: 1f) }));
        Assert.Throws<ArgumentException>(testCode: () => Rebuilt(group: SdfWallpaperGroup.Pmm, cell: new Vector2(value: 1.0e-5f), tamper: static instruction => instruction with { Data0 = new Vector4(w: 1.0e4f, x: 1.0e-5f, y: 1.0e-5f, z: 1.0e4f) }));
    }

    private static SdfProgram Rebuilt(SdfWallpaperGroup group, Vector2 cell, Func<SdfInstruction, SdfInstruction> tamper) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.WallpaperFold(cell: cell, group: group, limit: Limit(group: group) with { X = ((group >= SdfWallpaperGroup.P3) ? SdfWallpaperFold.UnboundedLimit : 2f), Y = ((group >= SdfWallpaperGroup.P3) ? SdfWallpaperFold.UnboundedLimit : 2f) }).Sphere(material: material, radius: 0.1f).Build();

        return new SdfProgram(program.Instructions.Select(selector: instruction => ((instruction.Op == SdfOp.WallpaperFold) ? tamper(instruction) : instruction)).ToArray(), [new SdfMaterial(Albedo: Vector3.One)]);
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
        var limit = Limit(group: group);

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

        _ = builder.WallpaperFold(cell: cell, group: group, limit: Limit(group: group)).Translate(offset: offset).Sphere(material: material, radius: radius);

        return builder.Build();
    }
    // The limit a group's lattice admits for these sweeps: a hex lattice only the unbounded one, a square lattice a few cells.
    private static Vector2 Limit(SdfWallpaperGroup group) => new(value: ((group >= SdfWallpaperGroup.P3) ? SdfWallpaperFold.UnboundedLimit : 50f));
    private static bool Accepts(SdfWallpaperGroup group, Vector2 cell, Vector2 limit) {
        try {
            _ = new SdfProgramBuilder().WallpaperFold(cell: cell, group: group, limit: limit);

            return true;
        } catch (ArgumentException) {
            return false;
        }
    }
    // The fold's worst stretch over pairs a thousandth of a cell apart, across twelve cells each way of the origin, past
    // every clamp in the table and the first wall of a reciprocal floored ten times too large: a fold that keeps distances
    // (a reflection, however the clamp collapses cells) reads about one, and a jump reads about a thousand.
    private static float Stretch(SdfWallpaperGroup group, Vector2 cell, Vector2 limit) {
        var stretch = 0f;

        for (var index = 1; (index <= 16384); index++) {
            var point = (new Vector2(x: ((Fraction(value: (index * 0.7548777f)) * 24f) - 12f), y: ((Fraction(value: (index * 0.5698403f)) * 24f) - 12f)) * cell);
            var angle = (Fraction(value: (index * 0.6180340f)) * MathF.Tau);

            Measure(point: point, step: ((0.001f * cell.X) * new Vector2(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle))));
        }
        // A jump along a line is straddled by a random pair about once in ten thousand, so every cell wall within the
        // spread is also crossed on purpose: a thousandth of a cell across it, at eight places along it, on each axis.
        for (var wall = -14; (wall <= 14); wall++) {
            for (var along = 0; (along < 8); along++) {
                var offset = ((along * 1.37f) - 5f);

                Measure(point: new Vector2(x: (((wall + 0.5f) * cell.X) - (0.0005f * cell.X)), y: (offset * cell.Y)), step: new Vector2(x: (0.001f * cell.X), y: 0f));
                Measure(point: new Vector2(x: (offset * cell.X), y: (((wall + 0.5f) * cell.Y) - (0.0005f * cell.Y))), step: new Vector2(x: 0f, y: (0.001f * cell.Y)));
            }
        }

        return stretch;

        static float Fraction(float value) => (value - MathF.Floor(x: value));
        void Measure(Vector2 point, Vector2 step) {
            var a = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: point);
            var b = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: (point + step));

            stretch = MathF.Max(x: stretch, y: (Vector2.Distance(value1: a, value2: b) / step.Length()));
        }
    }
    private static (Vector2 Cell, Vector2 Limit)[] Configurations(SdfWallpaperGroup group) {
        var cells = ((group == SdfWallpaperGroup.Pmm)
            ? new[] { new Vector2(x: 1f, y: 0.8f), new Vector2(x: 1.0e-5f, y: 2.0e-5f), new Vector2(x: 3.0e3f, y: 1.0e3f) }
            : new[] { Vector2.One, new Vector2(value: 1.0e-5f), new Vector2(value: 3.0e3f) });
        var limits = new[] {
            new Vector2(value: SdfWallpaperFold.UnboundedLimit), Vector2.Zero, Vector2.One, new Vector2(x: 2f, y: 3f), new Vector2(value: 7f),
            new Vector2(value: 0.25f), new Vector2(x: 0.5f, y: 2f), new Vector2(x: 1.5f, y: 1.5f), new Vector2(x: 2.000001f, y: 1f),
        };

        return [.. cells.SelectMany(selector: cell => limits.Select(selector: limit => (cell, limit)))];
    }
}
