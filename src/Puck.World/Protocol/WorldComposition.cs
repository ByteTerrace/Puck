namespace Puck.World.Protocol;

/// <summary>
/// A LIVE window-composition override — the closed set of composition-authority messages the server grant-checks
/// (<see cref="WorldCapability.Control"/> over <see cref="GrantSubject.Composition"/>) and pushes to the client's
/// composer through <see cref="IClientSink.DeliverComposition"/>. Neither is durable: they change what every seat sees
/// for the session, never the document. This is the delivery path Arc 9's <c>MilestoneEffect.SelectCamera</c> publishes
/// into — a milestone's camera cut is an ordinary <see cref="SelectCamera"/>, not a second mechanism.
/// </summary>
internal abstract record WorldComposition {
    private WorldComposition() {
    }

    /// <summary>Forces the active window layout, or returns to the composer's own selection.</summary>
    /// <param name="Name">The authored layout name, or <see langword="null"/> for auto selection.</param>
    internal sealed record SetActiveLayout(string? Name) : WorldComposition;

    /// <summary>Overrides which authored camera every camera-bearing slot resolves to, or returns to each slot's own
    /// assignment.</summary>
    /// <param name="Name">The camera name, or <see langword="null"/> to clear the override.</param>
    internal sealed record SelectCamera(string? Name) : WorldComposition;
}
