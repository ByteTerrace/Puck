using System.Numerics;
using Puck.Assets;
using Puck.Authoring;
using Puck.Demo.Creator;
using Puck.Demo.Forge;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.Demo.World;

/// <summary>
/// Emits the world scene into the SDF program and packs its per-frame dynamic transforms — the render half of the
/// sculptor, mirroring <c>Puck.Demo.Creator.CreatorSceneRenderer</c>'s envelope discipline. THE BINDING RULE: each
/// placement is exactly ONE static <c>BeginInstance</c> (its shapes replayed with baked Translate/Rotate/Scale —
/// placements cost ZERO dynamic slots at rest), wrapping an optional <c>RepeatLimited</c> for its repeat block; a
/// row whose count exceeds <see cref="WorldScene.MaxRepeatPerSegment"/> on an axis auto-splits into several tighter
/// placements so no single instance bound balloons past the tile cull's usefulness. The only TWO dynamic-transform
/// slots the whole scene ever uses are the ghost stamp and the selected stamp mid-drag — every placed, settled
/// stamp is fully static.
///
/// <see cref="EmitWorld"/>'s <c>probeWorstCase</c> path emits the synthetic worst case the frame source measures
/// once at construction (<see cref="WorldScene.MaxPlacements"/> instances, each the densest legal stamp: a
/// <c>RepeatLimited</c> + <c>SymmetryX</c> + <c>WallpaperFold</c> wrapping <see cref="WorldScene.MaxShapesPerStamp"/>
/// shapes, plus both dynamic slots and the worst-case distinct-palette material count) — so a REAL emission can
/// never legally exceed the probed envelope, and <c>SdfWorldEngine.UploadProgram</c> throwing becomes structurally
/// impossible rather than a runtime risk. Any new optional emission this renderer grows MUST grow the probe in the
/// same change (see the sdf-world skill's capacity-probe doctrine).
/// </summary>
public sealed class WorldSceneRenderer {
    /// <summary>The scene's two dynamic-transform slots: the ghost stamp, then the selected stamp mid-drag.</summary>
    public const int GhostSlotOffset = 0;
    public const int DragSlotOffset = 1;
    /// <summary>How many dynamic-transform slots the whole world scene ever occupies.</summary>
    public static int DynamicSlotCount => 2;

    // A generous per-placement bound margin at unit scale (added past the creation's own reach) — covers rounding,
    // authoring slack, and the repeat span; a fat bound only costs a rare extra evaluation.
    private const float PlacementBoundMargin = 0.4f;
    // Terrain slabs and lights are simple, constant-cost world-set (unbounded, unmasked) emissions — cheap enough
    // that giving them their own per-item instance buys nothing; they ride the always-evaluated set like the
    // overworld's floor/walls.
    private const float LightRadius = 0.08f;

    private static readonly Vector3 LightAlbedo = new(x: 1f, y: 0.92f, z: 0.75f);
    // The ghost stamp's preview accent — the same bright cyan CreatorSceneRenderer uses for its ghost, so a
    // not-yet-placed stamp reads as a hologram everywhere in the engine.
    private static readonly Vector3 GhostAlbedo = new(x: 0.35f, y: 0.92f, z: 1.0f);
    // The walk-override ghost outline (renders only while the overrides page is active) — a thin amber frame so a
    // blocker/walkable rectangle reads as an overlay, never authored geometry.
    private static readonly Vector3 WalkOverrideAlbedo = new(x: 0.95f, y: 0.65f, z: 0.2f);
    private readonly WorldScene m_scene;
    private readonly ContentAddressedStore m_store;
    private readonly int m_slotBase;
    // The SHARED world-glyph atlas (the SAME FontAtlas the DiegeticUiDirector builds once and the engine uploads via
    // ISdfFrameSource.GlyphAtlas) + its layout engine — bound after construction by the render assembly. Null off
    // Windows / when no font resolved, in which case a placement's text runs simply don't emit (its boxes still stamp).
    private FontAtlas? m_font;
    private readonly TextLayout m_textLayout = new();

    /// <summary>Binds the shared world-glyph atlas so a placed creation's <see cref="TextRunDocument"/>s lay out
    /// against the EXACT atlas the shader samples — one atlas, one TextLayout. Threaded in from the render assembly
    /// after the atlas-owning director is composed; the frame source that owns this renderer never names the atlas
    /// type (it sits at its CA1506 coupling ceiling). Null leaves text runs unemitted.</summary>
    /// <param name="font">The shared font atlas (or null when none was built).</param>
    public void SetGlyphAtlas(FontAtlas? font) => m_font = font;

    /// <summary>Initializes the renderer over a scene at its dynamic-transform slot base.</summary>
    /// <param name="scene">The authored scene to emit.</param>
    /// <param name="store">The content-addressed store placements resolve creations against.</param>
    /// <param name="slotBase">The scene's first dynamic-transform slot (ghost = <paramref name="slotBase"/>, drag =
    /// <paramref name="slotBase"/> + 1).</param>
    public WorldSceneRenderer(WorldScene scene, ContentAddressedStore store, int slotBase) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(store);

