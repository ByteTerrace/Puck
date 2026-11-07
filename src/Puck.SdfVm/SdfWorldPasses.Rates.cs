using System.Numerics;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.SdfVm;

// The far cascade's rate (SdfUpdateRates.ShadowFar): a view holds its far cascade's depth and fit while a refresh at
// least 30 times a second of presentation time allows (SdfUpdateRateTiers.Holds), so the shadow-far pass stands between
// refreshes whatever else of the frame renders, and every
// pass of the view reads that depth through the camera it was drawn from. A hold ends early when the fit it stands for
// no longer covers what the view shows: the light turns or the camera turns or moves past the tiers' tolerances, the
// cascade's extent or texels change, the camera cuts, or a capture converges. A sun on a day clock turns every frame, by
// far less than its tolerance over a 30th of a second, so it holds as a still light does.
//
// A refresh the rate alone calls for (the fit still covers the view) draws the cascade's two layers on consecutive frames,
// the depth first and the exit layer next, which holds the depth (SdfFarLayers): a 60 Hz view pays half a refresh every
// frame rather than a whole one every other frame, each layer still refreshing 30 times a second, and a slower view never
// pays a whole refresh on every frame, the cost that kept it slow, at the price of a depth interval one frame longer. The exit
// layer's camera then trails the depth's for one frame, and the surface pass reads the exit layer through that camera
// (the cascades' fourth). Which camera each layer holds is committed only where the recorder draws that layer
// (RecordFar), so a pass that stands never moves a camera.
public sealed partial class SdfWorldPasses {
    /// <summary>Returns the cascades a pass of an instance draws and reads this frame: the frame's own fit, its far
    /// cascade held from the frame that last refreshed it while the frame rates it.</summary>
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

        if ((entry.FarDecided != m_frame) || (entry.FarHeld && (entry.FarTexels != cascades.Texels))) {
            DecideFar(cascades: in cascades, entry: entry, frame: frame, view: view);
        }

