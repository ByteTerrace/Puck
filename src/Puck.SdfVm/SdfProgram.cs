using System.Numerics;

namespace Puck.SdfVm;

// Packed layout (each element is one uvec4 = 16 bytes); KEEP IN SYNC with Shaders/Sdf/sdf-vm.hlsli sdfWords[] indexing:
//   word[0]             = (instructionCount, materialCount, dataOffset, materialOffset) in uvec4 units
//   [1 .. 1+N)          = instruction headers (op, shape, blend, material)
//   [dataOffset ..)     = instruction data, 2 uvec4 per instruction (data0, data1 as float bits)
//   [materialOffset ..) = materials, 2 uvec4 each (m0 = albedo.rgb + emissive, m1 = specular + shininess + 2 reserved,
//                         all as float bits)
//   [materialOffset + 2*materialCount ..) = the per-SHAPE bounding-sphere table, 2 uvec4 per instruction:
//                         b0 = center/offset.xyz + radius (float bits), b1 = (mode, dynamicSlot, index, index+1).
//   [.. + 2*instructionCount ..) = the SEGMENT directory: one (segmentCount, 0, 0, 0) header uvec4, then 2 uvec4 per
//                         segment — s0 = center/offset.xyz + radius (float bits), s1 = (mode, dynamicSlot, first,
//                         end) — the chain-level skip map()'s outer loop walks. Both tables' offsets derive from
//                         word[0]'s existing lanes, so the header is unchanged. See PackBounds.
//   [.. + 1 + 2*segmentCount ..) = the INSTANCE directory (world render path only): one (instanceCount, 0, 0, 0)
//                         header uvec4, then 2 uvec4 per instance — i0 = bound center/offset.xyz + radius (float
//                         bits), i1 = (mode, dynamicSlot, segmentFirst, segmentEnd) — segmentFirst/segmentEnd index
//                         the SEGMENT directory above (not raw instructions): every segment in that range is
//                         guaranteed (by construction — AnalyzeBounds splits/merges never cross an instance
//                         boundary) to be owned by exactly that instance, so mapCore (sdf-vm.hlsli) evaluates the
//                         whole range when the instance's per-tile mask bit is set and never touches it otherwise,
//                         and the BEAM prepass reads only i0/i1 for its per-tile cull. See PackInstances.
//   [.. + 1 + 2*instanceCount ..) = the WORLD-SEGMENT list (world render path only): one (worldSegmentCount, 0, 0,
//                         0) header uvec4, then one uvec4 per WORLD segment (only .x used — kept uvec4-granular for
//                         simplicity), value = the segment-directory index of a segment owned by NO instance,
//                         ascending. mapCore merges this always-evaluated list with the visible instances' segment
//                         ranges by ascending segment index (the flat stream's blend order), so a map() call costs
//                         O(world segments + visible instances' segments), never O(all segments). See
//                         PackWorldSegments.
// Screen surfaces are a SEPARATE fixed-size side table (ScreenSurfaceWords), not part of the sdfWords stream above —
// they are shading-only data the world renderer's Stage 1 binds into its own buffer, ALWAYS sized to
// SdfProgramBuilder.MaxScreenSurfaces and indexed DIRECTLY by screen index (KEEP IN SYNC with SdfWorldEngine and
// sdf-world.hlsli's ScreenSurfaceData).
public sealed class SdfProgram {
    // Bounding-sphere entry modes (KEEP IN SYNC with the SDF_BOUND_* skip in Assets/Shaders/Sdf/sdf-vm.hlsli map()).
    private const uint BoundModeDynamic = 2;
    private const uint BoundModeStatic = 1;
    /// <summary>The float-safety inflation applied to every packed bound radius: the skip compares a HOST-float bound
    /// against a SHADER-float running minimum, so the radius is padded past both precisions' worst rounding — a skip
    /// then only fires with real margin, keeping the skipped field bit-identical to full evaluation.</summary>
    private const float BoundRadiusPadding = 0.001f;
    private const float BoundRadiusScale = 1.0001f;
    /// <summary>The PARKED-instance sentinel radius (KEEP IN SYNC with sdf-world.hlsli's <c>collectInstanceMaskWord</c>
    /// negative-radius skip). An <see cref="SdfInstanceRange.Active"/> = <see langword="false"/> instance packs this
    /// instead of a real (always non-negative) radius, so the beam prepass rejects it with one <c>bound.w &lt; 0</c>
    /// branch — no sphere-vs-cone math, mask bit left 0 — while the slot still occupies its reserved capacity. Chosen
    /// well below any legitimate rounding of a real radius toward 0 so the branch never misfires on a genuine bound.</summary>
    private const float ParkedBoundRadius = -1f;
    /// <summary>The largest legal dynamic-transform slot index: the derived capacity is <c>slot + 1</c>, which must
    /// itself fit in an <see cref="int"/>.</summary>
    public const int MaxDynamicTransformSlot = (int.MaxValue - 1);
    /// <summary>Each packed screen-surface entry's uvec4 (16-byte) stride: right.xyz+halfWidth, up.xyz+halfHeight,
    /// origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli's ScreenSurfaceData).</summary>
    private const int ScreenSurfaceVectorsPerEntry = 3;
    private const int WordsPerVector = 4;

