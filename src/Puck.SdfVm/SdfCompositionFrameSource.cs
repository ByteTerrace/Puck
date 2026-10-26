using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The host seam <see cref="SdfCompositionFrameSource"/> calls once per <see cref="ISdfFrameSource.CaptureFrame"/>
/// to turn the composed <see cref="SdfProgram"/> + dynamic transforms into a full <see cref="SdfFrame"/> — the "dress"
/// half of frame production (views, camera/lighting mood, the grid-lock overlay, debug view flags) that has nothing to
/// do with what geometry exists and everything to do with how this frame presents it. Composition owns the emitter
/// list (content); a host implements this to own presentation, without needing to know how the program was built.</summary>
public interface ISdfFrameDresser {
    /// <summary>Gets whether this frame belongs to a tick-locked presentation, whose content builds finish before the
    /// frame reads them. Ordinary presentation may keep its previous program while a replacement builds.</summary>
    bool TickLocked => false;

    /// <summary>Builds this frame's <see cref="SdfFrame"/> from the composed program/transforms.</summary>
    /// <param name="program">This frame's program (freshly rebuilt this call, or the same instance as last call when
    /// the composed content revision hasn't changed — compare by reference against a previous frame's
    /// <see cref="SdfFrame.Program"/> to detect a real change, exactly like <see cref="SdfFrame.ProgramChanged"/>).</param>
    /// <param name="transforms">This frame's packed dynamic-transform buffer (every registered emitter's slots) — the
    /// host's OWN buffer, handed over as the concrete array rather than a read-only view because a dresser typically
    /// retains it past this call (an offscreen view pass rendering the same world after capture) and passes it to
    /// span-taking consumers. Never mutated by the host until the next <see cref="ISdfFrameSource.CaptureFrame"/>.</param>
    /// <param name="moved">The table's moved set, which the dressed frame carries as
    /// <see cref="SdfFrame.MovedTransforms"/> so every engine rendering it stages only the rows it is owed.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The presentation frame delta in seconds.</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> toward the current fixed simulation tick.</param>
    /// <param name="meshDraws">Every emitter's mesh draws (<see cref="ISdfSceneEmitter.MeshDraws"/>), composed in list
    /// order, which the dressed frame carries as <see cref="SdfFrame.MeshDraws"/>. The host may rewrite one list in place
    /// from frame to frame; <paramref name="meshDrawsRevision"/> moves whenever it does.</param>
    /// <param name="meshDrawsRevision">The composed draws' revision, which the dressed frame carries as
    /// <see cref="SdfFrame.MeshDrawsRevision"/>.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha);

    /// <summary>Gets the font atlas the dressed world's text samples, which the composed frame source hands its host as
    /// its <see cref="ISdfFrameSource.GlyphAtlas"/>; <see langword="null"/>, the default, for a world with no text.</summary>
    SdfGlyphAtlas? GlyphAtlas => null;
    /// <summary>Gets the glyph decals the dressed world's text screens draw, which the composed frame source hands its
    /// host as its <see cref="ISdfFrameSource.ScreenDecals"/>; <see langword="null"/>, the default, for none.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => null;
    /// <summary>Gets the screen-surface transforms of the dressed world's screens that move, which the composed frame
    /// source hands its host as its <see cref="ISdfFrameSource.ScreenSurfaceTransforms"/>; <see langword="null"/>, the
    /// default, for none.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => null;
}
/// <summary>Composes geometry sources, allocating their actual encoded transforms and rigid-copy roots. Only geometry revisions rebuild the field program; copy membership and poses do not.</summary>
public sealed partial class SdfCompositionFrameSource : ISdfFrameSource {
    /// <summary>Gets the generic "far below anything" fallback park position — a host that needs a matching literal
    /// outside a live <see cref="SdfEmitContext"/> (a construction-time capacity probe, say) reads this rather than
    /// carrying its own copy.</summary>
    public static readonly Vector3 DefaultParkPosition = new(
        x: 0f,
        y: -1000f,
        z: 0f
    );

