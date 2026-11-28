using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One emitting patch of a scene's static draws: the triangles of one emissive material of one draw whose
/// centroids share a cell (<see cref="SdfMeshIrradianceScene.EmitterCell"/>), summed in world space. The irradiance
/// volume samples it explicitly at each probe (<see cref="SdfMeshIrradiance"/>'s relight), so an emitter far smaller
/// than a probe ray's solid angle still lights what stands about it.</summary>
/// <param name="Centroid">The patch's area-weighted centroid, in world space.</param>
/// <param name="Normal">The sum of its triangles' outward normals each scaled by the triangle's area, in square metres:
/// as long as <paramref name="Area"/> for a flat patch, near zero for a closed one (a bulb).</param>
/// <param name="Area">The patch's surface area, in square metres.</param>
/// <param name="Material">The program material its triangles carry.</param>
public readonly record struct SdfMeshEmitter(Vector3 Centroid, Vector3 Normal, float Area, int Material) {
    /// <summary>Returns the area the patch shows along a direction leaving it: its flat part's projection
    /// (<c>max(0, N · d)</c>) plus a quarter of the area its normals cancel, which a closed surface shows every way (a
    /// sphere shows a quarter of its area from any side). KEEP IN SYNC with <c>sdfProbeEmitterArea</c> in
    /// <c>shade/sdf-probe-lighting.hlsli</c>.</summary>
    /// <param name="direction">The unit direction from the patch toward the receiver.</param>
    /// <returns>The projected area, in square metres; never negative.</returns>
    public float ProjectedArea(Vector3 direction) => (MathF.Max(x: 0f, y: Vector3.Dot(vector1: Normal, vector2: direction)) + (0.25f * MathF.Max(x: 0f, y: (Area - Normal.Length()))));
    /// <summary>Gets the most area the patch shows from any direction.</summary>
    public float MostProjectedArea => (Normal.Length() + (0.25f * MathF.Max(x: 0f, y: (Area - Normal.Length()))));
}

public sealed partial class SdfMeshIrradianceScene {
    /// <summary>The edge, in metres, of the cells an emitter's triangles are gathered in: half the finest probe spacing,
    /// so a patch is small beside the probes that sample it. A draw whose emissive triangles span more than
    /// <see cref="EmitterCellsPerAxis"/> of these gathers them in cells of that share of its span instead, which bounds
    /// any one draw's patches.</summary>
    public const float EmitterCell = (0.5f * SdfMeshIrradianceProbe.FinestSpacing);
    /// <summary>The most cells an emitting draw's patches divide its emissive span into along an axis.</summary>
    public const int EmitterCellsPerAxis = 16;

    /// <summary>Returns whether a program material emits light the irradiance volume samples as an emitter: a positive
    /// emissive with no lane driving it, which no emitter face of the program lights with. A lane-driven emissive reads
    /// its lane from a frame slot, which a static draw has none of, so the views shade it dark and the volume leaves it
    /// out too; an emitter face's material (<see cref="SdfAppearance.EmitterFaces"/>) lights through the screen-light walk,
    /// which a patch of it would light again.</summary>
    /// <param name="appearance">The program's appearance.</param>
    /// <param name="material">The program material id; one past the palette emits nothing.</param>
    /// <returns>Whether the material emits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="appearance"/> is <see langword="null"/>.</exception>
    public static bool Emits(SdfAppearance appearance, int material) {
        ArgumentNullException.ThrowIfNull(argument: appearance);

        if ((((uint)material) >= ((uint)appearance.Materials.Count)) || !(appearance.Materials[material].Emissive > 0f) || (appearance.Materials[material].EmissiveLane is not null)) { return false; }

        var faces = appearance.EmitterFaces;

        for (var face = 0; (face < faces.Count); face++) {
            if (faces[face].EmitterMaterial == material) { return false; }
        }
        return true;
    }

    /// <summary>Gets the emitting patches of the scene's static draws: every triangle whose material the build's
    /// <c>emits</c> admitted, gathered per draw, material and cell. Empty when the build was given no test.</summary>
    public SdfMeshEmitter[] Emitters { get; private init; } = [];
    /// <summary>Gets a digest of <see cref="Emitters"/> that does not depend on their order: equal digests stand for the
    /// same patches.</summary>
    public ulong EmitterDigest { get; private init; }

