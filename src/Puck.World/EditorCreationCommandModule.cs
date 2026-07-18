using System.Globalization;
using Puck.Authoring;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The creation-asset console surface (§D6/P5) — the assist-layer twins of the place page's place-by-name chords.
/// <c>editor.import</c> reads a creation FILE through the ONE <see cref="CreationCanonicalizer"/> pipeline and inlines
/// an <c>UpsertCreation</c> (doc + hash from the SAME <see cref="CanonicalCreation"/> — the UIE-6 contract);
/// <c>editor.creations</c> lists the world's creation rows; <c>editor.creation.next</c>/<c>prev</c> cycle a per-seat
/// ARMED creation (the place page's D-pad chords); <c>editor.spawn.creation</c> begins a ghost drag of the armed (or
/// named) creation at the editor focus — the P3 ghost/spawn pattern, committed by <c>editor.grab</c> as one
/// <c>UpsertPlacement</c>. A SEPARATE module to keep every class under its analyzer ceilings.
/// </summary>
/// <remarks>Import routing is Simulation (it submits a mutation; the stdin barrier serializes a following
/// <c>world.status</c>); the armed-cycle and list verbs are pure client state and stay Immediate. A CAS-ref import
/// form stays deliberately unbuilt this arc — World names no content-store root; the file path IS the authoring
/// cache's front door.</remarks>
internal sealed class EditorCreationCommandModule(WorldEditorSession session, WorldEditorDrag drag, WorldClient client, IServerLink link) : ICommandModule {
    /// <summary>The armed-creation cycle-next act (D-pad Right on the place page).</summary>
    public const string NextCommand = "editor.creation.next";
    /// <summary>The armed-creation cycle-previous act (D-pad Left on the place page).</summary>
    public const string PrevCommand = "editor.creation.prev";
    /// <summary>The creation ghost act (North on the place page): a ghost placement of the armed creation.</summary>
    public const string SpawnCommand = "editor.spawn.creation";

