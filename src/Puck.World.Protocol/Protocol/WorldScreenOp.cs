namespace Puck.World.Protocol;

/// <summary>
/// The closed screen-machine-lifecycle union a <see cref="WorldSubmissionPayload.ScreenOp"/> submission carries —
/// <c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/<c>.options</c>/<c>.link</c>/<c>.unlink</c> travel as this leaf
/// through the one ordered domain, landing in <c>Server.WorldServer.Machines</c> — the only project that boots, steps, or holds a live
/// <c>Puck.Abstractions.Machines.IScreenMachine</c>. Applied synchronously at submit, exactly like
/// <see cref="WorldCommand"/>/<see cref="WorldGrant"/> — never buffered to the tick boundary — because
/// <c>player.engage</c>'s auto-insert precheck submits a <see cref="Select"/> immediately ahead of the
/// <see cref="WorldCommand.ComposeControl"/> that follows it in the same batch, and the second submission must observe the
/// first's effect (the "walk over, press the button, the screen lights" one-act UX). Deliberately narrower than
/// <see cref="WorldDefinition.Screens"/>' <c>UpsertScreen</c>/<c>RemoveScreen</c> mutations, which are document-only
/// (what a screen declares) and never boot or step anything themselves: this union is the runtime-facing
/// counterpart, exactly mirroring how <see cref="WorldAddonLifecycle"/> relates to the document-only
/// <c>world.row.set addons</c>/<c>world.row.remove addons</c> mutations.
/// </summary>
public abstract record WorldScreenOp {
    private WorldScreenOp() {
    }

    /// <summary>Boots (or live-swaps) a machine onto a declared screen from an arbitrary content-file path — the
    /// runtime <c>screen.insert</c> path. CAS-pinned: a re-drive re-reads <paramref name="ContentPath"/> fresh and
    /// refuses by name on a content-hash disagreement (the <c>WorldReplayEntry.ScreenOp.ContentHash</c> pin), because
    /// nothing else on the tape records what an arbitrary caller-supplied path once held. Contrast <see cref="Select"/>,
    /// which never needs its own pin — a magazine entry's content path is document data already captured in the
    /// tape's embedded definition.</summary>
    /// <param name="Index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="ContentPath">The content file (a cartridge ROM) to boot.</param>
    /// <param name="EngineId">The screen-machine engine id, or <see langword="null"/> for the sole-registered default.</param>
    /// <param name="Options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    public sealed record Insert(int Index, string ContentPath, string? EngineId, string? Options) : WorldScreenOp;
    /// <summary>Ejects a screen's live machine — the runtime <c>screen.eject</c> path. Camera/capture/window-capture
    /// producers are not this union's concern (genuinely presentation);
    /// this only ever clears a booted machine.</summary>
    /// <param name="Index">The engine screen-surface index.</param>
    public sealed record Eject(int Index) : WorldScreenOp;
    /// <summary>Advances a screen's source magazine to <paramref name="Entry"/> and, when that entry is a
    /// <c>WorldScreenSource.Machine</c> row, boots it — the runtime <c>screen.select</c> path. The selector always
    /// moves even for a non-machine entry (camera/capture/view rows apply on the presentation side once the binder
    /// observes the moved selector, which is why those sources stay client-owned).</summary>
    /// <param name="Index">The engine screen-surface index.</param>
    /// <param name="Entry">The 0-based magazine entry to select.</param>
    public sealed record Select(int Index, int Entry) : WorldScreenOp;
    /// <summary>Reconfigures a screen's live machine across the engine's options vocabulary — the runtime
    /// <c>screen.options</c> live device swap (dmg↔cgb↔agb with no reboot).</summary>
    /// <param name="Index">The engine screen-surface index.</param>
    /// <param name="Options">The engine-specific options string to retarget to.</param>
    public sealed record SetOptions(int Index, string? Options) : WorldScreenOp;
    /// <summary>Establishes (or records dormant) a runtime cable link over two or more declared screens' machines —
    /// the <c>screen.link</c> path.</summary>
    /// <param name="Name">The link's stable name.</param>
    /// <param name="Members">The engine screen indices in cable order.</param>
    public sealed record Link(string Name, IReadOnlyList<int> Members) : WorldScreenOp;
    /// <summary>Severs a runtime cable link by name — the <c>screen.unlink</c> path.</summary>
    /// <param name="Name">The link name.</param>
    public sealed record Unlink(string Name) : WorldScreenOp;
}