    // The emitting patches of a scene's draws (SdfMeshEmitter) and their digest. A draw whose triangle materials name no
    // material `emits` admits costs one look at each distinct run of materials; only an emitting draw's triangles are read.
    private static (SdfMeshEmitter[] Emitters, ulong Digest) GatherEmitters(ReadOnlySpan<Instance> instances, Func<int, bool> emits) {
        var runs = new Dictionary<(SdfMesh Mesh, int Level, int Base), bool>();
        var cells = new Dictionary<(int Material, int X, int Y, int Z), (float Area, Vector3 Centroid, Vector3 Normal)>();
        var emitters = new List<SdfMeshEmitter>();
        var digest = MultisetDigest.Empty;

        foreach (ref readonly var instance in instances) {
            if (!runs.TryGetValue(key: (instance.Mesh, instance.Geometry.Level, instance.Material), value: out var any)) {
                any = AnyEmits(emits: emits, run: instance);
                runs.Add(key: (instance.Mesh, instance.Geometry.Level, instance.Material), value: any);
            }
            if (!any) { continue; }

            var geometry = instance.Geometry;
            using var held = geometry.Mesh.Hold();
            using var materialHold = instance.Mesh.Hold();
            var positions = held.Positions.Span;
            var indices = held.LevelIndices(geometry.Level).Span;
            var materials = materialHold.LevelMaterials(geometry.Level).Span;
            var low = new Vector3(value: float.PositiveInfinity);
            var high = new Vector3(value: float.NegativeInfinity);

            for (var triangle = 0; (triangle < geometry.Count); triangle++) {
                if (!emits(arg: MaterialAt(instance: in instance, materials: materials, triangle: triangle))) { continue; }
                var (a, b, c) = WorldCorners(indices: indices, instance: in instance, positions: positions, triangle: triangle);

                low = Vector3.Min(value1: low, value2: Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c)));
                high = Vector3.Max(value1: high, value2: Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c)));
            }
            if (!(low.X <= high.X)) { continue; }

            var span = (high - low);
            var cell = MathF.Max(x: EmitterCell, y: (MathF.Max(x: span.X, y: MathF.Max(x: span.Y, y: span.Z)) / EmitterCellsPerAxis));

            cells.Clear();
            for (var triangle = 0; (triangle < geometry.Count); triangle++) {
                var material = MaterialAt(instance: in instance, materials: materials, triangle: triangle);

                if (!emits(arg: material)) { continue; }
                var (a, b, c) = WorldCorners(indices: indices, instance: in instance, positions: positions, triangle: triangle);
                // The triangle's outward normal times its area: half the cross product of its world edges, wound as its mesh
                // winds it (a mirroring placement reverses the winding, which the cross product of carried edges follows).
                var normal = (0.5f * Vector3.Cross(vector1: (b - a), vector2: (c - a)));
                var area = normal.Length();

                if (!(area > 0f)) { continue; }

                var centroid = ((a + b + c) / 3f);
                var at = ((centroid - low) / cell);
                var key = (material, (int)MathF.Floor(x: at.X), (int)MathF.Floor(x: at.Y), (int)MathF.Floor(x: at.Z));
                var sum = cells.GetValueOrDefault(key: key);

                cells[key] = ((sum.Area + area), (sum.Centroid + (centroid * area)), (sum.Normal + normal));
            }
            foreach (var ((material, _, _, _), sum) in cells) {
                var emitter = new SdfMeshEmitter(Area: sum.Area, Centroid: (sum.Centroid / sum.Area), Material: material, Normal: sum.Normal);
                var hash = Fnv1aHash.Create();

                hash.AddBits(value: emitter.Centroid);
                hash.AddBits(value: emitter.Normal);
                hash.AddBits(value: emitter.Area);
                hash.Add(value: (uint)emitter.Material);
                digest = digest.Add(element: hash.Value);
                emitters.Add(item: emitter);
            }
        }
        return ([.. emitters], digest.Value);
    }
    // Whether any material a draw's triangles carry emits: its base alone when its mesh carries no triangle materials.
    private static bool AnyEmits(in Instance run, Func<int, bool> emits) {
        using var held = run.Mesh.Hold();
        var materials = held.LevelMaterials(run.Geometry.Level).Span;

        if (materials.IsEmpty) { return emits(arg: run.Material); }

        var last = -1L;

        foreach (var material in materials) {
            if (material == last) { continue; }
            last = material;
            if (emits(arg: (run.Material + (int)material))) { return true; }
        }
        return false;
    }
    // A traced triangle's program material: the draw's base plus the triangle's own entry. `triangle` numbers the traced
    // level's triangles, as the mesh's held materials are numbered.
    private static int MaterialAt(in Instance instance, ReadOnlySpan<uint> materials, int triangle) =>
        (instance.Material + (materials.IsEmpty ? 0 : (int)materials[triangle]));
    // A traced triangle's corners in world space.
    private static (Vector3 A, Vector3 B, Vector3 C) WorldCorners(in Instance instance, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, int triangle) {
        var a = positions[(int)indices[(3 * triangle)]];
        var b = positions[(int)indices[((3 * triangle) + 1)]];
        var c = positions[(int)indices[((3 * triangle) + 2)]];

        return (instance.Identity
            ? (a, b, c)
            : (Vector3.Transform(position: a, matrix: instance.ObjectToWorld), Vector3.Transform(position: b, matrix: instance.ObjectToWorld), Vector3.Transform(position: c, matrix: instance.ObjectToWorld)));
    }
}