    // The components as they stood when the program currently held in m_program was built. No sentinel is needed:
    // m_program is null until the first build, and that null is what forces it.
    private readonly int[] m_builtRevisions;
    private readonly ISdfFrameDresser m_dresser;
    private readonly IReadOnlyList<ISdfSceneEmitter> m_emitters;
    // This frame's freshly-read components, and the STAGING half of the built record: captured before a build, promoted
    // into m_builtRevisions only once that build has returned. See CaptureRevisions/CaptureFrame for why both halves
    // of that ordering are load-bearing.
    private readonly int[] m_pendingRevisions;
    // Where each emitter's revision components start inside the two flattened vectors below — Count + 1 entries, so
    // emitter i owns [m_revisionOffsets[i], m_revisionOffsets[i + 1]) and the last entry is the total length. Sized
    // once at construction from each emitter's RevisionComponentCount, which its contract pins for the emitter's life.
    private readonly int[] m_revisionOffsets;
    private readonly int[] m_slotBases;
    // Each emitter's mesh draws and their revision as the host last read them, and their composition, which the
    // dresser receives with its revision: recomposed only when some emitter's list or revision moved, into one list the
    // host keeps and rewrites, so an unchanged frame hands the engine what it already packed and a changed one
    // allocates nothing once the list has grown.
    private readonly IReadOnlyList<SdfMeshDraw>?[] m_meshDrawSources;
    private readonly long[] m_meshDrawRevisions;

    private readonly List<SdfMeshDraw> m_composedMeshDraws = [];
    private IReadOnlyList<SdfMeshDraw> m_meshDraws = [];

    private long m_meshDrawsRevision;
    // The shared table: it keeps every slot's last packed transform across frames, so an owner at rest costs nothing.
    private DynamicTransform[] m_transforms;
    private int m_programSlots;

    private readonly List<SdfMeshInstance> m_meshInstances = [];
    private readonly List<int> m_meshAssetSlots = [];
    private readonly SdfMovedTransforms m_moved = new();

    // The table as it stood before a repack, which the repack is compared with; made at the first repack.
    private DynamicTransform[]? m_repackedTransforms;
    private bool m_transformsPacked;

    // The background build in flight, if any, and the components it was emitted from.
    private readonly int[] m_buildingRevisions;

    private Task<(SdfProgram Program, SdfProgramDelta? Delta)>? m_building;
    private Puck.Abstractions.Presentation.FrameCaptureRequest? m_convergence;
    private float m_interpolationAlpha;
    private Vector3 m_packedParkPosition;
    private SdfProgram? m_program;
    // The words the held program differs in from the one held before it, when its background build measured them.
    private SdfProgramDelta? m_programDelta;
    private float m_time;
    // What a background build hands the program it finished (ObserveBuilds), and the transforms the build's program was
    // emitted under, copied at the emission: one build runs at a time, so the next emission waits for this one's to finish.
    private Action<SdfProgram, IReadOnlyList<DynamicTransform>>? m_built;

    private DynamicTransform[] m_builtTransforms = [];

    /// <summary>Gets whether a rebuild after the first builds its program on the thread pool: the frame that finds the
    /// content moved emits the program, and every frame until the build finishes renders the program held before it, with
    /// its transforms, mesh draws and dress live; the first frame after the build finishes adopts it. A converging capture
    /// (<see cref="BeginConvergence"/>) or tick-locked dresser waits for the build instead. <see langword="false"/>, the default, builds every
    /// program in the frame that finds the content moved.</summary>
    public bool BuildsInBackground { get; init; }
    /// <summary>Gets whether a background build is in flight (<see cref="BuildsInBackground"/>).</summary>
    public bool Building => (m_building is not null);
    /// <summary>Gets how many frames <see cref="CaptureFrame"/> has captured. A frame captured after a reading of it has
    /// read every emitter's content as it stood then, so once <see cref="Building"/> is false after such a frame,
    /// <see cref="Program"/> is the program that content calls for.</summary>
    public long Frames { get; private set; }
    /// <summary>Gets the program the frames carry, or <see langword="null"/> before the first frame.</summary>
    public SdfProgram? Program => m_program;
    /// <summary>Gets the number of completed program builds, separate from per-frame instance poses.</summary>
    public long ProgramBuilds { get; private set; }

