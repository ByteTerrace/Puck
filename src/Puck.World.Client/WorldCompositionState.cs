using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The client-local composition overrides — the LIVE session authority that changes what every seat sees, delivered from
/// the server (which grant-checks <c>Control</c> over <see cref="Puck.World.Protocol.GrantSubject.Composition"/>) into
/// the composer (which composes the window). Neither is durable: they never
/// fold into the document — <c>view.override layout auto</c> / <c>view.override camera auto</c> clear them back to the layout's own
/// assignment.
/// </summary>
public sealed class WorldCompositionState {
    /// <summary>The forced active-layout name, or <see langword="null"/> (auto) for the composer's own selection.</summary>
    public string? ActiveLayout { get; set; }
    /// <summary>The forced camera for every camera-bearing slot, or <see langword="null"/> to use each slot's own
    /// authored assignment.</summary>
    public string? SelectedCamera { get; set; }
    /// <summary>The operator's free camera, or <see langword="null"/> for the layout's own views. While set it
    /// replaces what every slot shows, seat slots included, and outranks <see cref="SelectedCamera"/>.</summary>
    public WorldOperatorCamera? OperatorCamera { get; private set; }
    /// <summary>The operator camera's current shot: one object for as long as its poses continue one another (a turn,
    /// swing, move or field of view), and a new one for each pose that does not (pose, look, orbit) and whenever the camera
    /// is cleared. The presenter keys the operator view's source and cut on it, so a delta move keeps the view's temporal
    /// history and exposure as a seat camera's motion does, while a new shot cuts them like a teleport.</summary>
    public object? OperatorShot { get; private set; }

    /// <summary>Sets or clears the operator camera, the one door to <see cref="OperatorCamera"/> and its
    /// <see cref="OperatorShot"/>.</summary>
    /// <param name="pose">The pose, or <see langword="null"/> to clear the camera.</param>
    /// <param name="continues">Whether the pose moves on from the camera already set
    /// (<see cref="WorldComposition.SetOperatorCamera.Continues"/>).</param>
    public void SetOperatorCamera(WorldOperatorCamera? pose, bool continues) {
        if ((pose is null) || !continues || (OperatorCamera is null)) {
            OperatorShot = ((pose is null) ? null : new object());
        }
        OperatorCamera = pose;
    }
    /// <summary>The camera name the composer forces onto every slot: <see cref="WorldViewComposer.OperatorCameraName"/>
    /// while an operator camera is set, otherwise <see cref="SelectedCamera"/>.</summary>
    public string? EffectiveCamera => ((OperatorCamera is null)
        ? SelectedCamera
        : WorldViewComposer.OperatorCameraName
    );
}
