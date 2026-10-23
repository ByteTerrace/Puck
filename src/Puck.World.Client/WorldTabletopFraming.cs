using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The board view: a seat sitting at a tabletop looks down on the whole board from the side its body stands on, at the
/// elevation the board's engage block authors (<see cref="WorldTabletopEngage.Pitch"/>), far enough back that the
/// board's footprint plus <see cref="WorldTabletopEngage.Margin"/> fits the viewport's limiting axis at the seat
/// camera's field of view. The side and footprint come from <see cref="WorldTabletopFrame"/>, the same fixed-point
/// frame the simulation reads the seat's side and pointed cell from, so the view and the rules agree on which way is
/// up the board. One instance per seat eases between the seat's ordinary camera and that view on the seat rig's own
/// reported dynamics, and back when the seat leaves; while framed, the near plane stands just short of the board, so
/// the seated avatar draws nothing over it. Presentation only: nothing here feeds the simulation.
/// </summary>
public sealed class WorldTabletopFraming {
    // A view straight down has no side to look from, so the elevation stops just short of vertical.
    private const float MaximumPitchRadians = (MathF.PI * 0.4972f);

    private readonly WorldFramingEase m_ease = new();
    private Vector3 m_frameEye;
    private float m_frameNear;
    private Vector3 m_frameTarget;
    private bool m_hasFrame;

    /// <summary>Gets how far the seat's camera stands toward the board view: 0 is the seat's ordinary camera, 1 the
    /// board view.</summary>
    public float Weight => m_ease.Weight;

    /// <summary>Frames a tabletop's board from one side: the eye stands above the board's centre, back toward the side
    /// <paramref name="seatPosition"/> stands on, at the authored elevation, far enough that a sphere around the
    /// board's footprint (grown by the authored margin) fills the viewport's limiting axis.</summary>
    /// <param name="topology">The board's anchored topology.</param>
    /// <param name="engage">The board's engage block, which authors the elevation, margin and playing surface.</param>
    /// <param name="seatPosition">The seated body's position, in world units, which picks the side.</param>
    /// <param name="fieldOfViewRadians">The camera's vertical field of view, in radians; must be in (0, pi).</param>
    /// <param name="aspectRatio">The viewport's width over its height; must be positive.</param>
    /// <param name="eye">The framing eye, in world units.</param>
    /// <param name="target">The centre of the playing surface, which the eye looks at.</param>
    /// <param name="near">The framing's near-plane distance from the eye, in world units: the distance to the sphere
    /// around the board, so the board and what stands on it draw and nothing nearer does.</param>
    /// <returns><see langword="false"/> when the field of view or aspect is out of range; the outputs are then zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> or <paramref name="engage"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryFrame(CompiledTopology topology, WorldTabletopEngage engage, Vector3 seatPosition, float fieldOfViewRadians, float aspectRatio, out Vector3 eye, out Vector3 target, out float near) {
        ArgumentNullException.ThrowIfNull(argument: topology);
        ArgumentNullException.ThrowIfNull(argument: engage);

        eye = default;
        target = default;
        near = 0f;

        if (
            !(fieldOfViewRadians > 0f) ||
            !(fieldOfViewRadians < MathF.PI) ||
            !(aspectRatio > 0f)
        ) {
            return false;
        }

        WorldTabletopFrame.Bounds(
            maximum: out var maximum,
            minimum: out var minimum,
            topology: topology
        );

        var low = minimum.ToVector3();
        var high = maximum.ToVector3();
        var halfX = ((high.X - low.X) * 0.5f);
        var halfZ = ((high.Z - low.Z) * 0.5f);
        var radius = (MathF.Sqrt(x: ((halfX * halfX) + (halfZ * halfZ))) + ((float)engage.Margin));

        target = new Vector3(
            x: ((low.X + high.X) * 0.5f),
            y: (low.Y + ((float)engage.Surface)),
            z: ((low.Z + high.Z) * 0.5f)
        );

        var toward = WorldTabletopFrame.SideOf(
            position: FixedVector3.FromVector3(value: seatPosition),
            topology: topology
        ) switch {
            WorldTabletopFrame.SidePositiveX => Vector3.UnitX,
            WorldTabletopFrame.SidePositiveZ => Vector3.UnitZ,
            WorldTabletopFrame.SideNegativeX => -Vector3.UnitX,
            _ => -Vector3.UnitZ,
        };
        var pitch = MathF.Min(
            x: (((float)engage.Pitch) * (MathF.PI / 180f)),
            y: MaximumPitchRadians
        );
        var tanHalf = MathF.Tan(x: (fieldOfViewRadians * 0.5f));
        var limitingHalf = MathF.Min(
            x: (fieldOfViewRadians * 0.5f),
            y: MathF.Atan(x: (tanHalf * aspectRatio))
        );
        var distance = (radius / MathF.Sin(x: limitingHalf));

        eye = (target + ((((toward * MathF.Cos(x: pitch)) + (Vector3.UnitY * MathF.Sin(x: pitch)))) * distance));
        near = MathF.Max(
            x: 0f,
            y: (distance - radius)
        );

        return true;
    }

    /// <summary>Advances the ease one frame and blends the seat's camera toward the board view by it.</summary>
    /// <param name="definition">The delivered document the tabletop resolves in.</param>
    /// <param name="placementId">The tabletop placement the seat sits at, or <see langword="null"/> when it sits at
    /// none; the ease then returns to the seat's camera from the last view it held.</param>
    /// <param name="seatPosition">The seated body's position, in world units.</param>
    /// <param name="dynamics">The seat rig's reported dynamics, which time the ease; <see cref="SdfCameraDynamics.None"/>
    /// cuts.</param>
    /// <param name="fieldOfViewRadians">The seat camera's vertical field of view, in radians.</param>
    /// <param name="aspectRatio">The viewport's width over its height.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <param name="eye">The seat camera's eye, blended in place.</param>
    /// <param name="target">The seat camera's target, blended in place.</param>
    /// <returns>The camera's near-plane distance, in world units: zero for the seat's own camera, the board view's near
    /// plane once framed, and the blend between while easing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public float Apply(WorldDefinition definition, string? placementId, Vector3 seatPosition, in SdfCameraDynamics dynamics, float fieldOfViewRadians, float aspectRatio, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var goal = 0f;

        if (
            (placementId is not null) &&
            WorldTabletopFrame.TryFind(
            definition: definition,
            engage: out var engage,
            placementId: placementId,
            topology: out var topology
        ) &&
            TryFrame(
            aspectRatio: aspectRatio,
            engage: engage,
            eye: out var frameEye,
            fieldOfViewRadians: fieldOfViewRadians,
            near: out var frameNear,
            seatPosition: seatPosition,
            target: out var frameTarget,
            topology: topology
        )
        ) {
            m_frameEye = frameEye;
            m_frameTarget = frameTarget;
            m_frameNear = frameNear;
            m_hasFrame = true;
            goal = 1f;
        }

        if (!m_hasFrame) {
            return 0f;
        }

        var weight = m_ease.Step(
            deltaSeconds: deltaSeconds,
            dynamics: in dynamics,
            goal: goal
        );

        if (weight <= 0f) {
            m_hasFrame = false;

            return 0f;
        }

        eye = Vector3.Lerp(
            value1: eye,
            value2: m_frameEye,
            amount: weight
        );
        target = Vector3.Lerp(
            value1: target,
            value2: m_frameTarget,
            amount: weight
        );

        return (weight * m_frameNear);
    }
}