    private bool Converging => (m_convergence is { Completion.IsCompleted: false });
    private bool WaitsForBuild => (Converging || m_dresser.TickLocked);

    /// <inheritdoc/>
    /// <remarks>Until the request completes, a frame adopts a background build in flight, waiting for it, and builds a
    /// moved program in the frame itself.</remarks>
    public void BeginConvergence(Puck.Abstractions.Presentation.FrameCaptureRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        m_convergence = request;
    }
    /// <inheritdoc/>
    /// <remarks>A program built in the frame that emitted it is never handed over.</remarks>
    public void ObserveBuilds(Action<SdfProgram, IReadOnlyList<DynamicTransform>> built) {
        ArgumentNullException.ThrowIfNull(argument: built);
        Volatile.Write(location: ref m_built, value: built);
    }

    /// <inheritdoc/>
    /// <remarks>The dresser's (<see cref="ISdfFrameDresser.GlyphAtlas"/>).</remarks>
    public SdfGlyphAtlas? GlyphAtlas => m_dresser.GlyphAtlas;
    /// <inheritdoc/>
    /// <remarks>The dresser's (<see cref="ISdfFrameDresser.ScreenDecals"/>).</remarks>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => m_dresser.ScreenDecals;
    /// <inheritdoc/>
    /// <remarks>The dresser's (<see cref="ISdfFrameDresser.ScreenSurfaceTransforms"/>).</remarks>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => m_dresser.ScreenSurfaceTransforms;

    /// <summary>Composes <paramref name="emitters"/> into one frame source, dressed each frame by
    /// <paramref name="dresser"/>.</summary>
    /// <param name="emitters">The fixed emitter list, in emission order (also the order their dynamic-transform slot
    /// ranges are assigned — see the type remarks). Copied defensively; mutating the source list afterward has no
    /// effect.</param>
    /// <param name="dresser">Builds each frame's <see cref="SdfFrame"/> from the composed program/transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="emitters"/> or <paramref name="dresser"/> is
    /// <see langword="null"/>, or <paramref name="emitters"/> contains a <see langword="null"/> entry.</exception>
    public SdfCompositionFrameSource(IReadOnlyList<ISdfSceneEmitter> emitters, ISdfFrameDresser dresser) {
        ArgumentNullException.ThrowIfNull(emitters);
        ArgumentNullException.ThrowIfNull(dresser);

        m_emitters = [.. emitters];
        m_dresser = dresser;
        m_slotBases = new int[m_emitters.Count];
        m_meshDrawSources = new IReadOnlyList<SdfMeshDraw>?[m_emitters.Count];
        m_meshDrawRevisions = new long[m_emitters.Count];
        m_revisionOffsets = new int[(m_emitters.Count + 1)];

        var slotCursor = 0;
        var revisionCursor = 0;

        for (var index = 0; (index < m_emitters.Count); index++) {
            if (m_emitters[index] is not { } emitter) {
                throw new ArgumentNullException(
                    paramName: nameof(emitters),
                    message: $"emitters[{index}] is null."
                );
            }

            m_slotBases[index] = slotCursor;
            slotCursor += Math.Max(
                val1: 0,
                val2: emitter.DynamicSlotCount
            );
            // Both counts are read exactly ONCE, here, and both are contract-pinned for the emitter's lifetime — an
            // emitter that grew a component later would silently overrun the slice this layout handed it.
            m_revisionOffsets[index] = revisionCursor;
            revisionCursor += Math.Max(
                val1: 0,
                val2: emitter.RevisionComponentCount
            );
        }

        m_revisionOffsets[m_emitters.Count] = revisionCursor;
        m_builtRevisions = new int[revisionCursor];
        m_pendingRevisions = new int[revisionCursor];
        m_buildingRevisions = new int[revisionCursor];
        m_transforms = new DynamicTransform[slotCursor];

        m_programSlots = slotCursor;
    }

