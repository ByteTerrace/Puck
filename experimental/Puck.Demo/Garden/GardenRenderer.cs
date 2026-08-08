using System.Numerics;
using Puck.Demo.Overworld;
using Puck.SdfVm;

namespace Puck.Demo.Garden;

/// <summary>
/// Emits every planted garden into the shared room program — the presentation half of the deterministic-garden
/// feature (see <see cref="GardenTreeGenerator"/> for the structure it draws). ONE static <see cref="SdfProgramBuilder.Instance"/>
/// per tree (bound generously over the tallest legal draw — see <see cref="TreeBoundRadius"/>), never smooth-blended
/// across trees, exactly the "wrap the whole plant in one Instance" rule the sdf-world skill's scoped-accumulator
/// notes call for. Growth only ever SCALES the frontier branch order's length/radius (see <see cref="GardenGrowth"/>)
/// — every position was already decided, fixed-point, by <see cref="GardenTreeGenerator.Generate"/>, so a growing
/// tree never slides or re-roots as it fills in.
/// <para>
/// THE PROBE CONTRACT: <c>probeWorstCase</c> emits ALL <see cref="OverworldWorld.MaxGardens"/> slots (occupied or
/// not) at their structure's own worst-case draw (<see cref="GardenTreeGenerator.Generate"/> with <c>worstCase: true</c>)
/// and full growth (<c>FrontierScale = 1</c>, every depth revealed) — the true ceiling no live rebuild (fewer trees,
/// partially grown, gentler draws) can ever exceed.
/// </para>
/// </summary>
internal static class GardenRenderer {
    // Covers the tallest/widest legal draw (root length up to ~3.5, canopy spread past that) with margin — tuned
    // against Generate's root-length/shrink presets; grow this if those presets ever grow.
    private const float TreeBoundRadius = 3.6f;
    private const float BranchSmooth = 0.10f;
    private const float LeafSmooth = 0.16f;
    private const float MinRenderedHeight = 0.01f;
    private const float MinRenderedRadius = 0.01f;

    // Seed-selected trunk/leaf color pairs — visible seed-to-seed variety (a seed's PaletteIndex picks one). Kept
    // small and hand-picked rather than continuously hue-shifted, matching the room's other hand-authored palettes.
    private static readonly (Vector3 Trunk, Vector3 Leaf)[] Palette = [
        (new Vector3(x: 0.36f, y: 0.24f, z: 0.14f), new Vector3(x: 0.26f, y: 0.52f, z: 0.20f)), // oak: warm bark, summer green
        (new Vector3(x: 0.32f, y: 0.20f, z: 0.13f), new Vector3(x: 0.55f, y: 0.60f, z: 0.16f)), // willow: pale bark, yellow-green
        (new Vector3(x: 0.40f, y: 0.27f, z: 0.16f), new Vector3(x: 0.20f, y: 0.42f, z: 0.36f)), // pine: ruddy bark, blue-green
        (new Vector3(x: 0.30f, y: 0.17f, z: 0.11f), new Vector3(x: 0.62f, y: 0.38f, z: 0.16f)), // maple: dark bark, autumn orange
    ];

    /// <summary>Emits every planted garden's tree(s) into <paramref name="builder"/>.</summary>
    /// <param name="builder">The shared room program builder.</param>
    /// <param name="origin">The render-relative origin to subtract from world-space positions (matches every other
    /// room emitter's convention — see <c>OverworldFrameSource.Emitters.cs</c>).</param>
    /// <param name="floorY">The room's floor height — every garden sits on it.</param>
    /// <param name="gardens">The planted-garden slots (see <see cref="OverworldWorld.Gardens"/>).</param>
    /// <param name="currentTick">The sim's current tick (see <see cref="OverworldWorld.CurrentTick"/>).</param>
    /// <param name="probeWorstCase">Whether this is the construction-time capacity probe (see the type remarks).</param>
    internal static void Emit(SdfProgramBuilder builder, Vector3 origin, float floorY, IReadOnlyList<OverworldWorld.GardenPlant?> gardens, ulong currentTick, bool probeWorstCase) {
        for (var slot = 0; (slot < OverworldWorld.MaxGardens); slot++) {
            var planted = ((slot < gardens.Count) ? gardens[slot] : null);
            var occupied = (probeWorstCase || (planted is not null));

            if (!occupied) {
                continue;
            }

            EmitTree(builder: builder, origin: origin, floorY: floorY, slot: slot, planted: planted, currentTick: currentTick, probeWorstCase: probeWorstCase);
        }
    }