        // Until the pass first draws the exit layer it holds no camera, and reads through the depth's.
        return cascades.WithFar(far: entry.FarCamera, exit: ((entry.FarExitCamera.HalfHeight > 0f) ? entry.FarExitCamera : entry.FarCamera));
    }
    // Whether the instance's far cascade pass stands this frame, deciding it when no pass has yet: held, with no layer owed.
    private bool HoldsFar(string instance, SdfFrame frame, int view) {
        _ = CascadesOf(frame: frame, instance: instance, view: view);

        return (Refresh(instance: instance).FarLayers == SdfFarLayers.None);
    }
    /// <summary>Returns the far cascade layers the shadow-far pass draws this frame, and commits the camera each drawn
    /// layer now holds: the one door through which a layer's camera changes, so the surface pass, recorded after it,
    /// reads each layer through the camera it was drawn from.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The layers to draw: both unless a split refresh draws one of them.</returns>
    internal SdfFarLayers RecordFar(string instance) {
        var entry = Refresh(instance: instance);
        var layers = ((entry.FarLayers == SdfFarLayers.None) ? SdfFarLayers.Both : entry.FarLayers);

        entry.FarExitOwed = (layers == SdfFarLayers.Depth);
        if (layers != SdfFarLayers.Depth) {
            entry.FarExitCamera = entry.FarCamera;
        }

        return layers;
    }
    // Decides once a frame whether the far cascade holds, and latches the fresh fit when it refreshes.
    private void DecideFar(Entry entry, SdfFrame frame, int view, in SdfMeshShadowCascades cascades) {
        var fresh = cascades[1];
        var snapshot = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))];
        var camera = snapshot.Camera;
        var rated = ((frame.UpdateRates & SdfUpdateRates.ShadowFar) != 0);
        // Every frame since the refresh decided, so the time since it is this view's presentation time.
        var continuous = (entry.FarDecided == (m_frame - 1));
        var elapsed = (continuous ? (entry.FarElapsedSeconds + m_context.FrameDeltaSeconds) : 0d);
        var rate = PresentationRate.Of(context: in m_context);
        // The fit still covers what the view shows: only the rate can call for a refresh.
        var covers = (
            rated &&
            continuous &&
            (entry.FarTexels == cascades.Texels) &&
            (Vector3.Dot(vector1: entry.FarCamera.Forward, vector2: fresh.Forward) >= MathF.Cos(x: SdfUpdateRateTiers.ShadowFarLightTurnRadians)) &&
            (entry.FarCamera.HalfHeight == fresh.HalfHeight) &&
            (entry.FarCamera.Far == fresh.Far) &&
            (entry.FarCut == snapshot.CutRevision) &&
            (Vector3.Dot(vector1: camera.Forward, vector2: entry.FarForward) >= MathF.Cos(x: SdfUpdateRateTiers.ShadowFarTurnRadians)) &&
            (Vector3.DistanceSquared(value1: camera.Position, value2: entry.FarEye) <= (SdfUpdateRateTiers.ShadowFarMoveMetres * SdfUpdateRateTiers.ShadowFarMoveMetres)) &&
            (entry.Convergence is not { IsActive: true })
        );
        // A split refresh's exit layer is drawn on the frame after its depth, which holds the depth whatever the rate: a
        // frame never pays for both layers of one refresh.
        var held = (covers && (entry.FarExitOwed || SdfUpdateRateTiers.Holds(elapsedSeconds: elapsed, frameSeconds: m_context.FrameDeltaSeconds, hertz: SdfUpdateRateTiers.ShadowFarHertz, rate: rate)));

        entry.FarDecided = m_frame;
        entry.FarHeld = held;
        if (held) {
            entry.FarElapsedSeconds = elapsed;
            entry.FarLayers = (entry.FarExitOwed ? SdfFarLayers.Exit : SdfFarLayers.None);
            if (entry.FarLayers == SdfFarLayers.None) {
                frame.RateCounts?.Count(kind: SdfUpdateRateCounts.ShadowFarHeld);
            }

            return;
        }
        if (rated && continuous) {
            frame.RateIntervals?.Record(hertz: SdfUpdateRateTiers.ShadowFarHertz, lever: SdfUpdateRates.ShadowFar, seconds: elapsed);
        }

        // The rate alone calls for this refresh, the exit layer has a camera of its own to be read through until the next
        // frame redraws it, and a frame fits in the rate's period: draw the depth now and the exit layer then. Anything
        // else redraws both.
        entry.FarLayers = ((
            covers &&
            (entry.FarExitCamera.HalfHeight > 0f) &&
            SdfUpdateRateTiers.Holds(elapsedSeconds: 0d, frameSeconds: m_context.FrameDeltaSeconds, hertz: SdfUpdateRateTiers.ShadowFarHertz, rate: rate)
        ) ? SdfFarLayers.Depth : SdfFarLayers.Both);
        entry.FarElapsedSeconds = 0d;
        entry.FarCamera = fresh;
        entry.FarTexels = cascades.Texels;
        entry.FarEye = camera.Position;
        entry.FarForward = camera.Forward;
        entry.FarCut = snapshot.CutRevision;
        entry.FarRefreshFrame = m_frame;
        entry.FarSignature = null;
    }
    // The shadow-far pass's signature: the one its refresh recorded while the cascade holds, so the pass stands; on a
    // refresh, the pass's own, or one of this frame alone when the frame forces every pass, so it records and the holds
    // that follow stand for it.
    private ulong? FarSignatureOf(Entry entry, ulong? signature) {
        if ((entry.FarLayers == SdfFarLayers.None) && entry.FarHeld && (entry.FarSignature is { } held)) {
            return held;
        }
        // A split refresh's exit frame records whatever the scene's signature: its depth recorded under the last one.
        if (entry.FarLayers == SdfFarLayers.Exit) {
            var exit = Fnv1aHash.Create();

            exit.Add(value: entry.FarSignature ?? 0UL);
            exit.Add(value: unchecked((ulong)entry.FarRefreshFrame));
            exit.Add(value: ((ulong)SdfFarLayers.Exit));
            entry.FarSignature = exit.Value;

            return exit.Value;
        }

        if (signature is null) {
            var hash = Fnv1aHash.Create();

            hash.Add(value: unchecked((ulong)m_frame));
            hash.Add(value: unchecked((ulong)entry.FarRefreshFrame));
            signature = hash.Value;
        }

        entry.FarSignature = signature;

        return signature;
    }

    private sealed partial class Entry {
        // The camera the far depth was drawn from (latched by each refresh), and the one the exit layer was drawn from.
        public SdfMeshShadowCamera FarCamera { get; set; }
        public SdfMeshShadowCamera FarExitCamera { get; set; }
        // Whether a split refresh drew the depth and still owes the exit layer, and which layers this frame draws.
        public bool FarExitOwed { get; set; }
        public SdfFarLayers FarLayers { get; set; }
        public Vector3 FarEye { get; set; }
        public Vector3 FarForward { get; set; }
        public long FarCut { get; set; }
        public long FarDecided { get; set; } = -1;
        // The presentation time from the frame that last refreshed the far cascade to the latest frame that decided it.
        public double FarElapsedSeconds { get; set; }
        public bool FarHeld { get; set; }
        public long FarRefreshFrame { get; set; } = -1;
        public ulong? FarSignature { get; set; }
        public int FarTexels { get; set; }
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
