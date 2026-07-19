using System.Numerics;
using Puck.Authoring;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// Emits the world's STATIC placements (§D6) into the program under construction — each placement one (or several,
/// repeat-auto-split) static <see cref="SdfProgramBuilder.BeginInstance"/> whose shapes replay the referenced
/// creation's shape list with the FULL placement transform baked into EVERY shape's own segment (Translate → yaw
/// Rotate → the optional mirror fold → the optional repeat → the uniform placement Scale, then the shape's local
/// T·R·S and the canonical primitive) — the settled Demo stamp path, ported. Animated placements (framed creations)
/// are NOT emitted here — they ride <see cref="WorldPlacementAnimator"/>'s reserved dynamic pool.
/// </summary>
/// <remarks>Text runs count against the per-stamp shape budget (<see cref="CreationDocument.StampShapeCount"/>) but do
/// not EMIT this arc: World deliberately binds no world-space glyph atlas (the UIE-7 memory posture — the combined
/// MTSDF decode is the condemned path), so a run's letters wait on a compact world-text atlas artifact. The budget
/// already reserves their envelope, so binding an atlas later is emission-only, never a capacity change.</remarks>
internal static class WorldPlacementStamper {
    // The instance-bound slack past a creation's own reach (a contract of the tile cull, not a policy: a too-tight
    // bound CLIPS real geometry at masked tile edges; a fat one only costs a rare extra evaluation).
    private const float PlacementBoundMargin = 0.4f;
    // Probe instances are spaced far apart so the program's segment-merge pass can never collapse consecutive probe
    // segments — a merged probe would under-reserve the segment directory a real scattered world needs (contract).
    private const float ProbeSpread = 100f;

    /// <summary>Whether a creation row replays a timeline (frames present) — the static/animated fork every consumer
    /// shares.</summary>
    /// <param name="creation">The creation row.</param>
    public static bool IsAnimated(WorldCreation creation) => (creation.Document.Frames is { Count: > 0 });

    /// <summary>The emitted SEGMENT count of one placement (its repeat auto-split total; 1 unrepeated) — the unit the
    /// capacity reservation charges in, so a segmented row can never out-instance its charge.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="maxRepeatPerSegment">The largest per-axis repeat count one emitted segment carries (BOOT-CONSUMED
    /// — the frame source's captured <see cref="WorldAuthoringDefaults.MaxRepeatPerSegment"/>, identical across the
    /// construction probe, every live rebuild, and the apply-time measure).</param>
    public static int SegmentCount(WorldPlacement placement, int maxRepeatPerSegment) {
        if ((placement.Repeat is not { } repeat) || (repeat.TotalCount <= 1)) {
            return 1;
        }

        return (SegmentTotal(count: repeat.CountX, maxRepeatPerSegment: maxRepeatPerSegment) * SegmentTotal(count: repeat.CountZ, maxRepeatPerSegment: maxRepeatPerSegment));
    }

    /// <summary>The total STATIC stamp segments of a placement set (animated rows ride the constant replay pool and
    /// charge nothing here) — the apply-time measure's placement charge unit.</summary>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The placement rows.</param>
    /// <param name="maxRepeatPerSegment">See <see cref="SegmentCount(WorldPlacement, int)"/>.</param>
    public static int StaticStampSegments(IReadOnlyList<WorldCreation> creations, IReadOnlyList<WorldPlacement> placements, int maxRepeatPerSegment) {
        var segments = 0;

        foreach (var placement in placements) {
            if ((FindCreation(creations: creations, id: placement.CreationId) is { } creation) && !IsAnimated(creation: creation)) {
                segments += SegmentCount(placement: placement, maxRepeatPerSegment: maxRepeatPerSegment);
            }
        }

        return segments;
    }

