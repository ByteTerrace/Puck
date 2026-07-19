using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>
/// The single place in World that turns an authored <see cref="WorldRig"/> into an engine <see cref="ISdfCameraRig"/>.
/// One switch with a loud default; the fold helpers bake a camera's <see cref="WorldCamera.Offset"/> into the compiled
/// rig so an unanchored camera poses at its offset and an anchored camera attaches at its anchor-local offset — the same
/// two shapes the binder (offscreen views) and the composer (main-window slots) both consume.
/// </summary>
internal static class WorldRigCompiler {
    /// <summary>Compiles an authored rig to a fresh engine rig instance. <see cref="WorldRig.Chase"/> distinguishes the
    /// two engine chase rigs by a bool: world axes → <see cref="FollowRig"/>, anchor-local → <see cref="OrientedFollowRig"/>.</summary>
    /// <param name="rig">The authored rig.</param>
    /// <returns>The engine rig it compiles to.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rig is an unknown kind (a closed switch's loud default).</exception>
    public static ISdfCameraRig Compile(WorldRig rig) => rig switch {
        WorldRig.Chase c => (c.WorldAxes
            ? new FollowRig { EyeOffset = c.EyeOffset, TargetOffset = c.TargetOffset, FovRadians = c.FieldOfViewRadians }
            : (ISdfCameraRig)new OrientedFollowRig { EyeOffset = c.EyeOffset, TargetOffset = c.TargetOffset, FovRadians = c.FieldOfViewRadians }),
        WorldRig.FirstPerson f => new FirstPersonRig { EyeOffset = f.EyeOffset, FocusDistance = f.FocusDistance, FovRadians = f.FieldOfViewRadians },
        WorldRig.Orbit o => new OrbitRig { Distance = o.Distance, Yaw = o.Yaw, Pitch = o.Pitch, FovRadians = o.FieldOfViewRadians },
        WorldRig.LookAt l => new FixedRig { Target = l.Target, FovRadians = l.FieldOfViewRadians },
        WorldRig.Dolly d => new DollyRig { Start = d.Start, End = d.End, DurationSeconds = d.DurationSeconds, PingPong = d.PingPong, FovRadians = d.FieldOfViewRadians },
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(rig), actualValue: rig, message: $"unknown WorldRig kind '{rig.GetType().Name}'."),
    };

    /// <summary>Bakes an UNANCHORED camera's world-space eye offset into a freshly compiled rig — the eye poses directly
    /// at <paramref name="worldOffset"/> (an unanchored camera's offset IS its world position). A <see cref="FixedRig"/>
    /// takes it as its eye; the anchor-following rigs take it as an addend to their offsets (their default anchor pose is
    /// the origin). <see cref="OrbitRig"/> orbits the origin and cannot be repositioned this way (a binder-only edge; the
    /// composer resolves orbit against a live pose).</summary>
    /// <param name="rig">The freshly compiled rig to bake into.</param>
    /// <param name="worldOffset">The camera's world eye position.</param>
    public static void BakeUnanchored(ISdfCameraRig rig, Vector3 worldOffset) {
        switch (rig) {
            case FixedRig fixedRig:
                fixedRig.Eye += worldOffset;

                break;
            case FirstPersonRig firstPerson:
                firstPerson.EyeOffset += worldOffset;

                break;
            case OrientedFollowRig oriented:
                oriented.EyeOffset += worldOffset;
                oriented.TargetOffset += worldOffset;

                break;
            case FollowRig follow:
                follow.EyeOffset += worldOffset;
                follow.TargetOffset += worldOffset;

                break;
            case DollyRig dolly:
                dolly.Start += worldOffset;
                dolly.End += worldOffset;

                break;
        }
    }

    /// <summary>Folds an ANCHORED camera's anchor-local offset into a freshly compiled rig — the attachment point on top
    /// of the anchor's resolved pose. A first-person/chase rig adds it to its own anchor-local offsets; a
    /// <see cref="FixedRig"/> takes it as a world addend; a <see cref="DollyRig"/> shifts its track. <see cref="OrbitRig"/>
    /// pivots on the anchor position and ignores the offset (a binder edge; the composer applies the pivot lift).</summary>
    /// <param name="rig">The freshly compiled rig to fold into.</param>
    /// <param name="anchorLocalOffset">The camera's anchor-local offset.</param>
    public static void FoldAnchorLocal(ISdfCameraRig rig, Vector3 anchorLocalOffset) {
        switch (rig) {
            case FirstPersonRig firstPerson:
                firstPerson.EyeOffset += anchorLocalOffset;

                break;
            case OrientedFollowRig oriented:
                oriented.EyeOffset += anchorLocalOffset;
                oriented.TargetOffset += anchorLocalOffset;

                break;
            case FollowRig follow:
                follow.EyeOffset += anchorLocalOffset;
                follow.TargetOffset += anchorLocalOffset;

                break;
            case FixedRig fixedRig:
                fixedRig.Eye += anchorLocalOffset;

                break;
            case DollyRig dolly:
                dolly.Start += anchorLocalOffset;
                dolly.End += anchorLocalOffset;

                break;
        }
    }
}

/// <summary>An <see cref="ISdfAnchorSource"/> that resolves one fixed pose for every id — the binding a placement- or
/// group-anchored offscreen camera view rides so its rig frames a computed world point without a live entity anchor.</summary>
internal sealed class FixedAnchorSource(SdfAnchor anchor) : ISdfAnchorSource {
    private SdfAnchor m_anchor = anchor;

    /// <summary>Repoints the fixed pose (a live re-resolve of a placement/group centroid).</summary>
    /// <param name="anchor">The new fixed pose.</param>
    public void Set(SdfAnchor anchor) => m_anchor = anchor;

    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        anchor = m_anchor;

        return true;
    }
}
