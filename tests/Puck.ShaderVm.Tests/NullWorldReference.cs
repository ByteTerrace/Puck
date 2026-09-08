using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.ShaderVm.Tests;

// The same field as NullWorldScene, hand-written as direct scalar C# for this one world. This is the bespoke
// baseline the interpreted
// program is measured against: identical math, identical process, no dispatch and no four-lane widening.
public static class NullWorldReference {
    /// <summary>Evaluates the world's distance and material at one point.</summary>
    public static (float Distance, int Material) Sample(Vector3 point) {
        var world = (Distance: point.Y, Material: NullWorldScene.Stone);

        world = Union(current: world, candidate: GroundNorthEast(point: point));
        world = Union(current: world, candidate: GroundSouthEast(point: point));
        world = Union(current: world, candidate: GroundSouthWest(point: point));
        world = Union(current: world, candidate: Pillars(point: point));
        world = Union(current: world, candidate: Planetoids(point: point));
        world = Union(current: world, candidate: Pip(point: point));

        return world;
    }

    private static (float Distance, int Material) GroundNorthEast(Vector3 point) {
        var key = WallpaperKey(cell: 1f, point: point);

        return Quadrant(
            candidate: (Distance: (point.Y - 0.001f), Material: (NullWorldScene.Paper + key)),
            keepNegativeX: true,
            keepPositiveZ: true,
            point: point
        );
    }
    private static (float Distance, int Material) GroundSouthEast(Vector3 point) => Quadrant(
        candidate: (Distance: (point.Y - 0.001f), Material: NullWorldScene.Sage),
        keepNegativeX: true,
        keepPositiveZ: false,
        point: point
    );
    private static (float Distance, int Material) GroundSouthWest(Vector3 point) {
        var key = WallpaperKey(cell: 2f, point: point);

        return Quadrant(
            candidate: (Distance: (point.Y - 0.001f), Material: (NullWorldScene.BlueDeep + key)),
            keepNegativeX: false,
            keepPositiveZ: false,
            point: point
        );
    }
    private static int WallpaperKey(Vector3 point, float cell) {
        var indexX = MathF.Round(x: (point.X / cell));
        var indexY = MathF.Round(x: (point.Z / cell));
        var sum = (indexX + indexY);

        return ((int)(sum - (2f * MathF.Floor(x: (sum / 2f)))));
    }
    private static (float Distance, int Material) Quadrant((float Distance, int Material) candidate, Vector3 point, bool keepNegativeX, bool keepPositiveZ) {
        var alongX = (keepNegativeX ? -point.X : point.X);
        var alongZ = (keepPositiveZ ? point.Z : -point.Z);

        return (Distance: MathF.Max(x: MathF.Max(x: candidate.Distance, y: -alongX), y: -alongZ), Material: candidate.Material);
    }
    private static (float Distance, int Material) Pillars(Vector3 point) {
        var folded = new Vector3(x: MathF.Abs(x: point.X), y: point.Y, z: MathF.Abs(x: point.Z));
        var plinth = (Distance: Cylinder(halfHeight: 0.3f, point: (folded - new Vector3(x: 6f, y: 0.3f, z: 6f)), radius: 0.8f), Material: NullWorldScene.PillarTrim);
        var shaft = (Distance: Cylinder(halfHeight: 2.9f, point: (folded - new Vector3(x: 6f, y: 3.3f, z: 6f)), radius: 0.45f), Material: NullWorldScene.PillarStone);
        var capital = (Distance: Cylinder(halfHeight: 0.3f, point: (folded - new Vector3(x: 6f, y: 6.35f, z: 6f)), radius: 0.8f), Material: NullWorldScene.PillarTrim);
        var lamp = (Distance: ((folded - new Vector3(x: 6f, y: 7f, z: 6f)).Length() - 0.45f), Material: NullWorldScene.PillarLamp);
        var column = SmoothUnion(candidate: shaft, current: plinth, radius: 0.25f);

        column = SmoothUnion(candidate: capital, current: column, radius: 0.25f);

        return SmoothUnion(candidate: lamp, current: column, radius: 0.1f);
    }
    private static (float Distance, int Material) Planetoids(Vector3 point) {
        var world = Planetoid(point: point, position: new Vector3(x: 6f, y: 17f, z: 6f), scale: 1f);

        foreach (var (position, scale) in (((Vector3, float)[])[
            (new Vector3(x: -6f, y: 17f, z: 6f), 1f),
            (new Vector3(x: 6f, y: 17f, z: -6f), 1f),
            (new Vector3(x: -6f, y: 17f, z: -6f), 1f),
            (new Vector3(x: 0f, y: 24f, z: 0f), 1.8f),
        ])) {
            world = Union(candidate: Planetoid(point: point, position: position, scale: scale), current: world);
        }

        return world;
    }
    private static (float Distance, int Material) Planetoid(Vector3 point, Vector3 position, float scale) {
        var local = ((point - position) / scale);
        var core = (Distance: (local.Length() - 1.5f), Material: NullWorldScene.PlanetoidCrust);
        var sectorAngle = ((2f * MathF.PI) / 6f);
        var raised = (MathF.Atan2(x: local.X, y: local.Z) + (0.5f * sectorAngle));
        var angle = ((raised - (MathF.Floor(x: (raised / sectorAngle)) * sectorAngle)) - (0.5f * sectorAngle));
        var radius = MathF.Sqrt(x: ((local.X * local.X) + (local.Z * local.Z)));
        var folded = new Vector3(x: ((MathF.Cos(x: angle) * radius) - 1.32f), y: local.Y, z: (MathF.Sin(x: angle) * radius));
        var outcrop = (Distance: (folded.Length() - 0.3f), Material: NullWorldScene.PlanetoidRock);
        var blended = SmoothUnion(candidate: outcrop, current: core, radius: 0.3f);

        return (Distance: (blended.Distance * scale), Material: blended.Material);
    }
    private static (float Distance, int Material) Pip(Vector3 point) {
        var endpoint = new Vector3(x: 0f, y: 0.9f, z: 0f);
        var local = (point - new Vector3(x: 0f, y: 0.25f, z: 0f));
        var along = Math.Clamp(max: 1f, min: 0f, value: (Vector3.Dot(vector1: local, vector2: endpoint) / Vector3.Dot(vector1: endpoint, vector2: endpoint)));
        var body = (Distance: ((local - (endpoint * along)).Length() - 0.35f), Material: NullWorldScene.PipBody);
        var mirrored = new Vector3(x: MathF.Abs(x: point.X), y: point.Y, z: point.Z);
        var eye = (Distance: ((mirrored - new Vector3(x: 0.2f, y: 1.1f, z: 0.42f)).Length() - 0.14f), Material: NullWorldScene.PipEye);
        var pupil = (Distance: ((mirrored - new Vector3(x: 0.2f, y: 1.1f, z: 0.54f)).Length() - 0.055f), Material: NullWorldScene.PipPupil);

        return Union(candidate: pupil, current: Union(candidate: eye, current: body));
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static float Cylinder(Vector3 point, float radius, float halfHeight) {
        var radial = (MathF.Sqrt(x: ((point.X * point.X) + (point.Z * point.Z))) - radius);
        var axial = (MathF.Abs(x: point.Y) - halfHeight);
        var outsideX = MathF.Max(x: radial, y: 0f);
        var outsideY = MathF.Max(x: axial, y: 0f);

        return (MathF.Sqrt(x: ((outsideX * outsideX) + (outsideY * outsideY))) + MathF.Min(x: MathF.Max(x: radial, y: axial), y: 0f));
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static (float Distance, int Material) Union((float Distance, int Material) current, (float Distance, int Material) candidate) => ((candidate.Distance < current.Distance)
        ? candidate
        : current
    );
    private static (float Distance, int Material) SmoothUnion((float Distance, int Material) current, (float Distance, int Material) candidate, float radius) {
        var weight = Math.Clamp(max: 1f, min: 0f, value: (0.5f + ((0.5f * (current.Distance - candidate.Distance)) / radius)));

        return (
            Distance: ((current.Distance + ((candidate.Distance - current.Distance) * weight)) - ((radius * weight) * (1f - weight))),
            Material: Union(candidate: candidate, current: current).Material
        );
    }
}