    private static void EmitTree(SdfProgramBuilder builder, Vector3 origin, float floorY, int slot, OverworldWorld.GardenPlant? planted, ulong currentTick, bool probeWorstCase) {
        // The probe seeds each of its MaxGardens synthetic trees differently only so BeginInstance's bound centers
        // don't all land exactly on top of each other — the seed/position never render, so any distinct value works.
        var seed = (probeWorstCase ? (uint)(slot + 1) : planted!.Value.Seed);
        var structure = GardenTreeGenerator.Generate(seed: seed, worstCase: probeWorstCase);
        var growth = (probeWorstCase
            ? new GardenGrowthState(Stage: structure.MaxDepth, FrontierScale: 1f)
            : GardenGrowth.Compute(ticksSincePlanting: (currentTick - planted!.Value.PlantedTick), maxDepth: structure.MaxDepth));

        var basePosition = (probeWorstCase
            ? new Vector3(x: (slot * 0.01f), y: floorY, z: 0f)
            : new Vector3(x: (float)planted!.Value.LocalX, y: floorY, z: (float)planted!.Value.LocalZ));

        var palette = Palette[(structure.PaletteIndex % Palette.Length)];
        var trunkMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: palette.Trunk));
        var leafMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: palette.Leaf));
        var boundCenter = ((basePosition + new Vector3(x: 0f, y: (TreeBoundRadius * 0.55f), z: 0f)) - origin);

        _ = builder.BeginInstance(boundCenter: boundCenter, boundRadius: TreeBoundRadius);

        foreach (var segment in structure.Segments) {
            EmitSegment(builder: builder, origin: origin, basePosition: basePosition, segment: segment, growth: growth, material: trunkMaterial);
        }

        foreach (var leaf in structure.Leaves) {
            EmitLeaf(builder: builder, origin: origin, basePosition: basePosition, leaf: leaf, growth: growth, material: leafMaterial);
        }

        _ = builder.EndInstance();
    }
    private static void EmitSegment(SdfProgramBuilder builder, Vector3 origin, Vector3 basePosition, GardenSegment segment, GardenGrowthState growth, int material) {
        if (segment.Depth > growth.Stage) {
            return;
        }

        var scale = ((segment.Depth == growth.Stage) ? growth.FrontierScale : 1f);
        var height = ((float)segment.Length * scale);

        if (height <= MinRenderedHeight) {
            return;
        }

        var basePoint = ((basePosition + segment.Base.ToVector3()) - origin);
        var direction = segment.Direction.ToVector3();
        var rotation = AlignUpTo(direction: direction);

        _ = builder.ResetPoint().Translate(offset: basePoint).Rotate(rotation: rotation).RoundCone(
            lowerRadius: ((float)segment.RadiusStart * scale),
            upperRadius: ((float)segment.RadiusEnd * scale),
            height: height,
            material: material,
            smooth: BranchSmooth
        );
    }
    private static void EmitLeaf(SdfProgramBuilder builder, Vector3 origin, Vector3 basePosition, GardenLeaf leaf, GardenGrowthState growth, int material) {
        if (leaf.Depth > growth.Stage) {
            return;
        }

        var scale = ((leaf.Depth == growth.Stage) ? growth.FrontierScale : 1f);
        var radius = ((float)leaf.Radius * scale);

        if (radius <= MinRenderedRadius) {
            return;
        }

        var position = ((basePosition + leaf.Position.ToVector3()) - origin);

        _ = builder.ResetPoint().Translate(offset: position).Sphere(radius: radius, material: material, smooth: LeafSmooth);
    }

    // The shortest-arc rotation carrying local +Y (RoundCone's own authoring axis) onto an arbitrary world direction
    // — the standard half-way-vector quaternion construction. Presentation-only float math (placing an already-
    // decided fixed-point direction), matching every other room prop's Quaternion.CreateFromAxisAngle usage.
    private static Quaternion AlignUpTo(Vector3 direction) {
        var from = Vector3.UnitY;
        var to = ((direction.LengthSquared() > 1e-8f) ? Vector3.Normalize(value: direction) : Vector3.UnitY);
        var dot = Vector3.Dot(vector1: from, vector2: to);

        if (dot >= 0.999999f) {
            return Quaternion.Identity;
        }

        if (dot <= -0.999999f) {
            var axis = Vector3.Cross(vector1: Vector3.UnitX, vector2: from);

            if (axis.LengthSquared() < 1e-6f) {
                axis = Vector3.Cross(vector1: Vector3.UnitZ, vector2: from);
            }

            return Quaternion.CreateFromAxisAngle(axis: Vector3.Normalize(value: axis), angle: MathF.PI);
        }

        var cross = Vector3.Cross(vector1: from, vector2: to);
        var w = (MathF.Sqrt(x: (from.LengthSquared() * to.LengthSquared())) + dot);

        return Quaternion.Normalize(value: new Quaternion(x: cross.X, y: cross.Y, z: cross.Z, w: w));
    }
}