    /// <summary>Resolves a creation row by id, or <see langword="null"/>.</summary>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="id">The row id.</param>
    public static WorldCreation? FindCreation(IReadOnlyList<WorldCreation> creations, string id) {
        foreach (var creation in creations) {
            if (string.Equals(a: creation.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return creation;
            }
        }

        return null;
    }

    /// <summary>Emits every STATIC placement (animated rows skip — the animator owns them). Palettes register once per
    /// distinct untinted creation; a tinted stamp (selection amber / change shimmer) registers its own lerped palette
    /// (act-scale rare, never steady-state).</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The (possibly drag-composed) placement rows.</param>
    /// <param name="tintFor">Resolves a placement id's albedo tint (color + blend), or <see langword="null"/> untinted.</param>
    /// <param name="maxRepeatPerSegment">See <see cref="SegmentCount(WorldPlacement, int)"/>.</param>
    public static void EmitStatic(SdfProgramBuilder builder, IReadOnlyList<WorldCreation> creations, IReadOnlyList<WorldPlacement> placements, int maxRepeatPerSegment, Func<string, (Vector3 Color, float Blend)?>? tintFor = null) {
        var paletteById = new Dictionary<string, int[]>(comparer: StringComparer.Ordinal);

        foreach (var placement in placements) {
            if (FindCreation(creations: creations, id: placement.CreationId) is not { } creation || IsAnimated(creation: creation)) {
                continue;
            }

            var tint = tintFor?.Invoke(arg: placement.Id);
            var paletteIds = ((tint is null)
                ? ResolvePalette(builder: builder, creation: creation, paletteById: paletteById)
                : RegisterPalette(builder: builder, document: creation.Document, tint: tint));

            EmitPlacement(builder: builder, creation: creation.Document, paletteIds: paletteIds, placement: placement, maxRepeatPerSegment: maxRepeatPerSegment);
        }
    }

    /// <summary>Emits the construction probe's placement reservation: <paramref name="reservedCount"/> worst-case
    /// stamps — each a distinct full 16-slot palette plus <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> shapes
    /// carrying the densest legal per-shape chain (the full placement prefix with mirror fold + repeat) — so any real
    /// static emission within the placement policy fits the once-sized buffers by construction. Never rendered.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="reservedCount">The reserved stamp count (boot placements + the authoring headroom).</param>
    /// <param name="maxRepeatPerSegment">See <see cref="SegmentCount(WorldPlacement, int)"/>.</param>
    public static void EmitProbe(SdfProgramBuilder builder, int reservedCount, int maxRepeatPerSegment) {
        for (var index = 0; (index < reservedCount); index++) {
            // Worst-case distinct materials: every reserved stamp references a DISTINCT creation with a full palette
            // (the per-id cache only relaxes this; probing as if every stamp were unique is the conservative bound).
            var paletteIds = new int[CreationDocument.PaletteSize];

            for (var slot = 0; (slot < CreationDocument.PaletteSize); slot++) {
                paletteIds[slot] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
            }

            var center = new Vector3(x: (index * ProbeSpread), y: 4f, z: 0f);

            _ = builder.BeginInstance(boundCenter: center, boundRadius: 12f);

            for (var shape = 0; (shape < WorldPlacementPolicy.MaxShapesPerStamp); shape++) {
                _ = CreationGeometry.AppendPrimitive(
                    chain: builder.ResetPoint()
                        .Translate(offset: center)
                        .Rotate(rotation: Quaternion.Identity)
                        .SymmetryX()
                        .RepeatLimited(spacing: new Vector3(x: 1f, y: 0f, z: 1f), limit: new Vector3(x: maxRepeatPerSegment, y: 0f, z: maxRepeatPerSegment))
                        .Scale(scale: Vector3.One)
                        .Translate(offset: Vector3.Zero)
                        .Rotate(rotation: Quaternion.Identity)
                        .Scale(scale: Vector3.One),
                    type: AvatarPrimitive.Sphere,
                    material: paletteIds[(shape % CreationDocument.PaletteSize)]
                );
            }

            _ = builder.EndInstance();
        }
    }

    /// <summary>Registers a creation's palette (16-slot clamp) with an optional tint lerp, returning program-relative
    /// material ids indexed like the creation's own palette slots.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="tint">The albedo tint (color + blend), or <see langword="null"/>.</param>
    internal static int[] RegisterPalette(SdfProgramBuilder builder, CreationDocument document, (Vector3 Color, float Blend)? tint) {
        var palette = (document.Palette ?? []);
        var count = Math.Min(val1: palette.Count, val2: CreationDocument.PaletteSize);
        var ids = new int[Math.Max(val1: count, val2: 1)];

        for (var index = 0; (index < ids.Length); index++) {
            var entry = ((index < count) ? palette[index] : null);
            var albedo = (entry?.Albedo ?? new Vector3(value: 0.7f));

            if (tint is { } applied) {
                albedo = Vector3.Lerp(value1: albedo, value2: applied.Color, amount: applied.Blend);
            }

            ids[index] = builder.AddMaterial(material: new SdfMaterial(
                Albedo: albedo,
                Emissive: (entry?.Emissive ?? 0f),
                Shininess: (entry?.Shininess ?? 32f),
                Specular: (entry?.Specular ?? 0f)
            ));
        }

        return ids;
    }

    private static int[] ResolvePalette(SdfProgramBuilder builder, WorldCreation creation, Dictionary<string, int[]> paletteById) {
        if (paletteById.TryGetValue(key: creation.Id, value: out var cached)) {
            return cached;
        }

        var ids = RegisterPalette(builder: builder, document: creation.Document, tint: null);

        paletteById[creation.Id] = ids;

        return ids;
    }

    // One placement's static instances: the single-copy fast path, or the repeat auto-split (no segment bound covers
    // more than maxRepeatPerSegment copies per axis — the tile-cull contract the Demo path settled).
    private static void EmitPlacement(SdfProgramBuilder builder, CreationDocument creation, int[] paletteIds, WorldPlacement placement, int maxRepeatPerSegment) {
        var reach = CreationGeometry.Reach(document: creation);
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));

