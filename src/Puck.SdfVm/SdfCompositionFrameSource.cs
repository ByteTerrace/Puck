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
    /// <returns>The frame to render. The composition completes it in place (its pick map, slot clips, geometry assets
    /// and sampled snapshots), so a dresser returns a frame it owns and hands to no one else to complete: a frame its
    /// <see cref="SdfFrame.Writer"/> writes again at the next dress.</returns>
    SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha);

    /// <summary>Gets the font atlas the dressed world's text samples, which the composed frame source hands its host as
    /// its <see cref="ISdfFrameSource.GlyphAtlas"/>; <see langword="null"/>, the default, for a world with no text.</summary>
    SdfGlyphAtlas? GlyphAtlas => null;
    /// <summary>Gets the glyph decals the dressed world's text screens draw, which the composed frame source hands its
    /// host as its <see cref="ISdfFrameSource.ScreenDecals"/>; <see langword="null"/>, the default, for none.</summary>
    IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => null;
    /// <summary>Gets what supplies the glyphs of the dressed world's surface text slots, which the composed frame source
    /// hands its host as its <see cref="ISdfFrameSource.SurfaceTexts"/>; <see langword="null"/>, the default, for
    /// none.</summary>
    Func<int, SdfSurfaceTextFrame?>? SurfaceTexts => null;
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

    // The copies and template slots every frame publishes, rewritten in place, and the journal of the copies each pack
    // changed (another copy at an index, the copy at an index on another root slot or at another revision), which the dresser and the bake
    // read through their own cursors instead of comparing the whole list.
    private readonly List<SdfMeshInstance> m_meshInstances = [];
    // Each published copy's revision as this pack read it, beside m_meshInstances.
    private readonly List<long> m_meshInstanceRevisions = [];
    private readonly List<int> m_meshAssetSlots = [];
    private readonly Puck.Abstractions.Gpu.GpuChangedRows m_copyChanges = new();
    private readonly SdfMovedTransforms m_moved = new();

    // The table as it stood before a repack, which the repack is compared with; made at the first repack.
    private DynamicTransform[]? m_repackedTransforms;
    private bool m_transformsPacked;

    // The background build in flight, if any, and the components it was emitted from.
    private readonly int[] m_buildingRevisions;

    private Task<(SdfProgram Program, SdfProgramDelta? Delta)>? m_building;
    // The slot layout: it moves whenever a change of an emitter's dynamic slot count moves a slot base or the total, and
    // the layouts the held program and the build in flight were emitted under. A frame packs and dresses only under a
    // program emitted with the current layout (CaptureFrame), since the program addresses the slots by those bases.
    private int m_layout;
    private int m_programLayout;
    private int m_buildingLayout;
    private Puck.Abstractions.Presentation.FrameCaptureRequest? m_convergence;
    private float m_interpolationAlpha;
    private Vector3 m_packedParkPosition;
    private SdfProgram? m_program;
    // The snapshots of the held program's sampled fields the latest frame carried.
    private SdfSampledSet? m_sampled;
    // The words the held program differs in from the one held before it, when its background build measured them.
    private SdfProgramDelta? m_programDelta;
    private float m_time;
    // What a background build hands the program it finished (ObserveBuilds), and the transforms the build's program was
    // emitted under, copied at the emission: one build runs at a time, so the next emission waits for this one's to finish.
    private Action<SdfProgram, IReadOnlyList<DynamicTransform>>? m_built;

    private DynamicTransform[] m_builtTransforms = [];

    /// <summary>Gets whether every program builds on the thread pool, so no frame waits for a build: the frame that finds
    /// the content moved emits the program, and every frame until the build finishes renders the program held before it,
    /// with its transforms, mesh draws and dress live; the first frame after the build finishes adopts it. A frame with no
    /// program it can pack under (the first, or one after a change of the slot layout) films nothing
    /// (<see cref="CaptureFrame"/> returns <see langword="null"/>), and a converging capture (<see cref="BeginConvergence"/>)
    /// or tick-locked dresser carries the held program while a build stands; both name the build in
    /// <see cref="Waiting"/>. <see langword="false"/>, the default, builds every program in the frame that finds the
    /// content moved.</summary>
    public bool BuildsInBackground { get; init; }
    /// <summary>Gets why the latest <see cref="CaptureFrame"/> filmed nothing or a frame that does not yet show what its
    /// content calls for: one of <see cref="FirstProgramWaiting"/>, <see cref="LayoutWaiting"/> or
    /// <see cref="LockedWaiting"/>; <see langword="null"/> when the frame shows the program its content calls for, or
    /// ordinary presentation holds the program before a build. Read on the frame's thread.</summary>
    public string? Waiting { get; private set; }

    /// <summary>The <see cref="Waiting"/> reason of a frame that films nothing while the first program builds.</summary>
    public const string FirstProgramWaiting = "the composition's first program is building";
    /// <summary>The <see cref="Waiting"/> reason of a frame that films nothing while the program for a changed slot
    /// layout builds: the held program addresses the slots by the bases before the change.</summary>
    public const string LayoutWaiting = "the composition's program for its changed slot layout is building";
    /// <summary>The <see cref="Waiting"/> reason of a tick-locked or converging frame that carries the held program
    /// while the program its content calls for builds.</summary>
    public const string LockedWaiting = "the composition's program is building, and a tick-locked or converging frame shows only the program its content calls for";
    /// <summary>Gets whether a background build is in flight (<see cref="BuildsInBackground"/>).</summary>
    public bool Building => (m_building is not null);
    /// <summary>Gets how many frames <see cref="CaptureFrame"/> has captured. A frame captured after a reading of it has
    /// read every emitter's content as it stood then, so once <see cref="Building"/> is false after such a frame,
    /// <see cref="Program"/> is the program that content calls for.</summary>
    public long Frames { get; private set; }
    /// <summary>Gets the program the frames carry, or <see langword="null"/> before the first frame.</summary>
    public SdfProgram? Program => m_program;
    /// <summary>Gets the number of completed program builds that derived a field, separate from per-frame instance poses.
    /// A rebuild whose emission left the field as it stood derives none (<see cref="SdfProgram.SharesField"/>) and counts
    /// under <see cref="AppearanceBuilds"/> instead, or under neither when it emitted the held program exactly.</summary>
    public long ProgramBuilds { get; private set; }
    /// <summary>Gets the number of completed rebuilds that took the held program's field with another appearance: a look
    /// value (a colour, a material value, a shade parameter) packed without a field build.</summary>
    public long AppearanceBuilds { get; private set; }

    // Counts an adopted build against the program it replaced.
    private void CountBuild(SdfProgram built, SdfProgram? held) {
        if (ReferenceEquals(objA: built, objB: held)) { return; }
        if (built.SharesField(other: held)) { AppearanceBuilds++; } else { ProgramBuilds++; }
    }

    private bool Converging => (m_convergence is { Completion.IsCompleted: false });
    private bool WaitsForBuild => (Converging || m_dresser.TickLocked);

    /// <inheritdoc/>
    /// <remarks>Until the request completes, a frame carries the held program only with <see cref="LockedWaiting"/>
    /// while a build stands.</remarks>
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
    /// <remarks>The dresser's (<see cref="ISdfFrameDresser.SurfaceTexts"/>).</remarks>
    public Func<int, SdfSurfaceTextFrame?>? SurfaceTexts => m_dresser.SurfaceTexts;
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
        // The last component is the host's own: the code revision every emitter's emission is a product of.
        m_builtRevisions = new int[(revisionCursor + 1)];
        m_pendingRevisions = new int[(revisionCursor + 1)];
        m_buildingRevisions = new int[(revisionCursor + 1)];
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
    public int ProgramWordCapacity => (m_program?.WordCount ?? 0);

    // The composition owns its emission storage. Capture emits only after the previous asynchronous build has joined;
    // Build snapshots its inputs, so neither a built program nor an observer borrows this builder after that join.
    private readonly SdfProgramBuilder m_builder = new();

    private SdfProgramBuilder EmitProgram(SdfEmitContext context) {
        var builder = m_builder;

        builder.Clear();

        Emit(
            builder: builder,
            context: context
        );

        return builder;
    }
    // A build packs, validates and analyses the emitted builder, which owns everything it reads, so it runs on any thread.
    // Handed the program it replaces, it derives no field the builder holds unchanged: an emission whose field stands
    // takes that program's derivation with its own appearance, or is that program (SdfProgram.Of).
    private SdfProgram Build(SdfProgramBuilder builder, SdfProgram? replaces) {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var program = builder.Build(replaces: replaces);

        Volatile.Write(location: ref m_buildMilliseconds, value: System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalMilliseconds);
        return program;
    }

    private double m_emitMilliseconds;
    private double m_buildMilliseconds;

    /// <summary>Gets how long, in milliseconds, the latest program's emission took on the frame's thread: every emitter
    /// writing its instructions into the builder.</summary>
    public double EmitMilliseconds => Volatile.Read(location: ref m_emitMilliseconds);
    /// <summary>Gets how long, in milliseconds, the latest program build took on whatever thread ran it: packing,
    /// validating and analysing the emitted builder.</summary>
    public double BuildMilliseconds => Volatile.Read(location: ref m_buildMilliseconds);

    // Adopts the program a background build finished; a build still running is never waited for. The adopted program was
    // built from m_buildingRevisions under m_buildingLayout, which become the built record; whether this frame's revisions
    // moved is judged against them afresh. A build that threw throws here and leaves the built record describing the
    // program still held, so the next frame emits again.
    private bool AdoptBuild(ref bool moved) {
        if ((m_building is { } building) && building.IsCompleted) {
            m_building = null;
            var held = m_program;

            (m_program, m_programDelta) = building.GetAwaiter().GetResult();
            CountBuild(built: m_program, held: held);
            m_programLayout = m_buildingLayout;
            Array.Copy(
                sourceArray: m_buildingRevisions,
                destinationArray: m_builtRevisions,
                length: m_builtRevisions.Length
            );
            moved = !m_pendingRevisions.AsSpan().SequenceEqual(other: m_builtRevisions);

            return true;
        }

        return false;
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

        // A hot-reload edit to any emission code re-emits every emitter, as a moved component of each would.
        var code = m_revisionOffsets[m_emitters.Count];

        m_pendingRevisions[code] = Puck.Abstractions.CodeRevision.Current;
        moved |= (m_pendingRevisions[code] != m_builtRevisions[code]);

        return moved;
    }
    // Reads every emitter's straddling slot runs after packing, then each straddling copy's run: one emitter's list with no
    // straddling copy passes through as it is, anything more is written in order into the composition's one list, and none
    // is the one empty list. A consumer copies the clips it keeps (SdfMeshRows.Writer.SetClips), so the list is rewritten
    // in place each frame and composing allocates nothing.
    private IReadOnlyList<SdfSlotClip> ComposeSlotClips() {
        IReadOnlyList<SdfSlotClip>? only = null;
        var lists = 0;
        var copies = 0;

        for (var index = 0; (index < m_emitters.Count); index++) {
            if (m_emitters[index].SlotClips is { Count: > 0 } clips) { only = clips; lists++; }
        }
        copies = m_clippedCopies.Count;
        if ((lists + copies) == 0) { return []; }
        if ((lists == 1) && (copies == 0)) { return only!; }
        m_composedClips.Clear();
        for (var index = 0; (index < m_emitters.Count); index++) {
            var clips = m_emitters[index].SlotClips;

            for (var at = 0; (at < clips.Count); at++) { m_composedClips.Add(item: clips[at]); }
        }
        foreach (var position in m_clippedCopies) {
            var copy = m_meshInstances[position];

            if (copy.Clip is { } clip) { m_composedClips.Add(item: (clip with { FirstSlot = copy.FrameSlot, SlotCount = 1 })); }
        }
        return m_composedClips;
    }

    private readonly List<SdfSlotClip> m_composedClips = [];
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

        // The template slots first: a frame whose slots moved owes every copy, since a copy's group membership reads them.
        var slots = 0;
        var slotsMoved = false;
        var copyCount = 0;

        for (var owner = 0; (owner < m_emitters.Count); owner++) {
            var assets = m_emitters[owner].MeshAssetSlots;

            copyCount += m_emitters[owner].MeshInstances.Count;
            for (var at = 0; (at < assets.Count); at++, slots++) {
                if (slots == m_meshAssetSlots.Count) { m_meshAssetSlots.Add(item: assets[at]); slotsMoved = true; }
                else if (m_meshAssetSlots[slots] != assets[at]) { m_meshAssetSlots[slots] = assets[at]; slotsMoved = true; }
            }
        }
        if (slots < m_meshAssetSlots.Count) { m_meshAssetSlots.RemoveRange(index: slots, count: (m_meshAssetSlots.Count - slots)); slotsMoved = true; }
        var resized = (copyCount != m_meshInstances.Count);

        if (copyCount < m_meshInstances.Count) {
            m_meshInstances.RemoveRange(index: copyCount, count: (m_meshInstances.Count - copyCount));
            m_meshInstanceRevisions.RemoveRange(index: copyCount, count: (m_meshInstanceRevisions.Count - copyCount));
            while ((m_clippedCopies.Count > 0) && (m_clippedCopies.Max >= copyCount)) { _ = m_clippedCopies.Remove(item: m_clippedCopies.Max); }
        }
        m_copyChanges.Begin(everything: slotsMoved, rows: copyCount);

        // The copies stand where the last pack left them unless the table was parked, the roots moved, or an emitter's
        // roster changed or names no changed copies; then only the copies each emitter's pack rewrote are read.
        var standing = (!everything && !repack && !slotsMoved && !resized && (m_copyRoot == m_programSlots));

        if (m_copyRevisions.Length != m_emitters.Count) {
            m_copyRevisions = new long[m_emitters.Count];
            standing = false;
        }
        for (var owner = 0; (owner < m_emitters.Count); owner++) {
            var emitter = m_emitters[owner];
            var revision = emitter.MeshInstancesRevision;

            standing &= ((m_copyRevisions[owner] == revision) && ((emitter.MeshInstances.Count == 0) || (emitter.MeshInstanceChanges is not null)));
            m_copyRevisions[owner] = revision;
        }

        m_copyRoot = m_programSlots;

        var position = 0;

        for (var owner = 0; (owner < m_emitters.Count); owner++) {
            var copies = m_emitters[owner].MeshInstances;

            if (standing) {
                if (m_emitters[owner].MeshInstanceChanges is { } changes) {
                    for (var at = 0; (at < changes.Count); at++) {
                        ReadCopy(copy: copies[changes[at]], position: (position + changes[at]));
                    }
                }
            } else {
                for (var at = 0; (at < copies.Count); at++) {
                    ReadCopy(copy: copies[at], position: (position + at));
                }
            }

            position += copies.Count;
        }
        if (repack) {
            m_moved.CompleteRepack(
                current: m_transforms,
                previous: m_repackedTransforms
            );
        }

        m_transformsPacked = true;
    }
    // Reads one rigid copy into the composition at its position: seats its root row, owes the row and the copy where
    // either differs from what the composition held, and keeps the set of copies drawn clipped at a door.
    private void ReadCopy(SdfMeshInstance copy, int position) {
        var root = (m_programSlots + position);
        // A copy the producer rewrote in place (a moving body's pose) is the same row at another revision.
        var held = ((position < m_meshInstances.Count) && ReferenceEquals(objA: m_meshInstances[position], objB: copy) &&
            (m_meshInstanceRevisions[position] == copy.Revision) && (copy.FrameSlot == root));

        copy.FrameSlot = root;
        var previous = m_transforms[root];

        m_transforms[root] = copy.Transform;
        if (previous != copy.Transform) { m_moved.Owe(count: 1, start: root); }
        if (position == m_meshInstances.Count) {
            m_meshInstances.Add(item: copy);
            m_meshInstanceRevisions.Add(item: copy.Revision);
        } else {
            m_meshInstances[position] = copy;
            m_meshInstanceRevisions[position] = copy.Revision;
        }
        if (!held) { m_copyChanges.Owe(count: 1, start: position); }
        if (copy.Clip is null) { _ = m_clippedCopies.Remove(item: position); } else { _ = m_clippedCopies.Add(item: position); }
    }

    // The positions in m_meshInstances of the copies that straddle a door, in order: the only copies ComposeSlotClips reads.
    private readonly SortedSet<int> m_clippedCopies = [];
    // The root slot the copies were last seated from, and each emitter's MeshInstancesRevision at the last pack.
    private int m_copyRoot = -1;
    private long[] m_copyRevisions = [];

    // Apart from CaptureFrame, whose closure scope would otherwise cover every frame: a steady frame starts no build.
    private void StartBuild(SdfProgramBuilder builder) {
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
        // A program sharing the held one's field has the plans the held one's observers made already, so only a new field
        // is observed.
        m_building = Task.Run(function: () => {
            var built = Build(builder: builder, replaces: held);
            var delta = (((held is null) || ReferenceEquals(objA: held, objB: built)) ? null : ((SdfProgramDelta?)SdfProgramDelta.Between(from: held, to: built)));

            if (!built.SharesField(other: held)) { observer?.Invoke(arg1: built, arg2: emittedUnder); }

            return (built, delta);
        });
    }

    /// <inheritdoc/>
    /// <remarks>Never waits for a build (<see cref="BuildsInBackground"/>): a frame with no program emitted under the
    /// current slot layout films nothing and packs nothing, so the frame a residency holds keeps its transforms and the
    /// moved set loses no row; <see cref="Waiting"/> names why.</remarks>
    public SdfFrame? CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
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
        for (var index = 0; (index < m_emitters.Count); index++) { m_emitters[index].PrepareFrame(); }
        var slots = 0;
        var layoutChanged = false;

        for (var index = 0; (index < m_emitters.Count); index++) {
            layoutChanged |= (m_slotBases[index] != slots);
            m_slotBases[index] = slots;
            slots = checked((slots + m_emitters[index].DynamicSlotCount));
        }
        layoutChanged |= (slots != m_programSlots);
        m_programSlots = slots;
        if (layoutChanged) { m_layout++; }
        var roots = 0;

        for (var index = 0; (index < m_emitters.Count); index++) { roots += m_emitters[index].MeshInstances.Count; }

        // A resize allocates a new table, so a frame captured before it keeps the one it carries.
        if (m_transforms.Length != (slots + roots)) {
            Array.Resize(array: ref m_transforms, newSize: (slots + roots));
            m_transformsPacked = false;
        }
        var moved = CaptureRevisions();
        var rebuilt = AdoptBuild(moved: ref moved);
        var fits = ((m_program is not null) && (m_programLayout == m_layout));

        if ((m_building is null) && (!fits || moved)) {
            var emitted = System.Diagnostics.Stopwatch.GetTimestamp();
            var builder = EmitProgram(
                context: new SdfEmitContext(
                    Probe: false,
                    Time: m_time,
                    RenderOrigin: Vector3.Zero,
                    ParkPosition: ParkPosition,
                    SlotBase: 0,
                    InterpolationAlpha: interpolationAlpha
                )
            );

            Volatile.Write(location: ref m_emitMilliseconds, value: System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: emitted).TotalMilliseconds);
            Array.Copy(
                sourceArray: m_pendingRevisions,
                destinationArray: m_buildingRevisions,
                length: m_buildingRevisions.Length
            );
            m_buildingLayout = m_layout;

            if (!BuildsInBackground) {
                var held = m_program;

                m_program = Build(builder: builder, replaces: held);
                CountBuild(built: m_program, held: held);
                m_programDelta = null;
                m_programLayout = m_buildingLayout;
                Array.Copy(
                    sourceArray: m_buildingRevisions,
                    destinationArray: m_builtRevisions,
                    length: m_builtRevisions.Length
                );
                rebuilt = true;
                fits = true;
            } else {
                StartBuild(builder: builder);
            }
        }

        // The one door every frame passes before packing: no program emitted under the current layout, no frame.
        if (!fits) {
            Waiting = ((m_program is null) ? FirstProgramWaiting : LayoutWaiting);

            return null;
        }

        Waiting = ((WaitsForBuild && (m_building is not null)) ? LockedWaiting : null);
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

        // The held program's fields as they stand after every emitter's PrepareFrame published this frame's values: a field
        // that moved makes a new set, and the program stands.
        m_sampled = SdfSampledSet.Capture(program: m_program!, previous: (m_sampled ?? m_program!.Sampled));
        dressed.PickMap = ComposePickMap();
        dressed.Compose(meshAssetSlots: m_meshAssetSlots, meshInstances: m_meshInstances, sampled: m_sampled, slotClips: ComposeSlotClips(),
            copyChanges: m_copyChanges.Reader, copyChangesSerial: m_copyChanges.Serial);
        dressed.ProgramDelta = (ReferenceEquals(objA: m_programDelta?.To, objB: dressed.Program) ? m_programDelta : null);
        if (Frames == 0) { Console.Error.WriteLine(value: $"[world.render] envelope: {m_program!.Instances.Count} instances, {m_program.MaterialCount} materials, {m_program.WordCount} program words, {m_transforms.Length} dynamic slots, {m_meshAssetSlots.Count} geometry assets, {m_meshInstances.Count} rigid copies"); }
        Frames++;
        return dressed;
    }
}
