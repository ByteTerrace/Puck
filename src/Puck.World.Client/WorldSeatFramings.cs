using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// One seat's authored framings (<c>views.framings</c>, <see cref="WorldSeatFraming"/>): each frame the first row whose
/// <see cref="WorldSeatFraming.When"/> reads positive for the seat is the framing it holds, the row's camera program
/// resolves against the seat's own body pose, and the seat's camera eases onto it and back
/// (<see cref="WorldEasedFraming"/>) on the program's own dynamics, or the chase rig's where the program authors none.
/// A <c>$body</c> key in <c>when</c> reads the seat's body, so one row frames each seat that satisfies it and no other.
/// Presentation only: nothing here feeds the simulation.
/// </summary>
public sealed class WorldSeatFramings {
    private readonly WorldEasedFraming m_ease = new();
    private readonly WorldCameraRigCompiler.Cache m_rig = new();
    private WorldDefinition? m_definition;
    private WorldCameraProgram? m_program;
    private WorldSeatFraming? m_row;

    /// <summary>Gets the id of the row the seat held at its last frame, or <see langword="null"/> when it held none.</summary>
    public string? Active => m_row?.Id;
    /// <summary>Gets how far the seat's camera stands toward the framing: 0 is its ordinary camera, 1 the framing.</summary>
    public float Weight => m_ease.Weight;

    /// <summary>Selects the seat's framing for this frame, advances the ease and blends the seat's camera by it.</summary>
    /// <param name="definition">The document the seat is routed to, whose <c>views.framings</c> rows and camera
    /// programs these are; a document that authors no row only eases back from the last framing held.</param>
    /// <param name="mirror">The routed authority's state mirror, which the programs' bound operands read.</param>
    /// <param name="reads">The seat's reads of that mirror, bound to the seat's body, which each row's
    /// <see cref="WorldSeatFraming.When"/> reads through.</param>
    /// <param name="domains">The guard the compiled programs map their bound operands through.</param>
    /// <param name="enabled">Whether a row may hold this frame; <see langword="false"/> eases back to the seat's
    /// camera.</param>
    /// <param name="anchor">The seat's body pose, the reference a framing program's ops resolve against.</param>
    /// <param name="clock">The frame's camera clock.</param>
    /// <param name="chase">The chase rig's reported dynamics, which time the ease for a program that authors none.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <param name="eye">The seat camera's eye, blended in place.</param>
    /// <param name="target">The seat camera's target, blended in place.</param>
    /// <param name="fieldOfView">The seat camera's vertical field of view, in radians, blended in place.</param>
    /// <returns>The camera's near-plane distance, in world units: zero for the seat's own camera, the row's
    /// <see cref="WorldSeatFraming.Near"/> once framed, and the blend between while easing.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public float Apply(WorldDefinition definition, WorldStateMirror mirror, WorldStateLease reads, WorldValueDomainGuard domains, bool enabled, in SdfAnchor anchor, in SdfCameraClock clock, in SdfCameraDynamics chase, float deltaSeconds, ref Vector3 eye, ref Vector3 target, ref float fieldOfView) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: reads);
        ArgumentNullException.ThrowIfNull(argument: domains);

        var row = (enabled ? Select(definition: definition, reads: reads) : null);

        if (!ReferenceEquals(objA: row, objB: m_row) || !ReferenceEquals(objA: definition, objB: m_definition)) {
            m_row = row;
            m_definition = definition;
            m_program = ((row is null) ? null : WorldCameraPrograms.Find(definition: definition, name: row.Program));
        }

        if ((row is null) || (m_program is not { } program)) {
            return m_ease.Apply(
                deltaSeconds: deltaSeconds,
                dynamics: SdfCameraDynamics.None,
                eye: ref eye,
                fieldOfView: ref fieldOfView,
                framing: null,
                target: ref target
            );
        }

        var rig = m_rig.Resolve(definition: definition, domains: domains, mirror: mirror, program: program);
        var (frameEye, frameTarget, frameFieldOfView) = rig.Resolve(anchor: in anchor, clock: in clock);

        return m_ease.Apply(
            deltaSeconds: deltaSeconds,
            dynamics: (rig.Dynamics.IsLive ? rig.Dynamics : chase),
            eye: ref eye,
            fieldOfView: ref fieldOfView,
            framing: (frameEye, frameTarget, row.Near, frameFieldOfView),
            target: ref target
        );
    }

    // The first row, in document order, whose condition reads positive for the seat.
    private static WorldSeatFraming? Select(WorldDefinition definition, WorldStateLease reads) {
        if (definition.ViewsRaw?.Framings is not { Count: > 0 } rows) {
            return null;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (reads.Scalar(fallback: 0f, scalar: row.When) > 0f) {
                return row;
            }
        }

        return null;
    }
}