    /// <summary>Gets or sets where a hidden/unused dynamic-transform slot parks this frame (<see cref="SdfEmitContext.ParkPosition"/>)
    /// — settable so a host can move it to sit well outside its own world's camera/tile-cull reach. Changing this does
    /// not rebuild the program; the next frame repacks the whole table.</summary>
    public Vector3 ParkPosition { get; set; } = DefaultParkPosition;

    /// <summary>Gets the dynamic-transform table's moved set, which also counts the packing work as
    /// <see cref="SdfMovedTransforms.SourceName"/>.</summary>
    public SdfMovedTransforms MovedTransforms => m_moved;
    /// <summary>Gets the dynamic-transform slot floor the render assembly must reserve — the sum of every registered
    /// emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/>.</summary>
    public int DynamicTransformCapacity => m_transforms.Length;
    /// <summary>Gets the live program's instance count, or zero before its first build.</summary>
    public int InstanceCapacity => (m_program?.Instances.Count ?? 0);
    /// <summary>Gets the live program's word count, or zero before its first build.</summary>
    public int ProgramWordCapacity => (m_program?.Words.Length ?? 0);

    // A live rebuild's emission, which reads the emitters' live state and so runs in the frame. `previous`, the program
    // this call rebuilds, supplies List capacity hints for the fresh builder: its own counts are exactly what this build's
    // counts were last time, and a live scene's shape rarely swings wildly frame to frame, so seeding from them removes
    // most of List<T>'s geometric-growth reallocation without guessing at a number nothing measured. A capacity hint
    // changes nothing observable (List<T>.Capacity affects no read value).
    // The convex-polygon profiles the last live emission emitted: the program keeps its side table to itself.
    private int m_emittedConvexPolygons;

    private SdfProgramBuilder EmitProgram(SdfEmitContext context, SdfProgram? previous) {
        var builder = new SdfProgramBuilder(
            instructionCapacity: (previous?.InstructionCount ?? 0),
            instanceCapacity: (previous?.Instances.Count ?? 0),
            materialCapacity: (previous?.MaterialCount ?? 0),
            screenSurfaceCapacity: (previous?.ScreenSurfaces.Count ?? 0),
            convexPolygonCapacity: m_emittedConvexPolygons
        );

        Emit(
            builder: builder,
            context: context
        );
        m_emittedConvexPolygons = builder.ConvexPolygonCount;

        return builder;
    }
    // A build packs, validates and analyses the emitted builder, which owns everything it reads, so it runs on any thread.
    private static SdfProgram Build(SdfProgramBuilder builder) => builder.Build();
    // Adopts the program a background build finished, or waits for it in a converging or tick-locked frame, so that frame
    // never holds a program its source has moved past. The adopted program was built from m_buildingRevisions, which
    // become the built record; whether this frame's revisions moved is judged against them afresh. A build that threw
    // throws here and leaves the built record describing the program still held, so the next frame emits again.
    private bool AdoptBuild(ref bool moved) {
        if ((m_building is not { } building) || (!building.IsCompleted && !WaitsForBuild)) {
            return false;
        }

        m_building = null;
        (m_program, m_programDelta) = building.GetAwaiter().GetResult();
        ProgramBuilds++;
        Array.Copy(
            sourceArray: m_buildingRevisions,
            destinationArray: m_builtRevisions,
            length: m_builtRevisions.Length
        );
        moved = !m_pendingRevisions.AsSpan().SequenceEqual(other: m_builtRevisions);

        return true;
    }
    // Shared by construction's probe and every live rebuild: every emitter's Emit in list order, wrapped in a material
    // scope for any OwnsMaterialScope emitter (the only behavior difference from calling Emit directly; see
    // SdfMaterialScope). context.SlotBase is overwritten per emitter from m_slotBases; the caller's context otherwise
    // carries Probe/Time/RenderOrigin/ParkPosition through unchanged.
    private void Emit(SdfProgramBuilder builder, SdfEmitContext context) {
        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];
            var emitContext = new SdfEmitContext(
                Probe: context.Probe,
                Time: context.Time,
                RenderOrigin: context.RenderOrigin,
                ParkPosition: context.ParkPosition,
                SlotBase: m_slotBases[index],
                InterpolationAlpha: context.InterpolationAlpha
            );

