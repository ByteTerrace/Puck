using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SdfVm;

// The distant shadow map (SdfFrame.DistantShadows, the cascade set's distant entry): a view holds the map it last drew
// until the light turns past the map's authored tolerance, the eye crosses into another cell of the map's grid, the map's
// reach, casters or texels change, the instance follows another view, a capture converges, or a static draw changes
// (SdfWorldTables.StaticDrawRevision). It draws only the static scene's draws (SdfMeshRecord.DynamicFlag), so nothing a
// held map stands for moves while it stands.
//
// The hold is the pass's signature: the camera a frame chooses (the committed one while it holds, the frame's own fit
// otherwise) with the static revision, the map's texels, the cull and detail levers and the instance's bindings. While
// none of them changes the signature does not, and the shadow-distant pass stands, whatever forces the frame's other
// passes.
//
// A map whose redraw is spread over several frames (SdfDistantShadows.Slices) draws it into its back map instead, one
// share of the casters a frame (the draws whose index leaves that share's remainder, SdfMeshOcclusion.Stage), every share
// from the camera the redraw started from, and copies the back map into the map receivers read once the last share is
// drawn. A redraw in progress runs to its end whatever the light or the eye does meanwhile; a static draw that changes
// during it leaves the committed map one revision behind, which the next frame's decision redraws again. A capture's
// convergence draws a redraw whole.
//
// The map is read through the camera it was drawn from. One door enforces it: the recorder asks RecordDistant, which stages
// the chosen camera for this frame's readers only when the frame's recording completes a map (a whole redraw, or a
// redraw's last share), and SubmittedDistant commits it once the frame's submission succeeds; a share that completes no map
// stages nothing for readers and advances the redraw only on submission. A pass that stands, skips or whose recording is
// abandoned commits nothing, and readers then read the committed camera, or no distant entry at all until a recording has
// committed one.
public sealed partial class SdfWorldPasses {
    // The distant entry readers read this frame: the camera this frame's recording drew a complete map from once the
    // recorder has asked (RecordDistant), otherwise the one the last submitted recording committed, which is empty before
    // any.
    private SdfMeshShadowCamera DistantCameraOf(Entry entry) =>
        (((entry.DistantPendingFrame == m_frame) && entry.DistantPending.Completes) ? entry.DistantPending.Camera : entry.DistantCamera);
    // Decides once a frame which camera the distant map is drawn from and whether this frame draws a share of a redraw:
    // the share of the redraw in progress; else the committed camera while every input of its fit but the light's slow turn
    // and the eye's step inside its cell is unchanged (and, for a map whose redraws are spread, the static scene it was
    // drawn at); else the frame's own fit, drawn whole or as the first share of a new redraw.
    private void DecideDistant(Entry entry, SdfFrame frame, int view, in SdfMeshShadowCascades cascades) {
        var fresh = cascades[SdfMeshShadowCascades.DistantEntry];
        var distant = frame.DistantShadows;
        var cell = distant.CellOf(eye: frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))].Camera.Position);
        var drawn = entry.DistantCamera;
        var converging = (entry.Convergence is { IsActive: true });
        var slices = (converging ? 1 : distant.Slices);
        var revision = (entry.View?.Residency.Tables?.StaticDrawRevision ?? 0L);

        entry.DistantDecided = m_frame;
        // A redraw in progress keeps the camera it started from until its last share is drawn, unless what it draws into
        // changed: the map's texels, the instance's bindings or how many shares a redraw takes.
        if ((entry.DistantRedraw is { } redraw) && ((redraw.Texels != cascades.DistantTexels) || (redraw.Bindings != entry.Bindings) || (redraw.Slices != slices))) {
            entry.DistantRedraw = null;
        }
        if (entry.DistantRedraw is { } running) {
            entry.DistantChosen = new SdfDistantDraw(Camera: running.Camera, Cell: running.Cell, Texels: running.Texels, Revision: running.Revision, Slice: running.Next, Slices: running.Slices);

            return;
        }

        var held = (
            ((frame.UpdateRates & SdfUpdateRates.ShadowDistant) != 0) &&
            (drawn.HalfHeight > 0f) &&
            (entry.DistantTexels == cascades.DistantTexels) &&
            (drawn.HalfHeight == fresh.HalfHeight) &&
            (drawn.Far == fresh.Far) &&
            (entry.DistantCell == cell) &&
            (entry.DistantBindings == entry.Bindings) &&
            // The chord between unit directions is their angle to well under a part in a million at these turns.
            (Vector3.Distance(value1: drawn.Forward, value2: fresh.Forward) <= distant.TurnRadians) &&
            !converging &&
            // A whole redraw follows a static change through the signature; a spread one must start a redraw for it.
            ((slices == 1) || (entry.DistantRevision == revision))
        );

        if (held || (slices == 1)) {
            entry.DistantChosen = new SdfDistantDraw(Camera: (held ? drawn : fresh), Cell: (held ? entry.DistantCell : cell), Texels: cascades.DistantTexels, Revision: revision, Slice: -1, Slices: 1);

            return;
        }

        entry.DistantRedraw = new DistantRedrawState(Camera: fresh, Cell: cell, Texels: cascades.DistantTexels, Bindings: entry.Bindings, Revision: revision, Slices: slices, Next: 0);
        entry.DistantChosen = new SdfDistantDraw(Camera: fresh, Cell: cell, Texels: cascades.DistantTexels, Revision: revision, Slice: 0, Slices: slices);
    }
    // The shadow-distant pass's signature: the camera this frame chose and every input the map's contents follow besides.
    // Every share of a redraw but its last adds its own index, so each records; the last share's signature is the one the
    // map then holds under, so the frames after it stand.
    private ulong DistantSignatureOf(Entry entry, SdfFrame frame, SdfWorldTables tables) {
        var chosen = entry.DistantChosen;
        var camera = chosen.Camera;
        var hash = Fnv1aHash.Create();

        hash.AddBits(value: camera.Position.X);
        hash.AddBits(value: camera.Position.Y);
        hash.AddBits(value: camera.Position.Z);
        hash.AddBits(value: camera.Forward.X);
        hash.AddBits(value: camera.Forward.Y);
        hash.AddBits(value: camera.Forward.Z);
        hash.AddBits(value: camera.HalfHeight);
        hash.AddBits(value: camera.Far);
        hash.Add(value: unchecked((uint)chosen.Texels));
        hash.Add(value: unchecked((ulong)tables.StaticDrawRevision));
        hash.Add(value: ((uint)frame.MeshCull));
        hash.Add(value: ((uint)frame.MeshDetail));
        hash.Add(value: unchecked((ulong)entry.Bindings));
        if (chosen.IsShare && !chosen.Completes) {
            hash.Add(value: unchecked((uint)(chosen.Slice + 1)));
        }

        return hash.Value;
    }
    /// <summary>Stages what the instance's distant map draws this frame, as its signature chose it: a whole map, or one
    /// share of a redraw into the back map. A drawing that completes a map stages its camera for this frame's readers
    /// (<see cref="CascadesOf"/>); <see cref="SubmittedDistant"/> commits it, or advances the redraw past a share.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="counts">The frame's rate counters, or <see langword="null"/>.</param>
    /// <param name="revision">The static revision the tables stage this frame (<see cref="SdfWorldTables.StaticDrawRevision"/>),
    /// which a whole map is drawn at.</param>
    /// <param name="hasBack">Whether the recording's graph holds the back map; one that does not yet (its graph is still
    /// building the fragment that holds it) draws a chosen share as a whole map instead.</param>
    /// <returns>The camera the map is drawn from and which share, if any, this frame draws.</returns>
    internal SdfDistantDraw RecordDistant(string instance, WorkCounterSet? counts, long revision, bool hasBack) {
        var entry = Refresh(instance: instance);
        var chosen = entry.DistantChosen;

        if (chosen.IsShare && !hasBack) {
            chosen = chosen with { Slice = -1, Slices = 1 };
            entry.DistantRedraw = null;
        }

        entry.DistantPendingFrame = m_frame;
        entry.DistantPending = (chosen.IsShare ? chosen : (chosen with { Revision = revision }));
        entry.DistantPendingCounts = counts;

        return chosen;
    }
    /// <summary>Commits what the instance's last distant map recording drew, once its frame's submission succeeded: the
    /// camera a complete map was drawn from, or a share of a redraw, which moves the redraw to its next share.</summary>
    /// <param name="instance">The instance's name.</param>
    internal void SubmittedDistant(string instance) {
        var entry = Refresh(instance: instance);

        if (entry.DistantPendingFrame < 0) {
            return;
        }

        var pending = entry.DistantPending;

        entry.DistantPendingFrame = -1;
        if (!pending.Completes) {
            entry.DistantPendingCounts?.Count(kind: SdfUpdateRateCounts.ShadowDistantShared);
            if (entry.DistantRedraw is { } redraw && (redraw.Next == pending.Slice)) {
                entry.DistantRedraw = redraw with { Next = (pending.Slice + 1) };
            }

            return;
        }

        entry.DistantPendingCounts?.Count(kind: SdfUpdateRateCounts.ShadowDistantRendered);
        entry.DistantCamera = pending.Camera;
        entry.DistantCell = pending.Cell;
        entry.DistantTexels = pending.Texels;
        entry.DistantRevision = pending.Revision;
        entry.DistantBindings = entry.Bindings;
        entry.DistantRedraw = null;
    }

    // A redraw spread over several frames: the camera, cell, texels, bindings and static revision it started from, its
    // share count and the share it draws next.
    private readonly record struct DistantRedrawState(SdfMeshShadowCamera Camera, Vector3 Cell, int Texels, long Bindings, long Revision, int Slices, int Next);

    private sealed partial class Entry {
        // The camera, cell, texels and static revision the distant map was drawn from and at, as the last submitted
        // recording committed them (SubmittedDistant), and the bindings it was drawn under.
        public SdfMeshShadowCamera DistantCamera { get; set; }
        public Vector3 DistantCell { get; set; }
        public int DistantTexels { get; set; }
        public long DistantRevision { get; set; } = -1;
        public long DistantBindings { get; set; } = -1;
        // The redraw in progress, or null for none.
        public DistantRedrawState? DistantRedraw { get; set; }
        // What this frame chose (DecideDistant) and what its recording drew, until its submission commits it.
        public long DistantDecided { get; set; } = -1;
        public SdfDistantDraw DistantChosen { get; set; }
        public long DistantPendingFrame { get; set; } = -1;
        public SdfDistantDraw DistantPending { get; set; }
        public WorkCounterSet? DistantPendingCounts { get; set; }
    }
}
/// <summary>What the distant shadow map's pass draws on a frame (<see cref="SdfWorldPasses"/>): the camera, cell, texels
/// and static revision it is drawn from and at, and the share of a redraw spread over several frames it draws.</summary>
/// <param name="Camera">The light camera the map is drawn from.</param>
/// <param name="Cell">The cell of the map's grid the camera is centred on.</param>
/// <param name="Texels">The texels along each side of the map's depth.</param>
/// <param name="Revision">The static revision the map is drawn at.</param>
/// <param name="Slice">The share this frame draws into the back map, from zero below <paramref name="Slices"/>, or -1 for a
/// map drawn whole into the map receivers read.</param>
/// <param name="Slices">The shares a redraw takes, one for a map drawn whole.</param>
internal readonly record struct SdfDistantDraw(SdfMeshShadowCamera Camera, Vector3 Cell, int Texels, long Revision, int Slice, int Slices) {
    /// <summary>Gets whether the frame draws a share of a redraw into the back map.</summary>
    public bool IsShare => (Slice >= 0);
    /// <summary>Gets whether the frame's drawing completes a map receivers then read: a map drawn whole, or a redraw's last
    /// share, after which the back map is copied into the map.</summary>
    public bool Completes => (!IsShare || (Slice == (Slices - 1)));
}
