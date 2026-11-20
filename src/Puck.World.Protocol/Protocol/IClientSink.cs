namespace Puck.World.Protocol;

/// <summary>The server→client channel: the server pushes each tick's snapshot, any composed query answers, and — once
/// per step in which at least one buffered mutation/swap applied — the live world definition, delivered as a shape
/// change (<see cref="DeliverDefinition"/>, folded into the client's program-rebuild revision) or a value-only
/// write (<see cref="DeliverState"/>, stored for reads with nothing recompiled). Every member that carries content
/// carries the <see cref="WorldStreamStamp"/> of the stream it belongs to, and a spanned stream ends a span with
/// <see cref="DeliverCut"/>, or its transport with <see cref="CloseSpan"/>.</summary>
public interface IClientSink {
    /// <summary>Delivers a tick's authoritative snapshot — the whole entity table's render state plus its revision.</summary>
    /// <param name="snapshot">The tick snapshot.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream);
    /// <summary>Delivers a composed query answer for the client to print verbatim.</summary>
    /// <param name="answer">The answer string.</param>
    void DeliverAnswer(in QueryAnswer answer);
    /// <summary>Delivers the server's live world definition after an applied mutation batch that changed channels,
    /// target registers, or scene/HUD shape (once per step with at least one such applied edit) or a definition
    /// swap — the client stores it and bumps its definition revision so the frame source re-reads the scene/screens
    /// and every shape-derived table recompiles on its next rebuild.</summary>
    /// <param name="definition">The world definition now live on the server.</param>
    /// <param name="version">The version of <paramref name="definition"/>: the server's activation and the install it
    /// reflects, which a reader orders mutation verdicts against.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream);
    /// <summary>Delivers changed creation and shade rows over a definition already held by this stream.</summary>
    void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) =>
        DeliverDefinition(definition, version, stream);
    /// <summary>Delivers the server's live world definition after an applied mutation batch that changed only cell
    /// values — a cell write, removal, transform, or draw-site fire, never a row/channel/register/HUD shape (the
    /// same state-mutation test the server's own installer used to decide the mutation touched values only). The
    /// client stores the fresh definition so state-value reads (cell contents, HUD bindings) see it, without
    /// bumping the definition-delivery revision or recompiling anything <see cref="DeliverDefinition"/>
    /// would.</summary>
    /// <param name="definition">The world definition now live on the server.</param>
    /// <param name="version">The version of <paramref name="definition"/>, as <see cref="DeliverDefinition"/> carries it.</param>
    /// <param name="stamp">The tick the values hold as of and the rows whose values moved since the previous
    /// delivery. When <see cref="WorldStateStamp.ArenaValues"/> is true, the declaration stays fixed and the following
    /// snapshot carries the changed cell values.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream);
    /// <summary>Delivers a record that carries no content and ends the stream's current span: a cut, after which the
    /// stream delivers only under <paramref name="stream"/>'s generation and owes the whole disclosed document before
    /// anything newer, or the stream's end. What a reader showed from an ended span it shows no longer.</summary>
    /// <param name="stream">The stamp of the record: the new generation for a cut, the last generation for an end.</param>
    /// <param name="terminal">Whether the stream ends: it delivers nothing further, and a reader bound to it lets it
    /// go.</param>
    void DeliverCut(in WorldStreamStamp stream, bool terminal);
    /// <summary>Tells the reader that the transport carrying a spanned stream to it ended (a lost connection, an
    /// invalidated observation): the stream can no longer tell it of a cut, so a reader holding that stream's span
    /// open closes it and shows nothing of it. Raised by the reader's own transport, never carried as a record, and a
    /// sink that holds no span ignores it.</summary>
    /// <param name="stream">The identity of the stream whose transport ended.</param>
    void CloseSpan(ulong stream);
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
