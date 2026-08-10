using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// The traveler-follow stage-1 render fill's shared geometry: four reserved, always-emitted, static screen-surface
/// indices at the top of the engine's surface ceiling (<see cref="Base"/>..<see cref="Base"/>+<see cref="Count"/>-1,
/// disjoint from the document's own authored screens and <c>WorldCreationFacets</c>' derived-face band by
/// construction — see <see cref="WorldSceneEmitter.WithAuthoringHeadroom"/>'s own exclusion), one per local seat —
/// and the fixed "periscope" camera pose that frames each one edge-to-edge. <see cref="WorldFrameSource.Dress"/>'s
/// away-seat branch points that seat's own viewport camera at its own periscope instead of the ordinary chase
/// camera, and <see cref="WorldSceneEmitter"/> binds that seat's reserved screen index to the away view's resolved
/// image — the same textured-quad technique <c>WorldScreenBinder.RegisterSessionView</c> already composites a
/// jumbotron with, aimed at a seat's own layout region instead of a wall face.
/// </summary>
/// <remarks>
/// <para><b>Why a fixed periscope, not a moving quad.</b> The shared program every viewport raymarches is identical
/// across all of one produced frame's views — <see cref="SdfFrame"/> carries exactly one <see cref="SdfProgram"/>
/// for every <see cref="SdfViewSnapshot"/> in it — so a per-seat "camera into a foreign world" cannot exist; only a
/// sampled image bound as a screen source can show foreign content, and a screen source samples only a real
/// <c>ScreenSlab</c> surface in the shared program. Repositioning that surface every frame to chase a moving seat
/// camera would need a dynamic-transform slot, per-seat aspect-matched resizing, and careful capacity accounting
/// for a payoff (a wall-mounted TV) this stage does not need: since nothing else ever points a camera at these
/// four reserved quads (an ordinary seat's chase camera never wanders into the parked band below), a fixed
/// quad-and-camera pair, oversized so the quad fills frame at any aspect with wide margin, is exact for this
/// purpose and needs no per-frame geometry update at all — pure static content, exactly like <c>HiddenAvatar</c>'s
/// own parking convention.</para>
/// </remarks>
internal static class WorldAwaySeatQuad {
    /// <summary>The first reserved screen index — the top <see cref="Count"/> of <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>.
    /// Single-sourced from <see cref="WorldPlacementPolicy"/>: document validation rejects authored indices in this
    /// band, caps the derived-face range below it, and proves requested headroom still fits beside it.</summary>
    internal const int Base = WorldPlacementPolicy.AwaySeatScreenBase;

    /// <summary>One reserved quad per local seat.</summary>
    internal const int Count = WorldPlacementPolicy.AwaySeatScreenCount;

    private const float Spacing = 30f;
    // NOT HiddenAvatar's Y (-1000): a camera's own EYE must sit in the engine's normal render extent (the uniform
    // instance grid / far-bound tuning assume ordinary scene scale), unlike a parked AVATAR instance, which only
    // needs to be unseen, never itself the origin of a march. A modest offset from the play area's own origin (well
    // past any seat's ordinary wander radius, but still inside normal scale) keeps the periscope's own march sane.
    private const float ParkY = 2f;
    private const float ParkBase = 200f;
    private const float CameraDistance = 3f;
    private const float HalfExtent = 8f;
    private const float FieldOfViewRadians = (MathF.PI * (70f / 180f));

    private static readonly Vector3 Right = Vector3.UnitX;
    private static readonly Vector3 Up = Vector3.UnitY;
    private static readonly Vector3 Normal = Vector3.UnitZ;

    /// <summary>Whether <paramref name="index"/> falls in the reserved away-seat quad band.</summary>
    internal static bool IsReservedIndex(int index) => ((index >= Base) && (index < (Base + Count)));

    /// <summary>The reserved screen index for local seat <paramref name="slot"/>.</summary>
    internal static int IndexForSeat(int slot) => (Base + slot);

    /// <summary>The reserved quad's front-face world position for seat <paramref name="slot"/> — parked well below
    /// the floor (<c>WorldSceneEmitter.HiddenAvatar</c>'s own Y) and spread along X so the four never overlap.</summary>
    internal static Vector3 Origin(int slot) => new(x: (ParkBase + (slot * Spacing)), y: ParkY, z: ParkBase);

    /// <summary>Emits seat <paramref name="slot"/>'s reserved quad's static geometry — a big flat, oversized
    /// <c>ScreenSlab</c> so its edges sit well outside any reasonable viewport aspect at the periscope's close
    /// framing distance. Always emitted (probe and live alike): fixed content, no probe branch needed.</summary>
    internal static void EmitQuad(SdfProgramBuilder builder, int slot) {
        var origin = Origin(slot: slot);
        var center = (origin - (Normal * 0.5f));

        _ = builder
            .Translate(offset: center)
            .ScreenSlab(
                halfExtents: new Vector3(x: HalfExtent, y: HalfExtent, z: 0.5f),
                round: 0f,
                worldOrigin: origin,
                worldRight: Right,
                worldUp: Up,
                screenIndex: IndexForSeat(slot: slot)
            )
            .ResetPoint();
    }

    /// <summary>The fixed camera pose that frames seat <paramref name="slot"/>'s own reserved quad edge-to-edge —
    /// close and wide enough that the oversized quad fills the frame at any viewport aspect this stage's layouts
    /// produce, with no per-frame repositioning.</summary>
    internal static CameraSnapshot PeriscopeCamera(int slot, uint width, uint height) {
        var origin = Origin(slot: slot);
        var eye = (origin + (Normal * CameraDistance));

        return CameraSnapshot.LookAt(
            position: eye,
            target: origin,
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
    }
}
