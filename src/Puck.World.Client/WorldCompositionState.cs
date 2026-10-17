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
    public WorldOperatorCamera? OperatorCamera { get; set; }
    /// <summary>The camera name the composer forces onto every slot: <see cref="WorldViewComposer.OperatorCameraName"/>
    /// while an operator camera is set, otherwise <see cref="SelectedCamera"/>.</summary>
    public string? EffectiveCamera => ((OperatorCamera is null)
        ? SelectedCamera
        : WorldViewComposer.OperatorCameraName
    );
}