            if (emitter.OwnsMaterialScope) {
                using var scope = builder.BeginMaterialScope();

                emitter.Emit(
                    builder: builder,
                    context: in emitContext
                );
            } else {
                emitter.Emit(
                    builder: builder,
                    context: in emitContext
                );
            }
        }
    }
    // THE REBUILD TRIGGER, and it must not be able to CANCEL — reading this frame's components into m_pendingRevisions
    // and reporting whether any of them moved, in one pass.
    //
    // CANCELLATION IS WHAT THIS SHAPE EXISTS TO KILL, and it has to be killed at the SOURCE, not layered over. An
    // elementwise compare of one number PER EMITTER is not enough while that number is itself a sum: not every counter
    // feeding it is monotonic (at least one is assigned from a server-supplied snapshot value and can move DOWN), so a
    // server revision falling by k while a sibling rises by k leaves that emitter's element unchanged, no rebuild
    // happens, and the frame renders silently stale geometry — the same defect, one level in. So emitters hand over
    // their COMPONENTS (ISdfSceneEmitter.WriteRevision) and this compares the flattened vector of all of them: with no
    // addition anywhere on the path, there is nothing left that can cancel. Impossible BY COMPARISON, not improbable
    // by hashing. Never re-fold any part of this into a sum, and never trade it for a digest — a digest reintroduces a
    // collision this shape does not have, and a per-emitter aggregate reintroduces the one just removed.
    //
    // Both arrays are sized once at construction against the defensively-copied emitter list, so the layout is fixed
    // for the instance's life and this allocates nothing; the indexed loop avoids an interface enumerator, and the
    // span slice hands each emitter exactly its own range. It deliberately does NOT stop at the first difference: the
    // whole pending vector must be filled, because a promotion after the build copies all of it.
    private bool CaptureRevisions() {
        var moved = false;

        for (var index = 0; (index < m_emitters.Count); index++) {
            var start = m_revisionOffsets[index];
            var length = (m_revisionOffsets[(index + 1)] - start);

            if (length == 0) {
                continue;
            }

            var components = m_pendingRevisions.AsSpan(
                length: length,
                start: start
            );

            m_emitters[index].WriteRevision(destination: components);

            for (var component = 0; (component < length); component++) {
                if (components[component] != m_builtRevisions[(start + component)]) {
                    moved = true;
                }
            }
        }

        return moved;
    }
    // Reads every emitter's straddling slot runs after packing: one emitter's list passes through as it is, several are
    // copied into a new list, and none is the one empty list, so a frame no body straddles a portal in costs nothing.
    private IReadOnlyList<SdfSlotClip> ComposeSlotClips() {
        IReadOnlyList<SdfSlotClip> composed = [];

        for (var index = 0; (index < m_emitters.Count); index++) {
            var clips = m_emitters[index].SlotClips;

            if (clips.Count == 0) {
                continue;
            }

            composed = ((composed.Count == 0)
                ? clips
                : [.. composed, .. clips]
            );
        }

        foreach (var copy in m_meshInstances) {
            if (copy.Clip is { } clip) { composed = [.. composed, clip with { FirstSlot = copy.FrameSlot, SlotCount = 1 }]; }
        }
        return composed;
    }
    // Reads every emitter's mesh draws after packing and recomposes them only when an emitter's list or revision moved:
    // one emitter's list passes through as it is, several are copied in list order into the host's own list. Either way
    // the composed revision moves, so an engine repacks even a list rewritten in place.
    private void ComposeMeshDraws() {
        var changed = false;
        var drawing = 0;
        IReadOnlyList<SdfMeshDraw> only = [];

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];
            var draws = emitter.MeshDraws;
            var revision = emitter.MeshDrawsRevision;

            if (
                !ReferenceEquals(
                    objA: draws,
                    objB: m_meshDrawSources[index]
                ) ||
                (revision != m_meshDrawRevisions[index])
            ) {
                m_meshDrawSources[index] = draws;
                m_meshDrawRevisions[index] = revision;
                changed = true;
            }
            if (draws.Count > 0) {
                drawing++;
                only = draws;
            }
        }

        if (!changed) {
            return;
        }

        m_meshDrawsRevision++;

        if (drawing <= 1) {
            m_meshDraws = only;

            return;
        }

        m_composedMeshDraws.Clear();

        foreach (var draws in m_meshDrawSources) {
            for (var index = 0; (index < draws!.Count); index++) {
                m_composedMeshDraws.Add(item: draws[index]);
            }
        }

        m_meshDraws = m_composedMeshDraws;
    }
    // The first frame and a moved park position owe the whole table, and a frame that adopts a rebuilt program repacks
    // it, since every slot's owner may have changed, owing only the rows the repack moved (SdfMovedTransforms.BeginRepack);
    // either way the table is parked first so a slot no owner claims reads hidden. Any other frame repacks only the
    // owners each emitter finds moved or settling.
    private void PackTransforms(bool rebuilt) {
        var everything = (!m_transformsPacked || (m_packedParkPosition != ParkPosition));
        var repack = (rebuilt && !everything);

        if (repack) {
            if (m_repackedTransforms?.Length != m_transforms.Length) { m_repackedTransforms = new DynamicTransform[m_transforms.Length]; }
            m_transforms.CopyTo(
                array: m_repackedTransforms,
                index: 0
            );
            m_moved.BeginRepack(tableRows: m_transforms.Length);
        } else {
            m_moved.Begin(
                everything: everything,
                tableRows: m_transforms.Length
            );
        }

        if (everything || repack) {
            Array.Fill(
                array: m_transforms,
                value: new DynamicTransform(
                    Position: ParkPosition,
                    Orientation: Quaternion.Identity
                )
            );
            m_packedParkPosition = ParkPosition;
        }

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];

            if (emitter.DynamicSlotCount == 0) {
                continue;
            }

            var context = new SdfEmitContext(
                Probe: false,
                Time: m_time,
                RenderOrigin: Vector3.Zero,
                ParkPosition: ParkPosition,
                SlotBase: m_slotBases[index],
                InterpolationAlpha: m_interpolationAlpha
            );

            emitter.PackDynamicTransforms(
                context: in context,
                moved: m_moved,
                slots: m_transforms
            );
        }

        m_meshInstances.Clear();
        m_meshAssetSlots.Clear();
        var root = m_programSlots;

        foreach (var emitter in m_emitters) {
            m_meshAssetSlots.AddRange(emitter.MeshAssetSlots);
            foreach (var copy in emitter.MeshInstances) {
                copy.FrameSlot = root;
                var previous = m_transforms[root];

                m_transforms[root] = copy.Transform;
                if (previous != copy.Transform) { m_moved.Owe(start: root, count: 1); }
                m_meshInstances.Add(copy);
                root++;
            }
        }
        if (repack) {
            m_moved.CompleteRepack(
                current: m_transforms,
                previous: m_repackedTransforms
            );
        }

        m_transformsPacked = true;
    }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += MathF.Max(
            x: deltaSeconds,
            y: 0f
        );
        m_interpolationAlpha = interpolationAlpha;

        // Captured BEFORE the build, deliberately, and into PENDING rather than straight into the built record. Both
        // halves of that are load-bearing, against two different failures:
        //
        //   BEFORE the build — because these are the revisions the program is built FROM. Were an emitter's Emit ever
        //   to bump its own revision inputs mid-build, capturing afterwards would record the post-bump values and
        //   silently absorb a change the built program does not reflect. Capturing first leaves it visible next frame.
        //
        //   Into PENDING — because a build can THROW. Writing the built record up front would leave it claiming the
        //   held program was built from revisions no program was ever built from, and since the compare then sees no
        //   movement, the stale program is held indefinitely: one failed build turns into permanently frozen geometry.
        //   Promotion happens only once a build has returned (here, or in AdoptBuild for a background build), so a
        //   throw leaves the record describing the program actually held and the next frame retries the rebuild.
        //
        // A frame holding the previous program while its successor builds packs only the moved transforms: a slot's owner
        // may differ between the two programs for the frames until the adoption, which repacks the whole table.
        foreach (var emitter in m_emitters) { emitter.PrepareFrame(); }
        var slots = 0;
        var layoutChanged = false;

        for (var index = 0; (index < m_emitters.Count); index++) {
            layoutChanged |= (m_slotBases[index] != slots);
            m_slotBases[index] = slots;
            slots = checked((slots + m_emitters[index].DynamicSlotCount));
        }
        layoutChanged |= (slots != m_programSlots);
        m_programSlots = slots;
        var roots = m_emitters.Sum(emitter => emitter.MeshInstances.Count);

        if (m_transforms.Length != (slots + roots)) {
            Array.Resize(ref m_transforms, (slots + roots));
            m_transformsPacked = false;
        }
        var moved = (CaptureRevisions() || layoutChanged);

        if (layoutChanged && (m_building is { } pending)) { pending.GetAwaiter().GetResult(); }
        var rebuilt = AdoptBuild(moved: ref moved);

        moved |= layoutChanged;

        if ((m_building is null) && ((m_program is null) || moved)) {
            var builder = EmitProgram(
                context: new SdfEmitContext(
                    Probe: false,
                    Time: m_time,
                    RenderOrigin: Vector3.Zero,
                    ParkPosition: ParkPosition,
                    SlotBase: 0,
                    InterpolationAlpha: interpolationAlpha
                ),
                previous: m_program
            );

            Array.Copy(
                sourceArray: m_pendingRevisions,
                destinationArray: m_buildingRevisions,
                length: m_buildingRevisions.Length
            );

            if ((m_program is null) || !BuildsInBackground || WaitsForBuild || layoutChanged) {
                m_program = Build(builder: builder);
                ProgramBuilds++;
                m_programDelta = null;
                Array.Copy(
                    sourceArray: m_buildingRevisions,
                    destinationArray: m_builtRevisions,
                    length: m_builtRevisions.Length
                );
                rebuilt = true;
            } else {
                var held = m_program;
                var observer = Volatile.Read(location: ref m_built);
                var emittedUnder = m_builtTransforms;

                if (observer is not null) {
                    if (emittedUnder.Length != m_transforms.Length) {
                        emittedUnder = new DynamicTransform[m_transforms.Length];
                        m_builtTransforms = emittedUnder;
                    }

                    m_transforms.AsSpan().CopyTo(destination: emittedUnder);
                }

                // The build also finds the words its program differs in from the one held, so the tables that hold
                // that program upload the new one without comparing it on the frame thread, and hands the program to its
                // observer before the frame that adopts it.
                m_building = Task.Run(function: () => {
                    var built = Build(builder: builder);
                    var delta = ((SdfProgramDelta?)SdfProgramDelta.Between(from: held, to: built));

                    observer?.Invoke(arg1: built, arg2: emittedUnder);

                    return (built, delta);
                });
            }
        }

        PackTransforms(rebuilt: rebuilt);
        ComposeMeshDraws();

        var dressed = m_dresser.Dress(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            meshDraws: m_meshDraws,
            meshDrawsRevision: m_meshDrawsRevision,
            moved: m_moved,
            program: m_program!,
            transforms: m_transforms,
            width: width
        );

        dressed.PickMap = ComposePickMap();
        dressed.SlotClips = ComposeSlotClips();
        dressed = dressed with { MeshAssetSlots = m_meshAssetSlots, MeshInstances = m_meshInstances };
        dressed.ProgramDelta = (ReferenceEquals(objA: m_programDelta?.To, objB: dressed.Program) ? m_programDelta : null);
        if (Frames == 0) { Console.Error.WriteLine($"[world.render] envelope: {m_program!.Instances.Count} instances, {m_program.MaterialCount} materials, {m_program.Words.Length} program words, {m_transforms.Length} dynamic slots, {m_meshAssetSlots.Count} geometry assets, {m_meshInstances.Count} rigid copies"); }
        Frames++;
        return dressed;
    }
}
