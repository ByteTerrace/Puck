namespace Puck.World.Protocol;

/// <summary>The server→client channel: the server pushes each tick's snapshot, any composed query answers, and — once
/// per step in which at least one buffered mutation/swap applied — the live world definition, delivered as a shape
/// change (<see cref="DeliverDefinition"/>, folded into the client's program-rebuild revision) or a value-only
/// write (<see cref="DeliverState"/>, stored for reads with nothing recompiled).</summary>
public interface IClientSink {
    /// <summary>Delivers a tick's authoritative snapshot — the whole entity table's render state plus its revision.</summary>
    /// <param name="snapshot">The tick snapshot.</param>
    void DeliverSnapshot(in WorldSnapshot snapshot);
    /// <summary>Delivers a composed query answer for the client to print verbatim.</summary>
    /// <param name="answer">The answer string.</param>
    void DeliverAnswer(in QueryAnswer answer);
    /// <summary>Delivers the server's live world definition after an applied mutation batch that changed channels,
    /// target registers, or scene/HUD shape (once per step with at least one such applied edit) or a definition
    /// swap — the client stores it and bumps its definition revision so the frame source re-reads the scene/screens
    /// and every shape-derived table recompiles on its next rebuild.</summary>
    /// <param name="definition">The world definition now live on the server.</param>
    void DeliverDefinition(WorldDefinition definition);
    /// <summary>Delivers the server's live world definition after an applied mutation batch that changed only cell
    /// values — a cell write, removal, transform, or draw-site fire, never a row/channel/register/HUD shape (the
    /// same state-mutation test the server's own installer used to decide the mutation touched values only). The
    /// client stores the fresh definition so state-value reads (cell contents, HUD bindings) see it, without
    /// bumping the definition-delivery revision or recompiling anything <see cref="DeliverDefinition"/>
    /// would.</summary>
    /// <param name="definition">The world definition now live on the server.</param>
    void DeliverState(WorldDefinition definition);
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
