using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// A world's one shadow solution, which every view of its residency reads and none draws: the cascade set's entries
/// (<see cref="SdfMeshShadowCascades"/>) kept as layers the residency's upload draws, ahead of every view's submission, so a
/// world shown on many screens pays for each place's shadows once, however many views look at it.
/// <para>The entries are anchored to the world, never to the way a camera faces: the near, far and secondary entries are
/// held by anchors, each eye-centred on one view and snapped to cells of an eighth of each entry's reach, and every view of
/// the world reads the anchor whose near cell holds its eye, a view in a cell no anchor holds taking one of its own, up to
/// <see cref="MaxAnchors"/> and while the memory account has room for its layers, past which it reads the anchor nearest
/// it. The distant entry is one for the world, centred on its first shadowed view: its reach spans a place of a kilometre
/// and more from anywhere in it.</para>
/// <para>Every entry is cached: its layers are drawn whole only when the light turns past the entry's tolerance, its eye
/// crosses into another cell or its texels change, and the distant map also when a static draw changes
/// (<see cref="SdfWorldTables.StaticDrawRevision"/>); the other entries redraw a changed static caster by rectangles.
/// A whole redraw is drawn in shares, each a part of the draw list, and the world schedules every entry's shares together:
/// each upload draws the shares that fit <see cref="UploadVertices"/>, in order of the redraws already under way, the
/// entries no view has seen drawn yet, the anchor, and the entry (far, near, secondary, distant), so a world first shown
/// draws its shadows over several frames, the place the player stands in first, and never in one hitch. An entry's shares
/// follow the vertices its last redraw measured (the selection's vertex words), never fewer than the authored rate asks.
/// A redraw of an entry no view reads draws into its own layers; one of a standing entry draws into a pair of layers lent
/// from the world's spares, which takes the entry's place when its last share lands, the entry's old pair returning to the
/// spares.</para>
/// <para>Between redraws the near, far and secondary entries follow what moves by rectangles: every draw record a staging
/// changes reports the box it held and the box it holds (<see cref="Moved"/>). A change whose box has moved less than the
/// finest standing entry's texel since it was last judged costs one comparison; the rest are judged by each entry against
/// where it last drew the caster and, once it has moved a texel of that entry's, owe the rectangles where it was and where
/// it is, merged into at most <see cref="MaxRectangles"/>; the upload clears and draws every caster inside each rectangle
/// again through a viewport of its own. A still world draws nothing; a walking player costs the rectangles its body's
/// shadow sweeps; a grass blade swaying less than a texel costs nothing. The distant entry holds the static scene
/// alone.</para>
/// <para>Invariant: a layer is read only through the camera it was drawn from. One door enforces it: <see cref="Decide"/>
/// chooses every entry's work for the upload about to be submitted, the cameras readers read after it and the layers
/// they bind (<see cref="ViewOf"/>), <see cref="Record"/> draws exactly that work into the upload's command buffer, which
/// the residency submits ahead of every view, and <see cref="Submitted"/> commits it; a camera no submitted upload drew is
/// never read, and a layer a redraw is still drawing into is never bound.</para>
/// <para>Every image rests in the shader-readable layout between uploads; the upload moves each one it writes to the depth
/// attachment layout and back, so a view's submission samples them without a transition of its own. Every layer and
/// extinction buffer is held under one fixed reservation in the process's memory account, which a new anchor and a spare
/// pair must fit before they are made; an anchor no view reads gives its layers back when the account asks for room.</para>
/// </summary>
internal sealed class SdfWorldShadows : IDisposable {
    /// <summary>The most anchors one world holds: the places its views look at that each hold near, far and secondary
    /// entries of their own.</summary>
    public const int MaxAnchors = 8;
    /// <summary>The most rectangles one entry redraws in one upload; more moved boxes merge into these.</summary>
    public const int MaxRectangles = 4;
    /// <summary>The distance, in an entry's texels, a caster moves across the entry's plane or along its light before the
    /// entry draws it again: below a texel its shadow cannot be told from the one the entry holds.</summary>
    public const float MoveTexels = 1f;
    /// <summary>The vertices one upload's whole redraws rasterize together, by the shares' measured vertices: a share
    /// past it waits for a later upload, though every upload draws at least one share.</summary>
    public const long UploadVertices = 4_000_000L;
    /// <summary>The vertices one entry's rectangles are expected to rasterize in one upload, by the vertices its
    /// rectangles have measured a texel: rectangles past it wait for a later upload, though an entry owing any draws at
    /// least one.</summary>
    public const long RectangleVertices = (UploadVertices / 2L);
    /// <summary>The share of the softest penumbra the surface pass draws (<see cref="SdfKernelInterfaces.MeshShadowFilterTexels"/>
    /// texels of radius, narrower where the light is small or the caster near) that a held layer's shadows may lag the
    /// light's turn by, with one texel besides, before an entry with no authored tolerance is drawn again.</summary>
    public const float PenumbraShare = 0.25f;
    // The most pairs of layers the spares hold and lend at once: the standing entries one world redraws together.
    private const int MaxSparePairs = 2;
    // The uploads an anchor no view reads is kept for, so a view that leaves and comes back finds its layers.
    private const ulong AnchorKeptUploads = 240;
    // The texels a moved box's footprint is widened by on each side: rasterization covers a pixel whose centre a triangle
    // holds, and the bake's coarser levels stand a little proud of the box.
    private const int RectanglePad = 2;
    // The drawings an entry may record: its whole layer (a redraw or a share of one), its glass extinction, and one per
    // rectangle.
    private const int WholeKind = 0;
    private const int OpticalKind = 1;
    private const int FirstRectangleKind = 2;
    private const int Kinds = (FirstRectangleKind + MaxRectangles);
    // The buffers a mesh set binds and those the selection kernel's sets add (SdfWorldPassRecorder lays them alike).
    private const int MeshSetBuffers = 7;
    private const int MeshSetCounters = 2;
    private const int MeshSetVisibility = 3;
    private const int MeshSetGlassSums = 4;
    private const int MeshSetOcclusionBuffers = 6;
    private const int OcclusionSets = 2;
    // The phase the selection emits glass extinction draws into (SdfKernelInterfaces.MeshShadowPhases).
    private const int GlassPhase = 4;
    // The bits of a whole drawing's measurement tag that hold the shares its redraw takes (SdfMeshOcclusion.MaxSlices).
    private const int SliceBits = 9;

    private static readonly string[] EntryNames = ["near", "far", "second", "distant"];

    private readonly SdfWorldTables m_tables;
    private readonly GpuDeviceServices m_gpu;
    private readonly List<Anchor> m_anchors = [];
    private readonly Entry m_distant;
    private readonly IGpuBuffer m_counters;
    private readonly IGpuRenderPass m_loadPass;
    private readonly byte[] m_block = new byte[SdfFrameBlock.SizeBytes];
    private readonly MemoryAccount.DeviceReservation m_reservation;
    private readonly List<Pair> m_spares = [];
    private readonly List<Entry> m_candidates = [];
    // Each entry index's latest measured whole redraw, in vertices: the first redraw of another anchor's entry of that
    // index takes its shares from it. Zero while none is measured.
    private readonly long[] m_indexVertices = new long[SdfMeshShadowCascades.MaxEntries];
    private bool m_disposed;

    // The decision for the upload about to be submitted (Decide): the frame it was made for, whether it stands, and
    // whether the upload that records it was submitted this frame, after which no reader of the frame may see another.
    private SdfFrame? m_decidedFrame;
    private bool m_decided;
    private bool m_frozen;
    private ulong m_uploads;
    // Each view's anchor, -1 for none, and the cascade set it reads; the cascade set of a view with no anchor is empty.
    private int[] m_viewAnchors = [];
    // Assign's scratch: each view's anchor before the upload's assignment.
    private int[] m_previousAnchors = [];
    private SdfMeshShadowCascades[] m_viewReaders = [];
    private long m_contentRevision;
    private long m_bindingRevision;
    // The pairs the spares hold or have lent, every size together.
    private int m_sparePairs;
    // The bytes every layer and extinction buffer holds, which the reservation holds.
    private long m_bytes;
    // Set by the memory account's trim request, on any thread; the next decision gives back what no view reads.
    private int m_trimRequested;
    private long m_trims;
    // Where each staged row's box stood when it was last handed to the entries (Moved), valid where marked; and the finest
    // texel of the standing entries that follow motion, in metres, zero while none stands.
    private SdfMeshBox[] m_judged = [];
    private bool[] m_judgedValid = [];
    private float m_finestTexel;
    // What the stagings reported, for the report: the changes reported, those no box bounds, and those handed to the
    // entries.
    private long m_movedChanges;
    private long m_movedUnbounded;
    private long m_movedJudged;
    private long m_redrawSerial;