    private readonly WorldEditorSession m_session = session;
    private readonly WorldEditorDrag m_drag = drag;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;
    // The per-seat armed creation id (place-by-name state; client-local, self-healing against removed rows).
    private readonly string?[] m_armed = new string?[PlayerRoster.MaxSlots];

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.import",
            description: "Imports a creation FILE (puck.creation.v1) as a world creation asset row: editor.import <path> [id]. The file crosses the strict canonicalize pipeline (validate + normalize + hash — an absent/foreign schema or structural violation rejects loudly, nothing is submitted); the row id defaults to the creation's sanitized name. One UpsertCreation mutation; stamp it with editor.place <id> or the place page's creation ghost.",
            handler: ImportHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.creations",
            description: "Lists the world's creation asset rows: editor.creations — id, shape/stamp cost, frame count (animated rows replay their timeline), and the hash pin's head. The place-by-name catalog the place page cycles.",
            handler: ListHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: NextCommand,
            description: "Arms the NEXT creation row for placement (wraps; the place page's D-pad Right): editor.creation.next [seat]. The armed creation is what editor.spawn.creation ghosts.",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: 1, verb: NextCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: PrevCommand,
            description: "Arms the PREVIOUS creation row for placement (wraps; the place page's D-pad Left): editor.creation.prev [seat].",
            handler: (context, args) => CycleHandler(context: context, args: args, direction: -1, verb: PrevCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: SpawnCommand,
            description: "Begins a GHOST placement drag of the armed (or named) creation at the editor focus point (previewed client-side; enters the document only on release): editor.spawn.creation [creationId] [seat]. Commit with editor.grab, discard with editor.cancel. The chord twin is North on the place page.",
            handler: SpawnHandler
        );
    }

    private CommandResult ImportHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return Error(text: "[editor.import: expected <path> [id]]");
        }

        var path = args[0];

        if (!File.Exists(path: path)) {
            return Error(text: $"[editor.import: no file at {path}]");
        }

        CanonicalCreation canonical;

        try {
            var document = (System.Text.Json.JsonSerializer.Deserialize<CreationDocument>(json: File.ReadAllText(path: path), options: DocumentJsonOptions.Shared)
                ?? throw new CreationValidationException(errors: [new CreationValidationError(Path: "(root)", Message: "the file deserialized to null")], source: path));

            canonical = CreationCanonicalizer.Canonicalize(document: document, source: path);
        } catch (Exception exception) when (exception is CreationValidationException or System.Text.Json.JsonException or IOException) {
            return Error(text: $"[editor.import: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }

        var id = ((args.Length >= 2) ? args[1] : (canonical.Document.Name ?? "creation"));
        var slot = ((context.Parse is null) ? context.Slot : 0);

        // Doc + hash from the SAME CanonicalCreation — the UIE-6 sentence, satisfied structurally.
        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: WorldPrincipal.Seat(slot: slot),
            Creation: new WorldCreation(Id: id, Document: canonical.Document, Hash: canonical.Hash)
        ));

        return new CommandResult(Output: $"[editor.import: '{id}' sha256 {canonical.Hash[..12]}… ({canonical.Document.StampShapeCount()} stamp shapes, {(canonical.Document.Frames?.Count ?? 0)} frames) — one mutation submitted]");
    }

    private CommandResult ListHandler(CommandContext context, string[] args) {
        var creations = m_client.Definition.Creations;

        if (creations.Count == 0) {
            return new CommandResult(Output: "[editor.creations: none — editor.import <path> adds one]");
        }

        var lines = new System.Text.StringBuilder();

        _ = lines.Append(value: $"[editor.creations: {creations.Count}]");

        foreach (var creation in creations) {
            var frames = (creation.Document.Frames?.Count ?? 0);

            _ = lines.Append(value: Environment.NewLine).Append(provider: CultureInfo.InvariantCulture,
                handler: $"  {creation.Id}: {creation.Document.StampShapeCount()} stamp shapes{((frames > 0) ? $", {frames} frames (animated)" : string.Empty)} sha256 {creation.Hash[..12]}…");
        }

        return new CommandResult(Output: lines.ToString());
    }

    private CommandResult CycleHandler(CommandContext context, string[] args, int direction, string verb) {
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: 0, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: verb) is { } guard) {
            return guard;
        }

        var creations = m_client.Definition.Creations;

        if (creations.Count == 0) {
            return Error(text: $"[{verb}: no creation rows — editor.import <path> first]");
        }

        var position = 0;

        if (m_armed[slot] is { } armed) {
            for (var index = 0; (index < creations.Count); index++) {
                if (string.Equals(a: creations[index].Id, b: armed, comparisonType: StringComparison.Ordinal)) {
                    position = ((((index + direction) % creations.Count) + creations.Count) % creations.Count);

                    break;
                }
            }
        }

        var picked = creations[position];

        m_armed[slot] = picked.Id;

        return Echo(slot: slot, verb: verb, detail: $"armed '{picked.Id}' ({(position + 1)}/{creations.Count}) — {SpawnCommand} ghosts it");
    }

    private CommandResult SpawnHandler(CommandContext context, string[] args) {
        // Shapes: none = the armed creation, acting seat; [creationId] and/or trailing [seat].
        var explicitId = ((args.Length >= 1) && !int.TryParse(s: args[0], provider: CultureInfo.InvariantCulture, result: out _));
        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: (explicitId ? 1 : 0), verb: SpawnCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: SpawnCommand) is { } guard) {
            return guard;
        }

        var creationId = (explicitId ? args[0] : m_armed[slot]);

        if (creationId is not { Length: > 0 }) {
            return Error(text: $"[{SpawnCommand}: no creation armed — {NextCommand} or editor.spawn.creation <creationId>]");
        }

        if (!Exists(id: creationId)) {
            m_armed[slot] = null;

            return Error(text: $"[{SpawnCommand}: no creation row '{creationId}' — see editor.creations]");
        }

        var placement = new WorldPlacement(
            Id: m_drag.NextFreePlacementId(),
            CreationId: creationId,
            Position: m_session.Focus(slot: slot),
            YawDegrees: 0f,
            Scale: 1f
        );

        if (!m_drag.TrySpawnPlacementGhost(slot: slot, placement: placement, error: out var reason)) {
            return Error(text: $"[{SpawnCommand}: {reason}]");
        }

        return Echo(slot: slot, verb: SpawnCommand, detail: $"ghost placement '{placement.Id}' of '{creationId}' — sticks move it, editor.grab commits, editor.cancel discards");
    }

    private bool Exists(string id) {
        foreach (var creation in m_client.Definition.Creations) {
            if (string.Equals(a: creation.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private CommandResult? Guard(int slot, string verb) {
        if (m_session.IsEditing(slot: slot)) {
            return null;
        }

        return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
    }

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
