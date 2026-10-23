using System.Numerics;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.SdfVm;

// The far cascade's rate (SdfUpdateRates.ShadowFar): a view holds its far cascade's depth and fit while a refresh at
// least 30 times a second of presentation time allows (SdfUpdateRateTiers.Holds), so the shadow-far pass stands between
// refreshes whatever else of the frame renders, and every
// pass of the view reads that depth through the camera it was drawn from. A hold ends early when the fit it stands for
// no longer covers what the view shows: the light, the cascade's extent or texels change, the camera cuts, turns or
// moves past the tiers' tolerances, or a capture converges.
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

        return (entry.FarHeld ? cascades.WithFar(far: entry.FarCamera) : cascades);
    }
    // Whether the instance holds its far cascade this frame, deciding it when no pass has yet.
    private bool HoldsFar(string instance, SdfFrame frame, int view) {
        _ = CascadesOf(frame: frame, instance: instance, view: view);

        return Refresh(instance: instance).FarHeld;
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
        var held = (
            rated &&
            continuous &&
            SdfUpdateRateTiers.Holds(elapsedSeconds: elapsed, frameSeconds: m_context.FrameDeltaSeconds, hertz: SdfUpdateRateTiers.ShadowFarHertz, rate: PresentationRate.Of(context: in m_context)) &&
            (entry.FarTexels == cascades.Texels) &&
            (entry.FarCamera.Forward == fresh.Forward) &&
            (entry.FarCamera.HalfHeight == fresh.HalfHeight) &&
            (entry.FarCamera.Far == fresh.Far) &&
            (entry.FarCut == snapshot.CutRevision) &&
            (Vector3.Dot(vector1: camera.Forward, vector2: entry.FarForward) >= MathF.Cos(x: SdfUpdateRateTiers.ShadowFarTurnRadians)) &&
            (Vector3.DistanceSquared(value1: camera.Position, value2: entry.FarEye) <= (SdfUpdateRateTiers.ShadowFarMoveMetres * SdfUpdateRateTiers.ShadowFarMoveMetres)) &&
            (entry.Convergence is not { IsActive: true })
        );

        entry.FarDecided = m_frame;
        entry.FarHeld = held;
        if (held) {
            entry.FarElapsedSeconds = elapsed;
            frame.RateCounts?.Count(kind: SdfUpdateRateCounts.ShadowFarHeld);

            return;
        }
        if (rated && continuous) {
            frame.RateIntervals?.Record(hertz: SdfUpdateRateTiers.ShadowFarHertz, lever: SdfUpdateRates.ShadowFar, seconds: elapsed);
        }

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
        if (entry.FarHeld && (entry.FarSignature is { } held)) {
            return held;
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
        public SdfMeshShadowCamera FarCamera { get; set; }
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