    private readonly SdfInstanceRange[] m_instances;
    private readonly SdfInstruction[] m_instructions;
    private readonly SdfScreenSurface[] m_screenSurfaces;
    private readonly uint[] m_screenSurfaceWords;
    private readonly uint[] m_words;

    public SdfProgram(IReadOnlyList<SdfInstruction> instructions, IReadOnlyList<SdfMaterial> materials, IReadOnlyList<SdfInstanceRange>? instances = null, IReadOnlyList<SdfScreenSurface>? screenSurfaces = null) {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(materials);

        m_instances = [.. (instances ?? [])];
        m_instructions = [.. instructions];
        m_screenSurfaces = [.. (screenSurfaces ?? [])];

        if (m_instances.Length > SdfProgramBuilder.MaxInstances) {
            throw new ArgumentException(message: $"A program may declare at most {SdfProgramBuilder.MaxInstances} instances; got {m_instances.Length}.", paramName: nameof(instances));
        }

        if (m_screenSurfaces.Length > SdfProgramBuilder.MaxScreenSurfaces) {
            throw new ArgumentException(message: $"A program may declare at most {SdfProgramBuilder.MaxScreenSurfaces} screen surfaces; got {m_screenSurfaces.Length}.", paramName: nameof(screenSurfaces));
        }

        RequiredDynamicTransformCapacity = CalculateRequiredDynamicTransformCapacity(instructions: m_instructions, instances: m_instances);

        // ALWAYS the full MaxScreenSurfaces capacity, indexed by ScreenIndex (not declaration order): the shader
        // resolves a hit's surface with a direct index, no search, so an undeclared slot's all-zero entry is simply
        // never addressed (no material id in a consistent program points at it).
        m_screenSurfaceWords = new uint[(SdfProgramBuilder.MaxScreenSurfaces * ScreenSurfaceVectorsPerEntry * WordsPerVector)];

        foreach (var surface in m_screenSurfaces) {
            var entryBase = (surface.ScreenIndex * ScreenSurfaceVectorsPerEntry * WordsPerVector);

            WriteVector4(words: m_screenSurfaceWords, baseIndex: entryBase, x: surface.Right.X, y: surface.Right.Y, z: surface.Right.Z, w: surface.HalfWidth);
            WriteVector4(words: m_screenSurfaceWords, baseIndex: (entryBase + WordsPerVector), x: surface.Up.X, y: surface.Up.Y, z: surface.Up.Z, w: surface.HalfHeight);
            WriteVector4(words: m_screenSurfaceWords, baseIndex: (entryBase + (2 * WordsPerVector)), x: surface.Origin.X, y: surface.Origin.Y, z: surface.Origin.Z, w: 0f);
        }

        // The bounds analysis runs FIRST: the segment directory's length is part of the packed layout below.
        var (shapeBounds, segments) = AnalyzeBounds();

        var instructionCount = instructions.Count;
        var materialCount = materials.Count;
        var worldSegmentCount = 0;

        foreach (var segment in segments) {
            if (segment.InstanceIndex < 0) {
                worldSegmentCount++;
            }
        }

        var dataOffsetVectors = (1 + instructionCount);
        var materialOffsetVectors = (dataOffsetVectors + (2 * instructionCount));
        var boundsOffsetVectors = (materialOffsetVectors + (2 * materialCount));
        var segmentOffsetVectors = (boundsOffsetVectors + (2 * instructionCount));
        var instanceOffsetVectors = (segmentOffsetVectors + 1 + (2 * segments.Count));
        var worldSegmentOffsetVectors = (instanceOffsetVectors + 1 + (2 * m_instances.Length));
        var totalVectors = (worldSegmentOffsetVectors + 1 + worldSegmentCount);

        InstructionCount = instructionCount;
        m_words = new uint[(totalVectors * WordsPerVector)];

        m_words[0] = (uint)instructionCount;
        m_words[1] = (uint)materialCount;
        m_words[2] = (uint)dataOffsetVectors;
        m_words[3] = (uint)materialOffsetVectors;

        for (var index = 0; (index < instructionCount); index++) {
            var instruction = instructions[index];
            var headerBase = ((1 + index) * WordsPerVector);

            m_words[headerBase] = (uint)instruction.Op;
            m_words[(headerBase + 1)] = instruction.Shape;
            m_words[(headerBase + 2)] = instruction.Blend;
            m_words[(headerBase + 3)] = instruction.Material;

            var dataBase = ((dataOffsetVectors + (2 * index)) * WordsPerVector);

            WriteVector4(
                words: m_words,
                baseIndex: dataBase,
                w: instruction.Data0.W,
                x: instruction.Data0.X,
                y: instruction.Data0.Y,
                z: instruction.Data0.Z
            );
            WriteVector4(
                words: m_words,
                baseIndex: (dataBase + WordsPerVector),
                w: instruction.Data1.W,
                x: instruction.Data1.X,
                y: instruction.Data1.Y,
                z: instruction.Data1.Z
            );
        }

        for (var index = 0; (index < materialCount); index++) {
            var material = materials[index];
            var materialBase = ((materialOffsetVectors + (2 * index)) * WordsPerVector);

            WriteVector4(
                words: m_words,
                baseIndex: materialBase,
                w: material.Emissive,
                x: material.Albedo.X,
                y: material.Albedo.Y,
                z: material.Albedo.Z
            );
            WriteVector4(
                words: m_words,
                baseIndex: (materialBase + WordsPerVector),
                w: 0f,
                x: material.Specular,
                y: material.Shininess,
                z: 0f
            );
        }

        PackBounds(boundsOffsetVectors: boundsOffsetVectors, segmentOffsetVectors: segmentOffsetVectors, segments: segments, shapeBounds: shapeBounds);
        PackInstances(instanceOffsetVectors: instanceOffsetVectors, segments: segments);
        PackWorldSegments(segments: segments, worldSegmentCount: worldSegmentCount, worldSegmentOffsetVectors: worldSegmentOffsetVectors);
    }