        m_scene = scene;
        m_slotBase = slotBase;
        m_store = store;
    }

    /// <summary>Emits the world scene into the program under construction: terrain, lights, walk-override ghost
    /// outlines (while active), every placement (each its own static instance, auto-split when its repeat row
    /// exceeds the per-segment cap), and the two dynamic slots (ghost stamp + selected-drag stamp). With
    /// <paramref name="probeWorstCase"/> the emission takes its LARGEST legal form for capacity measurement (see the
    /// type remarks) — never rendered.</summary>
    /// <param name="builder">The program builder (room content is already emitted by the host).</param>
    /// <param name="probeWorstCase">Emit the synthetic worst case for capacity measurement.</param>
    public void EmitWorld(SdfProgramBuilder builder, bool probeWorstCase = false) {
        ArgumentNullException.ThrowIfNull(builder);

        if (probeWorstCase) {
            EmitProbe(builder: builder);

            return;
        }

        EmitTerrain(builder: builder);
        EmitLights(builder: builder);
        EmitWalkOverrideGhosts(builder: builder);

        // Register each DISTINCT referenced creation's palette ONCE per program build (keyed by hash) — 128
        // placements referencing the same handful of creations must not multiply materials.
        var paletteByHash = new Dictionary<string, int[]>(comparer: StringComparer.Ordinal);

        foreach (var placement in m_scene.Placements) {
            // A `companion` placement stays in the model (so it round-trips through world.save/world.load like any
            // other) but emits no STATIC stamp here — it is dispatched into the live CompanionRoster instead
            // (CompanionRoster.SpawnFromWorld), which renders it as an animated, wandering instance. Emitting it here
            // too would double-render the same creation: once frozen at its authored spot, once roaming.
            if (string.Equals(a: placement.Role, b: "companion", comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            EmitPlacement(builder: builder, paletteByHash: paletteByHash, placement: placement);
        }

        EmitGhostSlot(builder: builder, paletteByHash: paletteByHash);
        EmitDragSlot(builder: builder, paletteByHash: paletteByHash);
    }

    /// <summary>Packs the scene's two dynamic-transform slots: the ghost stamp rides its live position/yaw while a
    /// creation is armed (hidden below the floor otherwise), and the selected stamp rides its live position/yaw only
    /// while <see cref="WorldScene.Dragging"/> is true (hidden otherwise — a settled stamp is fully static and never
    /// touches this buffer). The armed ghost HOVERS — a small render-clock bob and sway so a pending stamp reads as
    /// a hologram awaiting South, never as placed geometry (dynamic transforms are rigid, so life comes from motion,
    /// not scale).</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the scene writes its own slot range).</param>
    /// <param name="hiddenPosition">Where a hidden slot parks (far below the floor).</param>
    /// <param name="timeSeconds">The render clock (drives the ghost's hover; presentation only).</param>
    public void PackTransforms(Span<DynamicTransform> transforms, Vector3 hiddenPosition, float timeSeconds = 0f) {
        var hover = new Vector3(x: 0f, y: (0.05f + (0.04f * MathF.Sin(x: (2.1f * timeSeconds)))), z: 0f);
        var sway = (1.5f * MathF.Sin(x: (0.9f * timeSeconds)));

        transforms[(m_slotBase + GhostSlotOffset)] = (m_scene.GhostReady
            ? new DynamicTransform(Orientation: YawQuaternion(degrees: (m_scene.GhostYawDegrees + sway)), Position: (m_scene.GhostPosition + hover))
            : new DynamicTransform(Orientation: Quaternion.Identity, Position: hiddenPosition));

        transforms[(m_slotBase + DragSlotOffset)] = ((m_scene.Dragging && (m_scene.SelectedPlacement is { } dragging))
            ? new DynamicTransform(Orientation: YawQuaternion(degrees: dragging.YawDegrees), Position: dragging.Position)
            : new DynamicTransform(Orientation: Quaternion.Identity, Position: hiddenPosition));
    }

    private void EmitTerrain(SdfProgramBuilder builder) {
        foreach (var patch in m_scene.Terrain) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: WorldPalette.MaterialColor(slot: patch.Material)));

            _ = builder.ResetPoint().Translate(offset: patch.Center).Box(halfExtents: patch.HalfExtents, material: material, round: 0.02f);
        }
    }
    private void EmitLights(SdfProgramBuilder builder) {
        foreach (var light in m_scene.Lights) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: (light.Color * light.Intensity), Emissive: (0.8f + light.Intensity)));

            _ = builder.ResetPoint().Translate(offset: light.Position).Sphere(material: material, radius: LightRadius);
        }
    }
    private void EmitWalkOverrideGhosts(SdfProgramBuilder builder) {
        if (m_scene.WalkOverrides.Count == 0) {
            return;
        }

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: WalkOverrideAlbedo, Emissive: 0.5f));

        foreach (var entry in m_scene.WalkOverrides) {
            var center = new Vector3(x: (0.5f * (entry.MinX + entry.MaxX)), y: m_scene.Bounds.FloorY, z: (0.5f * (entry.MinZ + entry.MaxZ)));
            var halfExtents = new Vector3(x: (0.5f * (entry.MaxX - entry.MinX)), y: 0.01f, z: (0.5f * (entry.MaxZ - entry.MinZ)));

            // A plain solid plate. Onion is a field operation — it rewrites the running distance as abs(d) - t — and mapCore's
            // accumulator is never reset (ResetPoint resets the POINT, not result.distance). So it shelled the terrain and
            // the lamps emitted before it, and NESTED with every earlier ghost's onion. Measured against the exact mapCore
            // arithmetic with two overrides: a terrain slice fell from 83% solid to 15%, and its top rose by 0.06 —
            // burying the very 0.02-thick plates the op preceded, and deeper for every extra override. No emission order
            // can scope an onion to one of SEVERAL markers in a flat-accumulator program; see the accumulator rule on
            // SdfBlendOp.
            _ = builder.ResetPoint().Translate(offset: center).Box(halfExtents: halfExtents, material: material, round: 0f);
        }
    }

    // One placement = one (or several, auto-split) static instance(s): the resolved creation's shape chain replayed
    // with baked Translate/Rotate/Scale under the placement's own transform, wrapped in the placement's optional
    // FOLD ops (SymmetryX/Z for Mirror, WallpaperFold for Pattern — both BEFORE the repeat, per the frozen wire
    // format) and an optional RepeatLimited for its repeat block. SmoothUnion inside the creation's own chain stays
    // inside THIS instance (structural: each placement — or split segment — is its own instance, so a smooth blend
    // never reaches across a maskable boundary). A mirrored/patterned chain forgoes the whole-chain instance skip
    // (the settled cull contract — non-rigid ops disqualify the segment sphere); do not "fix" that.
    private void EmitPlacement(SdfProgramBuilder builder, Dictionary<string, int[]> paletteByHash, WorldPlacement placement) {
        if (m_scene.ResolveCreation(hash: placement.SourceHash, store: m_store) is not { } creation) {
            return; // A placement whose source no longer resolves in the store renders as nothing (load already
                    // drops these; a live cache eviction mid-session degrades the same way rather than throwing).
        }

        var paletteIds = ResolvePalette(creation: creation, hash: placement.SourceHash, paletteByHash: paletteByHash, builder: builder);
        var reach = CreationReach(creation: creation);
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));
        // A patterned placement's geometry spans the fold lattice — the instance bound must cover it (a too-tight
        // bound would CULL real fold copies at tile edges; an unlimited fold gets an effectively-infinite bound
        // that simply never culls, which is correct and the author's own performance choice).
        var foldSpan = PatternSpan(pattern: placement.Pattern);

        if ((placement.Repeat is not { } repeat) || (repeat.TotalCount <= 1)) {
            var boundRadius = (((reach + foldSpan) * placement.Scale) + PlacementBoundMargin);

            _ = builder.BeginInstance(boundCenter: placement.Position, boundRadius: boundRadius);
            EmitPlacedShapes(builder: builder, creation: creation, paletteIds: paletteIds, placement: in placement, placementOrigin: placement.Position, placementRotation: rotation, repeatLimit: null, repeatSpacing: null);
            EmitTextRuns(builder: builder, creation: creation, paletteIds: paletteIds, placement: in placement, placementOrigin: placement.Position, placementRotation: rotation, repeatLimit: null, repeatSpacing: null);
            _ = builder.EndInstance();

            return;
        }

        // Auto-split: a row longer than MaxRepeatPerSegment on either axis becomes several placements internally —
        // each segment's own RepeatLimited span, so no single instance bound covers more than the cap's worth of
        // copies (a full-length row's enclosing sphere would otherwise defeat the tile cull for the whole row).
        var segmentsX = Math.Max(val1: 1, val2: (((repeat.CountX + WorldScene.MaxRepeatPerSegment) - 1) / WorldScene.MaxRepeatPerSegment));
        var segmentsZ = Math.Max(val1: 1, val2: (((repeat.CountZ + WorldScene.MaxRepeatPerSegment) - 1) / WorldScene.MaxRepeatPerSegment));

        for (var segmentX = 0; (segmentX < segmentsX); segmentX++) {
            var countX = SegmentCount(total: repeat.CountX, segment: segmentX, segments: segmentsX);

            if (countX <= 0) {
                continue;
            }

            for (var segmentZ = 0; (segmentZ < segmentsZ); segmentZ++) {
                var countZ = SegmentCount(total: repeat.CountZ, segment: segmentZ, segments: segmentsZ);

                if (countZ <= 0) {
                    continue;
                }

                var offsetX = (SegmentStartIndex(total: repeat.CountX, segment: segmentX, segments: segmentsX) * repeat.SpacingX);
                var offsetZ = (SegmentStartIndex(total: repeat.CountZ, segment: segmentZ, segments: segmentsZ) * repeat.SpacingZ);
                var segmentOrigin = (placement.Position + Vector3.Transform(value: new Vector3(x: offsetX, y: 0f, z: offsetZ), rotation: rotation));
                var span = new Vector3(x: (((countX - 1) * repeat.SpacingX) * 0.5f), y: 0f, z: (((countZ - 1) * repeat.SpacingZ) * 0.5f));
                var segmentCenter = (segmentOrigin + Vector3.Transform(value: span, rotation: rotation));
                var segmentReach = ((reach + foldSpan) + span.Length());
                var boundRadius = ((segmentReach * placement.Scale) + PlacementBoundMargin);

                _ = builder.BeginInstance(boundCenter: segmentCenter, boundRadius: boundRadius);
                EmitPlacedShapes(
                    builder: builder,
                    creation: creation,
                    paletteIds: paletteIds,
                    placement: in placement,
                    placementOrigin: segmentOrigin,
                    placementRotation: rotation,
                    repeatLimit: new Vector3(x: (countX - 1), y: 0f, z: (countZ - 1)),
                    repeatSpacing: new Vector3(x: repeat.SpacingX, y: 0f, z: repeat.SpacingZ)
                );
                EmitTextRuns(
                    builder: builder,
                    creation: creation,
                    paletteIds: paletteIds,
                    placement: in placement,
                    placementOrigin: segmentOrigin,
                    placementRotation: rotation,
                    repeatLimit: new Vector3(x: (countX - 1), y: 0f, z: (countZ - 1)),
                    repeatSpacing: new Vector3(x: repeat.SpacingX, y: 0f, z: repeat.SpacingZ)
                );
                _ = builder.EndInstance();
            }
        }
    }

    // Appends the placement's optional fold ops onto an already-rotated chain, BEFORE the repeat (the frozen
    // order): Mirror = SymmetryX/Z in the placement's LOCAL frame; Pattern = WallpaperFold on the ground plane.
    // The probe's per-instance chain reserves exactly one of each plus the RepeatLimited — a real chain can carry
    // at most that set, so the reservation holds by construction.
    private static SdfProgramBuilder AppendFoldOps(SdfProgramBuilder chain, in WorldPlacement placement) {
        if (string.Equals(a: placement.Mirror, b: "z", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            chain = chain.SymmetryZ();
        } else if (string.Equals(a: placement.Mirror, b: "x", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            chain = chain.SymmetryX();
        }

        if (placement.Pattern is { } pattern) {
            chain = chain.WallpaperFold(
                cell: new Vector2(x: MathF.Max(x: pattern.CellWidth, y: 0.0001f), y: MathF.Max(x: pattern.CellHeight, y: 0.0001f)),
                group: ParseWallpaperGroup(name: pattern.Group),
                limit: new Vector2(x: (pattern.LimitX ?? UnlimitedFoldLimit), y: (pattern.LimitZ ?? UnlimitedFoldLimit)),
                materialStride: (pattern.MaterialStride ?? 0)
            );
        }

        return chain;
    }
    private static SdfWallpaperGroup ParseWallpaperGroup(string? name) =>
        (((name is { Length: > 0 }) && Enum.TryParse<SdfWallpaperGroup>(ignoreCase: true, result: out var group, value: name)) ? group : SdfWallpaperGroup.P1);

    // The RepeatLimited-style cell-count value an unlimited fold axis uses — large enough to read as infinite,
    // small enough that cell × limit stays finite in the bound math.
    private const float UnlimitedFoldLimit = 1e6f;

    // The extra reach a pattern's fold lattice adds past the creation's own extent (0 for no pattern) — cell size ×
    // cell-count limit per axis, combined radially; an unlimited axis saturates to an effectively-infinite span.
    private static float PatternSpan(WorldPlacementPattern? pattern) {
        if (pattern is not { } value) {
            return 0f;
        }

        var spanX = ((value.LimitX is { } limitX) ? (MathF.Max(x: value.CellWidth, y: 0.0001f) * limitX) : UnlimitedFoldLimit);
        var spanZ = ((value.LimitZ is { } limitZ) ? (MathF.Max(x: value.CellHeight, y: 0.0001f) * limitZ) : UnlimitedFoldLimit);

        return MathF.Min(x: MathF.Sqrt(x: ((spanX * spanX) + (spanZ * spanZ))), y: UnlimitedFoldLimit);
    }

    // Emits a STATIC placement's shapes, each as ITS OWN segment carrying the FULL placement-transform prefix
    // (Translate → Rotate → the optional fold ops → the optional repeat → the uniform placement Scale) composed with
    // the shape's own local Translate/Rotate/Scale, then the primitive. The prefix MUST ride EVERY shape's segment:
    // the shader splits the instruction stream at each ResetPoint and a segment's transforms are local to that
    // segment, so a single shared prefix segment (with no primitive of its own) is DEAD — every shape would render at
    // the creation's local origin at unit scale, ignoring where and how big the stamp was placed. Uniform placement
    // scale commutes with the per-shape rotations, so the composition is shear-free (a single T·R·S per shape after
    // the fold/repeat domain ops), and the probe reserves this full per-shape op budget.
    private static void EmitPlacedShapes(SdfProgramBuilder builder, CreationDocument creation, IReadOnlyList<int> paletteIds, in WorldPlacement placement, Vector3 placementOrigin, Quaternion placementRotation, Vector3? repeatSpacing, Vector3? repeatLimit) {
        var placementScale = placement.Scale;

        foreach (var shape in (creation.Shapes ?? [])) {
            var material = paletteIds[Math.Clamp(value: (shape.Material ?? 0), max: (paletteIds.Count - 1), min: 0)];
            var chain = AppendFoldOps(chain: builder.ResetPoint().Translate(offset: placementOrigin).Rotate(rotation: placementRotation), placement: in placement);

            if ((repeatSpacing is { } spacing) && (repeatLimit is { } limit)) {
                chain = chain.RepeatLimited(spacing: spacing, limit: limit);
            }

            chain = chain
                .Scale(scale: new Vector3(value: placementScale))
                .Translate(offset: shape.Position)
                .Rotate(rotation: shape.Rotation)
                .Scale(scale: shape.Scale);
            _ = AvatarDefinition.AppendPrimitive(blend: (shape.Blend ?? SdfBlendOp.Union), chain: chain, material: material, smooth: (shape.Smooth ?? 0f), type: shape.Type);
        }
    }

    // Expands a placement's creation TEXT RUNS into Glyph shapes INSIDE the same static instance as its boxes: each
    // glyph is a self-contained segment carrying the FULL placement-transform prefix (Translate → Rotate → the optional
    // fold ops → the optional repeat → the uniform placement Scale), then the glyph's own in-plane Translate/Rotate —
    // exactly as EmitPlacedShapes bakes a box, so the lettering rides the stamp's place/rotate/fold/repeat/scale. The
    // per-glyph layout (cell UVs, half-extents, plane centre) comes from the SHARED FontAtlas + TextLayout the diegetic
    // UI uses, so a run samples the same atlas the engine uploads; EmHeight/Depth are in the creation's LOCAL units and
    // scale to world through the placement Scale (its distanceScale channel), like every box half-extent. Engrave =
    // Subtraction (carved recess), emboss = Union (proud relief); the slab straddles the surface, so it is never
    // coplanar. No-op with no atlas bound (off-Windows), no runs, or empty text — the placement's boxes still stamp.
    // A run's glyph count competes for the SAME MaxShapesPerStamp budget the boxes do (WorldScene.StampShapeCount),
    // and each glyph's op-chain is no longer than the probe's per-shape reservation, so the capacity envelope holds
    // with NO probe growth (a glyph is just another shape in the reserved MaxShapesPerStamp).
    private void EmitTextRuns(SdfProgramBuilder builder, CreationDocument creation, IReadOnlyList<int> paletteIds, in WorldPlacement placement, Vector3 placementOrigin, Quaternion placementRotation, Vector3? repeatSpacing, Vector3? repeatLimit) {
        if ((m_font is not { } font) || (creation.TextRuns is not { Count: > 0 } runs)) {
            return;
        }

        var placementScale = placement.Scale;
        var atlasWidth = (float)font.Width;
        var atlasHeight = (float)font.Height;

        foreach (var run in runs) {
            if (run.Text is not { Length: > 0 } text) {
                continue;
            }

            var material = paletteIds[Math.Clamp(value: (run.Material ?? 0), max: (paletteIds.Count - 1), min: 0)];
            var blend = (string.Equals(a: run.Mode, b: TextRunDocument.ModeEngrave, comparisonType: StringComparison.OrdinalIgnoreCase) ? SdfBlendOp.Subtraction : SdfBlendOp.Union);
            var emHeight = MathF.Max(x: run.EmHeight, y: 0.001f);
            var depth = MathF.Max(x: (run.Depth ?? 0.02f), y: 0.001f);
            var worldPerTexel = (emHeight / font.Size);
            var distanceScale = (font.DistanceRange * worldPerTexel);
            // The run plane in the creation's LOCAL frame (+X advance, +Y ascent) from the run's own rotation — the
            // same basis SdfProgramBuilder.Text derives, so the layout math matches the shared emission path.
            var right = Vector3.Normalize(value: Vector3.Transform(value: Vector3.UnitX, rotation: run.Rotation));
            var up = Vector3.Normalize(value: Vector3.Transform(value: Vector3.UnitY, rotation: run.Rotation));
            var layout = m_textLayout.Layout(atlas: font, text: text, scale: emHeight);
            // Centre the run on its authored Position: the baseline-left pen shifts left half the run width and down
            // half the cap height (Ascender·em), so Position is the run's visual centre.
            var origin = ((run.Position - (right * (0.5f * layout.Width))) - (up * ((0.5f * font.Metrics.Ascender) * emHeight)));

            foreach (var glyph in layout.Placements) {
                var atlasBounds = glyph.AtlasBounds;
                var planeBounds = glyph.PlaneBounds;
                var halfWidth = ((0.5f * (atlasBounds.Right - atlasBounds.Left)) * worldPerTexel);
                var halfHeight = ((0.5f * (atlasBounds.Bottom - atlasBounds.Top)) * worldPerTexel);
                var centre2D = new Vector2(x: (0.5f * (planeBounds.Left + planeBounds.Right)), y: (0.5f * (planeBounds.Bottom + planeBounds.Top)));
                var localCentre = ((origin + (right * centre2D.X)) + (up * centre2D.Y));
                var uvBottomLeft = new Vector2(x: (atlasBounds.Left / atlasWidth), y: (atlasBounds.Bottom / atlasHeight));
                var uvTopRight = new Vector2(x: (atlasBounds.Right / atlasWidth), y: (atlasBounds.Top / atlasHeight));
                var chain = AppendFoldOps(chain: builder.ResetPoint().Translate(offset: placementOrigin).Rotate(rotation: placementRotation), placement: in placement);

                if ((repeatSpacing is { } spacing) && (repeatLimit is { } limit)) {
                    chain = chain.RepeatLimited(spacing: spacing, limit: limit);
                }

                _ = chain
                    .Scale(scale: new Vector3(value: placementScale))
                    .Translate(offset: localCentre)
                    .Rotate(rotation: run.Rotation)
                    .Glyph(uvBottomLeft: uvBottomLeft, uvTopRight: uvTopRight, halfWidth: halfWidth, halfHeight: halfHeight, extrudeHalfDepth: depth, distanceScale: distanceScale, material: material, blend: blend);
            }
        }
    }

    // Replays a creation's authored shape chain at the creation's LOCAL frame — the ghost/drag DYNAMIC slots, whose
    // per-frame TransformDynamic positions the whole instance (a dynamic instance applies its slot transform at the
    // instance level, so the shapes need only their own local transforms here). A STATIC placement instead uses
    // EmitPlacedShapes, which bakes the placement transform into every shape's own segment.
    private static void EmitCreationChain(SdfProgramBuilder builder, CreationDocument creation, IReadOnlyList<int> paletteIds) {
        foreach (var shape in (creation.Shapes ?? [])) {
            var material = paletteIds[Math.Clamp(value: (shape.Material ?? 0), max: (paletteIds.Count - 1), min: 0)];
            var chain = builder.ResetPoint().Translate(offset: shape.Position).Rotate(rotation: shape.Rotation).Scale(scale: shape.Scale);

            _ = AvatarDefinition.AppendPrimitive(blend: (shape.Blend ?? SdfBlendOp.Union), chain: chain, material: material, smooth: (shape.Smooth ?? 0f), type: shape.Type);
        }
    }
    private void EmitGhostSlot(SdfProgramBuilder builder, Dictionary<string, int[]> paletteByHash) {
        var ghostMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GhostAlbedo, Emissive: 0.5f));
        var reach = 0.6f;

        _ = builder.BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: ((reach * m_scene.GhostScale) + PlacementBoundMargin), slot: (m_slotBase + GhostSlotOffset));

        if ((m_scene.GhostReady) && (m_scene.ResolveCreation(hash: m_scene.GhostSourceHash!, store: m_store) is { } creation)) {
            var paletteIds = ResolvePalette(creation: creation, hash: m_scene.GhostSourceHash!, paletteByHash: paletteByHash, builder: builder);

            _ = builder.ResetPoint().TransformDynamic(slot: (m_slotBase + GhostSlotOffset)).Scale(scale: new Vector3(value: m_scene.GhostScale));
            EmitCreationChainWithHighlight(builder: builder, creation: creation, paletteIds: paletteIds, highlightMaterial: ghostMaterial);
        } else {
            // No creation armed yet: a small hologram marker so the slot still draws something coherent (never an
            // empty/invisible instance the player can't locate).
            _ = builder.ResetPoint().TransformDynamic(slot: (m_slotBase + GhostSlotOffset)).Sphere(material: ghostMaterial, radius: 0.25f);
        }

        _ = builder.EndInstance();
    }
    private void EmitDragSlot(SdfProgramBuilder builder, Dictionary<string, int[]> paletteByHash) {
        var highlightMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GhostAlbedo, Emissive: 0.9f));
        var dragging = (m_scene.Dragging ? m_scene.SelectedPlacement : null);
        var reach = ((dragging is { } selected) ? (CreationReach(creation: (m_scene.ResolveCreation(hash: selected.SourceHash, store: m_store))) * selected.Scale) : 0.5f);

        _ = builder.BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: (reach + PlacementBoundMargin), slot: (m_slotBase + DragSlotOffset));

        if ((dragging is { } placement) && (m_scene.ResolveCreation(hash: placement.SourceHash, store: m_store) is { } creation)) {
            var paletteIds = ResolvePalette(creation: creation, hash: placement.SourceHash, paletteByHash: paletteByHash, builder: builder);

            _ = builder.ResetPoint().TransformDynamic(slot: (m_slotBase + DragSlotOffset)).Scale(scale: new Vector3(value: placement.Scale));
            EmitCreationChainWithHighlight(builder: builder, creation: creation, paletteIds: paletteIds, highlightMaterial: highlightMaterial);
        } else {
            // Not dragging: a hidden zero-radius stub (the dynamic slot itself parks below the floor via
            // PackTransforms; this draws nothing productive but keeps the instance count/probe shape constant).
            _ = builder.ResetPoint().TransformDynamic(slot: (m_slotBase + DragSlotOffset)).Sphere(material: highlightMaterial, radius: 0.001f);
        }

        _ = builder.EndInstance();
    }

    // Like EmitCreationChain, but every shape's material is overridden to the same highlight — the ghost/drag
    // preview reads as a uniform hologram tint regardless of the creation's own palette.
    private static void EmitCreationChainWithHighlight(SdfProgramBuilder builder, CreationDocument creation, IReadOnlyList<int> paletteIds, int highlightMaterial) {
        _ = paletteIds; // Palette registration already ran (materials must be added so program-relative ids stay
                        // stable across builds); the preview simply paints every shape with the highlight instead.

        foreach (var shape in (creation.Shapes ?? [])) {
            var chain = builder.ResetPoint().Translate(offset: shape.Position).Rotate(rotation: shape.Rotation).Scale(scale: shape.Scale);

            _ = AvatarDefinition.AppendPrimitive(blend: (shape.Blend ?? SdfBlendOp.Union), chain: chain, material: highlightMaterial, smooth: (shape.Smooth ?? 0f), type: shape.Type);
        }
    }

    // Registers a creation's palette ONCE per program build, cached by content hash — 128 placements referencing the
    // same handful of creations must not multiply materials. Returns the program-relative material ids, indexed the
    // same as the creation's own palette slots. The registration clamps to CreatorScene.PaletteSize (16) entries —
    // the probe's per-creation material reservation — so an over-long authored palette can never outgrow the
    // envelope (shapes referencing a clamped-off slot color from the last kept entry).
    private static int[] ResolvePalette(CreationDocument creation, string hash, Dictionary<string, int[]> paletteByHash, SdfProgramBuilder builder) {
        if (paletteByHash.TryGetValue(key: hash, value: out var cached)) {
            return cached;
        }

        var palette = (creation.Palette ?? []);
        var count = Math.Min(val1: palette.Count, val2: CreatorScene.PaletteSize);
        var ids = new int[Math.Max(val1: count, val2: 1)];

        if (count == 0) {
            ids[0] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.7f)));
        } else {
            for (var index = 0; (index < count); index++) {
                var entry = palette[index];

                ids[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: entry.Albedo, Emissive: (entry.Emissive ?? 0f), Shininess: (entry.Shininess ?? 32f), Specular: (entry.Specular ?? 0f)));
            }
        }

        paletteByHash[hash] = ids;

        return ids;
    }

    // A creation's worst-case reach from its own local origin — the largest per-shape reach across every authored
    // shape AND every text run, so the placement's instance bound covers the whole replayed chain (a masked-out tile
    // must never clip a glyph that reaches past the boxes).
    private static float CreationReach(CreationDocument? creation) {
        if (creation is null) {
            return 0.6f;
        }

        var reach = 0f;
        var any = false;

        foreach (var shape in (creation.Shapes ?? [])) {
            reach = MathF.Max(x: reach, y: (shape.Position.Length() + AvatarDefinition.Reach(scale: shape.Scale, type: shape.Type)));
            any = true;
        }

        foreach (var run in (creation.TextRuns ?? [])) {
            // A generous run reach: its anchor offset + half the run's world extent (~0.6 em per glyph advance) + the
            // relief depth. A fat bound only costs a rare extra evaluation; a too-tight one would cull real glyphs.
            var runReach = ((run.Position.Length() + ((0.6f * MathF.Max(x: run.EmHeight, y: 0.001f)) * MathF.Max(x: run.GlyphCount, y: 1))) + (run.Depth ?? 0.02f));

            reach = MathF.Max(x: reach, y: runReach);
            any = true;
        }

        return (any ? reach : 0.6f);
    }
    private static Quaternion YawQuaternion(float degrees) =>
        Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (degrees * (MathF.PI / 180f)));

    // The number of copies segment `segment` of `segments` carries, splitting `total` as evenly as the
    // MaxRepeatPerSegment cap allows (the last segment absorbs any remainder short of the cap).
    private static int SegmentCount(int total, int segment, int segments) {
        var start = SegmentStartIndex(total: total, segment: segment, segments: segments);
        var end = SegmentStartIndex(total: total, segment: (segment + 1), segments: segments);

        return Math.Max(val1: 0, val2: (end - start));
    }
    private static int SegmentStartIndex(int total, int segment, int segments) =>
        Math.Min(val1: total, val2: (segment * WorldScene.MaxRepeatPerSegment));

    /// <summary>Emits the SYNTHETIC WORST CASE the frame source measures once at construction: MaxPlacements SEGMENT
    /// instances, each the densest legal stamp (the 7-op chain Translate + Rotate + SymmetryX + WallpaperFold +
    /// RepeatLimited + Scale after ResetPoint, wrapping MaxShapesPerStamp full 5-op shapes), the terrain/light/
    /// walk-override lists at their caps, both dynamic slots in their armed worst case, and the worst-case
    /// distinct-palette material count ((MaxPlacements + 1 ghost) distinct creations × 16 entries + one material
    /// per terrain patch and light + the three accents) — never rendered, only Build()-measured. Any new optional
    /// emission this renderer grows MUST grow this probe in the same change (the sdf-world skill's capacity-probe
    /// doctrine); the model's authoring budgets (<see cref="WorldScene.TotalSegmentCount"/>, the per-stamp shape
    /// budget, the terrain/light/override caps, the 16-entry palette clamp) make a real emission exceeding this
    /// impossible by construction.</summary>
    private static void EmitProbe(SdfProgramBuilder builder) {
        // Worst-case distinct materials: every placement references a DISTINCT creation with a full 16-slot palette
        // (plus one more distinct creation armed on the ghost) — the real ceiling only relaxes this via the
        // per-hash cache, so probing as if every placement were unique is the conservative bound.
        var placementMaterialIds = new int[CreatorScene.PaletteSize];

        for (var creationIndex = 0; (creationIndex < (WorldScene.MaxPlacements + 1)); creationIndex++) {
            for (var index = 0; (index < CreatorScene.PaletteSize); index++) {
                var id = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));

                if (creationIndex == 0) {
                    placementMaterialIds[index] = id;
                }
            }
        }

        var ghostMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GhostAlbedo));
        var highlightMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GhostAlbedo, Emissive: 0.9f));
        var walkOverrideMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: WalkOverrideAlbedo));

        // World-set (unbounded) worst case at each list's cap: terrain and lights each register ONE material per
        // entry (exactly as the real emission does), walk overrides share one. The items are spaced FAR apart
        // (100 units) so the program's segment-MERGE pass can never collapse consecutive probe segments — a merged
        // probe would under-reserve the segment directory a real scattered scene needs.
        const float probeSpread = 100f;

        for (var index = 0; (index < WorldScene.MaxTerrainPatches); index++) {
            var terrainMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: WorldPalette.MaterialColor(slot: index)));

            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (index * probeSpread), y: 0f, z: 0f)).Box(halfExtents: new Vector3(value: 0.5f), material: terrainMaterial, round: 0.02f);
        }

        for (var index = 0; (index < WorldScene.MaxLights); index++) {
            var lightMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: LightAlbedo, Emissive: 1f));

            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (index * probeSpread), y: 1f, z: 0f)).Sphere(material: lightMaterial, radius: LightRadius);
        }

        // KEEP IN SYNC with EmitWalkOverrideGhosts: the probe must remain a worst-case upper bound of the live emission,
        // instruction for instruction. Its Onion went with the live one (see there for why).
        for (var index = 0; (index < WorldScene.MaxWalkOverrides); index++) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (index * probeSpread), y: 0.01f, z: 1f)).Box(halfExtents: new Vector3(x: 0.5f, y: 0.01f, z: 0.5f), material: walkOverrideMaterial, round: 0f);
        }

        // MaxPlacements densest-legal instances. EmitPlacedShapes bakes the placement transform into EVERY shape's
        // own segment (the shader resets the point at each segment, so a shared prefix segment would be dead), so the
        // worst case is per-shape: the full placement prefix (Translate + Rotate + both fold ops + the repeat + the
        // placement Scale) followed by the shape's own Translate + Rotate + Scale + primitive — reserved for every
        // shape so a real folded/patterned/repeated stamp always fits the once-sized buffers.
        for (var index = 0; (index < WorldScene.MaxPlacements); index++) {
            var center = new Vector3(x: index, y: 4f, z: 0f);

            _ = builder.BeginInstance(boundCenter: center, boundRadius: 12f);

            for (var shapeIndex = 0; (shapeIndex < WorldScene.MaxShapesPerStamp); shapeIndex++) {
                var material = placementMaterialIds[(shapeIndex % CreatorScene.PaletteSize)];

                _ = builder.ResetPoint()
                    .Translate(offset: center)
                    .Rotate(rotation: Quaternion.Identity)
                    .SymmetryX()
                    .WallpaperFold(cell: new Vector2(x: 2f, y: 2f), group: SdfWallpaperGroup.P1, limit: new Vector2(x: 4f, y: 4f))
                    .RepeatLimited(spacing: new Vector3(x: 1f, y: 0f, z: 1f), limit: new Vector3(x: WorldScene.MaxRepeatPerSegment, y: 0f, z: WorldScene.MaxRepeatPerSegment))
                    .Scale(scale: Vector3.One)
                    .Translate(offset: Vector3.Zero)
                    .Rotate(rotation: Quaternion.Identity)
                    .Scale(scale: Vector3.One)
                    .Sphere(material: material, radius: 0.3f);
            }

            _ = builder.EndInstance();
        }

        // The two dynamic slots, in their armed/dragging worst case: the 3-op head chain plus MaxShapesPerStamp
        // full 5-op shapes each — exactly the real ghost/drag emission's largest form.
        EmitProbeDynamicSlot(builder: builder, material: ghostMaterial, slot: GhostSlotOffset);
        EmitProbeDynamicSlot(builder: builder, material: highlightMaterial, slot: DragSlotOffset);
    }
    private static void EmitProbeDynamicSlot(SdfProgramBuilder builder, int material, int slot) {
        _ = builder.BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: (0.6f * WorldScene.MaxScale), slot: slot);
        _ = builder.ResetPoint().TransformDynamic(slot: slot).Scale(scale: new Vector3(value: WorldScene.MaxScale));

        for (var shapeIndex = 0; (shapeIndex < WorldScene.MaxShapesPerStamp); shapeIndex++) {
            _ = builder.ResetPoint().Translate(offset: Vector3.Zero).Rotate(rotation: Quaternion.Identity).Scale(scale: Vector3.One).Sphere(material: material, radius: 0.3f);
        }

        _ = builder.EndInstance();
    }
}

/// <summary>The world's shared terrain/decoration material palette — a small fixed set of slot indices
/// <c>TerrainPatchDocument.Material</c> references, distinct from a creation's own 16-slot palette. Simple and
/// presentation-only; the dusk whimsy grows this later.</summary>
public static class WorldPalette {
    private static readonly Vector3[] Colors = [
        new Vector3(x: 0.42f, y: 0.42f, z: 0.46f), // 0: road/plaza gray
        new Vector3(x: 0.32f, y: 0.55f, z: 0.30f), // 1: lawn green
        new Vector3(x: 0.55f, y: 0.48f, z: 0.35f), // 2: dirt/path tan
        new Vector3(x: 0.30f, y: 0.32f, z: 0.40f), // 3: dusk-slate
    ];

    /// <summary>Resolves a world palette slot to its color (out-of-range slots wrap).</summary>
    /// <param name="slot">The palette slot.</param>
    /// <returns>The slot's albedo.</returns>
    public static Vector3 MaterialColor(int slot) =>
        Colors[(((slot % Colors.Length) + Colors.Length) % Colors.Length)];
}
