namespace Puck.World.Client;

/// <summary>
/// The client-local composition overrides — the LIVE session authority that changes what every seat sees, delivered from
/// the server (which grant-checks <c>Control</c> over <see cref="Puck.World.Protocol.GrantSubject.Composition"/> and, for
/// Arc 9, raises a milestone's camera cut) into the composer (which composes the window). Neither is durable: they never
/// fold into the document — <c>view.layout auto</c> / <c>view.camera auto</c> clear them back to the layout's own
/// assignment.
/// </summary>
internal sealed class WorldCompositionState {
    /// <summary>The forced active-layout name, or <see langword="null"/> (auto) for the composer's own selection.</summary>
    public string? ActiveLayout { get; set; }

    /// <summary>The forced camera for every camera-bearing slot, or <see langword="null"/> to use each slot's own
    /// authored assignment.</summary>
    public string? SelectedCamera { get; set; }
}