    /// <summary>Initializes a new instance of the <see cref="SdfWorldShadows"/> class: the distant entry, the timestamps the
    /// upload's shadow work is measured by, the render pass that loads a layer and the reservation its layers are held
    /// under; anchors are made as views need them, and layers when an entry is first drawn.</summary>
    /// <param name="tables">The tables whose upload draws the layers, whose device creates them and whose pipelines draw.</param>
    /// <param name="gpu">The tables' counted device services.</param>
    internal SdfWorldShadows(SdfWorldTables tables, GpuDeviceServices gpu) {
        m_tables = tables;
        m_gpu = gpu;

        var ring = SdfWorldTables.FrameRingSize;

        using var scope = new GpuCreationScope();

        // The work counters the shadow pipelines bind; every block disables counting (GpuKernelCounters.DisabledRow), so
        // they are never written.
        m_counters = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: Name(part: "counters"), sizeBytes: 256, usage: GpuBufferUsage.Storage));
        m_loadPass = scope.Own(created: gpu.RenderPassFactory.Create(
            description: SdfMeshRasterPass.ShadowRenderPass with { Depth = SdfWorldPackage.MeshShadowDepthAttachment with { Load = GpuAttachmentLoad.Load } },
            name: Name(part: "retained depth")));
        m_reservation = scope.Own(created: MemoryAccount.Shared.ReserveDevice(memoryClass: MemoryClass.Fixed, owner: Name(part: "layers").MemoryOwner(), pool: tables.ShadowPool));
        m_distant = scope.Own(created: new Entry(index: SdfMeshShadowCascades.DistantEntry, owner: this, ring: ring, anchor: 0));
        m_timestamps = ((gpu.TimestampFactory?.Create(count: (uint)(2 * ring), name: Name(part: "timestamps")) is { } pool) ? scope.Own(created: pool) : null);
        m_times = new IGpuReadbackBuffer[ring];
        m_timed = new bool[ring];
        for (var slot = 0; (slot < ring); slot++) {
            m_times[slot] = scope.Own(created: gpu.BufferFactory.CreateReadback(name: Name(part: "times", index: slot), sizeBytes: (2 * sizeof(ulong))));
        }
        scope.Complete();
        MemoryAccount.Shared.TrimRequested += RequestTrim;
    }

    /// <summary>Gets the revision of what the layers hold: one more for every upload that draws a layer whole, completes a
    /// redraw or redraws a rectangle, so a pass that samples them folds it into its signature and renders again exactly when
    /// what it would read changes.</summary>
    public long ContentRevision => m_contentRevision;
    /// <summary>Gets the device bytes every layer and glass extinction buffer of the world's shadows holds.</summary>
    public long Bytes => m_bytes;

    /// <summary>Returns the cascade set a view of the frame reads: the entries of its anchor and the world's distant entry,
    /// each through the camera its layers were drawn from, or none for a view that skips shadows.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The set.</returns>
    public SdfMeshShadowCascades CascadesOf(SdfFrame frame, int view) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        Decide(frame: frame);

        return (((view >= 0) && (view < m_viewReaders.Length)) ? m_viewReaders[view] : default);
    }
    /// <summary>Returns the revision of the images and buffers a view's pass binds: one more whenever a layer an entry's
    /// readers bind is replaced (an allocation, a redraw whose lent layers take the entry's place) or a buffer is, and
    /// another whenever the view reads another anchor, so the view's pass set writes them again before it binds them.</summary>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The revision.</returns>
    public long BindingRevisionOf(int view) => ((m_bindingRevision << 8) | (long)(AnchorOf(view: view) + 1));
    /// <summary>Returns the image view a view's pass samples at one of the layers' bindings, or <paramref name="filler"/>
    /// for a layer that is not allocated.</summary>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="entry">The entry, from zero below <see cref="SdfMeshShadowCascades.MaxEntries"/>.</param>
    /// <param name="exit">Whether the binding is the entry's exit layer rather than its depth.</param>
    /// <param name="filler">The view a binding with no layer samples.</param>
    /// <returns>The image view.</returns>
    public nint ViewOf(int view, int entry, bool exit, nint filler) => (EntryOf(view: view, entry: entry)?.Read(exit: exit)?.Image.ImageViewHandle ?? filler);
    /// <summary>Returns the glass extinction buffer an entry a view reads holds, or <see langword="null"/> for none.</summary>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="entry">The entry, from zero below <see cref="SdfMeshShadowCascades.MaxEntries"/>.</param>
    /// <returns>The buffer, or <see langword="null"/>.</returns>
    public IGpuBuffer? OpticalOf(int view, int entry) => EntryOf(view: view, entry: entry)?.Optical;
    private int AnchorOf(int view) => (((view >= 0) && (view < m_viewAnchors.Length)) ? m_viewAnchors[view] : -1);
    private Entry? EntryOf(int view, int entry) => ((entry == SdfMeshShadowCascades.DistantEntry)
        ? m_distant
        : ((AnchorOf(view: view) is var anchor and >= 0) ? m_anchors[anchor].Entries[entry] : null));

    /// <summary>Notes a change a staging made to one draw record: the box it held before, the box it holds after, each
    /// only while the draw drew something. A change whose box lies less than the finest standing entry's texel from where
    /// it stood when last handed to the entries costs nothing more; the rest are handed to each entry that follows motion,
    /// which owes the rectangles where the caster was when the entry last drew it and where it is now, once the caster
    /// has moved at least <see cref="MoveTexels"/> of that entry's texels across its plane or along its light since.</summary>
    /// <param name="row">The draw's row in the staged records, by which each entry remembers where it last drew the caster.</param>
    /// <param name="wasLive">Whether the draw drew something before the change.</param>
    /// <param name="before">The box it was drawn by before, or <see langword="null"/> for a draw no box bounds.</param>
    /// <param name="isLive">Whether the draw draws something after the change.</param>
    /// <param name="after">The box it is drawn by after, or <see langword="null"/> for a draw no box bounds.</param>
    /// <param name="glass">Whether the draw is glass on either side of the change, whose extinction the entries then draw
    /// again.</param>
    internal void Moved(int row, bool wasLive, in SdfMeshBox? before, bool isLive, in SdfMeshBox? after, bool glass) {
        if ((wasLive && (before is null)) || (isLive && (after is null))) {
            MovedEverywhere();
            return;
        }
        m_movedChanges++;
        if (row < 0) { return; }
        if (row >= m_judged.Length) {
            var size = Math.Max((row + 1), (m_judged.Length * 2));

            Array.Resize(array: ref m_judged, newSize: size);
            Array.Resize(array: ref m_judgedValid, newSize: size);
        }

        var was = (wasLive ? before : null);
        var now = (isLive ? after : null);

        // The box the entries last judged the row by stands for where every standing entry drew it to within the finest
        // texel, so a change that stays that close to it moves no entry's shadow by a texel.
        if ((m_finestTexel > 0f) && m_judgedValid[row] && (was is not null) && (now is { } held) && (Displacement(first: in m_judged[row], second: in held) < (m_finestTexel * MoveTexels))) {
            return;
        }
        var judged = (m_judgedValid[row] ? m_judged[row] : was);

        m_judgedValid[row] = (now is not null);
        m_judged[row] = (now ?? default);
        if (m_finestTexel <= 0f) { return; }
        m_movedJudged++;
        foreach (var anchor in m_anchors) {
            foreach (var entry in anchor.Entries) {
                entry.Note(row: row, before: ((was is null) ? null : judged), after: now, glass: glass);
            }
        }
    }
    // A bound, in metres, on how far any point of the second box lies from where the first box puts it, along any
    // direction: the centres' distance and each half axis's change.
    private static float Displacement(in SdfMeshBox first, in SdfMeshBox second) =>
        (((Vector3.Distance(value1: first.Center, value2: second.Center) + Vector3.Distance(value1: first.HalfX, value2: second.HalfX)) + Vector3.Distance(value1: first.HalfY, value2: second.HalfY)) + Vector3.Distance(value1: first.HalfZ, value2: second.HalfZ));
    /// <summary>Notes a change no box bounds: every entry that follows motion owes its whole layer.</summary>
    internal void MovedEverywhere() {
        m_movedUnbounded++;
        Array.Clear(array: m_judgedValid);
        foreach (var anchor in m_anchors) {
            foreach (var entry in anchor.Entries) { entry.OweAll(); }
        }
    }
    /// <summary>Decides, once for the upload about to be submitted, which anchor every view reads, what every entry draws
    /// and through which camera receivers read it after that upload. Every reader of the frame and the upload's recording
    /// share the one decision.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    internal void Decide(SdfFrame frame) {
        if (m_frozen || (m_decided && ReferenceEquals(objA: m_decidedFrame, objB: frame))) {
            return;
        }

        m_decided = true;
        m_decidedFrame = frame;
        if (Interlocked.Exchange(location1: ref m_trimRequested, value: 0) != 0) { Trim(); }

        var views = frame.Views.Count;

        if (m_viewAnchors.Length != views) {
            Array.Resize(array: ref m_viewAnchors, newSize: views);
            Array.Resize(array: ref m_viewReaders, newSize: views);
        }
        Assign(frame: frame);

        var first = Array.FindIndex(array: m_viewAnchors, match: static anchor => (anchor >= 0));
        var distantFit = ((first >= 0) ? SdfMeshShadowCascades.Fit(frame: frame, view: first) : default);

        m_distant.Want(fit: in distantFit, frame: frame, eye: ((first >= 0) ? frame.Views[first].Camera.Position : Vector3.Zero), rank: ((first >= 0) ? m_viewAnchors[first] : 0));
        foreach (var anchor in m_anchors) {
            if (anchor.View < 0) {
                foreach (var entry in anchor.Entries) { entry.Idle(); }
                continue;
            }
            anchor.Fit = SdfMeshShadowCascades.Fit(frame: frame, view: anchor.View);
            foreach (var entry in anchor.Entries) {
                entry.Want(fit: in anchor.Fit, frame: frame, eye: frame.Views[anchor.View].Camera.Position, rank: anchor.Index);
            }
        }
        Schedule();

        var changes = false;

        m_finestTexel = 0f;
        foreach (var entry in Entries()) {
            changes |= entry.Finish(frame: frame, upload: m_uploads);
            if (entry.FollowsMotion && (entry.JudgedTexel is var texel and > 0f)) {
                m_finestTexel = ((m_finestTexel > 0f) ? MathF.Min(x: m_finestTexel, y: texel) : texel);
            }
        }
        for (var view = 0; (view < views); view++) {
            var at = m_viewAnchors[view];

            if (at < 0) {
                m_viewReaders[view] = default;
                continue;
            }

            var anchor = m_anchors[at];
            var readers = anchor.Fit;

            foreach (var entry in anchor.Entries) {
                readers = (readers.Holds(index: entry.Index) ? readers.WithCamera(index: entry.Index, camera: entry.ReaderCamera) : readers);
            }
            if (readers.Holds(index: SdfMeshShadowCascades.DistantEntry)) {
                readers = readers.WithCamera(index: SdfMeshShadowCascades.DistantEntry, camera: m_distant.ReaderCamera);
            }
            m_viewReaders[view] = readers;
        }
        if (changes) { m_contentRevision++; }
    }
    // Admits this upload's shares of the redraws the entries want: the redraws already under way, then the entries no
    // reader has seen drawn, then by anchor and entry, each share costing its entry's measured vertices over its shares
    // (the whole budget while unmeasured), while the upload's budget lasts and at least one. A share of a standing entry's
    // redraw not yet under way needs a spare pair, without which it waits.
    private void Schedule() {
        m_candidates.Clear();
        foreach (var entry in Entries()) {
            if (entry.Wants) { m_candidates.Add(item: entry); }
        }
        if (m_candidates.Count == 0) { return; }
        m_candidates.Sort(comparison: static (first, second) => first.Priority.CompareTo(value: second.Priority));

        var spent = 0L;
        var admitted = false;

        foreach (var entry in m_candidates) {
            var cost = entry.ShareVertices;

            if (admitted && ((spent + cost) > UploadVertices)) { continue; }
            if (!entry.Admit()) { continue; }
            spent += cost;
            admitted = true;
        }
    }
    // Chooses each view's anchor: one whose near cell already holds the view's eye; else the anchor the view held last,
    // following it; else a free anchor or a new one the memory account has room for; else, past the most anchors, the
    // anchor whose eye lies nearest.
    private void Assign(SdfFrame frame) {
        var views = frame.Views.Count;
        var previous = ArrayCapacity.EnsureScratch(array: ref m_previousAnchors, required: views);

        Array.Copy(sourceArray: m_viewAnchors, destinationArray: previous, length: views);
        foreach (var anchor in m_anchors) { anchor.View = -1; }
        for (var view = 0; (view < views); view++) {
            m_viewAnchors[view] = -1;
            if (frame.Views[view].Quality.DisableSoftShadows) { continue; }

            var eye = frame.Views[view].Camera.Position;
            var cell = SdfMeshShadowCascades.CellOf(eye: eye, reach: SdfMeshShadowCascades.ReachOf(frame: frame, index: SdfMeshShadowCascades.NearEntry));
            var chosen = -1;

            // A loop rather than FindIndex: a predicate over this view's cell would allocate a closure each view and frame.
            for (var at = 0; ((at < m_anchors.Count) && (chosen < 0)); at++) {
                if ((m_anchors[at].View >= 0) && (m_anchors[at].Cell == cell)) { chosen = at; }
            }

            if ((chosen < 0) && (previous[view] is var held and >= 0) && (held < m_anchors.Count) && (m_anchors[held].View < 0)) {
                chosen = held;
            }
            if (chosen < 0) {
                chosen = m_anchors.FindIndex(match: static anchor => (anchor.View < 0));
            }
            if ((chosen < 0) && (m_anchors.Count < MaxAnchors)) {
                m_anchors.Add(item: new Anchor(owner: this, index: m_anchors.Count));
                chosen = (m_anchors.Count - 1);
            }
            // A taken anchor whose layers are given back holds them again only where the account has room, beside the
            // first, which the world cannot shadow without.
            if ((chosen > 0) && !m_anchors[chosen].Holds && !TryHold(extra: AnchorBytes(frame: frame, view: view))) {
                chosen = -1;
            }
            if (chosen < 0) {
                var nearest = float.MaxValue;

                for (var at = 0; (at < m_anchors.Count); at++) {
                    if (m_anchors[at].View < 0) { continue; }

                    var distance = Vector3.DistanceSquared(value1: frame.Views[m_anchors[at].View].Camera.Position, value2: eye);

                    if (distance < nearest) { (chosen, nearest) = (at, distance); }
                }
                m_viewAnchors[view] = chosen;
                if (previous[view] != chosen) { m_bindingRevision++; }
                continue;
            }
            if (m_anchors[chosen].View < 0) {
                m_anchors[chosen].View = view;
                m_anchors[chosen].Cell = cell;
            }
            m_anchors[chosen].LastUpload = m_uploads;
            m_viewAnchors[view] = chosen;
            if (previous[view] != chosen) { m_bindingRevision++; }
        }
        // An anchor no view has read for a while gives its layers back.
        foreach (var anchor in m_anchors) {
            if ((anchor.View < 0) && ((m_uploads - anchor.LastUpload) > AnchorKeptUploads)) {
                anchor.Release();
            }
        }
    }
    // The bytes one anchor's layers take at a view's texels: three pairs, with glass extinction where the program has
    // glass that shadows.
    private static long AnchorBytes(SdfFrame frame, int view) {
        var texels = SdfMeshShadowCascades.TexelsFor(frame: frame, view: view).Texels;

        return (3L * (PairBytes(texels: texels) + (frame.Program.HasGlassShadows ? OpticalBytes(texels: texels) : 0L)));
    }
    private static long PairBytes(int texels) => (2L * texels * texels * GpuPixelFormats.TexelBytes(format: SdfWorldPackage.MeshShadowDepthAttachment.Format));
    private static long OpticalBytes(int texels) => checked((long)((((ulong)texels * (ulong)texels * SdfWorldPackage.ShadowOpticalTexelWords) + SdfWorldPackage.ShadowOpticalBitmapWords(side: (uint)texels)) * sizeof(uint)));
    // Holds more bytes beside what the layers hold, refused where the pool has no room for them.
    private bool TryHold(long extra) => m_reservation.TryReserve(floor: m_bytes, peak: (m_bytes + extra));
    // Moves the bytes the layers hold, and the reservation with them: what is already allocated is never refused.
    private void Hold(long bytes) {
        m_bytes += bytes;
        _ = m_reservation.TryReserve(floor: m_bytes, peak: m_bytes);
    }
    // Gives back what no view reads: the layers of every anchor no view reads.
    // The spare pairs stay: at most MaxSparePairs, and the next redraw would make them again.
    private void Trim() {
        m_trims++;
        foreach (var anchor in m_anchors) {
            if (anchor.View < 0) { anchor.Release(); }
        }
    }
    private void RequestTrim() => Volatile.Write(location: ref m_trimRequested, value: 1);
    // Lends a spare pair of layers at an entry's texels for a redraw, making one where the spares hold none and the account
    // has room; a spare of another size gives way to it. Null where the spares are all lent.
    private Pair? Lend(int texels) {
        var at = -1;

        for (var spare = 0; ((spare < m_spares.Count) && (at < 0)); spare++) {
            if (m_spares[spare].Texels == texels) { at = spare; }
        }

        if (at >= 0) {
            var pair = m_spares[at];

            m_spares.RemoveAt(index: at);
            return pair;
        }
        if ((m_sparePairs >= MaxSparePairs) && (m_spares.Count > 0)) {
            RetirePair(pair: m_spares[^1]);
            m_spares.RemoveAt(index: (m_spares.Count - 1));
            m_sparePairs--;
        }
        if ((m_sparePairs >= MaxSparePairs) || !TryHold(extra: PairBytes(texels: texels))) {
            return null;
        }
        m_sparePairs++;

        return CreatePair(texels: texels, part: "shadow-spare");
    }
    // Takes back a pair an entry no longer holds: a lent pair whose redraw ended, or a pair a redraw replaced.
    private void Give(Pair pair) => m_spares.Add(item: pair);
    private Pair CreatePair(int texels, string part) {
        var pair = new Pair(depth: CreateLayer(texels: texels, part: part, detail: "depth"), exit: CreateLayer(texels: texels, part: part, detail: "exit"));

        Hold(bytes: pair.Bytes);

        return pair;
    }
    private Layer CreateLayer(int texels, string part, string detail) {
        var image = m_gpu.ImageFactory.CreateDepth(
            attachment: SdfWorldPackage.MeshShadowDepthAttachment,
            height: (uint)texels,
            name: Name(part: part, detail: detail),
            sampled: true,
            width: (uint)texels);

        return new Layer(
            image: image,
            clear: m_gpu.RenderPassFactory.CreateFramebuffer(colors: [], depth: image, renderPass: m_tables.ShadowRenderPass),
            load: m_gpu.RenderPassFactory.CreateFramebuffer(colors: [], depth: image, renderPass: m_loadPass));
    }
    private void RetirePair(Pair? pair) {
        if (pair is null) { return; }
        Hold(bytes: -pair.Bytes);
        Retire(replaced: pair);
    }
    /// <summary>Records the decided work into the upload's command buffer, every image it writes left shader-readable for
    /// the views' submissions that follow.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="commandBuffer">The upload's command buffer.</param>
    /// <param name="slot">The upload's ring slot.</param>
    internal void Record(SdfFrame frame, nint commandBuffer, int slot) {
        // One upload a frame: a freeze an earlier upload left without a frame start between ends here.
        if (m_frozen) {
            m_frozen = false;
            m_decided = false;
        }
        Decide(frame: frame);

        var timed = ((m_timestamps is not null) && AnyDraws());

        if (timed) {
            m_timestamps!.Reset(command: commandBuffer, count: 2, first: (uint)(2 * slot));
            m_timestamps.Write(command: commandBuffer, index: (uint)(2 * slot));
        }
        foreach (var entry in Entries()) {
            entry.Record(commandBuffer: commandBuffer, frame: frame, slot: slot);
        }
        if (timed) {
            m_timestamps!.Write(command: commandBuffer, index: (uint)((2 * slot) + 1));
            m_timestamps.Resolve(command: commandBuffer, count: 2, destination: m_times[slot].BufferHandle, first: (uint)(2 * slot), offset: 0);
            m_gpu.Recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_times[slot].BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
        }
        m_timed[slot] = timed;
    }
    // Every entry the world holds: the distant entry, then each anchor's. The walk is a struct, so the per-frame loops over
    // it allocate nothing.
    private EntryWalk Entries() => new(anchors: m_anchors, distant: m_distant);
    private struct EntryWalk(List<Anchor> anchors, Entry distant) {
        // The anchor being walked, -1 before the distant entry; the next entry of that anchor.
        private int m_anchor = -1;
        private int m_entry;

        public Entry Current { get; private set; } = null!;

        public readonly EntryWalk GetEnumerator() => this;
        public bool MoveNext() {
            if (m_anchor < 0) {
                m_anchor = 0;
                Current = distant;
                return true;
            }
            for (; (m_anchor < anchors.Count); m_anchor++, m_entry = 0) {
                var entries = anchors[m_anchor].Entries;

                if (m_entry < entries.Length) {
                    Current = entries[m_entry++];
                    return true;
                }
            }
            return false;
        }
    }
    // Whether any entry draws in the decided upload.
    private bool AnyDraws() {
        foreach (var entry in Entries()) {
            if (entry.Draws) { return true; }
        }
        return false;
    }
    // The GPU time the upload's shadow work took, measured where the device can: a timestamp pair a ring slot, read back
    // once the slot's upload is known complete; the total, the count and the most of those measured.
    private readonly IGpuTimestampPool? m_timestamps;
    private readonly IGpuReadbackBuffer[] m_times;
    private readonly bool[] m_timed;
    private double m_gpuSum;
    private double m_gpuMost;
    private long m_gpuSamples;
    // The last RecentSamples measured uploads, so the report tells a world's steady cost from its arrival.
    private const int RecentSamples = 600;
    private readonly double[] m_gpuRecent = new double[RecentSamples];
    /// <summary>Commits the decided work once the upload that records it is submitted: the cameras and revisions each
    /// entry drew, the rectangles it drew, the layers a completed redraw replaced, and the next share of a redraw in
    /// progress.</summary>
    /// <param name="slot">The upload's ring slot.</param>
    internal void Submitted(int slot) {
        foreach (var entry in Entries()) {
            entry.Submitted(slot: slot);
        }
        m_uploads++;
        m_frozen = true;
    }
    /// <summary>Starts a frame of the residency: the next reader decides the work of the next upload.</summary>
    internal void BeginFrame() {
        m_frozen = false;
        m_decided = false;
    }
    /// <summary>Lets the selections read what an earlier upload into a ring slot read back, once the upload before the
    /// one about to reuse the slot is known complete, and reads the shadow work's measured time.</summary>
    /// <param name="slot">The ring slot about to be reused.</param>
    internal void Completed(int slot) {
        foreach (var entry in Entries()) {
            entry.Completed(slot: slot);
        }
        if (m_timed[slot] && (m_timestamps is { } timestamps)) {
            Span<byte> times = stackalloc byte[(2 * sizeof(ulong))];

            m_times[slot].Read(destination: times);

            var mask = ((timestamps.ValidBits >= 64) ? ulong.MaxValue : ((1UL << ((int)timestamps.ValidBits)) - 1UL));
            var ticks = ((BitConverter.ToUInt64(value: times[sizeof(ulong)..]) - BitConverter.ToUInt64(value: times)) & mask);
            var milliseconds = ((ticks * timestamps.NanosecondsPerTick) * 1e-6);

            m_gpuSum += milliseconds;
            m_gpuMost = Math.Max(m_gpuMost, milliseconds);
            m_gpuRecent[(int)(m_gpuSamples % RecentSamples)] = milliseconds;
            m_gpuSamples++;
            m_timed[slot] = false;
        }
    }
    /// <summary>Describes the world's shadows for <c>world.residency</c>: the uploads, the measured GPU time of their shadow
    /// work, what moved and what of it the entries judged, the bytes held and the spare pairs, the anchors and the views
    /// each serves, and each entry's camera, texels, holds, redraw, owed rectangles and measured triangles.</summary>
    /// <returns>The description.</returns>
    public string Describe() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"uploads={m_uploads} gpu={((m_gpuSamples > 0) ? (m_gpuSum / m_gpuSamples) : 0d):0.000}ms/{m_gpuSamples}timed/most{m_gpuMost:0.000}ms recent={RecentMean():0.000}ms/most{RecentMost():0.000}ms moved={m_movedChanges}changes/{m_movedJudged}judged/{m_movedUnbounded}unbounded finest={m_finestTexel * 1000f:0.#}mm bytes={m_bytes} spares={m_spares.Count}/{m_sparePairs} trims={m_trims} anchors={m_anchors.Count(predicate: static anchor => (anchor.View >= 0))}/{m_anchors.Count} views={string.Join(separator: ',', values: m_viewAnchors)} {m_distant.Describe()} {string.Join(separator: ' ', values: m_anchors.SelectMany(selector: static anchor => anchor.Entries).Select(selector: static entry => entry.Describe()))}");

    // The mean and the most of the recent measured uploads.
    private double RecentMean() {
        var count = (int)Math.Min(val1: m_gpuSamples, val2: RecentSamples);
        var sum = 0d;

        for (var at = 0; (at < count); at++) { sum += m_gpuRecent[at]; }
        return ((count > 0) ? (sum / count) : 0d);
    }
    private double RecentMost() {
        var count = (int)Math.Min(val1: m_gpuSamples, val2: RecentSamples);
        var most = 0d;

        for (var at = 0; (at < count); at++) { most = Math.Max(val1: most, val2: m_gpuRecent[at]); }
        return most;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        MemoryAccount.Shared.TrimRequested -= RequestTrim;
        foreach (var entry in Entries()) { entry.Dispose(); }
        foreach (var spare in m_spares) { RetirePair(pair: spare); }
        m_spares.Clear();
        foreach (var times in m_times) { times.Dispose(); }
        m_timestamps?.Dispose();
        m_loadPass.Dispose();
        m_counters.Dispose();
        m_reservation.Dispose();
    }

    private GpuObjectName Name(string part, string? detail = null, int index = 0) => new(detail: detail, index: index, owner: $"{m_tables.DebugLabel} shadows", part: part);
    // The image, framebuffer or buffer a reallocation replaced, retired behind every submission that may still read it.
    private void Retire(IDisposable? replaced) {
        if (replaced is null) { return; }
        m_tables.RetireShadowObject(replaced: replaced);
    }

    // One place the world's views look at: its near, far and secondary entries, the view it is centred on this upload (-1
    // while none reads it), that view's near cell and fit, and the upload a view last read it.
    private sealed class Anchor(SdfWorldShadows owner, int index) {
        public Entry[] Entries { get; } = [
            new(index: SdfMeshShadowCascades.NearEntry, owner: owner, ring: SdfWorldTables.FrameRingSize, anchor: index),
            new(index: SdfMeshShadowCascades.FarEntry, owner: owner, ring: SdfWorldTables.FrameRingSize, anchor: index),
            new(index: SdfMeshShadowCascades.SecondEntry, owner: owner, ring: SdfWorldTables.FrameRingSize, anchor: index),
        ];
        public int Index { get; } = index;
        public int View { get; set; } = -1;
        public Vector3 Cell { get; set; }
        public SdfMeshShadowCascades Fit;
        public ulong LastUpload { get; set; }
        // Whether any entry holds its layers.
        public bool Holds {
            get {
                foreach (var entry in Entries) {
                    if (entry.Allocated) { return true; }
                }
                return false;
            }
        }

        // Gives the layers back; the next view that takes the anchor allocates them again.
        public void Release() {
            foreach (var entry in Entries) { entry.ReleaseLayers(); }
        }
    }

    // One image an entry draws into, the framebuffers that clear and load it, and the layout the upload last left it in.
    private sealed class Layer(IGpuImage image, IGpuFramebuffer clear, IGpuFramebuffer load) : IDisposable {
        public IGpuImage Image { get; } = image;
        public IGpuFramebuffer Clear { get; } = clear;
        public IGpuFramebuffer Load { get; } = load;
        public GpuImageLayout Layout { get; set; } = GpuImageLayout.Undefined;
        public void Dispose() {
            Clear.Dispose();
            Load.Dispose();
            Image.Dispose();
        }
    }

    // An entry's depth and exit layers, which a redraw draws together and readers bind together.
    private sealed class Pair(Layer depth, Layer exit) : IDisposable {
        public Layer Depth { get; } = depth;
        public Layer Exit { get; } = exit;
        public int Texels => (int)Depth.Image.Width;
        public long Bytes => PairBytes(texels: Texels);
        public void Dispose() {
            Depth.Dispose();
            Exit.Dispose();
        }
    }

    // A rectangle of an entry's texels, X0 and Y0 inclusive, X1 and Y1 exclusive, rows counted down from the top.
    private readonly record struct TexelRect(int X0, int Y0, int X1, int Y1) {
        public long Area => ((long)(X1 - X0) * (Y1 - Y0));
        public bool IsEmpty => ((X1 <= X0) || (Y1 <= Y0));
        public TexelRect Union(in TexelRect other) => new(X0: Math.Min(X0, other.X0), Y0: Math.Min(Y0, other.Y0), X1: Math.Max(X1, other.X1), Y1: Math.Max(Y1, other.Y1));
        public bool Touches(in TexelRect other) => ((X0 <= other.X1) && (other.X0 <= X1) && (Y0 <= other.Y1) && (other.Y0 <= Y1));
    }

    // The rectangles of a layer owed a redraw, at most MaxRectangles and never overlapping, or the whole layer.
    private sealed class OwedRects {
        private readonly TexelRect[] m_rects = new TexelRect[MaxRectangles];

        public int Count { get; private set; }
        public bool All { get; private set; }
        public TexelRect this[int index] => m_rects[index];
        public long Area {
            get {
                var area = 0L;

                for (var at = 0; (at < Count); at++) { area += m_rects[at].Area; }
                return area;
            }
        }
        public bool IsEmpty => (!All && (Count == 0));

        public void Clear() {
            Count = 0;
            All = false;
        }
        // Takes the rectangle at an index out of the owed ones.
        public TexelRect TakeAt(int index) {
            var rect = m_rects[index];

            m_rects[index] = m_rects[--Count];
            return rect;
        }
        // Owes one rectangle of a layer of `texels` a side.
        public void Add(TexelRect rect, int texels) => Owe(rect: rect, texels: texels);
        public void OweAll() {
            Count = 0;
            All = true;
        }
        // Owes a footprint's rectangle in a layer of `texels` a side.
        public void Owe(in Footprint footprint, int texels) {
            if (All) { return; }
            Owe(rect: new TexelRect(
                X0: Math.Clamp(value: ((int)MathF.Floor(footprint.U - footprint.HalfU) - RectanglePad), min: 0, max: texels),
                Y0: Math.Clamp(value: ((int)MathF.Floor(footprint.V - footprint.HalfV) - RectanglePad), min: 0, max: texels),
                X1: Math.Clamp(value: ((int)MathF.Ceiling(footprint.U + footprint.HalfU) + RectanglePad), min: 0, max: texels),
                Y1: Math.Clamp(value: ((int)MathF.Ceiling(footprint.V + footprint.HalfV) + RectanglePad), min: 0, max: texels)
            ), texels: texels);
        }
        // Takes every rectangle another owes, in a layer of `texels` a side.
        public void Merge(OwedRects other, int texels) {
            if (other.All) {
                OweAll();
                return;
            }
            for (var at = 0; (at < other.Count); at++) { Owe(rect: other.m_rects[at], texels: texels); }
        }
        // Merges a rectangle into the owed ones: into one it touches, else a new one, else the one whose union grows least;
        // a grown union then absorbs every other it touches, so no two owed rectangles overlap. Each rectangle selects and
        // draws every caster in its box, so past half the layer one whole redraw costs less.
        private void Owe(TexelRect rect, int texels) {
            if (All || rect.IsEmpty) { return; }

            var into = -1;

            for (var at = 0; ((at < Count) && (into < 0)); at++) {
                if (m_rects[at].Touches(other: in rect)) { into = at; }
            }
            if ((into < 0) && (Count < MaxRectangles)) {
                m_rects[Count++] = rect;
            } else {
                if (into < 0) {
                    var growth = long.MaxValue;

                    for (var at = 0; (at < Count); at++) {
                        var grown = (m_rects[at].Union(other: in rect).Area - m_rects[at].Area);

                        if (grown < growth) { (into, growth) = (at, grown); }
                    }
                }
                m_rects[into] = m_rects[into].Union(other: in rect);
                for (var at = 0; (at < Count);) {
                    if ((at == into) || !m_rects[at].Touches(other: in m_rects[into])) {
                        at++;
                        continue;
                    }
                    m_rects[into] = m_rects[into].Union(other: in m_rects[at]);
                    m_rects[at] = m_rects[--Count];
                    if (into == Count) { into = at; }
                    at = 0;
                }
            }
            if ((2L * Area) >= ((long)texels * texels)) { OweAll(); }
        }
    }

    // Where a caster lies in an entry's texels: the centre across the plane and along the light, the half extents across
    // the plane, and the generation of the layer it was judged against.
    private readonly record struct Footprint(float U, float V, float W, float HalfU, float HalfV, int Generation) {
        // Where a box lies in a camera's texels, `texels` a side: its centre and half extents across the plane, and its
        // centre's depth along the light, in texels.
        public static Footprint Of(in SdfMeshBox box, in SdfMeshShadowCamera camera, int texels) {
            var relative = (box.Center - camera.Position);
            var scale = ((0.5f * texels) / camera.HalfHeight);

            return new Footprint(
                U: ((((Vector3.Dot(relative, camera.Right) / camera.HalfHeight) * 0.5f) + 0.5f) * texels),
                V: ((0.5f - ((Vector3.Dot(relative, camera.Up) / camera.HalfHeight) * 0.5f)) * texels),
                W: (Vector3.Dot(relative, camera.Forward) * scale),
                HalfU: (((MathF.Abs(Vector3.Dot(box.HalfX, camera.Right)) + MathF.Abs(Vector3.Dot(box.HalfY, camera.Right))) + MathF.Abs(Vector3.Dot(box.HalfZ, camera.Right))) * scale),
                HalfV: (((MathF.Abs(Vector3.Dot(box.HalfX, camera.Up)) + MathF.Abs(Vector3.Dot(box.HalfY, camera.Up))) + MathF.Abs(Vector3.Dot(box.HalfZ, camera.Up))) * scale),
                Generation: 0);
        }
        // Whether another footprint of the same caster lies MoveTexels or more away, across the plane, in extent or along
        // the light.
        public bool Movable(in Footprint other) =>
            ((MathF.Abs(U - other.U) + MathF.Abs(HalfU - other.HalfU)) >= MoveTexels) ||
            ((MathF.Abs(V - other.V) + MathF.Abs(HalfV - other.HalfV)) >= MoveTexels) ||
            (MathF.Abs(W - other.W) >= MoveTexels);
    }

    // What an entry draws for one upload.
    private struct Work {
        // The share of a redraw drawn, -1 for none; the shares the redraw takes; whether this share completes it; the
        // pair it draws into (null for the entry's own) and the tag its measurement carries.
        public int Share;
        public int Slices;
        public bool Completes;
        public SdfMeshShadowCamera Camera;
        public Vector3 Cell;
        public long Revision;
        public Pair? Target;
        public long Tag;
        // The rectangles drawn again this upload, taken from the entry's owed rectangles when decided.
        public int Rectangles;
        // Whether the glass extinction is drawn again over the whole layer.
        public bool Optical;
        // The rate counters the frame counts into, which the upload's submission counts the work into.
        public WorkCounterSet? Counts;
    }

    // A redraw of an entry's layers: the camera, cell, static revision and texels it draws from, its shares, the share it
    // draws next, the pair lent to it (null while it draws into the entry's own layers, which no reader reads), and its
    // serial, which its shares' measurements carry. Until its first share is submitted it follows the entry's fit.
    private readonly record struct Redraw(SdfMeshShadowCamera Camera, Vector3 Cell, long Revision, int Slices, int Next, int Texels, Pair? Target, long Serial) {
        public bool Started => (Next > 0);
    }

    // One entry of the cascade set: its layers, its selections and their sets per ring slot, and what it holds.
    private sealed class Entry : IDisposable {
        private readonly SdfWorldShadows m_owner;
        private readonly nint m_pool;
        private readonly SdfMeshOcclusion[] m_selections;
        private readonly IGpuStorageBuffer[][] m_blocks;
        private readonly nint[][] m_meshSets;
        private readonly IGpuBuffer?[][][] m_meshSetBuffers;
        private readonly IGpuBuffer?[][][] m_meshSetOcclusion;
        private readonly nint[][] m_meshSetTargets;
        private readonly nint[][] m_occlusionSets;
        private readonly IGpuBuffer?[][] m_occlusionSetBuffers;
        private readonly nint[] m_occlusionSetTargets;
        private readonly nint[] m_glassSets;
        private readonly IGpuBuffer?[][] m_glassSetBuffers;
        private readonly IGpuBuffer?[][] m_glassSetOcclusion;
        private readonly nint[] m_glassSetTargets;
        private readonly SdfShadowEntryCounts m_kinds;
        // The rectangles this entry owes in the texels of the camera receivers read; those this upload's work took from
        // them when decided, returned to them if the upload is decided again unsubmitted; and those a redraw under way
        // owes its own layers for what moved while it drew, which become the owed rectangles when it lands.
        private readonly OwedRects m_owed = new();
        private readonly OwedRects m_taken = new();
        private readonly OwedRects m_pending = new();
        private bool m_owedOptical;
        private bool m_takenOptical;
        // Where the entry last drew each staged row's caster (Note), valid while its generation is the layer's: every
        // whole redraw moves the generation, after which a row's next change says where the redraw drew it.
        private Footprint[] m_drawn = [];
        private int m_drawnGeneration = 1;

        private Pair? m_front;
        private bool m_opticalInitialized;
        // The side the glass extinction buffer was sized for; zero while none is held.
        private int m_opticalTexels;
        private int m_texels;
        // The pair readers bound after the last decision, whose replacement moves the views' binding revision.
        private Pair? m_bound;

        // What the last submitted upload committed: the camera, cell, revision and texels the layers were drawn at and
        // whether they stand.
        private SdfMeshShadowCamera m_camera;
        private Vector3 m_cell;
        private long m_revision = -1;
        private bool m_stands;
        private Redraw? m_redraw;
        private int m_heldUploads;
        // This upload's work, decided and then recorded; whether the scheduler admitted a share and whether the recording
        // drew the work, which Submitted commits.
        private Work m_work;
        private bool m_admitted;
        private bool m_recorded;
        private bool m_holds;
        private int m_rank;
        // The vertices this entry's last whole redraw measured (zero while none is), and the measurements of the redraw
        // being measured: its serial, the vertices its shares measured and how many; and the vertices its rectangles drew.
        private long m_wholeVertices;
        private long m_measuringSerial = -1;
        private long m_measuringVertices;
        private int m_measuringShares;
        private long m_lastWholeVertices;
        private long m_rectVertices;
        private long m_rectArea;
        private long m_rectDrawings;
        private int m_slicesDrawn;
        // What ended the standing layers' holds: the light's turn, the eye's cell, a static caster, the fit's extent.
        private long m_endedByTurn;
        private long m_endedByCell;
        private long m_endedByStatic;
        private long m_endedByFit;
        private long m_endedByMotion;

        public Entry(int index, SdfWorldShadows owner, int ring, int anchor) {
            Index = index;
            m_owner = owner;
            FollowsMotion = (index != SdfMeshShadowCascades.DistantEntry);
            m_kinds = SdfUpdateRateCounts.ShadowEntries[index];
            Part = (FollowsMotion ? $"shadow-{EntryNames[index]}.a{anchor}" : $"shadow-{EntryNames[index]}");

            var gpu = owner.m_gpu;
            var tables = owner.m_tables;
            var named = $"{tables.DebugLabel} shadows";
            var sizes = default(GpuDescriptorPoolSizes);

            for (var set = 0; (set < (Kinds * ring)); set++) {
                // A mesh set and its two selection sets, and a glass extinction set beside each.
                sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
                sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
                for (var phase = 0; (phase < OcclusionSets); phase++) {
                    sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.MeshOcclusion.Groups);
                }
            }
            m_pool = gpu.Bindings.CreatePool(name: owner.Name(part: Part, detail: "descriptors"), sizes: sizes);

            m_selections = new SdfMeshOcclusion[Kinds];
            for (var kind = 0; (kind < Kinds); kind++) {
                m_selections[kind] = new SdfMeshOcclusion(buffers: gpu.BufferFactory, owner: named, slots: ring, cascade: true, optical: (kind == OpticalKind), measures: true,
                    part: kind switch { WholeKind => Part, OpticalKind => $"{Part} glass", _ => $"{Part} rect{kind - FirstRectangleKind}" });
            }
            m_selections[WholeKind].Measured = MeasuredWhole;
            for (var kind = FirstRectangleKind; (kind < Kinds); kind++) {
                m_selections[kind].Measured = MeasuredRectangle;
            }
            m_blocks = new IGpuStorageBuffer[Kinds][];
            m_meshSets = new nint[Kinds][];
            m_meshSetBuffers = new IGpuBuffer?[Kinds][][];
            m_meshSetOcclusion = new IGpuBuffer?[Kinds][][];
            m_meshSetTargets = new nint[Kinds][];
            m_occlusionSets = new nint[Kinds * ring * OcclusionSets][];
            m_occlusionSetBuffers = new IGpuBuffer?[Kinds * ring * OcclusionSets][];
            m_occlusionSetTargets = new nint[Kinds * ring * OcclusionSets];
            m_glassSets = new nint[ring];
            m_glassSetBuffers = new IGpuBuffer?[ring][];
            m_glassSetOcclusion = new IGpuBuffer?[ring][];
            m_glassSetTargets = new nint[ring];

            var meshLayout = tables.MeshPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)];
            var occlusionLayout = tables.OcclusionPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)];
            var blockBytes = (ulong)IGpuBindings.ConstantBufferBytes(blockBytes: (uint)SdfFrameBlock.SizeBytes);

            for (var kind = 0; (kind < Kinds); kind++) {
                m_blocks[kind] = new IGpuStorageBuffer[ring];
                m_meshSets[kind] = new nint[ring];
                m_meshSetBuffers[kind] = new IGpuBuffer?[ring][];
                m_meshSetOcclusion[kind] = new IGpuBuffer?[ring][];
                m_meshSetTargets[kind] = new nint[ring];
                for (var slot = 0; (slot < ring); slot++) {
                    var block = gpu.BufferFactory.CreateHostVisible(name: owner.Name(part: Part, detail: $"pass block {kind}", index: slot), sizeBytes: blockBytes, usage: GpuBufferUsage.Uniform);

                    m_blocks[kind][slot] = block;
                    m_meshSets[kind][slot] = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: meshLayout, name: owner.Name(part: Part, detail: $"mesh group {kind}", index: slot), poolHandle: m_pool);
                    gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_meshSets[kind][slot]);
                    m_meshSetBuffers[kind][slot] = new IGpuBuffer?[MeshSetBuffers];
                    m_meshSetOcclusion[kind][slot] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                    for (var phase = 0; (phase < OcclusionSets); phase++) {
                        var at = OcclusionSetOf(kind: kind, slot: slot, phase: phase);

                        m_occlusionSets[at] = [gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: occlusionLayout, name: owner.Name(part: Part, detail: $"occlusion group {kind}.{phase}", index: slot), poolHandle: m_pool)];
                        gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_occlusionSets[at][0]);
                        m_occlusionSetBuffers[at] = new IGpuBuffer?[MeshSetBuffers + MeshSetOcclusionBuffers];
                    }
                    if (kind == OpticalKind) {
                        m_glassSets[slot] = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: meshLayout, name: owner.Name(part: Part, detail: "glass group", index: slot), poolHandle: m_pool);
                        gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_glassSets[slot]);
                        m_glassSetBuffers[slot] = new IGpuBuffer?[MeshSetBuffers];
                        m_glassSetOcclusion[slot] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                    }
                }
            }
        }

        public int Index { get; }
        // The name its layers and selections are created under.
        public string Part { get; }
        // Whether the entry draws every caster and follows what moves by rectangles (the near, far and secondary entries),
        // rather than holding the static scene alone (the distant map).
        public bool FollowsMotion { get; }
        public IGpuBuffer? Optical { get; private set; }
        // Whether the entry holds its layers.
        public bool Allocated => (m_front is not null);
        // The camera receivers read through after this upload: the one this upload completes a redraw from, else the
        // committed one, else none.
        public SdfMeshShadowCamera ReaderCamera => (Completes ? m_work.Camera : (m_stands ? m_camera : default));
        // The finest texel, in metres, a change is judged against: the layer receivers read after this upload, and a
        // redraw under way, which owes what moves while it draws; zero for neither.
        public float JudgedTexel {
            get {
                var standing = ((ReaderCamera.HalfHeight > 0f) ? ((2f * ReaderCamera.HalfHeight) / Math.Max(val1: m_texels, val2: 1)) : float.MaxValue);
                var drawing = ((m_redraw is { Started: true } redraw) ? ((2f * redraw.Camera.HalfHeight) / Math.Max(val1: redraw.Texels, val2: 1)) : float.MaxValue);
                var finest = MathF.Min(x: standing, y: drawing);

                return ((finest < float.MaxValue) ? finest : 0f);
            }
        }
        // Whether this upload's admitted share completes a redraw.
        private bool Completes => (m_admitted && m_work.Completes);
        // The pair receivers bind after this upload: the pair a redraw completing this upload drew into, else the entry's
        // own.
        private Pair? ReadPair => ((Completes && (m_work.Target is { } target)) ? target : m_front);

        // The layer receivers sample.
        public Layer? Read(bool exit) => ((ReadPair is { } pair) ? (exit ? pair.Exit : pair.Depth) : null);
        // Whether this upload's work draws anything.
        public bool Draws => (m_holds && (m_admitted || (m_work.Rectangles > 0) || m_work.Optical || (m_front?.Depth.Layout == GpuImageLayout.Undefined)));
        // Whether the entry wants a share of a redraw this upload.
        public bool Wants => (m_holds && (m_redraw is not null));
        // The order the scheduler admits shares in: redraws under way first (they hold a lent pair), then entries no
        // reader has seen drawn, then by anchor, then the far entry, the near, the secondary and the distant map.
        public long Priority => ((((m_redraw is { Started: true }) || (m_redraw?.Target is not null)) ? 0L : (1L << 40)) |
            (m_stands ? (1L << 32) : 0L) | (((long)m_rank) << 8) | (long)(Index switch {
                SdfMeshShadowCascades.FarEntry => 0,
                SdfMeshShadowCascades.NearEntry => 1,
                SdfMeshShadowCascades.SecondEntry => 2,
                _ => 3,
            }));
        // The vertices this upload's share of the wanted redraw is expected to draw: the entry's measured whole over its
        // shares, else another anchor's of the same entry, else the whole budget.
        public long ShareVertices => ((m_redraw is { } redraw) && (Estimate is var whole and > 0L)) ? ((whole + redraw.Slices - 1L) / redraw.Slices) : UploadVertices;
        // The vertices a whole redraw of the entry is expected to draw: its own last measurement, else its index's.
        private long Estimate => ((m_wholeVertices > 0L) ? m_wholeVertices : m_owner.m_indexVertices[Index]);
        // The vertices a rectangle of the entry is expected to draw a texel: what its rectangles measured, else its whole
        // spread over the layer, else nothing known.
        private double RectangleDensity => ((m_rectArea > 0L)
            ? (m_rectVertices / (double)m_rectArea)
            : ((m_texels > 0) ? (Estimate / ((double)m_texels * m_texels)) : 0d));

        private static int OcclusionSetOf(int kind, int slot, int phase) => ((((kind * SdfWorldTables.FrameRingSize) + slot) * OcclusionSets) + phase);

        // Chooses the redraw this upload wants, before the scheduler admits a share of it: the one under way, else one of
        // the fresh fit while the layers no longer hold for it.
        public void Want(in SdfMeshShadowCascades fit, SdfFrame frame, Vector3 eye, int rank) {
            Restore();
            m_work = new Work { Share = -1, Counts = frame.RateCounts };
            m_admitted = false;
            m_recorded = false;
            m_rank = rank;
            m_holds = fit.Holds(index: Index);
            if (!m_holds) {
                return;
            }

            var tables = m_owner.m_tables;
            var fresh = fit[Index];
            var texels = fit.TexelsOf(index: Index);
            var reach = SdfMeshShadowCascades.ReachOf(frame: frame, index: Index);
            var cell = SdfMeshShadowCascades.CellOf(eye: eye, reach: reach);
            var revision = tables.StaticDrawRevision;
            var rates = frame.UpdateRates;
            var holds = ((rates & SdfUpdateRates.ShadowDistant) != 0);
            var tolerance = TurnRadiansOf(frame: frame, camera: in fresh, texels: texels, reach: reach, slope: fit.Entry(index: Index).Slope);
            var reallocated = Allocate(frame: frame, texels: texels);

            if ((m_redraw is { } running) && ((running.Texels != texels) || reallocated)) {
                EndRedraw();
            }
            if (m_redraw is { Started: true }) {
                return;
            }

            var held = (
                holds &&
                m_stands &&
                (m_camera.HalfHeight == fresh.HalfHeight) &&
                (m_camera.Far == fresh.Far) &&
                (m_cell == cell) &&
                // The chord between unit directions is their angle to well under a part in a million at these turns.
                (Vector3.Distance(value1: m_camera.Forward, value2: fresh.Forward) <= tolerance) &&
                // An entry that follows motion redraws a changed static caster by the rectangles its change owes; only
                // the distant map, which follows none, holds for the static revision.
                (FollowsMotion || (m_revision == revision)) &&
                // A layer owed whole is drawn again by a scheduled redraw, never by one rectangle in one upload.
                !m_owed.All
            );

            if (held) {
                EndRedraw();
                return;
            }
            // What ended a standing layer's hold, counted once a redraw for the report.
            if (m_stands && (m_redraw is null)) {
                if ((m_camera.HalfHeight != fresh.HalfHeight) || (m_camera.Far != fresh.Far)) { m_endedByFit++; }
                else if (m_cell != cell) { m_endedByCell++; }
                else if (!FollowsMotion && (m_revision != revision)) { m_endedByStatic++; }
                else if (m_owed.All) { m_endedByMotion++; }
                else if (holds) { m_endedByTurn++; }
            }
            // A redraw not yet under way follows the fit, keeping a pair already lent to it.
            m_redraw = new Redraw(Camera: fresh, Cell: cell, Revision: revision, Slices: SlicesOf(frame: frame, rates: rates), Next: 0, Texels: texels,
                Target: m_redraw?.Target, Serial: (m_redraw?.Serial ?? ++m_owner.m_redrawSerial));
        }
        // Admits this upload's share of the wanted redraw: one of a standing entry not yet under way borrows a spare pair,
        // and waits where none is free. Returns whether the share is admitted.
        public bool Admit() {
            if (m_redraw is not { } redraw) { return false; }
            if (m_stands && (redraw.Target is null)) {
                if (m_owner.Lend(texels: redraw.Texels) is not { } lent) { return false; }
                m_redraw = redraw with { Target = lent };
            }
            m_admitted = true;

            return true;
        }
        // Completes this upload's work once the scheduler has admitted the shares: the share, the rectangles and the glass
        // extinction, and the pair readers bind; returns whether it changes what receivers read.
        public bool Finish(SdfFrame frame, ulong upload) {
            if (!m_holds) {
                Bind();
                return false;
            }
            if (m_admitted && (m_redraw is { } drawing)) {
                m_work.Share = drawing.Next;
                m_work.Slices = drawing.Slices;
                m_work.Completes = (drawing.Next == (drawing.Slices - 1));
                m_work.Camera = drawing.Camera;
                m_work.Cell = drawing.Cell;
                m_work.Revision = drawing.Revision;
                m_work.Target = drawing.Target;
                m_work.Tag = ((drawing.Serial << SliceBits) | (long)drawing.Slices);
            }

            var completes = Completes;
            var reallocated = (ReadPair != m_bound);

            if (FollowsMotion) {
                // The far entry draws its moving casters at its authored rate, owing what moved between.
                var rates = frame.UpdateRates;
                var paced = (((rates & SdfUpdateRates.ShadowFar) == 0) || (Index != SdfMeshShadowCascades.FarEntry) || ((upload % (ulong)FarPeriodOf(frame: frame)) == 0UL));

                // A layer drawn anew answers every move its redraw saw; what moved while it drew is owed when it lands.
                // This upload takes the owed rectangles whose expected vertices fit RectangleVertices, at least one, and
                // the rest wait; a layer owed whole waits for the scheduled redraw that owing it wants.
                if ((ReaderCamera.HalfHeight > 0f) && paced && !completes && !m_owed.All) {
                    var density = RectangleDensity;
                    var spent = 0d;

                    for (var at = 0; (at < m_owed.Count);) {
                        var cost = (density * m_owed[at].Area);

                        if ((m_taken.Count > 0) && ((spent + cost) > RectangleVertices)) {
                            at++;
                            continue;
                        }
                        m_taken.Add(rect: m_owed.TakeAt(index: at), texels: m_texels);
                        spent += cost;
                    }
                    m_work.Rectangles = m_taken.Count;
                    m_takenOptical = m_owedOptical;
                    m_owedOptical = false;
                }
                m_work.Optical = ((completes || m_takenOptical) && (Optical is not null));
            }
            Bind();

            return (completes || (m_work.Rectangles > 0) || m_work.Optical || reallocated);
        }
        // Moves the views' binding revision when the pair readers bind is replaced.
        private void Bind() {
            var read = (m_holds ? ReadPair : m_front);

            if (!ReferenceEquals(objA: read, objB: m_bound)) {
                m_bound = read;
                m_owner.m_bindingRevision++;
            }
        }
        // Returns the rectangles and extinction an unsubmitted decision took from the owed ones.
        private void Restore() {
            if (m_taken.IsEmpty && !m_takenOptical) { return; }
            m_owed.Merge(other: m_taken, texels: m_texels);
            m_owedOptical |= m_takenOptical;
            m_taken.Clear();
            m_takenOptical = false;
        }
        // Ends a redraw before it lands, its lent pair returned to the spares.
        private void EndRedraw() {
            if (m_redraw?.Target is { } lent) { m_owner.Give(pair: lent); }
            m_redraw = null;
            m_pending.Clear();
        }
        // Owes the whole layer: a change no box bounds may have moved any caster.
        public void OweAll() {
            if (m_stands) {
                m_owed.OweAll();
                m_owedOptical = true;
            }
            if (m_redraw is { Started: true }) { m_pending.OweAll(); }
        }
        // Judges one draw's change against where this entry last drew it, in the committed camera's texels: a draw that
        // appears or goes owes the rectangle it covers, and one that moves owes where it was drawn and where it is once it
        // has moved MoveTexels across the plane or along the light since. A redraw under way owes its own layers where
        // the caster was and is, since a share already drawn may hold it where it was.
        public void Note(int row, in SdfMeshBox? before, in SdfMeshBox? after, bool glass) {
            if (m_redraw is { Started: true } redraw) { NotePending(redraw: in redraw, before: in before, after: in after); }
            if (!m_stands || m_owed.All || (m_texels <= 0) || (row < 0)) { return; }

            var camera = m_camera;
            var owed = m_owed.Count;

            if (row >= m_drawn.Length) {
                Array.Resize(array: ref m_drawn, newSize: Math.Max((row + 1), (m_drawn.Length * 2)));
            }
            ref var drawn = ref m_drawn[row];

            if (after is not { } now) {
                if (before is { } gone) { m_owed.Owe(footprint: Footprint.Of(box: in gone, camera: in camera, texels: m_texels), texels: m_texels); }
                drawn = default;
            } else {
                var current = Footprint.Of(box: in now, camera: in camera, texels: m_texels);

                if (drawn.Generation != m_drawnGeneration) {
                    // Not judged since the layer was drawn: it was drawn where it stood before this change.
                    if (before is { } was) {
                        drawn = (Footprint.Of(box: in was, camera: in camera, texels: m_texels) with { Generation = m_drawnGeneration });
                    } else {
                        m_owed.Owe(footprint: current, texels: m_texels);
                        drawn = current with { Generation = m_drawnGeneration };
                    }
                }
                if (drawn.Movable(other: in current)) {
                    m_owed.Owe(footprint: drawn, texels: m_texels);
                    m_owed.Owe(footprint: current, texels: m_texels);
                    drawn = current with { Generation = m_drawnGeneration };
                }
            }
            if (glass && ((m_owed.Count != owed) || m_owed.All)) { m_owedOptical = true; }
        }
        // Owes a redraw under way the places a changed caster was and is, in its own camera's texels, once they lie a
        // texel apart.
        private void NotePending(in Redraw redraw, in SdfMeshBox? before, in SdfMeshBox? after) {
            if (m_pending.All) { return; }

            var camera = redraw.Camera;
            var was = ((before is { } box) ? Footprint.Of(box: in box, camera: in camera, texels: redraw.Texels) : (Footprint?)null);
            var now = ((after is { } moved) ? Footprint.Of(box: in moved, camera: in camera, texels: redraw.Texels) : (Footprint?)null);

            if ((was is { } from) && (now is { } to) && !from.Movable(other: in to)) { return; }
            if (was is { } old) { m_pending.Owe(footprint: old, texels: redraw.Texels); }
            if (now is { } current) { m_pending.Owe(footprint: current, texels: redraw.Texels); }
        }
        // The shares one redraw takes: the shares that keep each under the upload's budget by the entry's measured
        // vertices, never fewer than the distant map's authored frames or the far entry's refresh period (the authored far
        // rate, farShadows.hertz, spreads a redraw over the frames of one of its periods). Unmeasured, the near and
        // secondary entries, whose light boxes hold a room's casters, take a few shares, and the far and distant entries,
        // whose boxes hold a valley's, many.
        private int SlicesOf(SdfFrame frame, SdfUpdateRates rates) {
            var authored = Index switch {
                SdfMeshShadowCascades.DistantEntry when ((rates & SdfUpdateRates.ShadowDistant) != 0) => frame.DistantShadows.Slices,
                SdfMeshShadowCascades.FarEntry when ((rates & SdfUpdateRates.ShadowFar) != 0) => FarPeriodOf(frame: frame),
                _ => 1,
            };
            var unmeasured = (Index is SdfMeshShadowCascades.FarEntry or SdfMeshShadowCascades.DistantEntry) ? 16 : 4;
            var measured = ((Estimate is var whole and > 0L) ? (int)Math.Min(val1: SdfMeshOcclusion.MaxSlices, val2: ((whole + UploadVertices - 1L) / UploadVertices)) : unmeasured);

            return Math.Clamp(value: Math.Max(val1: authored, val2: measured), min: 1, max: SdfMeshOcclusion.MaxSlices);
        }
        // The frames between the far entry's refreshes of its moving casters at the authored rate.
        private static int FarPeriodOf(SdfFrame frame) =>
            Math.Clamp(value: (int)MathF.Round(x: (SdfUpdateRateTiers.EveryFrameHertz / (float)Math.Max(val1: frame.FarShadows.Hertz, val2: 1))), min: 1, max: SdfMeshOcclusion.MaxSlices);
        // The light turn held layers tolerate: the distant map's and the far entry's authored tolerance, and otherwise the
        // turn that moves the shadow of a caster as tall as the entry's reach by PenumbraShare of the penumbra the surface
        // pass draws for it, and one texel besides. The penumbra of a caster h above its receiver spans h times the light's
        // half-slope, which the filter takes up to its widest radius; the shadow moves h times the turn, so the tallest
        // caster bounds the lag, and a light with no size holds through a texel.
        private float TurnRadiansOf(SdfFrame frame, in SdfMeshShadowCamera camera, int texels, float reach, float slope) {
            var authored = Index switch {
                SdfMeshShadowCascades.DistantEntry => frame.DistantShadows.TurnRadians,
                SdfMeshShadowCascades.FarEntry => frame.FarShadows.TurnRadians,
                _ => 0f,
            };

            if (authored > 0f) { return authored; }

            var texel = ((2f * camera.HalfHeight) / Math.Max(val1: texels, val2: 1));
            var span = MathF.Max(x: reach, y: texel);
            var penumbra = MathF.Min(x: ((span * MathF.Max(x: slope, y: 0f)) / texel), y: SdfKernelInterfaces.MeshShadowFilterTexels);

            return ((texel * ((PenumbraShare * penumbra) + MoveTexels)) / span);
        }
        // Allocates the layers an entry needs at its texels; returns whether anything was allocated anew, which no
        // standing layer survives.
        private bool Allocate(SdfFrame frame, int texels) {
            var optical = (FollowsMotion && frame.Program.HasGlassShadows);
            var changed = ((m_texels != texels) || (m_front is null));

            if (changed) {
                ReleaseLayers();
                m_texels = texels;
                m_front = m_owner.CreatePair(texels: texels, part: Part);
                m_opticalInitialized = false;
            }
            // The glass stages index the buffer by the entry's side (SdfWorldPackage.ShadowOpticalTexelWords), so it is
            // replaced whenever the texels change.
            if ((optical != (Optical is not null)) || (optical && (m_opticalTexels != texels))) {
                ReleaseOptical();
                m_opticalTexels = (optical ? texels : 0);
                Optical = (optical
                    ? m_owner.m_gpu.BufferFactory.CreateDeviceLocal(
                        name: m_owner.Name(part: Part, detail: "optical"),
                        sizeBytes: (ulong)OpticalBytes(texels: texels),
                        usage: GpuBufferUsage.Storage)
                    : null);
                if (Optical is { } created) { m_owner.Hold(bytes: (long)created.SizeBytes); }
                m_opticalInitialized = false;
                // The extinction is drawn with a whole layer, so a buffer allocated now needs one.
                m_stands = false;
                changed = true;
                m_owner.m_bindingRevision++;
            }

            return changed;
        }
        private void ReleaseOptical() {
            if (Optical is not { } held) { return; }
            m_owner.Hold(bytes: -(long)held.SizeBytes);
            m_owner.Retire(replaced: held);
            Optical = null;
            m_opticalTexels = 0;
        }
        public void ReleaseLayers() {
            EndRedraw();
            m_owner.RetirePair(pair: m_front);
            m_front = null;
            m_stands = false;
            m_owed.Clear();
            m_taken.Clear();
            m_owedOptical = false;
            m_takenOptical = false;
        }
        // Draws nothing this upload: no view reads the entry's anchor, so a redraw under way ends and returns its pair.
        public void Idle() {
            Restore();
            EndRedraw();
            m_work = new Work { Share = -1 };
            m_admitted = false;
            m_recorded = false;
            m_holds = false;
        }

        public void Record(nint commandBuffer, SdfFrame frame, int slot) {
            if (!m_holds || (m_front is not { } front)) { return; }
            m_recorded = true;

            var target = (m_work.Target ?? front);
            var read = (ReadPair ?? front);

            // A layer allocated or lent anew holds nothing: it is cleared, so a view binding it before it is drawn samples
            // the far plane, and left shader-readable.
            foreach (var layer in (ReadOnlySpan<Layer>)[front.Depth, front.Exit, target.Depth, target.Exit]) {
                if (layer.Layout == GpuImageLayout.Undefined) {
                    Clear(commandBuffer: commandBuffer, layer: layer);
                }
            }

            var whole = new GpuPixelRect(Height: (uint)m_texels, Width: (uint)m_texels, X: 0, Y: 0);
            var motion = (FollowsMotion ? SdfMeshMotion.Any : SdfMeshMotion.Static);

            if (m_admitted) {
                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: WholeKind, camera: in m_work.Camera, motion: motion,
                    share: m_work.Share, slices: m_work.Slices, pair: target, area: whole, clear: (m_work.Share == 0), tag: m_work.Tag);
                // A layer drawn now may miss a caster absent from the records, whose landing must then move the static
                // revision.
                m_owner.m_tables.CastersRead();
                m_slicesDrawn = m_work.Slices;
                Rest(commandBuffer: commandBuffer, layer: target.Depth);
                Rest(commandBuffer: commandBuffer, layer: target.Exit);
            }

            var camera = ReaderCamera;

            for (var at = 0; (at < m_work.Rectangles); at++) {
                var rect = m_taken[at];
                var area = new GpuPixelRect(Height: (uint)(rect.Y1 - rect.Y0), Width: (uint)(rect.X1 - rect.X0), X: rect.X0, Y: rect.Y0);

                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: (FirstRectangleKind + at), camera: in camera, motion: motion,
                    share: 0, slices: 1, pair: read, area: area, clear: true, tag: rect.Area);
            }
            if (m_work.Optical) {
                Extinction(commandBuffer: commandBuffer, frame: frame, slot: slot, camera: in camera, area: whole, pair: read, optical: Optical!);
            }
            Rest(commandBuffer: commandBuffer, layer: read.Depth);
            Rest(commandBuffer: commandBuffer, layer: read.Exit);
        }
        // Selects a drawing's draws on the GPU and draws them into a pair's two layers within an area of its texels: the
        // entry depth (back-face culled where the draw encloses a solid), then the exit layer. The light camera is the
        // entry's; an area less than the whole layer draws through a viewport of its own, from the camera of that
        // rectangle of the plane, so its texels land where the whole camera puts them, and keeps only the casters in that
        // rectangle's box. A drawing that clears clears the area first; one that loads draws over what the layers hold.
        private void Draw(nint commandBuffer, SdfFrame frame, int slot, int kind, in SdfMeshShadowCamera camera, SdfMeshMotion motion, int share, int slices,
            Pair pair, GpuPixelRect area, bool clear, long tag) {
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[kind];
            var count = m_owner.m_tables.MeshDrawCount;
            var set = m_meshSets[kind][slot];
            var (drawn, aspect, cull) = Through(camera: in camera, area: area);

            Stage(frame: frame, slot: slot, kind: kind, blockCamera: in drawn, aspect: aspect, detailCamera: in camera, cull: in cull, motion: motion, share: share, slices: slices, optical: null, tag: tag);
            Move(commandBuffer: commandBuffer, layer: pair.Depth, to: GpuImageLayout.DepthAttachment);
            Move(commandBuffer: commandBuffer, layer: pair.Exit, to: GpuImageLayout.DepthAttachment);
            if (count > 0) {
                selection.RecordEarly(commandBuffer: commandBuffer, pipeline: m_owner.m_tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[OcclusionSetOf(kind: kind, slot: slot, phase: 0)][0]);
            }
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: (clear ? pair.Depth.Clear : pair.Depth.Load));
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 0, pipeline: m_owner.m_tables.ShadowPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: SdfKernelInterfaces.MeshShadowCulledEntryPhase, pipeline: m_owner.m_tables.ShadowEntryPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            // The exit layer: front-face culled solids, then sheets and clipped draws unculled.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: (clear ? pair.Exit.Clear : pair.Exit.Load));
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 2, pipeline: m_owner.m_tables.ShadowExitPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: 3, pipeline: m_owner.m_tables.ShadowPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
        // The camera an area of the entry's plane draws through, its aspect, and the cull that keeps the casters its box
        // may hold: the entry's own camera for the whole layer.
        private (SdfMeshShadowCamera Camera, float Aspect, SdfMeshCull Cull) Through(in SdfMeshShadowCamera camera, GpuPixelRect area) {
            var cullsNothing = (m_owner.m_decidedFrame?.MeshCull == SdfMeshCullLevel.Off);

            if ((area.Width == (uint)m_texels) && (area.Height == (uint)m_texels)) {
                return (camera, 1f, (cullsNothing ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in camera)));
            }

            var texel = ((2f * camera.HalfHeight) / m_texels);
            var centreU = ((area.X + (0.5f * area.Width)) - (0.5f * m_texels));
            var centreV = ((0.5f * m_texels) - (area.Y + (0.5f * area.Height)));
            var drawn = camera with {
                HalfHeight = (0.5f * area.Height * texel),
                Position = ((camera.Position + (camera.Right * (centreU * texel))) + (camera.Up * (centreV * texel))),
            };
            // The cull's box is square: the drawn camera's, widened to the area's longer side.
            var square = drawn with { HalfHeight = (0.5f * Math.Max(area.Width, area.Height) * texel) };

            return (drawn, (((float)area.Width) / area.Height), (cullsNothing ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in square)));
        }
        // Writes a drawing's pass block and its mesh and selection sets, and stages its selection.
        private void Stage(SdfFrame frame, int slot, int kind, in SdfMeshShadowCamera blockCamera, float aspect, in SdfMeshShadowCamera detailCamera, in SdfMeshCull cull,
            SdfMeshMotion motion, int share, int slices, IGpuBuffer? optical, long tag) {
            var tables = m_owner.m_tables;
            var selection = m_selections[kind];
            var count = tables.MeshDrawCount;
            var set = m_meshSets[kind][slot];
            var block = m_owner.m_block.AsSpan();
            var detail = ((frame.MeshDetail == SdfMeshDetailLevel.Auto) ? SdfMeshDetail.Of(cascade: in detailCamera, texels: m_texels) : SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)));

            block.Clear();
            SdfFrameBlock.Write(block: block, frame: frame, height: (uint)m_texels, lightMask: tables.LightMaskOf(frame: frame, view: 0), tables: tables.PassValues, view: 0, width: (uint)m_texels);
            SdfFrameBlock.WriteShadowCamera(block: block, camera: in blockCamera, texels: m_texels, aspect: aspect);
            SdfFrameBlock.WriteWorkCounterRow(block: block, row: GpuKernelCounters.DisabledRow);
            SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: GpuKernelCounters.DisabledRow);
            m_blocks[kind][slot].Write<byte>(data: block);
            if (count == 0) { return; }

            var bound = m_meshSetBuffers[kind][slot];

            tables.WriteMeshTables(bound: bound, set: set, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: set);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: set);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: tables.DummyBuffer, member: SdfWorldPackage.GlassSumsWritten, set: set);
            SdfMeshOcclusion.Unbind(bound: m_meshSetOcclusion[kind][slot], boundTarget: ref m_meshSetTargets[kind][slot], first: 0, set: set, tables: tables);
            selection.Stage(tables: tables, count: count, width: (uint)m_texels, height: (uint)m_texels, slot: slot, cull: in cull, detail: in detail, held: tables.HoldsFrame,
                glass: false, occlude: false, splitExit: true, motion: motion, slice: share, slices: slices, tag: tag);
            selection.BindDraws(tables: tables, set: set, bound: m_meshSetOcclusion[kind][slot]);
            for (var phase = 0; (phase < OcclusionSets); phase++) {
                var at = OcclusionSetOf(kind: kind, slot: slot, phase: phase);
                var occlusionSet = m_occlusionSets[at][0];
                var occlusionBound = m_occlusionSetBuffers[at];

                tables.WriteMeshTables(bound: occlusionBound, set: occlusionSet, slot: tables.CurrentSlot);
                tables.WriteMeshWorkCounters(bound: ref occlusionBound[MeshSetCounters], counters: m_owner.m_counters, set: occlusionSet);
                tables.WriteMeshBuffer(bound: ref occlusionBound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: occlusionSet);
                tables.WriteMeshBuffer(bound: ref occlusionBound[MeshSetGlassSums], buffer: (((phase == 0) ? optical : null) ?? tables.DummyBuffer), member: SdfWorldPackage.GlassSumsWritten, set: occlusionSet);
                selection.Bind(bound: occlusionBound, boundTarget: ref m_occlusionSetTargets[at], first: MeshSetBuffers, set: occlusionSet, tables: tables, target: tables.SampledFiller.ImageViewHandle);
            }
        }
        // Clears an entry's glass extinction and draws every glass draw's into it against a pair's final depth, which the
        // glass fragment samples (its own attachment is the pair's exit layer, whose test the glass fragment's zero depth
        // always fails, so only its side effects land).
        private void Extinction(nint commandBuffer, SdfFrame frame, int slot, in SdfMeshShadowCamera camera, GpuPixelRect area, Pair pair, IGpuBuffer optical) {
            var tables = m_owner.m_tables;
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[OpticalKind];
            var glassSet = m_glassSets[slot];
            var bound = m_glassSetBuffers[slot];
            var full = !m_opticalInitialized;

            if (tables.MeshDrawCount == 0) { return; }
            Stage(frame: frame, slot: slot, kind: OpticalKind, blockCamera: in camera, aspect: 1f, detailCamera: in camera,
                cull: ((frame.MeshCull == SdfMeshCullLevel.Off) ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in camera)), motion: SdfMeshMotion.Any, share: 0, slices: 1, optical: optical, tag: 0L);
            selection.RecordEarly(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[OcclusionSetOf(kind: OpticalKind, slot: slot, phase: 0)][0]);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), sourceStageMask: (GpuStage.ComputeShader | GpuStage.FragmentShader),
                destinationAccessMask: (full ? GpuAccess.TransferWrite : (GpuAccess.ShaderRead | GpuAccess.ShaderWrite)), destinationStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader));
            if (full) {
                recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle, sizeBytes: optical.SizeBytes);
                m_opticalInitialized = true;
            } else {
                selection.RecordOpticalClear(recorder: recorder, commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline,
                    set: m_occlusionSets[OcclusionSetOf(kind: OpticalKind, slot: slot, phase: 0)][0], side: (uint)m_texels);
            }
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (full ? GpuAccess.TransferWrite : GpuAccess.ShaderWrite), sourceStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader),
                destinationAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));

            tables.WriteMeshTables(bound: bound, set: glassSet, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: optical, member: SdfWorldPackage.GlassSumsWritten, set: glassSet);
            SdfMeshOcclusion.Unbind(bound: m_glassSetOcclusion[slot], boundTarget: ref m_glassSetTargets[slot], first: 0, set: glassSet, tables: tables);
            tables.WriteMeshTarget(bound: ref m_glassSetTargets[slot], set: glassSet, view: pair.Depth.Image.ImageViewHandle);
            selection.BindDraws(tables: tables, set: glassSet, bound: m_glassSetOcclusion[slot]);
            Move(commandBuffer: commandBuffer, layer: pair.Depth, to: GpuImageLayout.ShaderReadOnly);
            Move(commandBuffer: commandBuffer, layer: pair.Exit, to: GpuImageLayout.DepthAttachment);
            // The exit layer's framebuffer that loads it: the pass clears nothing.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: pair.Exit.Load);
            Phase(commandBuffer: commandBuffer, phase: GlassPhase, pipeline: tables.ShadowGlassPipeline, selection: selection, set: glassSet);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));
        }
        private void Phase(nint commandBuffer, int phase, IGpuPipeline pipeline, SdfMeshOcclusion selection, nint set) {
            var recorder = m_owner.m_gpu.Recorder;

            recorder.BindPipeline(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: commandBuffer, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: commandBuffer, descriptorSetHandle: set,
                group: ((uint)ShaderInterfaceGroup.Pass), pipelineLayoutHandle: pipeline.LayoutHandle);
            selection.RecordDraws(commandBuffer: commandBuffer, phase: phase, pipeline: pipeline, recorder: recorder);
        }
        // Clears a layer through its framebuffer whose render pass clears its depth to the far plane, and leaves it
        // shader-readable.
        private void Clear(nint commandBuffer, Layer layer) {
            var recorder = m_owner.m_gpu.Recorder;

            Move(commandBuffer: commandBuffer, layer: layer, to: GpuImageLayout.DepthAttachment);
            recorder.BeginRenderPass(area: new GpuPixelRect(Height: layer.Image.Height, Width: layer.Image.Width, X: 0, Y: 0), commandBufferHandle: commandBuffer, framebuffer: layer.Clear);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            Rest(commandBuffer: commandBuffer, layer: layer);
        }
        // Leaves a layer shader-readable, the layout every view's submission samples it in.
        private void Rest(nint commandBuffer, Layer layer) => Move(commandBuffer: commandBuffer, layer: layer, to: GpuImageLayout.ShaderReadOnly);
        private void Move(nint commandBuffer, Layer layer, GpuImageLayout to) {
            if (layer.Layout == to) { return; }
            var (sourceAccess, sourceStage) = Use(layout: layer.Layout);
            var (destinationAccess, destinationStage) = Use(layout: to);

            m_owner.m_gpu.Recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: layer.Image.ImageHandle, oldLayout: layer.Layout, newLayout: to,
                sourceAccessMask: sourceAccess, sourceStageMask: sourceStage, destinationAccessMask: destinationAccess, destinationStageMask: destinationStage);
            layer.Layout = to;
        }
        // The accesses and stages a layout is used by here: a depth attachment by the raster's depth tests, a shader-readable
        // image by every shader stage that samples it (the glass fragment here, the views' compute passes after).
        private static (GpuAccess Access, GpuStage Stage) Use(GpuImageLayout layout) => layout switch {
            GpuImageLayout.DepthAttachment => ((GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite), GpuStage.FragmentTests),
            GpuImageLayout.ShaderReadOnly => (GpuAccess.ShaderRead, (GpuStage.FragmentShader | GpuStage.ComputeShader)),
            _ => (GpuAccess.None, GpuStage.TopOfPipe),
        };

        public void Submitted(int slot) {
            foreach (var selection in m_selections) { selection.Submitted(slot: slot); }
            if (!m_holds || !m_recorded) { return; }

            var counts = m_work.Counts;

            if (m_admitted && (m_redraw is { } redraw)) {
                counts?.Count(kind: (m_work.Completes ? m_kinds.Static : m_kinds.Share));
                if (m_work.Completes) {
                    // The lent pair takes the entry's place, and the entry's old pair goes to the spares.
                    if (redraw.Target is { } lent) {
                        if (m_front is { } replaced) { m_owner.Give(pair: replaced); }
                        m_front = lent;
                    }
                    m_redraw = null;
                    m_camera = m_work.Camera;
                    m_cell = m_work.Cell;
                    m_revision = m_work.Revision;
                    m_stands = true;
                    m_heldUploads = 0;
                    // The new layer answers every move before its redraw; what moved while it drew is owed now, and where
                    // it drew each caster is unknown until it moves again.
                    m_owed.Clear();
                    m_owed.Merge(other: m_pending, texels: m_texels);
                    m_pending.Clear();
                    m_owedOptical = false;
                    m_drawnGeneration++;
                } else {
                    m_redraw = redraw with { Next = (redraw.Next + 1) };
                }
            }
            if (m_work.Rectangles > 0) { counts?.Count(kind: m_kinds.Live); }
            if (!m_admitted && (m_work.Rectangles == 0)) {
                counts?.Count(kind: m_kinds.Held);
                m_heldUploads++;
            }
            m_taken.Clear();
            m_takenOptical = false;
            m_recorded = false;
        }
        public void Completed(int slot) {
            foreach (var selection in m_selections) { selection.Completed(slot: slot); }
        }
        // A whole drawing's measured vertices: a share of the redraw its tag names, out of the shares the tag holds, which
        // sets the entry's expected whole and, once every share of the redraw is read, its last whole.
        private void MeasuredWhole(long tag, ulong vertices) {
            var slices = (int)(tag & ((1L << SliceBits) - 1L));
            var serial = (tag >> SliceBits);

            if (slices <= 0) { return; }
            m_wholeVertices = (long)Math.Min(val1: (ulong)long.MaxValue / 2UL, val2: (vertices * (ulong)slices));
            m_owner.m_indexVertices[Index] = m_wholeVertices;
            if (serial != m_measuringSerial) {
                m_measuringSerial = serial;
                m_measuringVertices = 0L;
                m_measuringShares = 0;
            }
            m_measuringVertices += (long)vertices;
            if (++m_measuringShares == slices) { m_lastWholeVertices = m_measuringVertices; }
        }
        // A rectangle drawing's measured vertices, its tag the texels it covered.
        private void MeasuredRectangle(long tag, ulong vertices) {
            m_rectVertices += (long)vertices;
            m_rectArea += Math.Max(val1: tag, val2: 1L);
            m_rectDrawings++;
        }
        public string Describe() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"shadow.{EntryNames[Index]}=half{m_camera.HalfHeight:0.#}m/texels{m_texels}/stands{(m_stands ? 1 : 0)}/held{m_heldUploads}/redraw{(m_redraw is { } r ? $"{r.Next}of{r.Slices}{((r.Target is null) ? string.Empty : "lent")}" : "-")}/owed{(m_owed.All ? "all" : $"{m_owed.Count}:{m_owed.Area}texels")}/tri{m_lastWholeVertices / 3}in{m_slicesDrawn}/rect-tri{((m_rectDrawings > 0) ? ((m_rectVertices / m_rectDrawings) / 3) : 0)}x{m_rectDrawings}/ended-turn{m_endedByTurn}-cell{m_endedByCell}-static{m_endedByStatic}-fit{m_endedByFit}-moved{m_endedByMotion}{((Optical is not null) ? "/optical" : string.Empty)}");

        public void Dispose() {
            ReleaseLayers();
            ReleaseOptical();
            foreach (var selection in m_selections) { selection.Dispose(); }
            m_owner.m_gpu.Bindings.DestroyPool(poolHandle: m_pool);
            foreach (var blocks in m_blocks) {
                foreach (var block in blocks) { block?.Dispose(); }
            }
        }
    }
}