    public int InstructionCount { get; }
    /// <summary>The per-(viewport, tile) instance-mask width in uints for THIS program: ceil(instance count / 32),
    /// never below 1 (a zero-instance program keeps one all-zero word so the mask buffer indexing stays uniform).
    /// <see cref="SdfWorldEngine"/> sizes its mask buffer from it and pushes the live uploaded program's value per
    /// frame as the kernels' indexing width (CompositeParams.instanceMaskWordCount); the reader's inner word
    /// iteration independently derives the same formula (KEEP IN SYNC with sdfInstanceMaskWordCount in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli).</summary>
    public int InstanceMaskWordCount => InstanceMaskWordCountFor(instanceCount: m_instances.Length);

    /// <summary>The per-(viewport, tile) instance-mask width in uints an <paramref name="instanceCount"/>-instance
    /// program derives (see <see cref="InstanceMaskWordCount"/>) — the one statement of the formula, also used by a
    /// host sizing an engine's mask buffer for a capacity envelope larger than its initial program.</summary>
    /// <param name="instanceCount">The instance count to derive the mask width for.</param>
    public static int InstanceMaskWordCountFor(int instanceCount) => Math.Max(1, ((instanceCount + 31) / 32));
    /// <summary>The minimum dynamic-transform slot capacity required to render this program without a shader reading
    /// past the supplied per-frame transform table. Equals one plus the highest <see cref="SdfOp.TransformDynamic"/>
    /// or dynamic-instance slot, or 0 for a static program.</summary>
    public int RequiredDynamicTransformCapacity { get; }
    /// <summary>The typed instructions the program was built from, in order — the source the packed
    /// <see cref="Words"/> are compiled from. Retained so a consumer (e.g. a ray-tracing instance extractor that
    /// needs per-primitive world bounds) can read the scene structure without decoding the packed word layout.</summary>
    public IReadOnlyList<SdfInstruction> Instructions => m_instructions;
    /// <summary>The per-object instances this program declared, in declaration order (matches the packed instance
    /// directory's index order — see the type-level remarks). Empty for a zero-instance (flat) program.</summary>
    public IReadOnlyList<SdfInstanceRange> Instances => m_instances;
    /// <summary>The screen surfaces this program declared, in declaration order — the source
    /// <see cref="ScreenSurfaceWords"/> is packed from (at each surface's <see cref="SdfScreenSurface.ScreenIndex"/>
    /// slot, not its position in this list). Empty for a program with none.</summary>
    public IReadOnlyList<SdfScreenSurface> ScreenSurfaces => m_screenSurfaces;
    /// <summary>The packed screen-surface side table (see the type-level remarks): always
    /// <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> 3-uvec4 (48-byte) entries, each at its declared screen
    /// index's slot; an undeclared slot is all-zero and never addressed by a program-consistent material id.</summary>
    public ReadOnlySpan<uint> ScreenSurfaceWords => m_screenSurfaceWords;
    public ReadOnlySpan<uint> Words => m_words;

