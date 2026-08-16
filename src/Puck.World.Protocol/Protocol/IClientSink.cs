namespace Puck.World.Protocol;

/// <summary>The server→client channel: the server pushes each tick's snapshot, any composed query answers, and — once
/// per step in which at least one buffered mutation/swap applied — the live world definition, which the client stores
/// and folds into its program-rebuild revision.</summary>
public interface IClientSink {
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
    /// <summary>Delivers an accepted LIVE window-composition override for the client to apply to its composer (the
    /// <c>view.override layout</c>/<c>view.override camera</c> path).</summary>
    /// <param name="composition">The composition override.</param>
    void DeliverComposition(WorldComposition composition);
    /// <summary>Delivers an ACCEPTED live session lever for the client to write onto the presentation service it names
    /// (render settings, present pacing, or the audio mix). Reached only after the server's
    /// <see cref="WorldCapability.Mutate"/> check on the lever's folded-into section, so a denied lever never arrives
    /// here at all.</summary>
    /// <param name="lever">The accepted lever write.</param>
    void DeliverSessionLever(WorldSessionLever lever);
}