        if ((placement.Repeat is not { } repeat) || (repeat.TotalCount <= 1)) {
            _ = builder.BeginInstance(boundCenter: placement.Position, boundRadius: ((reach * placement.Scale) + PlacementBoundMargin));
            EmitPlacedShapes(builder: builder, creation: creation, paletteIds: paletteIds, placement: placement, placementOrigin: placement.Position, placementRotation: rotation, repeatSpacing: null, repeatLimit: null);
            _ = builder.EndInstance();

            return;
        }

        var segmentsX = SegmentTotal(count: repeat.CountX, maxRepeatPerSegment: maxRepeatPerSegment);
        var segmentsZ = SegmentTotal(count: repeat.CountZ, maxRepeatPerSegment: maxRepeatPerSegment);

        for (var segmentX = 0; (segmentX < segmentsX); segmentX++) {
            var countX = SegmentCount(total: repeat.CountX, segment: segmentX, maxRepeatPerSegment: maxRepeatPerSegment);

            for (var segmentZ = 0; (segmentZ < segmentsZ); segmentZ++) {
                var countZ = SegmentCount(total: repeat.CountZ, segment: segmentZ, maxRepeatPerSegment: maxRepeatPerSegment);

                if ((countX <= 0) || (countZ <= 0)) {
                    continue;
                }

                var offsetX = (SegmentStart(segment: segmentX, maxRepeatPerSegment: maxRepeatPerSegment) * repeat.SpacingX);
                var offsetZ = (SegmentStart(segment: segmentZ, maxRepeatPerSegment: maxRepeatPerSegment) * repeat.SpacingZ);
                var segmentOrigin = (placement.Position + Vector3.Transform(value: new Vector3(x: offsetX, y: 0f, z: offsetZ), rotation: rotation));
                var span = new Vector3(x: (((countX - 1) * repeat.SpacingX) * 0.5f), y: 0f, z: (((countZ - 1) * repeat.SpacingZ) * 0.5f));
                var segmentCenter = (segmentOrigin + Vector3.Transform(value: span, rotation: rotation));
                var boundRadius = (((reach + span.Length()) * placement.Scale) + PlacementBoundMargin);

                _ = builder.BeginInstance(boundCenter: segmentCenter, boundRadius: boundRadius);
                EmitPlacedShapes(
                    builder: builder,
                    creation: creation,
                    paletteIds: paletteIds,
                    placement: placement,
                    placementOrigin: segmentOrigin,
                    placementRotation: rotation,
                    repeatSpacing: new Vector3(x: repeat.SpacingX, y: 0f, z: repeat.SpacingZ),
                    repeatLimit: new Vector3(x: (countX - 1), y: 0f, z: (countZ - 1))
                );
                _ = builder.EndInstance();
            }
        }
    }

    // Emits the creation's shapes, EACH its own segment carrying the FULL placement prefix — the shader splits the
    // stream at each ResetPoint and a segment's transforms are local to it, so a shared prefix segment would be dead
    // (the settled Demo stamp lesson). Uniform placement scale commutes with the per-shape rotations (shear-free).
    private static void EmitPlacedShapes(SdfProgramBuilder builder, CreationDocument creation, int[] paletteIds, WorldPlacement placement, Vector3 placementOrigin, Quaternion placementRotation, Vector3? repeatSpacing, Vector3? repeatLimit) {
        foreach (var shape in (creation.Shapes ?? [])) {
            var material = paletteIds[Math.Clamp(value: (shape.Material ?? 0), max: (paletteIds.Length - 1), min: 0)];
            var chain = builder.ResetPoint().Translate(offset: placementOrigin).Rotate(rotation: placementRotation);

            if (string.Equals(a: placement.Mirror, b: "z", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                chain = chain.SymmetryZ();
            } else if (string.Equals(a: placement.Mirror, b: "x", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                chain = chain.SymmetryX();
            }

            if ((repeatSpacing is { } spacing) && (repeatLimit is { } limit)) {
                chain = chain.RepeatLimited(spacing: spacing, limit: limit);
            }

            chain = chain
                .Scale(scale: new Vector3(value: placement.Scale))
                .Translate(offset: shape.Position)
                .Rotate(rotation: shape.Rotation)
                .Scale(scale: shape.Scale);
            _ = CreationGeometry.AppendPrimitive(chain: chain, type: shape.Type, material: material, blend: (shape.Blend ?? SdfBlendOp.Union), smooth: (shape.Smooth ?? 0f));
        }
    }

    private static int SegmentTotal(int count, int maxRepeatPerSegment) => Math.Max(val1: 1, val2: (((count + maxRepeatPerSegment) - 1) / maxRepeatPerSegment));
    private static int SegmentStart(int segment, int maxRepeatPerSegment) => (segment * maxRepeatPerSegment);
    private static int SegmentCount(int total, int segment, int maxRepeatPerSegment) =>
        (Math.Min(val1: total, val2: SegmentStart(segment: (segment + 1), maxRepeatPerSegment: maxRepeatPerSegment)) - Math.Min(val1: total, val2: SegmentStart(segment: segment, maxRepeatPerSegment: maxRepeatPerSegment)));
}
