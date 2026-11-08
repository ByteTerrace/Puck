using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.SdfVm;

// The far cascade's rate (SdfUpdateRates.ShadowFar): a view holds its layers and fit while a refresh at
// least 30 times a second of presentation time allows (SdfUpdateRateTiers.Holds), so the shadow-far pass stands between
// refreshes whatever else of the frame renders. A hold ends early when the fit it stands for no longer covers what the
// view shows: the light turns by more than the far camera's depth span moves a shadow one texel, the camera turns or
// moves past the tiers' tolerances, the cascade's extent or texels change, the camera cuts, the instance follows another
// view or residency, or a capture converges. A sun on a day clock turns every frame, by less than that in a 30th of a
// second, so it holds as a still light does. Receiver-conditioned occlusion additionally requires both retained layers'
// receiver coverage identities to match: newly exposed receivers cannot use a mask from a previous view.
//
// A refresh the rate alone calls for (the fit still covers the view) draws the cascade's two layers on consecutive frames:
// the depth (with its glass extinction) first, then the exit layer on a frame that holds the depth (SdfFarLayers). A frame
// so never draws more than one far layer, whatever its pace: a 60 Hz view pays half a refresh every frame and each layer
// still refreshes 30 times a second; a slower view also draws one layer a frame, each layer refreshing every two frames,
// so the cascade never costs a slow frame a whole refresh and holds it slow. A refresh that ends a hold redraws both. The
// exit layer's camera trails the depth's for the one frame between, and the surface pass reads the exit layer through
// that camera (the cascades' fourth).
//
// Each layer is read through the camera it was drawn from. One door enforces it: the recorder asks RecordFar which layers
// it draws, which stages the cameras those layers are drawn from for this frame's readers, and SubmittedFar commits them
// once the frame's submission succeeds. A pass that stands, skips or whose recording is abandoned commits nothing, and a
// layer the recorder has not drawn in its current graph is always drawn (RecordFar answers both).
public sealed partial class SdfWorldPasses {
    // A layer's receiver coverage names the sample grid it classified the casters against: the cull level, the detail
    // choice, the render extent and scale, and the view's cut and bindings. The light is absent (readers use that layer's
    // committed light camera), and so are the geometry, the camera and the jitter: the hold already bounds how far the
    // camera turns and moves while a layer stands (SdfUpdateRateTiers.ShadowFarTurnRadians, ShadowFarMoveMetres), the
    // receiver cells (16x16 cascade texels, queried with a neighbouring cell each way, sdf-caster-receivers.hlsli) cover
    // that drift as they cover a sub-pixel jitter, and the receivers a moving caster uncovers lag by the same refresh its
    // shadow does. The mesh pass's geometry signature holds the camera and every pose, so hashing it here would end the
    // hold on every frame anything moves.
    internal ulong? ReceiverSignatureOf(string instance) => ReceiverSignatureOf(Refresh(instance));
    private static ulong? ReceiverSignatureOf(Entry entry) {
        if (entry.View is not { } view || view.Residency.Frame is not { } frame) { return null; }
        if (frame.MeshCull != SdfMeshCullLevel.Occlusion) { return 0UL; }
        var index = Math.Min(view.View, frame.Views.Count - 1);
        var hash = Fnv1aHash.Create();
        hash.Add(unchecked((ulong)entry.Bindings));
        hash.Add(unchecked((ulong)frame.Views[index].CutRevision));
        hash.Add((uint)frame.MeshDetail);
        hash.Add(entry.Temporal.Epoch.Width);
        hash.Add(entry.Temporal.Epoch.Height);
        hash.Add(BitConverter.DoubleToUInt64Bits(entry.CurrentScale));
        return hash.Value;
    }
    /// <summary>Returns the cascades a pass of an instance draws and reads this frame: the frame's own fit, its far
    /// cascade's layers through the cameras they were drawn from.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="frame">The frame its residency holds.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>, clamped to the frame's views.</param>
    /// <param name="texels">The texels along each side of the cascade depths the pass binds, or <see langword="null"/>
    /// for the view's quality.</param>
    /// <returns>The cascades.</returns>
    internal SdfMeshShadowCascades CascadesOf(string instance, SdfFrame frame, int view, int? texels = null) {
        var cascades = SdfMeshShadowCascades.Fit(frame: frame, texels: texels, view: view);

        if (cascades.Count == 0) {
            return cascades;
        }

        var entry = Refresh(instance: instance);

        if (entry.FarDecided != m_frame) {
            DecideFar(cascades: in cascades, entry: entry, frame: frame, view: view);
        }

        var (depth, exit) = FarCamerasOf(entry: entry);

        // A layer no frame has drawn yet reads through the frame's own fit.
        depth = ((depth.HalfHeight > 0f) ? depth : cascades[1]);

        return cascades.WithFar(far: depth, exit: ((exit.HalfHeight > 0f) ? exit : depth));
    }
    // The cameras the far layers hold for this frame's readers: those this frame's recording drew from once the recorder
    // has asked (RecordFar), otherwise those the last submitted recording committed.
    private (SdfMeshShadowCamera Depth, SdfMeshShadowCamera Exit) FarCamerasOf(Entry entry) {
        if (entry.FarPendingFrame != m_frame) {
            return (entry.FarCamera, entry.FarExitCamera);
        }

        return entry.FarPendingLayers switch {
            SdfFarLayers.Depth => (entry.FarPendingCamera, entry.FarExitCamera),
            SdfFarLayers.Exit => (entry.FarCamera, entry.FarCamera),
            _ => (entry.FarPendingCamera, entry.FarPendingCamera),
        };
    }
    // Whether the instance's far cascade pass stands this frame, deciding it when no pass has yet: held, with no layer owed.
    private bool HoldsFar(string instance, SdfFrame frame, int view) {
        _ = CascadesOf(frame: frame, instance: instance, view: view);

        return (Refresh(instance: instance).FarLayers == SdfFarLayers.None);
    }
    /// <summary>Returns the far cascade layers the shadow-far pass draws this frame and stages the cameras they are drawn
    /// from for this frame's readers (<see cref="CascadesOf"/>); <see cref="SubmittedFar"/> commits them.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="fresh">The far camera fitted to the cascade depth the pass binds, which a drawn depth is drawn from.</param>
    /// <param name="written">Whether the recorder has drawn both layers of the target it binds in its current graph: an
    /// unwritten layer has no contents, so a recording draws both.</param>
    /// <param name="counts">The frame's rate counters, or <see langword="null"/>.</param>
    /// <returns>The layers to draw: both unless a split refresh draws one of them.</returns>
    internal SdfFarLayers RecordFar(string instance, in SdfMeshShadowCamera fresh, bool written, WorkCounterSet? counts) {
        var entry = Refresh(instance: instance);
        var layers = (((entry.FarLayers == SdfFarLayers.None) || !written) ? SdfFarLayers.Both : entry.FarLayers);

        entry.FarPendingFrame = m_frame;
        entry.FarPendingLayers = layers;
        entry.FarPendingReceivers = ReceiverSignatureOf(entry);
        entry.FarPendingCounts = counts;
        // An exit layer alone is drawn from the camera its depth was drawn from.
        entry.FarPendingCamera = ((layers == SdfFarLayers.Exit) ? entry.FarCamera : fresh);

        return layers;
    }
    /// <summary>Commits the cameras the instance's last far cascade recording drew its layers from, once its frame's
    /// submission succeeded.</summary>
    /// <param name="instance">The instance's name.</param>
    internal void SubmittedFar(string instance) {
        var entry = Refresh(instance: instance);

        if (entry.FarPendingFrame < 0) {
            return;
        }

        var camera = entry.FarPendingCamera;
        entry.FarPendingCounts?.Count(kind: entry.FarPendingLayers == SdfFarLayers.Both
            ? SdfUpdateRateCounts.ShadowFarRendered : SdfUpdateRateCounts.ShadowFarSplit);

        switch (entry.FarPendingLayers) {
            case SdfFarLayers.Depth:
                entry.FarCamera = camera;
                entry.FarDepthReceivers = entry.FarPendingReceivers;
                entry.FarExitOwed = true;
                break;
            case SdfFarLayers.Exit:
                entry.FarExitCamera = camera;
                entry.FarExitReceivers = entry.FarPendingReceivers;
                entry.FarExitOwed = false;
                break;
            default:
                entry.FarCamera = camera;
                entry.FarExitCamera = camera;
                entry.FarDepthReceivers = entry.FarPendingReceivers;
                entry.FarExitReceivers = entry.FarPendingReceivers;
                entry.FarExitOwed = false;
                break;
        }

        entry.FarPendingFrame = -1;
    }
    // Decides once a frame whether the far cascade holds, and latches the fit a refresh stands for.
    private void DecideFar(Entry entry, SdfFrame frame, int view, in SdfMeshShadowCascades cascades) {
        var fresh = cascades[1];
        var snapshot = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))];
        var camera = snapshot.Camera;
        var rated = ((frame.UpdateRates & SdfUpdateRates.ShadowFar) != 0);
        var receivers = ReceiverSignatureOf(entry: entry);
        // Every frame since the refresh decided, so the time since it is this view's presentation time.
        var continuous = (entry.FarDecided == (m_frame - 1));
        var elapsed = (continuous ? (entry.FarElapsedSeconds + m_context.FrameDeltaSeconds) : 0d);
        var rate = PresentationRate.Of(context: in m_context);
        var drawn = entry.FarCamera;
        // The fit still covers what the view shows: only the rate can call for a refresh.
        var covers = (
            rated &&
            continuous &&
            (drawn.HalfHeight > 0f) &&
            (entry.FarTexels == cascades.Texels) &&
            // The chord between unit directions is their angle to well under a part in a million at these turns, where a
            // float cosine would round to one.
            (Vector3.Distance(value1: drawn.Forward, value2: fresh.Forward) <= SdfUpdateRateTiers.ShadowFarLightTurnRadians(camera: in fresh, texels: cascades.Texels)) &&
            (drawn.HalfHeight == fresh.HalfHeight) &&
            (drawn.Far == fresh.Far) &&
            (entry.FarCut == snapshot.CutRevision) &&
            (entry.FarBindings == entry.Bindings) &&
            (frame.MeshCull != SdfMeshCullLevel.Occlusion || (receivers is not null &&
                entry.FarDepthReceivers == receivers && entry.FarExitReceivers == receivers)) &&
            (Vector3.Dot(vector1: camera.Forward, vector2: entry.FarForward) >= MathF.Cos(x: SdfUpdateRateTiers.ShadowFarTurnRadians)) &&
            (Vector3.DistanceSquared(value1: camera.Position, value2: entry.FarEye) <= (SdfUpdateRateTiers.ShadowFarMoveMetres * SdfUpdateRateTiers.ShadowFarMoveMetres)) &&
            (entry.Convergence is not { IsActive: true })
        );
        // A split refresh's exit layer is drawn on the frame after its depth, which holds the depth.
        var held = (covers && (entry.FarExitOwed || SdfUpdateRateTiers.Holds(elapsedSeconds: elapsed, frameSeconds: m_context.FrameDeltaSeconds, hertz: SdfUpdateRateTiers.ShadowFarHertz, rate: rate)));

        entry.FarDecided = m_frame;
        entry.FarHeld = held;
        if (held) {
            entry.FarElapsedSeconds = elapsed;
            entry.FarLayers = (entry.FarExitOwed ? SdfFarLayers.Exit : SdfFarLayers.None);

            return;
        }
        if (rated && continuous) {
            frame.RateIntervals?.Record(hertz: SdfUpdateRateTiers.ShadowFarHertz, lever: SdfUpdateRates.ShadowFar, seconds: elapsed);
        }

        // The rate alone calls for this refresh and the exit layer has a camera to be read through until the next frame
        // redraws it: draw the depth now and the exit layer then, whatever the frames' pace, so no frame draws both layers
        // for the rate's sake. Anything else redraws both.
        entry.FarLayers = ((covers && (entry.FarExitCamera.HalfHeight > 0f)) ? SdfFarLayers.Depth : SdfFarLayers.Both);
        entry.FarElapsedSeconds = 0d;
        entry.FarTexels = cascades.Texels;
        entry.FarEye = camera.Position;
        entry.FarForward = camera.Forward;
        entry.FarCut = snapshot.CutRevision;
        entry.FarBindings = entry.Bindings;
        entry.FarRefreshFrame = m_frame;
    }
    // The shadow-far pass's signature. While the cascade holds, the one its last recording returned, so the pass stands.
    // On a split refresh's exit frame, one of that frame alone, so it records. On a refresh, the pass's own, or one of this
    // frame alone when the frame forces every pass; a refresh whose signature is the one both layers were last drawn under
    // returns the recorded signature instead and draws nothing, since the cascade drawn then still stands.
    private ulong? FarSignatureOf(Entry entry, ulong? signature) {
        if ((entry.FarLayers == SdfFarLayers.None) && entry.FarHeld && (entry.FarSignature is { } held)) {
            return held;
        }
        if (entry.FarLayers == SdfFarLayers.Exit) {
            var exit = Fnv1aHash.Create();

            exit.Add(value: entry.FarSignature ?? 0UL);
            exit.Add(value: unchecked((ulong)entry.FarRefreshFrame));
            exit.Add(value: ((ulong)SdfFarLayers.Exit));
            entry.FarExitSceneSignature = signature;
            entry.FarSignature = exit.Value;

            return exit.Value;
        }
        if (
            (signature is { } scene) &&
            !entry.FarExitOwed &&
            (entry.FarSignature is { } recorded) &&
            (entry.FarDepthSignature == scene) &&
            (entry.FarExitSceneSignature == scene)
        ) {
            entry.FarLayers = SdfFarLayers.None;

            return recorded;
        }
        if (signature is null) {
            var hash = Fnv1aHash.Create();

            hash.Add(value: unchecked((ulong)m_frame));
            hash.Add(value: unchecked((ulong)entry.FarRefreshFrame));
            signature = hash.Value;
        }

        entry.FarDepthSignature = signature;
        // A whole refresh draws the exit layer under the same signature as the depth.
        entry.FarExitSceneSignature = ((entry.FarLayers == SdfFarLayers.Both) ? signature : null);
        entry.FarSignature = signature;

        return signature;
    }

    private sealed partial class Entry {
        // The cameras the far depth and the far exit layer were drawn from, as the last submitted recording committed
        // them (SubmittedFar), and whether a split refresh drew the depth and still owes the exit layer.
        public SdfMeshShadowCamera FarCamera { get; set; }
        public SdfMeshShadowCamera FarExitCamera { get; set; }
        public bool FarExitOwed { get; set; }
        // What this frame's recording drew and from which camera, until its submission commits it (RecordFar).
        public long FarPendingFrame { get; set; } = -1;
        public SdfFarLayers FarPendingLayers { get; set; }
        public SdfMeshShadowCamera FarPendingCamera { get; set; }
        // The layers this frame draws, and the fit the last refresh stands for.
        public SdfFarLayers FarLayers { get; set; }
        public Vector3 FarEye { get; set; }
        public Vector3 FarForward { get; set; }
        public long FarCut { get; set; }
        public long FarBindings { get; set; } = -1;
        public long FarDecided { get; set; } = -1;
        // The presentation time from the frame that last refreshed the far cascade to the latest frame that decided it.
        public double FarElapsedSeconds { get; set; }
        public bool FarHeld { get; set; }
        public long FarRefreshFrame { get; set; } = -1;
        // The signature the pass last returned, and the scene signatures its depth and exit layer were last drawn under.
        public ulong? FarSignature { get; set; }
        public ulong? FarDepthSignature { get; set; }
        public ulong? FarExitSceneSignature { get; set; }
        public int FarTexels { get; set; }
        public ulong? FarDepthReceivers { get; set; }
        public ulong? FarExitReceivers { get; set; }
        public ulong? FarPendingReceivers { get; set; }
        public WorkCounterSet? FarPendingCounts { get; set; }
    }
}
/// <summary>Which of the far shadow cascade's two layers its pass draws on a frame (<see cref="SdfWorldPasses"/>).</summary>
[Flags]
internal enum SdfFarLayers : byte {
    /// <summary>Neither: the cascade holds.</summary>
    None = 0,
    /// <summary>The depth alone: the first frame of a split refresh.</summary>
    Depth = 1,
    /// <summary>The exit layer alone: the second frame of a split refresh.</summary>
    Exit = 2,
    /// <summary>Both layers: a whole refresh.</summary>
    Both = 3,
}