    private static int CalculateRequiredDynamicTransformCapacity(IReadOnlyList<SdfInstruction> instructions, IReadOnlyList<SdfInstanceRange> instances) {
        var required = 0;

        foreach (var instruction in instructions) {
            if (instruction.Op == SdfOp.TransformDynamic) {
                required = Math.Max(required, (DecodeDynamicSlot(value: instruction.Data0.X, paramName: nameof(instructions)) + 1));
            }
        }

        foreach (var instance in instances) {
            if (instance.IsDynamic) {
                if ((instance.Slot < 0) || (instance.Slot > MaxDynamicTransformSlot)) {
                    throw new ArgumentOutOfRangeException(paramName: nameof(instances), message: $"Dynamic instance slots must be in [0, {MaxDynamicTransformSlot}].");
                }

                required = Math.Max(required, (instance.Slot + 1));
            }
        }

        return required;
    }
    private static int DecodeDynamicSlot(float value, string paramName) {
        // The range compare runs in double: (float)int.MaxValue rounds UP to 2147483648f, so a float compare against
        // int.MaxValue admits it and the saturating cast + "slot + 1" would overflow past the capacity the slot must
        // fit (slot + 1 <= int.MaxValue is the real bound).
        if (
            !float.IsFinite(value) ||
            (value < 0f) ||
            ((double)value > MaxDynamicTransformSlot) ||
            (value != MathF.Truncate(value))
        ) {
            throw new ArgumentOutOfRangeException(paramName: paramName, message: $"TransformDynamic slots must be finite integer values in [0, {MaxDynamicTransformSlot}].");
        }

        return (int)value;
    }

    /// <summary>One bounding-sphere record from the bounds analysis: a per-shape entry (<see cref="Instruction"/> is
    /// the shape) or a segment-directory entry (<see cref="Instruction"/>/<see cref="End"/> are the segment's
    /// inclusive-first/exclusive-end instruction range). Mode <c>0</c> marks a segment that always evaluates.
    /// <see cref="InstanceIndex"/> is the owning instance (-1 = the WORLD set, unowned) — segment analysis only, so
    /// consecutive segments owned by different instances (or one owned and one not) never merge.</summary>
    private readonly record struct BoundRecord(int Instruction, int End, uint Mode, Vector3 Center, float Radius, int Slot, int InstanceIndex = -1);

