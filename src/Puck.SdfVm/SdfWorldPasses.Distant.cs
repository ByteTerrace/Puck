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
// The map is read through the camera it was drawn from. One door enforces it: the recorder asks RecordDistant, which stages
// the chosen camera for this frame's readers, and SubmittedDistant commits it once the frame's submission succeeds. A pass
// that stands, skips or whose recording is abandoned commits nothing, and readers then read the committed camera, or no
// distant entry at all until a recording has committed one.
public sealed partial class SdfWorldPasses {
    // The distant entry readers read this frame: the camera this frame's recording drew from once the recorder has asked
    // (RecordDistant), otherwise the one the last submitted recording committed, which is empty before any.
    private SdfMeshShadowCamera DistantCameraOf(Entry entry) => ((entry.DistantPendingFrame == m_frame) ? entry.DistantPendingCamera : entry.DistantCamera);
    // Decides once a frame which camera the distant map stands for: the committed one while every input of its fit but
    // the light's slow turn and the eye's step inside its cell is unchanged, the frame's own fit otherwise.
    private void DecideDistant(Entry entry, SdfFrame frame, int view, in SdfMeshShadowCascades cascades) {
        var fresh = cascades[SdfMeshShadowCascades.DistantEntry];
        var distant = frame.DistantShadows;
        var cell = distant.CellOf(eye: frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))].Camera.Position);
        var drawn = entry.DistantCamera;
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
            (entry.Convergence is not { IsActive: true })
        );

        entry.DistantDecided = m_frame;
        entry.DistantChosen = (held ? drawn : fresh);
        entry.DistantChosenCell = (held ? entry.DistantCell : cell);
        entry.DistantChosenTexels = cascades.DistantTexels;
    }
    // The shadow-distant pass's signature: the camera this frame chose and every input the map's contents follow besides.
    private ulong DistantSignatureOf(Entry entry, SdfFrame frame, SdfWorldTables tables) {
        var camera = entry.DistantChosen;
        var hash = Fnv1aHash.Create();

        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Position.X));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Position.Y));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Position.Z));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Forward.X));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Forward.Y));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Forward.Z));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.HalfHeight));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: camera.Far));
        hash.Add(value: unchecked((uint)entry.DistantChosenTexels));
        hash.Add(value: unchecked((ulong)tables.StaticDrawRevision));
        hash.Add(value: ((uint)frame.MeshCull));
        hash.Add(value: ((uint)frame.MeshDetail));
        hash.Add(value: unchecked((ulong)entry.Bindings));

        return hash.Value;
    }
    /// <summary>Stages the camera the instance's distant map is drawn from this frame, the one its signature chose, for this
    /// frame's readers (<see cref="CascadesOf"/>); <see cref="SubmittedDistant"/> commits it.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="counts">The frame's rate counters, or <see langword="null"/>.</param>
    /// <returns>The camera the map is drawn from.</returns>
    internal SdfMeshShadowCamera RecordDistant(string instance, WorkCounterSet? counts) {
        var entry = Refresh(instance: instance);

        entry.DistantPendingFrame = m_frame;
        entry.DistantPendingCamera = entry.DistantChosen;
        entry.DistantPendingCell = entry.DistantChosenCell;
        entry.DistantPendingTexels = entry.DistantChosenTexels;
        entry.DistantPendingCounts = counts;

        return entry.DistantChosen;
    }
    /// <summary>Commits the camera the instance's last distant map recording drew from, once its frame's submission
    /// succeeded.</summary>
    /// <param name="instance">The instance's name.</param>
    internal void SubmittedDistant(string instance) {
        var entry = Refresh(instance: instance);

        if (entry.DistantPendingFrame < 0) {
            return;
        }

        entry.DistantPendingCounts?.Count(kind: SdfUpdateRateCounts.ShadowDistantRendered);
        entry.DistantCamera = entry.DistantPendingCamera;
        entry.DistantCell = entry.DistantPendingCell;
        entry.DistantTexels = entry.DistantPendingTexels;
        entry.DistantBindings = entry.Bindings;
        entry.DistantPendingFrame = -1;
    }

    private sealed partial class Entry {
        // The camera, cell and texels the distant map was drawn from and at, as the last submitted recording committed
        // them (SubmittedDistant), and the bindings it was drawn under.
        public SdfMeshShadowCamera DistantCamera { get; set; }
        public Vector3 DistantCell { get; set; }
        public int DistantTexels { get; set; }
        public long DistantBindings { get; set; } = -1;
        // What this frame chose (DecideDistant) and what its recording drew, until its submission commits it.
        public long DistantDecided { get; set; } = -1;
        public SdfMeshShadowCamera DistantChosen { get; set; }
        public Vector3 DistantChosenCell { get; set; }
        public int DistantChosenTexels { get; set; }
        public long DistantPendingFrame { get; set; } = -1;
        public SdfMeshShadowCamera DistantPendingCamera { get; set; }
        public Vector3 DistantPendingCell { get; set; }
        public int DistantPendingTexels { get; set; }
        public WorkCounterSet? DistantPendingCounts { get; set; }
    }
}
