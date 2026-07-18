namespace Puck.World.Protocol;

/// <summary>The server→client channel: the server pushes each tick's snapshot, any composed query answers, and — once
/// per step in which at least one buffered mutation/swap applied — the live world definition, which the client stores
/// and folds into its program-rebuild revision.</summary>
internal interface IClientSink {
    /// <summary>Delivers a tick's authoritative snapshot — the whole entity table's render state plus its revision.</summary>
    /// <param name="snapshot">The tick snapshot.</param>
    void DeliverSnapshot(in WorldSnapshot snapshot);

    /// <summary>Delivers a composed query answer for the client to print verbatim.</summary>
    /// <param name="answer">The answer string.</param>
    void DeliverAnswer(in QueryAnswer answer);

    /// <summary>Delivers the server's live world definition after an applied mutation batch (once per step with at least
    /// one applied edit) or a definition swap — the client stores it and bumps its definition revision so the frame
    /// source re-reads the scene/screens on its next rebuild.</summary>
    /// <param name="definition">The world definition now live on the server.</param>
    void DeliverDefinition(WorldDefinition definition);
}