    // HOST-BAKED bounding-sphere skip data (programs build once, shapes evaluate millions of times per frame): for
    // every plain-Union shape reachable through a RIGID transform chain, a conservative world-space bounding sphere
    // lets map() skip work outright when the sphere's lower-bound distance cannot beat the running union minimum —
    // mathematically EXACT for Union (the skipped candidate's true distance is >= the bound, so the min, the
    // material winner, and every pixel are unchanged; a skip decision may even DIFFER between backends without any
    // pixel differing, because either path produces the identical result). Non-Union blends, unbounded/approximate
    // shapes (plane, ellipsoid), and chains through Scale/Repeat/symmetry/wallpaper/warp/elongate ops evaluate
    // fully, as today — no bound is always correct; a wrong bound is a rendering bug.
    //
    // Two levels:
    // - The SEGMENT DIRECTORY partitions the stream at ResetPoints into chain segments, each with one combined
    //   sphere over all its shapes; map()'s OUTER loop tests it and skips the whole chain — transforms included,
    //   which is where the per-step dynamic quaternion rotate cost lives. A skipped segment's transform state is
    //   provably dead (every later segment begins with the ResetPoint the split was made at). Directory iteration is
    //   also what keeps the skip fast: the outer loop's counter never depends on a loaded value, so the per-segment
    //   sphere loads pipeline instead of serializing. A segment with any non-rigid op, field op (Onion/Dilate — a
    //   skip must never jump one), non-Union shape, unbounded shape, or mixed static/dynamic spheres gets mode 0:
    //   always evaluated.
    // - The per-SHAPE table carries each qualifying shape's own (tighter) sphere, tested only inside an evaluated
    //   segment right before the shape evaluates — always sound, because shape ops never mutate chain state.
    //
    // A STATIC sphere's center is world-space. A DYNAMIC sphere (a single TransformDynamic in the chain, no rotation
    // before it) stores the chain's pre-dynamic translation as its center and the entity slot: the shader adds the
    // slot's per-frame position — center = offset + dynPos, NO quaternion rotate — with the post-dynamic local
    // geometry folded into the radius, which is the whole win for far-away moving entities.
    private (List<BoundRecord> ShapeBounds, List<BoundRecord> Segments) AnalyzeBounds() {
        var segments = new List<BoundRecord>();
        var shapeBounds = new List<BoundRecord>();
        var segmentStart = 0;

        // Segments split BEFORE each ResetPoint AND at every instance boundary (m_instances' First/End): a segment
        // never straddles two instances (or an instance and the WORLD set), so the instance table below can express
        // "this instance owns segments [a, b)" as a plain contiguous directory range. The next segment's leading
        // ResetPoint rebuilds every piece of chain state a segment skip leaves stale.
        while (segmentStart < m_instructions.Length) {
            var segmentEnd = segmentStart;

            while (
                ((segmentEnd + 1) < m_instructions.Length) &&
                (m_instructions[(segmentEnd + 1)].Op != SdfOp.ResetPoint) &&
                !IsInstanceBoundary(instruction: (segmentEnd + 1))
            ) {
                segmentEnd++;
            }

            segments.Add(item: AnalyzeSegment(segmentStart: segmentStart, segmentEnd: segmentEnd, instanceIndex: InstanceOwning(instruction: segmentStart), shapeBounds: shapeBounds));

            segmentStart = (segmentEnd + 1);
        }

        // Merge consecutive skippable segments into fewer, wider directory entries — every map() call pays one test
        // per entry, so co-located chains (a stand's pedestal/screen/slot/housing, a shelf of brackets, a d-pad's two
        // crossed boxes) should cost ONE test when the whole cluster is far. Exactness is untouched (a merged sphere
        // still contains every member shape); the trade is only that a FAILED merged test evaluates every member
        // chain (their per-shape spheres still gate the shape evaluations). Two flavours:
        // - DYNAMIC + DYNAMIC on the SAME entity slot: always merged (they move together).
        // - STATIC + STATIC: merged when the enclosing sphere stays within the members' summed radii — co-located
        //   clusters merge, well-separated ones (e.g. two different stands) stay individual so a wide sphere never
        //   erases a productive skip.
        // NEVER merges across an instance boundary (InstanceIndex differs) — the instance table's segment range must
        // stay contiguous and exclusive to its owner.
        for (var index = (segments.Count - 2); (index >= 0); index--) {
            var current = segments[index];
            var next = segments[(index + 1)];

            if (
                (current.Mode != next.Mode) ||
                (current.InstanceIndex != next.InstanceIndex)
            ) {
                continue;
            }

            if (BoundModeDynamic == current.Mode) {
                if (current.Slot != next.Slot) {
                    continue;
                }

                // Anchored on the first segment's center (the shared pre-dynamic offset); the enclosing max keeps it
                // conservative even if the offsets differ.
                segments[index] = current with {
                    End = next.End,
                    Radius = MathF.Max(current.Radius, (Vector3.Distance(next.Center, current.Center) + next.Radius)),
                };
                segments.RemoveAt(index: (index + 1));
            } else if (BoundModeStatic == current.Mode) {
                var (mergedCenter, mergedRadius) = EncloseSpheres(centerA: current.Center, radiusA: current.Radius, centerB: next.Center, radiusB: next.Radius);

                if (mergedRadius > (current.Radius + next.Radius)) {
                    continue;
                }

                segments[index] = current with {
                    Center = mergedCenter,
                    End = next.End,
                    Radius = mergedRadius,
                };
                segments.RemoveAt(index: (index + 1));
            }
        }

        return (shapeBounds, segments);
    }
    // Whether `instruction` is the FIRST instruction of some instance's range, or the first instruction AFTER some
    // instance's range ends (both force a segment split — the second because the ended instance's last segment must
    // not swallow the next, unowned/differently-owned instruction).
    private bool IsInstanceBoundary(int instruction) {
        foreach (var range in m_instances) {
            if (
                (range.First == instruction) ||
                (range.End == instruction)
            ) {
                return true;
            }
        }

        return false;
    }
    // The instance owning `instruction` (its declared [First, End) range contains it), or -1 for the WORLD set.
    // Instances never overlap (BeginInstance/EndInstance nesting is rejected at build time), so at most one matches.
    private int InstanceOwning(int instruction) {
        for (var index = 0; (index < m_instances.Length); index++) {
            var range = m_instances[index];

            if (
                (instruction >= range.First) &&
                (instruction < range.End)
            ) {
                return index;
            }
        }

        return -1;
    }
    // The minimal sphere containing two spheres: one containing the other wins outright; otherwise the classic
    // segment-spanning enclosure.
    private static (Vector3 Center, float Radius) EncloseSpheres(Vector3 centerA, float radiusA, Vector3 centerB, float radiusB) {
        var distance = Vector3.Distance(centerA, centerB);

        if ((distance + radiusB) <= radiusA) {
            return (centerA, radiusA);
        }

        if ((distance + radiusA) <= radiusB) {
            return (centerB, radiusB);
        }

        var radius = (0.5f * (distance + radiusA + radiusB));

        return ((centerA + (Vector3.Normalize(value: (centerB - centerA)) * (radius - radiusA))), radius);
    }
    // Walks one segment maintaining the FORWARD rigid transform (local shape space -> world) the chain's point ops
    // invert, appending per-shape records as it goes, and returns the segment's directory record (mode 0 when any
    // instruction disqualifies the whole-chain skip).
    private BoundRecord AnalyzeSegment(int segmentStart, int segmentEnd, int instanceIndex, List<BoundRecord> shapeBounds) {
        var chainBoundable = true;
        var dynamicOffset = Vector3.Zero;
        var dynamicSlot = -1;
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        var segmentEligible = true;
        var firstShapeBound = shapeBounds.Count;

        for (var index = segmentStart; (index <= segmentEnd); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                    // Only ever the segment's first instruction (segments split before each ResetPoint) — the walk's
                    // initial state IS the reset state.
                    break;
                }
                case SdfOp.Translate: {
                    position += Vector3.Transform(value: new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z), rotation: rotation);
                    break;
                }
                case SdfOp.Rotate: {
                    rotation = Quaternion.Concatenate(value1: new Quaternion(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z, instruction.Data0.W), value2: rotation);
                    break;
                }
                case SdfOp.TransformDynamic: {
                    // One dynamic per chain, and NO rotation before it — otherwise the shader-side center would need
                    // the very quaternion rotate the skip exists to avoid; be conservative and evaluate fully.
                    if (
                        (dynamicSlot >= 0) ||
                        !rotation.IsIdentity
                    ) {
                        chainBoundable = false;
                        segmentEligible = false;
                    } else {
                        dynamicOffset = position;
                        dynamicSlot = (int)instruction.Data0.X;
                        position = Vector3.Zero;
                    }

                    break;
                }
                case SdfOp.ShapeBlend: {
                    if (
                        chainBoundable &&
                        ((uint)SdfBlendOp.Union == instruction.Blend) &&
                        TryGetLocalBound(instruction: instruction, center: out var localCenter, radius: out var localRadius)
                    ) {
                        var chainCenter = (position + Vector3.Transform(value: localCenter, rotation: rotation));

                        // Dynamic: the post-dynamic local geometry folds into the radius, so the entity's orientation
                        // can never move the shape outside offset + dynPos ± radius — rotation-free in the shader.
                        shapeBounds.Add(item: ((dynamicSlot < 0)
                            ? new BoundRecord(Center: chainCenter, End: (index + 1), Instruction: index, Mode: BoundModeStatic, Radius: localRadius, Slot: 0)
                            : new BoundRecord(Center: dynamicOffset, End: (index + 1), Instruction: index, Mode: BoundModeDynamic, Radius: (chainCenter.Length() + localRadius), Slot: dynamicSlot)));
                    } else {
                        segmentEligible = false;
                    }

                    break;
                }
                default: {
                    // Scale (distance rescale), Repeat/RepeatLimited/Symmetry/WallpaperFold (space folding),
                    // Twist/Bend/Elongate (non-isometries), Onion/Dilate (field ops a skip must never jump over):
                    // no world-space sphere is sound past this point, and the segment cannot be skipped whole.
                    chainBoundable = false;
                    segmentEligible = false;

                    break;
                }
            }
        }

        var alwaysEvaluate = new BoundRecord(Center: Vector3.Zero, End: (segmentEnd + 1), InstanceIndex: instanceIndex, Instruction: segmentStart, Mode: 0, Radius: 0f, Slot: 0);

        // The segment sphere: every instruction qualified AND the shape spheres are homogeneous (all static, or all
        // dynamic on the chain's one slot — a mixed segment would need two centers in one entry).
        if (
            !segmentEligible ||
            (shapeBounds.Count == firstShapeBound)
        ) {
            return alwaysEvaluate;
        }

        var segmentMode = shapeBounds[firstShapeBound].Mode;

        for (var index = (firstShapeBound + 1); (index < shapeBounds.Count); index++) {
            if (shapeBounds[index].Mode != segmentMode) {
                return alwaysEvaluate;
            }
        }

        // Combined sphere: anchored on the first shape's center (dynamic entries all share the pre-dynamic offset,
        // so the max simply widens the radius).
        var segmentCenter = shapeBounds[firstShapeBound].Center;
        var segmentRadius = shapeBounds[firstShapeBound].Radius;

        for (var index = (firstShapeBound + 1); (index < shapeBounds.Count); index++) {
            segmentRadius = MathF.Max(segmentRadius, (Vector3.Distance(shapeBounds[index].Center, segmentCenter) + shapeBounds[index].Radius));
        }

        return new BoundRecord(Center: segmentCenter, End: (segmentEnd + 1), InstanceIndex: instanceIndex, Instruction: segmentStart, Mode: segmentMode, Radius: segmentRadius, Slot: ((BoundModeDynamic == segmentMode) ? dynamicSlot : 0));
    }
    // Packs the analysis into the two word-stream tables: the per-shape table (2 uvec4 per INSTRUCTION, only shape
    // records populated) and the segment directory (a count header, then 2 uvec4 per segment).
    private void PackBounds(int boundsOffsetVectors, int segmentOffsetVectors, List<BoundRecord> shapeBounds, List<BoundRecord> segments) {
        foreach (var record in shapeBounds) {
            WriteBound(entryBase: ((boundsOffsetVectors + (2 * record.Instruction)) * WordsPerVector), record: record);
        }

        m_words[(segmentOffsetVectors * WordsPerVector)] = (uint)segments.Count;

        for (var index = 0; (index < segments.Count); index++) {
            WriteBound(entryBase: ((segmentOffsetVectors + 1 + (2 * index)) * WordsPerVector), record: segments[index]);
        }
    }
    // Packs the world-segment list: a count header (worldSegmentCount — the ctor already counted the unowned
    // segments to size the layout, and this list is written from the same predicate, so the header and the entries
    // agree by construction), then one uvec4 (only .x used) per segment owned by NO instance, in ascending
    // segment-directory order — the always-evaluated side of mapCore's world/visible-instance merge, enumerated
    // directly instead of owner-testing every segment per map() call.
    private void PackWorldSegments(List<BoundRecord> segments, int worldSegmentCount, int worldSegmentOffsetVectors) {
        m_words[(worldSegmentOffsetVectors * WordsPerVector)] = (uint)worldSegmentCount;

        var next = 0;

        for (var segment = 0; (segment < segments.Count); segment++) {
            if (segments[segment].InstanceIndex >= 0) {
                continue;
            }

            m_words[((worldSegmentOffsetVectors + 1 + next) * WordsPerVector)] = (uint)segment;
            next++;
        }
    }
    // Packs the instance directory: a count header, then 2 uvec4 per instance — i0 = bound center/offset.xyz + radius
    // (the same float-safety padding as WriteBound), i1 = (mode, dynamicSlot, segmentFirst, segmentEnd). The
    // segment range is resolved here (not during AnalyzeBounds) by scanning the FINAL, POST-MERGE segment list for
    // the contiguous run whose InstanceIndex equals this instance's index — sound because splits/merges never let a
    // segment straddle an instance boundary, so that run is always contiguous and never empty.
    private void PackInstances(int instanceOffsetVectors, List<BoundRecord> segments) {
        m_words[(instanceOffsetVectors * WordsPerVector)] = (uint)m_instances.Length;

        for (var instanceIndex = 0; (instanceIndex < m_instances.Length); instanceIndex++) {
            var instance = m_instances[instanceIndex];
            var segmentFirst = -1;
            var segmentEnd = -1;

            for (var segment = 0; (segment < segments.Count); segment++) {
                if (segments[segment].InstanceIndex != instanceIndex) {
                    continue;
                }

                segmentFirst = (segmentFirst < 0) ? segment : segmentFirst;
                segmentEnd = (segment + 1);
            }

            var entryBase = ((instanceOffsetVectors + 1 + (2 * instanceIndex)) * WordsPerVector);

            // A PARKED instance packs the negative sentinel so the beam prepass skips its per-tile sphere test entirely
            // (one branch, no cull math, mask bit left 0) — the slot still exists (reserved capacity preserved), it just
            // costs nothing. A live instance packs its float-safety-padded radius as before.
            var packedRadius = (instance.Active ? ((instance.Radius * BoundRadiusScale) + BoundRadiusPadding) : ParkedBoundRadius);

            WriteVector4(
                words: m_words,
                baseIndex: entryBase,
                w: packedRadius,
                x: instance.Center.X,
                y: instance.Center.Y,
                z: instance.Center.Z
            );

            m_words[(entryBase + WordsPerVector)] = (instance.IsDynamic ? BoundModeDynamic : BoundModeStatic);
            m_words[(entryBase + WordsPerVector + 1)] = (uint)instance.Slot;
            m_words[(entryBase + WordsPerVector + 2)] = (uint)Math.Max(segmentFirst, 0);
            m_words[(entryBase + WordsPerVector + 3)] = (uint)Math.Max(segmentEnd, 0);
        }
    }
    private void WriteBound(int entryBase, in BoundRecord record) {
        WriteVector4(
            words: m_words,
            baseIndex: entryBase,
            w: ((record.Radius * BoundRadiusScale) + BoundRadiusPadding),
            x: record.Center.X,
            y: record.Center.Y,
            z: record.Center.Z
        );

        m_words[(entryBase + WordsPerVector)] = record.Mode;
        m_words[(entryBase + WordsPerVector + 1)] = (uint)record.Slot;
        m_words[(entryBase + WordsPerVector + 2)] = (uint)record.Instruction;
        m_words[(entryBase + WordsPerVector + 3)] = (uint)record.End;
    }
    // The shape's LOCAL bounding sphere. Plane is unbounded; ellipsoid's SDF is a first-order approximation that can
    // UNDERESTIMATE at range, so a geometric containment sphere is not a sound lower bound on its candidate — both
    // evaluate fully, forever correct.
    private static bool TryGetLocalBound(SdfInstruction instruction, out Vector3 center, out float radius) {
        var data0 = instruction.Data0;

        switch ((SdfShapeType)instruction.Shape) {
            case SdfShapeType.Box:
            case SdfShapeType.ScreenSlab: {
                // The rounded box is contained in the sharp half-extents box; the rounding is added anyway as slack
                // against degenerate authoring (round exceeding a half-extent).
                center = Vector3.Zero;
                radius = (new Vector3(data0.X, data0.Y, data0.Z).Length() + MathF.Abs(data0.W));

                return true;
            }
            case SdfShapeType.Capsule: {
                var endpoint = new Vector3(data0.X, data0.Y, data0.Z);

                center = (endpoint * 0.5f);
                radius = ((endpoint.Length() * 0.5f) + MathF.Abs(data0.W));

                return true;
            }
            case SdfShapeType.Sphere: {
                center = Vector3.Zero;
                radius = MathF.Abs(data0.X);

                return true;
            }
            case SdfShapeType.Torus: {
                center = Vector3.Zero;
                radius = (MathF.Abs(data0.X) + MathF.Abs(data0.Y));

                return true;
            }
            case SdfShapeType.Cylinder: {
                center = Vector3.Zero;
                radius = MathF.Sqrt((data0.X * data0.X) + (data0.Y * data0.Y));

                return true;
            }
            case SdfShapeType.RoundCone: {
                // The hull of the two end spheres (origin, r1) and ((0, h, 0), r2).
                var halfHeight = (data0.Z * 0.5f);

                center = new Vector3(0f, halfHeight, 0f);
                radius = (MathF.Abs(halfHeight) + MathF.Max(MathF.Abs(data0.X), MathF.Abs(data0.Y)));

                return true;
            }
            default: {
                center = Vector3.Zero;
                radius = 0f;

                return false;
            }
        }
    }
    private static void WriteVector4(uint[] words, int baseIndex, float x, float y, float z, float w) {
        words[baseIndex] = BitConverter.SingleToUInt32Bits(value: x);
        words[(baseIndex + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        words[(baseIndex + 2)] = BitConverter.SingleToUInt32Bits(value: z);
        words[(baseIndex + 3)] = BitConverter.SingleToUInt32Bits(value: w);
    }
}
