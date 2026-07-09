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
//   [.. + 2*instructionCount ..) = the SEGMENT directory: one (segmentCount, stepScale, 0, 0) header uvec4, then 2
//                         uvec4 per segment — s0 = center/offset.xyz + radius (float bits), s1 = (mode, dynamicSlot,
//                         first, end) — the chain-level skip map()'s outer loop walks. The header's .y lane (float
//                         bits) is the per-PROGRAM Lipschitz STEP SCALE (1/L, AnalyzeLipschitz): mapCore multiplies
//                         its FINAL returned distance by it so sphere tracing cannot overstep a non-1-Lipschitz warp
//                         (twist/bend) or an eccentric ellipsoid and hole — == 1.0 for an isometric program, so its
//                         scenes stay byte-identical. Both tables' offsets derive from word[0]'s existing lanes, so
//                         the header is unchanged. See PackBounds.
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
    /// <summary>The factor a <see cref="SdfBlendOp.ChamferUnion"/> member's bevel radius needs before its cull halo is
    /// far-neutral: <c>(2 + sqrt(2)) / 2</c>. See <see cref="MaxSmoothBlendRadius"/> for the derivation — the bevel plane
    /// keeps sagging below the accumulator until the candidate is 1.70711 radii away, unlike every other soft blend,
    /// which saturates at one radius.</summary>
    private const float ChamferUnionHaloScale = 1.7071068f;
    /// <summary>The UNMASKABLE-instance sentinel radius: an instance carrying an intersection-family blend
    /// (<see cref="HasUnmaskableCompose"/>) packs this instead of a real bound, so the beam prepass's sphere-vs-cone
    /// test <c>axisDistance &lt;= (radius + chord*alongRay) * inverseAperture</c> passes for every tile and the instance
    /// is always evaluated.
    /// <para>Deliberately a LARGE FINITE value rather than <see cref="float.PositiveInfinity"/>: DXC compiles the
    /// kernels with fast-math (<c>ninf</c>), so an infinity in that arithmetic would be undefined. 1e30 leaves ~8
    /// decades of headroom below <see cref="float.MaxValue"/> after the cull test's multiply, and no world extent comes
    /// within 20 decades of it. Needs NO shader change: the existing cull arithmetic already always admits it, and it is
    /// non-negative so neither the parked-slot skip in <c>collectInstanceMaskWord</c> nor the one in
    /// <c>sdfNextVisibleInstanceRange</c> misfires.</para></summary>
    private const float UnmaskableBoundRadius = 1.0e30f;
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

        // The per-program Lipschitz step scale (1/L): a SEPARATE static pass over the instruction stream, deliberately
        // kept off AnalyzeSegment/AnalyzeBounds (they sit at their CA150x complexity ceilings). Baked into the segment
        // header's otherwise-free .y lane by PackBounds and applied as ONE multiply on mapCore's final distance
        // (sdf-vm.hlsli). Exactly 1.0f for a warp-free, eccentricity-free program, so isometric scenes stay byte-identical.
        var stepScale = AnalyzeLipschitz(instructions: m_instructions);

        PackBounds(boundsOffsetVectors: boundsOffsetVectors, segmentOffsetVectors: segmentOffsetVectors, segments: segments, shapeBounds: shapeBounds, stepScale: stepScale);
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
    /// <summary>The per-program Lipschitz STEP SCALE (1/L, in (0, 1]) baked into the packed words — read back here
    /// from the segment-directory header's <c>.y</c> lane, which the packed stream makes the single source of truth.
    /// <c>mapCore</c> (sdf-vm.hlsli) multiplies its final returned distance by it so sphere tracing takes
    /// field-rate-safe steps through a non-1-Lipschitz warp (twist/bend) or an eccentric ellipsoid without
    /// overstepping and holing. EXACTLY <c>1.0f</c> for a warp-free, eccentricity-free (isometric) program, so
    /// isometric scenes stay byte-identical. See <see cref="AnalyzeLipschitz"/>. The <c>&gt; 0</c> guard mirrors the
    /// shader: a hypothetical legacy stream with an all-zero header lane reads as 1.0.</summary>
    public float StepScale {
        get {
            // Mirror the shader's segment-directory offset chain (sdf-vm.hlsli mapCore): materialOffset (m_words[3])
            // + 2*materialCount (m_words[1]) = boundsOffset; + 2*instructionCount = segmentOffset. The step scale is
            // the header uvec4's .y lane.
            var segmentOffsetVectors = ((int)m_words[3] + (2 * (int)m_words[1]) + (2 * InstructionCount));
            var raw = BitConverter.UInt32BitsToSingle(value: m_words[((segmentOffsetVectors * WordsPerVector) + 1)]);

            return ((raw > 0f) ? raw : 1.0f);
        }
    }
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

        // O(instructions + instances) lookups replacing the per-instruction linear scans of m_instances the segment
        // walk below would otherwise do (a boundary test per instruction, an owner resolve per segment — together
        // O(instructions x instances)). Built once here; consumed inline in the loop.
        var instanceBoundaries = BuildInstanceBoundaries();
        var instructionOwners = BuildInstructionOwners();

        // Segments split BEFORE each ResetPoint AND at every instance boundary (m_instances' First/End): a segment
        // never straddles two instances (or an instance and the WORLD set), so the instance table below can express
        // "this instance owns segments [a, b)" as a plain contiguous directory range. The next segment's leading
        // ResetPoint rebuilds every piece of chain state a segment skip leaves stale.
        while (segmentStart < m_instructions.Length) {
            var segmentEnd = segmentStart;

            while (
                ((segmentEnd + 1) < m_instructions.Length) &&
                (m_instructions[(segmentEnd + 1)].Op != SdfOp.ResetPoint) &&
                !instanceBoundaries.Contains(item: (segmentEnd + 1))
            ) {
                segmentEnd++;
            }

            segments.Add(item: AnalyzeSegment(segmentStart: segmentStart, segmentEnd: segmentEnd, instanceIndex: instructionOwners[segmentStart], shapeBounds: shapeBounds));

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
    // The set of instruction indices that force a segment split: the FIRST instruction of some instance's range, or the
    // first instruction AFTER some instance's range ends (the second because the ended instance's last segment must not
    // swallow the next, unowned/differently-owned instruction). Built once, O(instances); the segment walk tests
    // membership per instruction, so a per-test linear scan of m_instances was O(instructions x instances).
    private HashSet<int> BuildInstanceBoundaries() {
        var boundaries = new HashSet<int>(capacity: (m_instances.Length * 2));

        foreach (var range in m_instances) {
            boundaries.Add(item: range.First);
            boundaries.Add(item: range.End);
        }

        return boundaries;
    }
    // A dense per-instruction owner map: owners[i] is the instance whose [First, End) range contains instruction i, or
    // -1 for the WORLD set. Built once, O(instructions + instances) total (instances never overlap, so their spans
    // partition a subset of the instructions). The `< 0` guard preserves the old first-match-wins result exactly even
    // for a hypothetical overlap, so the packed words stay byte-identical.
    private int[] BuildInstructionOwners() {
        var owners = new int[m_instructions.Length];

        Array.Fill(array: owners, value: -1);

        for (var index = 0; (index < m_instances.Length); index++) {
            var range = m_instances[index];

            for (var instruction = range.First; (instruction < range.End); instruction++) {
                if (owners[instruction] < 0) {
                    owners[instruction] = index;
                }
            }
        }

        return owners;
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
                case SdfOp.PushField:
                case SdfOp.PopField: {
                    // A scope boundary op mutates the FIELD (the running accumulator), not the point/transform, so the
                    // chain's world-space sphere stays sound — chainBoundable is left TRUE, and any Union shape after the
                    // Push in this SAME chain still earns its per-shape cull bound (the accumulator-plan correction: the
                    // default arm's chainBoundable = false would suppress those bounds, a real cull regression). But a
                    // whole-segment skip must NEVER jump a Push (savedDistance would be unset) or a Pop (the parent
                    // compose would be lost), so the segment holding one is always evaluated.
                    segmentEligible = false;

                    break;
                }
                default: {
                    // Scale (distance rescale), Repeat/RepeatLimited/SymmetryPlane/WallpaperFold/CellJitter/RepeatPolar
                    // (space folding), Twist/Bend/Elongate/DomainWarp (non-isometries), Onion/Dilate/Displace (field ops a skip must never jump over):
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
    private void PackBounds(int boundsOffsetVectors, int segmentOffsetVectors, List<BoundRecord> shapeBounds, List<BoundRecord> segments, float stepScale) {
        foreach (var record in shapeBounds) {
            WriteBound(entryBase: ((boundsOffsetVectors + (2 * record.Instruction)) * WordsPerVector), record: record);
        }

        var segmentHeaderBase = (segmentOffsetVectors * WordsPerVector);

        m_words[segmentHeaderBase] = (uint)segments.Count;
        // The segment-directory header's .y lane is otherwise written zero and read by nothing (the shader reads only
        // .x, the segment count) — a free lane. It carries the per-program Lipschitz step scale as float bits; mapCore
        // reads it back and multiplies its FINAL returned distance by it (KEEP IN SYNC with the stepScale read in
        // Assets/Shaders/Sdf/sdf-vm.hlsli's mapCore). See AnalyzeLipschitz.
        m_words[(segmentHeaderBase + 1)] = BitConverter.SingleToUInt32Bits(value: stepScale);

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

        // Resolve every instance's contiguous [segmentFirst, segmentEnd) directory range in ONE pass over the segment
        // list (the old code re-scanned all segments per instance — O(instances x segments)). Splits/merges never let a
        // segment straddle an instance boundary, so an owner's segments are contiguous; first-seen is the min index and
        // last-seen + 1 the exclusive end — byte-identical to the per-instance scan.
        var segmentFirst = new int[m_instances.Length];
        var segmentEnd = new int[m_instances.Length];

        Array.Fill(array: segmentFirst, value: -1);
        Array.Fill(array: segmentEnd, value: -1);

        for (var segment = 0; (segment < segments.Count); segment++) {
            var owner = segments[segment].InstanceIndex;

            if (owner < 0) {
                continue;
            }

            if (segmentFirst[owner] < 0) {
                segmentFirst[owner] = segment;
            }

            segmentEnd[owner] = (segment + 1);
        }

        for (var instanceIndex = 0; (instanceIndex < m_instances.Length); instanceIndex++) {
            var instance = m_instances[instanceIndex];
            var entryBase = ((instanceOffsetVectors + 1 + (2 * instanceIndex)) * WordsPerVector);

            // A PARKED instance packs the negative sentinel so the beam prepass skips its per-tile sphere test entirely
            // (one branch, no cull math, mask bit left 0) — the slot still exists (reserved capacity preserved), it just
            // costs nothing. A live instance packs its float-safety-padded radius as before, PLUS its smooth-blend halo:
            // a SmoothUnion/SmoothSubtraction/Chamfer shape couples this instance to whatever it blends against within
            // its blend radius k, so beyond radius + k the seam has saturated and the tile cull can skip the instance
            // EXACTLY (blendSmoothUnion is far-exact). Inflating the bound by k keeps the instance evaluated across that
            // coupling halo — a smooth instance culls with a finite bound instead of an unmaskable one
            // (closes the SmoothUnion-vs-world cull gotcha; see the sdf-world skill and blendSmoothUnion in sdf-vm.hlsli).
            //
            // An instance carrying an op that READS the accumulator — an intersection-family blend, or an
            // Onion/Dilate/Displace field op — takes the unmaskable path instead: no halo can rescue it (see
            // HasUnmaskableCompose), and PARKING one is an authoring error, because a parked slot asserts "contributes
            // nothing", which such an op violates even where its geometry is absent.
            //
            // A SCOPED field op (Onion/Dilate/Displace inside a balanced PushField/PopField) is the exception: it is
            // maskable (HasUnmaskableCompose skips scope-nested ops), so it packs a finite bound — but the op moves the
            // scope's surface OUTWARD past the authored geometry radius, so the bound must be inflated by that reach
            // (MaxScopedFieldReach) exactly as the smooth halo is, or the tiles the grown shell reaches get masked out
            // and hole at the seams. Both margins add to the geometry radius (an over-covered instance is always cull-safe).
            var unmaskable = HasUnmaskableCompose(first: instance.First, end: instance.End);

            if (unmaskable && !instance.Active) {
                throw new ArgumentException(message: $"Instance {instanceIndex} is PARKED but carries an op that reads the running accumulator (an Intersection/SmoothIntersection/ChamferIntersection blend, or an Onion/Dilate/Displace field op). A parked slot must contribute nothing to the field, but such an op changes the field even where the instance's own geometry is absent. Emit the instance active, or use a union/subtraction-family blend with no field op.", paramName: "instances");
            }

            var smoothMargin = MaxSmoothBlendRadius(first: instance.First, end: instance.End);
            var scopedFieldReach = MaxScopedFieldReach(first: instance.First, end: instance.End);
            var packedRadius = instance.Active
                ? (unmaskable ? UnmaskableBoundRadius : (((instance.Radius + smoothMargin + scopedFieldReach) * BoundRadiusScale) + BoundRadiusPadding))
                : ParkedBoundRadius;

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
            m_words[(entryBase + WordsPerVector + 2)] = (uint)Math.Max(segmentFirst[instanceIndex], 0);
            m_words[(entryBase + WordsPerVector + 3)] = (uint)Math.Max(segmentEnd[instanceIndex], 0);
        }
    }
    /// <summary>The coupling HALO an instance's soft blends need on top of their geometry bound: past it, evaluating the
    /// member returns the accumulator bitwise, so a masked-out tile's skip stays exact (see PackInstances). 0 for an
    /// instance that uses only hard blends, so its bound is unchanged and byte-identical. The INTERSECTION family is
    /// deliberately absent: those instances are unmaskable, so no halo is computed for them (HasUnmaskableCompose).
    /// <para>The margin is NOT simply the blend radius for every family. Neutrality for a far candidate b >= a is:</para>
    /// <para>SmoothUnion / SmoothSubtraction — <c>b >= a + k</c> saturates <c>blendSmoothUnion</c>'s far endpoint, which
    /// is bit-exact by construction. A chain of N smooth unions of radius k approaches sag k monotonically from below and
    /// never exceeds it, so <c>max(k)</c> really is the supremum, not a coincidence. Margin = k.</para>
    /// <para>ChamferSubtraction — <c>max(max(a, -b), (a - b + c)/sqrt(2))</c>. Both alternatives fall away for b >= c.
    /// Margin = c.</para>
    /// <para>ChamferUnion — <c>min(min(a, b), (a + b - c)/sqrt(2))</c>. The bevel plane keeps SAGGING below the
    /// accumulator long after b passes c. Neutrality needs <c>(a + b - c)/sqrt(2) >= a</c>, whose worst case b == a gives
    /// <c>a >= c*(2 + sqrt(2))/2 = 1.70711*c</c>. Verified: with c = 1, a = b = 1.700 evaluates to 1.697056 (sags) while
    /// a = b = 1.710 is neutral. A margin of c under-inflates a ChamferUnion instance's halo by 0.71*c, so a masked-out
    /// tile is NOT bit-exact. Margin = 1.70711*c.</para>
    /// <para>Xor — DELIBERATELY zero halo, like the other hard blends (verified 2026-07-08, real-GPU slice comparison):
    /// <c>max(min(a, b), -max(a, b))</c> reduces to <c>min(a, b)</c> — the plain union — everywhere OUTSIDE the
    /// candidate (b &gt; 0; the negated arm only wins when a + b &lt; 0, deeper inside than a first-hit march ever
    /// samples), so a far candidate returns the accumulator exactly. Do not add Xor here or to HasUnmaskableCompose.
    /// SIZING: an Xor member competes on the running min wherever it is nearest, so its authored cull bound needs the
    /// UNION-style generous influence margin, never the subtraction-style tight bound.</para></summary>
    /// <param name="first">The instance's first instruction index (inclusive).</param>
    /// <param name="end">The instance's instruction end index (exclusive).</param>
    /// <returns>The largest coupling halo among the slice's soft blends.</returns>
    private float MaxSmoothBlendRadius(int first, int end) {
        var margin = 0.0f;

        for (var index = first; (index < end); index++) {
            var instruction = m_instructions[index];

            // The Blend lane + Data1.x carry a compose blend + smooth radius on a ShapeBlend AND on a PopField (whose
            // scope composes into the parent through that blend), but mean something else on the fold ops (CellJitter's
            // noise flavor, RepeatPolar's mirror flag, WallpaperFold's plane), so read them only under this guard. A
            // scoped soft compose couples the whole scope to the parent within its radius exactly as a soft ShapeBlend
            // couples one shape — so its halo joins the max the instance bound is inflated by (conservative: an
            // over-covered instance is always cull-safe).
            if ((instruction.Op != SdfOp.ShapeBlend) && (instruction.Op != SdfOp.PopField)) {
                continue;
            }

            var radius = MathF.Abs(instruction.Data1.X);

            if (
                (instruction.Blend == (uint)SdfBlendOp.SmoothUnion) ||
                (instruction.Blend == (uint)SdfBlendOp.SmoothSubtraction) ||
                (instruction.Blend == (uint)SdfBlendOp.ChamferSubtraction)
            ) {
                margin = MathF.Max(margin, radius);
            }
            else if (instruction.Blend == (uint)SdfBlendOp.ChamferUnion) {
                margin = MathF.Max(margin, (ChamferUnionHaloScale * radius));
            }
        }

        return margin;
    }
    /// <summary>The outward SURFACE REACH an instance's SCOPED field ops (an <see cref="SdfOp.Onion"/>/
    /// <see cref="SdfOp.Dilate"/>/<see cref="SdfOp.Displace"/> between a balanced <see cref="SdfOp.PushField"/>/
    /// <see cref="SdfOp.PopField"/>) add on top of its authored geometry bound — the cull-margin twin of
    /// <see cref="MaxSmoothBlendRadius"/> for the scoped-accumulator payoff. A scoped field op is MASKABLE
    /// (<see cref="HasUnmaskableCompose"/> only trips on an UNSCOPED field op or an intersection-family POP compose), so
    /// it packs a FINITE bound instead of the 1e30 sentinel that would otherwise cover everything — but the op moves the
    /// scope's zero-set OUTWARD past the packed radius, so that growth must be folded into the bound or the beam masks
    /// out the tiles the grown shell reaches and the surface holes at the tile seams. Each op's outward reach is exact:
    /// <list type="bullet">
    /// <item><description><c>Onion(t)</c> — <c>abs(d) - t</c>: the shell's OUTER surface sits exactly <c>t</c> beyond the
    /// original surface, so reach grows by <c>|t|</c> (<c>Data0.x</c>).</description></item>
    /// <item><description><c>Dilate(r)</c> — <c>d - r</c>: the surface inflates by <c>r</c> everywhere, so reach grows by
    /// <c>|r|</c> (<c>Data0.x</c>).</description></item>
    /// <item><description><c>Displace(a)</c> — <c>d + a·sin·sin·sin</c>: the relief pushes the surface out by at most
    /// <c>|a|</c> (the basis bottoms at −1), so reach grows by <c>|a|</c> (<c>Data0.w</c>).</description></item>
    /// </list>
    /// Field ops COMPOUND within one scope (an Onion then a Dilate grows the surface by <c>t</c> then <c>r</c>), so they
    /// SUM inside a scope; the instance margin is the largest such per-scope sum (nesting is capped at depth 1, and
    /// sibling scopes union, so a max over scopes is a sound conservative cover). An UNSCOPED field op never contributes
    /// here — it makes the whole instance unmaskable, so the sentinel bound already covers it. 0 for an instance with no
    /// scoped field op, so its bound is byte-identical.</summary>
    /// <param name="first">The instance's first instruction index (inclusive).</param>
    /// <param name="end">The instance's instruction end index (exclusive).</param>
    /// <returns>The largest per-scope outward field reach within the slice.</returns>
    private float MaxScopedFieldReach(int first, int end) {
        var margin = 0.0f;
        var scopeDepth = 0;
        var scopeReach = 0.0f;

        for (var index = first; (index < end); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op == SdfOp.PushField) {
                scopeDepth++;
                scopeReach = 0.0f; // depth is capped at 1, so a Push always opens a fresh (empty) scope

                continue;
            }

            if (instruction.Op == SdfOp.PopField) {
                margin = MathF.Max(margin, scopeReach);

                if (scopeDepth > 0) {
                    scopeDepth--;
                }

                continue;
            }

            // An UNSCOPED field op is unmaskable (the sentinel bound covers it) — only a scope-nested op grows the finite
            // bound this method sizes.
            if (scopeDepth <= 0) {
                continue;
            }

            scopeReach += instruction.Op switch {
                SdfOp.Onion => MathF.Abs(instruction.Data0.X),
                SdfOp.Dilate => MathF.Abs(instruction.Data0.X),
                SdfOp.Displace => MathF.Abs(instruction.Data0.W),
                _ => 0.0f,
            };
        }

        return margin;
    }
    /// <summary>Whether an instance's instruction slice contains an op that READS the running accumulator, which makes the
    /// instance UNMASKABLE. Two families qualify.
    /// <para>THE INTERSECTION BLENDS. Every other blend family, evaluated against a candidate far beyond the accumulator,
    /// returns the accumulator: <c>min(a, b) = a</c>, <c>max(a, -b) = a</c>, <c>blendSmoothUnion</c> is far-exact by
    /// construction, and the chamfer union/subtraction bevel planes fall away. So dropping a masked-out instance is exactly
    /// evaluating it. The intersection family inverts that — <c>max(a, b) = b</c> — so a masked-out intersection instance
    /// does not vanish, it REPLACES the field with its own candidate. Measured with a floor at 0.001 and the instance's
    /// shape 50 units away: skipping yields 0.001, evaluating yields 50.</para>
    /// <para>THE FIELD OPS — <see cref="SdfOp.Onion"/>, <see cref="SdfOp.Dilate"/>, <see cref="SdfOp.Displace"/>. These
    /// mutate the accumulator outright (<c>abs(d) - t</c>, <c>d - r</c>, <c>d + relief</c>), so a masked-out instance
    /// silently omits a transformation of the WHOLE field, the ground plane included. Measured: an <c>Onion(0.05)</c>
    /// inside an instance turns a solid ground plane into a 0.05-thick shell — the solid fraction of a slice through the
    /// scene falls from 51.3% to 6.3% — and masking the instance out restores it. This is the MORE visible of the two:
    /// the beam prepass's cone march hides the intersection case (an intersection annihilates everything outside its own
    /// shape, so the tiles that would differ are already empty) but cannot hide this one. Creator mode emits <c>Onion</c>
    /// inside <c>BeginInstanceDynamic</c>, so it is live. <see cref="SdfOp.DomainWarp"/> is deliberately NOT here: it is a
    /// POINT op and never reads the accumulator. <see cref="SdfBlendOp.Xor"/> is deliberately NOT here either (verified
    /// 2026-07-08, real-GPU slice comparison): it reads the accumulator, but <c>max(min(a, b), -max(a, b))</c> reduces to
    /// the plain union <c>min(a, b)</c> everywhere outside the candidate, and the extra surface it carves (the overlap
    /// hole) lives strictly inside the union hull — inside any covering bound — so masking an Xor instance with a
    /// covering, union-margin bound is exactly as safe as masking a union member (see MaxSmoothBlendRadius's sizing
    /// note).</para>
    /// <para>No bound inflation closes either gap, because the far-field answer is not the accumulator. Such an instance
    /// therefore packs <see cref="UnmaskableBoundRadius"/>, a bound so large that the beam prepass's sphere-vs-cone test
    /// passes for every tile and the instance is always evaluated — the same graceful degradation <c>AnalyzeSegment</c>
    /// already applies to a non-Union SEGMENT. The instance keeps working and simply stops being cullable. Gated by
    /// <c>world-instanced</c>'s two unmaskable-compose guard scenes.</para></summary>
    /// <param name="first">The instance's first instruction index (inclusive).</param>
    /// <param name="end">The instance's instruction end index (exclusive).</param>
    /// <returns><see langword="true"/> when the slice carries an op that reads the running accumulator.</returns>
    private bool HasUnmaskableCompose(int first, int end) {
        var scopeDepth = 0;

        for (var index = first; (index < end); index++) {
            var instruction = m_instructions[index];

            // A PushField reseeds the accumulator, so an accumulator-reading op INSIDE the scope (scopeDepth > 0) reads
            // only the scope's own field — it does NOT make the instance unmaskable. That is the scoped-accumulator
            // payoff: a scoped Onion/Dilate/intersection hands the instance a finite, cullable bound back, instead of
            // the flat model's 1e30 always-evaluated sentinel. Only the POP's OWN compose blend, acting at the parent
            // depth, can be unmaskable (an intersection-family compose composes the whole scope against the parent).
            if (instruction.Op == SdfOp.PushField) {
                scopeDepth++;

                continue;
            }

            if (instruction.Op == SdfOp.PopField) {
                // The builder guarantees balanced scopes, so a PopField at depth 0 is impossible on a real stream — but
                // decode it LOUD rather than silently clamping to 0 (the codebase's fail-fast convention): a silent
                // Math.Max would mask a corrupt/hand-assembled stream and let a bogus compose blend read as unmaskable.
                if (scopeDepth == 0) {
                    throw new InvalidOperationException(message: $"Unbalanced PopField at instruction {index} in the instance range [{first}, {end}): a PopField with no open PushField scope. The builder emits balanced Push/Pop pairs, so this indicates a corrupt instruction stream.");
                }

                scopeDepth--;

                if (
                    (scopeDepth == 0) &&
                    (
                        (instruction.Blend == (uint)SdfBlendOp.Intersection) ||
                        (instruction.Blend == (uint)SdfBlendOp.SmoothIntersection) ||
                        (instruction.Blend == (uint)SdfBlendOp.ChamferIntersection)
                    )
                ) {
                    return true;
                }

                continue;
            }

            // Ops nested in a scope are already handled by the scope's own compose (above): skip them.
            if (scopeDepth > 0) {
                continue;
            }

            if (
                (instruction.Op == SdfOp.Onion) ||
                (instruction.Op == SdfOp.Dilate) ||
                (instruction.Op == SdfOp.Displace)
            ) {
                return true;
            }

            // The Blend lane only carries an SdfBlendOp on a ShapeBlend instruction; the fold ops reuse it (CellJitter's
            // noise flavor, RepeatPolar's mirror flag, WallpaperFold's plane), so it must be read under this guard.
            if (instruction.Op != SdfOp.ShapeBlend) {
                continue;
            }

            if (
                (instruction.Blend == (uint)SdfBlendOp.Intersection) ||
                (instruction.Blend == (uint)SdfBlendOp.SmoothIntersection) ||
                (instruction.Blend == (uint)SdfBlendOp.ChamferIntersection)
            ) {
                return true;
            }
        }

        return false;
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
            case SdfShapeType.Vesica: {
                // Exact and convex, so a real containment bound: the lens reaches (r − d) radially in XZ and
                // b = data0.z axially along Y (max of the two is the farthest point from the local origin).
                center = Vector3.Zero;
                radius = MathF.Max((MathF.Abs(data0.X) - MathF.Abs(data0.Y)), MathF.Abs(data0.Z));

                return true;
            }
            // The 2D-primitive family: each 2D core has an exact 2D bounding radius (its reach from the local 2D
            // origin); LiftedBoundRadius grows it to a 3D containment sphere per the shape's lift (revolve/extrude).
            // All exact + 1-Lipschitz, so — like Vesica — they earn a real cull bound (unlike the approximate ellipsoid).
            case SdfShapeType.RoundedRectangle: {
                center = Vector3.Zero;
                // The rounded corners round INWARD, so the sharp half-extents box (data0.xy) contains the shape.
                radius = LiftedBoundRadius(radius2D: new Vector2(data0.X, data0.Y).Length(), instruction: instruction);

                return true;
            }
            case SdfShapeType.RegularPolygon:
            case SdfShapeType.Star: {
                center = Vector3.Zero;
                // Every vertex/tip is within the (circum/outer) radius data0.x of the 2D origin.
                radius = LiftedBoundRadius(radius2D: MathF.Abs(data0.X), instruction: instruction);

                return true;
            }
            case SdfShapeType.Trapezoid: {
                center = Vector3.Zero;
                // The farthest vertex: bottom (±r1, −he) or top (±r2, +he).
                radius = LiftedBoundRadius(
                    instruction: instruction,
                    radius2D: MathF.Max(new Vector2(data0.X, data0.Z).Length(), new Vector2(data0.Y, data0.Z).Length())
                );

                return true;
            }
            case SdfShapeType.Ellipse: {
                center = Vector3.Zero;
                radius = LiftedBoundRadius(radius2D: MathF.Max(MathF.Abs(data0.X), MathF.Abs(data0.Y)), instruction: instruction);

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
    // Grows a 2D-primitive family shape's exact 2D bounding radius into a 3D containment-sphere radius per its lift
    // (Data1.Y = mode, matching SdfLift / the shader's `data1.y > 0.5`; Data0.W = the lift amount). EXTRUDE sweeps the
    // 2D disc ±half-height along Z ⇒ √(r² + h²); REVOLVE offsets the disc by o then lathes it ⇒ the whole solid lies
    // within (o + r) of the axis-centred origin (see the enclose bound derivation). Both are exact conservative bounds
    // (KEEP IN SYNC with sdfExtrude2D/sdfRevolve2D in Assets/Shaders/Sdf/sdf-vm.hlsli).
    private static float LiftedBoundRadius(float radius2D, in SdfInstruction instruction) {
        var lift = MathF.Abs(instruction.Data0.W);

        return ((instruction.Data1.Y > 0.5f)
            ? MathF.Sqrt((radius2D * radius2D) + (lift * lift))
            : (lift + radius2D));
    }
    // The per-program Lipschitz factor L, returned as the STEP SCALE 1/L in (0, 1]. A SEPARATE static pass over the
    // instruction stream — deliberately NOT grafted into AnalyzeSegment/AnalyzeBounds (they sit at their CA150x
    // complexity ceilings). It answers the one question sphere tracing needs: by how much can this program's packed
    // distance field OVERESTIMATE true distance? A field that overestimates by factor L lets the marcher step L times
    // too far and tunnel through thin/twisted surfaces, so mapCore scales its final distance by 1/L to keep every step
    // conservative (KEEP IN SYNC with the stepScale read + final multiply in Assets/Shaders/Sdf/sdf-vm.hlsli's mapCore).
    //
    // Per chain (reset at each ResetPoint): domain ops that are isometries / non-expansive projections / field ops
    // (Translate/Rotate/TransformDynamic/Symmetry/Repeat/RepeatLimited/WallpaperFold/Elongate/Onion/Dilate; Scale is
    // handled conservatively by the runtime distanceScale) contribute factor 1. A coordinate-keyed plane rotation
    // (BendX/BendY/BendZ/TwistY) contributes the EXACT operator norm of its Jacobian over the chain's reach rho; an
    // ellipsoid (whose SDF can underestimate) contributes its eccentricity. A chain's factor is the product of its
    // domain-op factors times the max shape-approx factor in it (a twisted ellipsoid compounds both errors); the
    // program's L is the max over all chains. A warp-free, eccentricity-free chain yields exactly 1, so a warp-free,
    // eccentricity-free program yields stepScale == 1.0f to the bit and an isometric scene renders byte-identically.
    //
    // A warp's reach rho depends on shapes that can appear AFTER it in the chain (the usual Translate/warp/Shape
    // order), so the chain's warp rates and its reach accumulate as the walk proceeds and fold together at chain end.
    private static float AnalyzeLipschitz(IReadOnlyList<SdfInstruction> instructions) {
        var programLipschitz = 1.0f;
        // Whether ANY PopField composes its scope with a chamfer blend — the one non-1-Lipschitz compose. A chamfer POP
        // composing a warped scope needs √2·max(L_parent, L_child); since programLipschitz already folds the max L over
        // every chain (≥ both), multiplying the whole program by √2 at the end is a sound (conservative) upper bound and
        // never under-clamps. False (no chamfer POP) leaves programLipschitz untouched, so scope-free scenes stay exact.
        var hasChamferPop = false;
        // Each warp's |rate| plus whether its keyed coordinate lies inside the plane it rotates (see BendOperatorNorm).
        var chainWarpRates = new List<(float Rate, bool KeyInRotatedPlane)>();
        var chainShapeApproxMax = 1.0f;   // max ellipsoid eccentricity among the chain's shapes (1 = none / perfectly round)
        var chainShapeReach = 0.0f;       // max local bounding radius among the chain's shapes
        var chainTranslateReach = 0.0f;   // sum of |translate offset| accumulated on the chain
        var chainLogSphereProduct = 1.0f; // product of the chain's log-spherical shell-fold factors exp(w/2) (1 = none)
        var chainChamferFactor = 1.0f;    // sqrt(2) if the chain has ANY chamfer blend (its bevel gradient reaches sqrt(2) at an acute seam); 1 = none
        var chainDisplaceWarpProduct = 1.0f; // product of the chain's Displace/DomainWarp metric-stretch factors (1 + amp*max|freq_i|); reach-independent, like the log-sphere product (1 = none)
        var chainCellJitters = new List<(float MinSpacing, float Jitter)>(); // each CellJitter's (min spacing, jitter), folded at chain-close against the FINAL chainShapeReach

        for (var index = 0; (index < instructions.Count); index++) {
            var instruction = instructions[index];

            // Segments split BEFORE each ResetPoint, so a ResetPoint past the first instruction closes the chain that
            // preceded it: fold that chain, then begin a fresh one (the ResetPoint itself contributes nothing).
            if ((instruction.Op == SdfOp.ResetPoint) && (index != 0)) {
                programLipschitz = MathF.Max(programLipschitz, (FoldChainLipschitz(warpRates: chainWarpRates, shapeApproxMax: chainShapeApproxMax, reach: (chainTranslateReach + chainShapeReach)) * chainLogSphereProduct * chainChamferFactor * chainDisplaceWarpProduct * FoldCellJitterProduct(cellJitters: chainCellJitters, shapeReach: chainShapeReach)));
                chainWarpRates.Clear();
                chainShapeApproxMax = 1.0f;
                chainShapeReach = 0.0f;
                chainTranslateReach = 0.0f;
                chainLogSphereProduct = 1.0f;
                chainChamferFactor = 1.0f;
                chainDisplaceWarpProduct = 1.0f;
                chainCellJitters.Clear();
            }

            switch (instruction.Op) {
                case SdfOp.Translate: {
                    chainTranslateReach += new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z).Length();
                    break;
                }
                case SdfOp.BendX:
                case SdfOp.BendY:
                case SdfOp.BendZ: {
                    // Data0.x is the warp rate (radians of rotation per unit of the keyed coordinate). Every Bend keys
                    // on a coordinate INSIDE the plane it rotates, so its operator norm is the larger 1 + a form.
                    chainWarpRates.Add(item: (MathF.Abs(instruction.Data0.X), true));
                    break;
                }
                case SdfOp.TwistY: {
                    // TwistY keys on y and rotates XZ — the key axis is orthogonal to the rotated plane.
                    chainWarpRates.Add(item: (MathF.Abs(instruction.Data0.X), false));
                    break;
                }
                case SdfOp.ShapeBlend: {
                    chainShapeReach = MathF.Max(chainShapeReach, ShapeReachRadius(instruction: instruction));

                    if ((SdfShapeType)instruction.Shape == SdfShapeType.Ellipsoid) {
                        chainShapeApproxMax = MathF.Max(chainShapeApproxMax, EllipsoidEccentricity(instruction: instruction));
                    }

                    // A chamfer blend's 45° bevel plane reaches gradient sqrt(2) at an acute seam (exactly 1 at a
                    // perpendicular one), so the folded field can overestimate true distance by up to sqrt(2) there. It
                    // compounds as a PRODUCT with a same-chain warp/ellipsoid (like a twisted ellipsoid), so it rides its
                    // own chain factor rather than the shape-approx max. Smooth blends stay 1-Lipschitz — only chamfer.
                    if ((instruction.Blend == (uint)SdfBlendOp.ChamferUnion) || (instruction.Blend == (uint)SdfBlendOp.ChamferIntersection) || (instruction.Blend == (uint)SdfBlendOp.ChamferSubtraction)) {
                        chainChamferFactor = 1.41421356f;
                    }

                    break;
                }
                case SdfOp.LogSphere: {
                    // The log-spherical shell fold's metric-distortion factor compounds over nested folds (a product,
                    // not a max — like a twisted ellipsoid compounding both its errors). Reach-INDEPENDENT, so it does
                    // not join chainWarpRates (which fold over the chain reach); it multiplies the whole chain's factor.
                    chainLogSphereProduct *= LogSphereLipschitz(instruction: instruction);
                    break;
                }
                case SdfOp.CellJitter: {
                    // TWO orthogonal Lipschitz contributions, both kept:
                    //
                    // (1) REACH under a downstream warp. The per-cell displacement is INDEPENDENT on each axis
                    // ((r0 - 0.5) * Data0.w, r0 a float3), so a corner cell moves up to (sqrt(3)/2) * |Data0.w| in
                    // Euclidean distance toward a downstream warp, extending that warp's reach — treat it like a Translate
                    // of that magnitude. chainTranslateReach is a Euclidean-length sum (Translate adds Vector3(...).Length()),
                    // so the per-axis half-amplitude must be combined as a VECTOR (sqrt(3)/2), not summed as a scalar (0.5),
                    // or a jitter-under-a-warp chain would under-count reach and let the over-relaxed march overstep. The
                    // tumble is a rotation about the cell center (already inside chainShapeReach) and the fold is an
                    // isometry — NEITHER adds anything more.
                    chainTranslateReach += (0.8660254f * MathF.Abs(instruction.Data0.W));
                    // (2) The STANDALONE boundary-discontinuity step factor (the LogSphere-shaped fix). Stash this op's
                    // (min spacing, jitter) so the chain-close fold can compute a REACH-INDEPENDENT factor against the
                    // chain's FINAL max shapeReach (the shapes follow the fold, like chainLogSphereProduct's shells).
                    // See FoldCellJitterProduct / CellJitterLipschitz.
                    var cellSpacing = new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z);

                    chainCellJitters.Add(item: (MathF.Min(cellSpacing.X, MathF.Min(cellSpacing.Y, cellSpacing.Z)), instruction.Data0.W));
                    break;
                }
                case SdfOp.Displace: {
                    // The sinusoidal relief's gradient is bounded by amp*max|freq_i| (a global, reach-INDEPENDENT bound on the
                    // sin-product basis; see DisplaceWarpLipschitz), so the field can overestimate by that. It multiplies the whole
                    // chain like the log-sphere product. A FIELD op, so it adds no reach (the point is untouched).
                    chainDisplaceWarpProduct *= DisplaceWarpLipschitz(instruction: instruction);
                    break;
                }
                case SdfOp.DomainWarp: {
                    // Same reach-independent metric-stretch factor (1 + amp*max|freq_i|) as Displace. As a POINT op it also
                    // moves the point by up to amp*sqrt(3), extending a downstream twist/bend's reach like a Translate.
                    chainDisplaceWarpProduct *= DisplaceWarpLipschitz(instruction: instruction);
                    chainTranslateReach += (1.7320508f * MathF.Abs(instruction.Data0.W));
                    break;
                }
                case SdfOp.PopField: {
                    // A scope's compose blend rides the POP's Blend lane. A chamfer compose is the one that is not
                    // 1-Lipschitz (bevel gradient up to √2), so flag the program for the √2 factor folded in at the end.
                    // Every other compose (Union/Subtraction/Smooth) preserves the Lipschitz bound of the fields it
                    // composes, which their own chains already contributed to programLipschitz. PushField contributes
                    // nothing (it only reseeds the accumulator) — it falls to the default arm.
                    if (
                        (instruction.Blend == (uint)SdfBlendOp.ChamferUnion) ||
                        (instruction.Blend == (uint)SdfBlendOp.ChamferIntersection) ||
                        (instruction.Blend == (uint)SdfBlendOp.ChamferSubtraction)
                    ) {
                        hasChamferPop = true;
                    }

                    break;
                }
                default: {
                    // ResetPoint/Rotate/Scale/TransformDynamic/SymmetryPlane/Repeat/RepeatLimited/WallpaperFold/RepeatPolar/
                    // Elongate/Onion/Dilate: factor 1 (isometry, non-expansive projection, field op, or the runtime
                    // distanceScale-handled Scale) — nothing accumulates. (RepeatPolar is a rotation/reflection fold,
                    // exactly like Repeat; CellJitter is handled above: its jitter half-amplitude joins the chain reach,
                    // its tumble/fold are isometries.)
                    break;
                }
            }
        }

        // Fold the final (or only) chain.
        programLipschitz = MathF.Max(programLipschitz, (FoldChainLipschitz(warpRates: chainWarpRates, shapeApproxMax: chainShapeApproxMax, reach: (chainTranslateReach + chainShapeReach)) * chainLogSphereProduct * chainChamferFactor * chainDisplaceWarpProduct * FoldCellJitterProduct(cellJitters: chainCellJitters, shapeReach: chainShapeReach)));

        // A chamfer POP composes its scope with a √2-bevel seam that can overestimate true distance by up to √2 beyond
        // the max L of the fields it joins — fold that in over the whole program (a conservative bound on √2·max(L_parent,
        // L_child); see hasChamferPop). No chamfer POP leaves this exact, so scope-free scenes stay byte-identical.
        if (hasChamferPop) {
            programLipschitz *= 1.41421356f;
        }

        // stepScale = 1 / max(L, 1), clamped to (0, 1]. A warp-free, eccentricity-free program has L == 1 exactly, so
        // this returns 1.0f to the bit (max(1,1) = 1, 1/1 = 1). The finite guard keeps an extreme authored warp from
        // producing a non-finite scale, which the shader's `> 0` guard would wrongly read as "no clamp".
        var lipschitz = MathF.Max(programLipschitz, 1.0f);

        return (float.IsFinite(lipschitz) ? (1.0f / lipschitz) : 0.0001f);
    }
    // One chain's Lipschitz factor: the product of its warps' exact operator norms over the chain reach rho, times the
    // max shape-approx factor (ellipsoid eccentricity) in it. A warp-free, eccentricity-free chain returns 1.0f
    // exactly (an empty product times a 1.0 max).
    private static float FoldChainLipschitz(List<(float Rate, bool KeyInRotatedPlane)> warpRates, float shapeApproxMax, float reach) {
        var domainProduct = 1.0f;

        foreach (var warp in warpRates) {
            var a = (warp.Rate * reach);

            domainProduct *= (warp.KeyInRotatedPlane ? BendOperatorNorm(a: a) : TwistOperatorNorm(a: a));
        }

        return (domainProduct * shapeApproxMax);
    }
    // A coordinate-keyed plane rotation's Jacobian is an orthonormal rotation R times (I + a·n·mᵀ) — a rank-1 shear of
    // magnitude a = |rate| * rho. Its largest singular value depends on the angle between the shear direction n and the
    // key direction m, which is decided by whether the KEYED coordinate lies inside the ROTATED plane:
    //
    //   BendX (keys x, rotates XY), BendY (keys y, rotates XY), BendZ (keys y, rotates YZ) — the key axis lies IN the
    //   rotated plane, so n can align with m and sup‖I + a·n·mᵀ‖ = 1 + a, ATTAINED (BendX at (x, y) = (0, rho)).
    //
    //   TwistY (keys y, rotates XZ) — the key axis is ORTHOGONAL to the rotated plane, so n ⟂ m always and the norm
    //   collapses to sqrt((2 + a² + a·sqrt(a² + 4)) / 2). That form also beats sqrt(1 + a²), which underestimates it.
    //
    // Using the twist form for a bend UNDER-clamps by up to 24% and lets the over-relaxed march overstep and hole
    // (verified: central-difference Jacobian + power iteration; at a = 1 the bends measure 1.996, the twist form 1.618).
    // Both return exactly 1.0f at a == 0 (zero rate, or zero reach), so a warp-free program's stepScale stays 1.0f to
    // the bit and its scene renders byte-identically.
    private static float BendOperatorNorm(float a) => (1.0f + a);
    private static float TwistOperatorNorm(float a) {
        var aSquared = (a * a);

        return MathF.Sqrt((2.0f + aSquared + (a * MathF.Sqrt(aSquared + 4.0f))) / 2.0f);
    }
    // The ellipsoid's eccentricity max(radii)/min(radii): its SDF is a first-order approximation that degrades with
    // aspect ratio, so a 4:1 ellipsoid can underestimate true distance by ~4x. The clamped positive radii live in
    // Data0.xyz (SdfProgramBuilder.Ellipsoid), so max/min reads them directly. A perfectly round ellipsoid returns 1.0.
    private static float EllipsoidEccentricity(SdfInstruction instruction) {
        var rx = MathF.Abs(instruction.Data0.X);
        var ry = MathF.Abs(instruction.Data0.Y);
        var rz = MathF.Abs(instruction.Data0.Z);
        var largest = MathF.Max(rx, MathF.Max(ry, rz));
        var smallest = MathF.Min(rx, MathF.Min(ry, rz));

        return ((smallest > 0.0f) ? (largest / smallest) : 1.0f);
    }
    // The log-spherical shell fold's metric-distortion factor exp(w/2), where w = |Data0.x| (= ln shellRatio). WITHIN a
    // shell the corrected field is EXACTLY 1-Lipschitz (a uniform scale plus an isometric Z-spin), so the ONLY
    // non-1-Lipschitz source is the nearest-shell (round) boundary discontinuity: a sample sits within HALF a log-cell
    // of its shell centre, so the fold's Cartesian scale varies by at most exp(w/2) across the samples the marcher can
    // reach in one step. Baking 1/exp(w/2) into the step clamp holds the OVER-RELAXED march (omega = 1.2) a conservative
    // lower bound across shell boundaries (no tunnelling) for content that respects the shell cell — proven by the
    // world-log-sphere-solidity gate. Reach-INDEPENDENT (unlike twist/bend): the fold's scale distortion does not grow
    // with shape reach. w == 0 (shellRatio 1) yields exp(0) = 1 (no shells, no penalty), preserving byte-identity.
    private static float LogSphereLipschitz(SdfInstruction instruction) {
        return MathF.Exp(0.5f * MathF.Abs(instruction.Data0.X));
    }
    // The metric-stretch step factor for a Displace/DomainWarp sinusoidal field (Data0.xyz = frequency, Data0.w =
    // amplitude). The bound is amp * MAX|frequency component| — the infinity norm, NOT the Euclidean length:
    //
    //   Displace: grad(A·sin(fx·x)·sin(fy·y)·sin(fz·z)) has squared norm A²·h(s²ₓ, s²ᵧ, s²_z) with h MULTILINEAR in the
    //   three squared sines, so its maximum over the unit cube sits at a vertex — and every vertex evaluates to a single
    //   f_i². Hence sup‖grad‖ = A·max|f_i|.
    //
    //   DomainWarp: J = I + A·D·P with P the cyclic permutation and D = diag(f_i·cos(...)), so ‖J - I‖ = ‖D‖ = the
    //   largest entry <= A·max|f_i|.
    //
    // Using ‖f‖₂ (which is >= ‖f‖∞) was merely conservative, never wrong — but it over-clamps by up to sqrt(3)x on an
    // isotropic frequency and buys nothing but march steps. Reach-INDEPENDENT (a global bound on the sin basis, unlike
    // twist/bend which grow with reach), so it multiplies the whole chain like exp(w/2). amp == 0 yields 1.0f exactly,
    // so a displace/warp-free program stays byte-identical.
    private static float DisplaceWarpLipschitz(SdfInstruction instruction) {
        var amplitude = MathF.Abs(instruction.Data0.W);
        var frequency = MathF.Max(MathF.Abs(instruction.Data0.X), MathF.Max(MathF.Abs(instruction.Data0.Y), MathF.Abs(instruction.Data0.Z)));

        return (1.0f + (amplitude * frequency));
    }
    // The smallest in-cell margin CellJitterLipschitz will admit: a floor so a degenerate scene (jitter/2 + shape reach
    // meeting or exceeding half the cell) yields a LARGE-but-finite factor (a very small, safe step) instead of a
    // divide-by-zero or a negative — never a non-finite scale the shader's `> 0` guard would misread as "no clamp".
    private const float CellJitterMinMargin = 1.0e-4f;
    // The product of a chain's CellJitter boundary step factors, folded at chain-close against the chain's FINAL max
    // shape reach (mirrors chainLogSphereProduct's role, but reach-DEPENDENT on the shapes that follow the fold, so it
    // cannot be accumulated inline — the shape reach is only known once the whole chain has been walked). Compounds as a
    // PRODUCT (like nested log-spherical folds): two CellJitters in one chain each contribute their own overestimate.
    // Empty (no CellJitter in the chain) ⇒ 1.0f exactly, so a jitter-free program stays byte-identical.
    private static float FoldCellJitterProduct(List<(float MinSpacing, float Jitter)> cellJitters, float shapeReach) {
        var product = 1.0f;

        foreach (var (minSpacing, jitter) in cellJitters) {
            product *= CellJitterLipschitz(minSpacing: minSpacing, jitter: jitter, shapeReach: shapeReach);
        }

        return product;
    }
    // The boundary-discontinuity step factor for one CellJitter fold — the CellJitter twin of LogSphereLipschitz's
    // exp(w/2), and the fix for the world-cell-jitter-solidity gate. WHY a factor is needed: CellJitter folds space to
    // cells like Repeat (round(localPosition / spacing)), then displaces each cell by an INDEPENDENT hashed offset in
    // [-jitter/2, +jitter/2] per axis (and optionally tumbles it). At a point p just inside cell A near the A|B cell
    // wall, the field returns dist(p, shapeA) — round() picked A — but the TRUE nearest surface may be shapeB in the
    // adjacent cell, whose hashed offset pushed it toward the wall. So the folded field OVERESTIMATES true distance
    // across every cell wall (the classic domain-repetition overstep, sharpened by jitter and tumble), and the
    // OVER-RELAXED march (omega 1.2, sdf-world.hlsli) tunnels through the content behind the wall unless the step clamps.
    //
    // The bound. R = shapeReach is the prototype's FULL bounding radius (chainShapeReach). Two halves:
    //  * d_true >= m. With the in-cell margin m = min(spacing)/2 - jitter/2 - R, every cell's bounding sphere (radius R
    //    about its jittered centre) sits at least m inside each wall, so the NEIGHBOUR's nearest surface point stands off
    //    at least m from a point p on the shared wall — whatever the tumble (a rotation keeps the shape inside its
    //    bounding sphere). Using min(spacing) picks the tightest (smallest-m, largest-factor) axis.
    //  * d_field <= |p - cA|. The prototype CONTAINS its jittered centre cA (the intended compact scatter target — box,
    //    sphere, capsule, the 2D-lift family all do; a hollow prototype centred on the cell is out of contract), so the
    //    field's own candidate can be no farther than the centre. Its WORST value is with p on the tight-axis wall (a
    //    distance min(spacing)/2 from cell A's centre), the neighbour aligned with p so d_true = m, and cA displaced to
    //    the opposite jitter corner: along the tight axis |p - cA| reaches min(spacing)/2 + jitter/2, and along EACH of
    //    the two tangential axes up to a full jitter (p at +jitter/2 while cA is at -jitter/2). Hence
    //    |p - cA| <= sqrt((min(spacing)/2 + jitter/2)^2 + 2*jitter^2).
    // So d_field / d_true <= sqrt((min(spacing)/2 + jitter/2)^2 + 2*jitter^2) / m = L_cj, and baking 1/L_cj into the step
    // clamp restores a conservative <= underestimate — the over-relaxed march then stays hole-free across every cell
    // boundary (world-cell-jitter-solidity). This SUP is reached by a degenerate point prototype (thin_extent -> 0); a
    // fatter prototype only lowers the true ratio, so L_cj covers it — unlike the sphere-model 1 + sqrt(3)*jitter/m
    // (= (m + sqrt(3)*jitter)/m), which assumes d_field = |p - cA| - R and so UNDER-clamps a thin TUMBLING plate, whose
    // thinnest face turned toward p leaves d_field near |p - cA|. A standalone product factor folded at chain-close (it
    // needs the chain's final R, so it can't accumulate inline like exp(w/2)), NOT a warp rate over the chain reach.
    // jitter == 0 (an exact Repeat) or a margin so loose the ratio falls to 1 ⇒ factor 1.0f, preserving byte-identity.
    private static float CellJitterLipschitz(float minSpacing, float jitter, float shapeReach) {
        var amplitude = MathF.Abs(jitter);

        if (amplitude == 0.0f) {
            return 1.0f;
        }

        var halfSpacing = (0.5f * minSpacing);
        var halfJitter = (0.5f * amplitude);
        var margin = MathF.Max((halfSpacing - halfJitter - shapeReach), CellJitterMinMargin);
        var fieldReach = (halfSpacing + halfJitter);
        var fieldMax = MathF.Sqrt((fieldReach * fieldReach) + (2.0f * amplitude * amplitude));

        // >= 1: the overestimate ratio can never be below 1 (the field never UNDER-reports the current cell's shape); a
        // loose margin that drives the raw ratio below 1 must not pull a same-chain twist/ellipsoid factor DOWN.
        return MathF.Max((fieldMax / margin), 1.0f);
    }
    // A conservative LOCAL bounding radius for a shape's reach rho (how far the evaluated point can range from the
    // chain origin): reuses TryGetLocalBound where it applies, ADDS the ellipsoid (which TryGetLocalBound rejects as a
    // cull bound — its SDF can underestimate — but whose geometric max radius is a fine reach), and treats the
    // unbounded plane as 0 (planes are never warped in practice, and any bounded shape sharing the chain dominates the
    // max). Over-estimating rho only slows the march, never makes it unsafe.
    private static float ShapeReachRadius(SdfInstruction instruction) {
        if (TryGetLocalBound(instruction: instruction, center: out var center, radius: out var radius)) {
            return (center.Length() + radius);
        }

        if ((SdfShapeType)instruction.Shape == SdfShapeType.Ellipsoid) {
            return MathF.Max(MathF.Abs(instruction.Data0.X), MathF.Max(MathF.Abs(instruction.Data0.Y), MathF.Abs(instruction.Data0.Z)));
        }

        return 0.0f;
    }
    private static void WriteVector4(uint[] words, int baseIndex, float x, float y, float z, float w) {
        words[baseIndex] = BitConverter.SingleToUInt32Bits(value: x);
        words[(baseIndex + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        words[(baseIndex + 2)] = BitConverter.SingleToUInt32Bits(value: z);
        words[(baseIndex + 3)] = BitConverter.SingleToUInt32Bits(value: w);
    }
}
