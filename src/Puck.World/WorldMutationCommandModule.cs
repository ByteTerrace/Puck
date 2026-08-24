using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.Launcher;
using Puck.Audio.Mixing;
using Puck.World.Protocol;
using Puck.World.Server;
using static Puck.World.WorldCommandDefinition;

namespace Puck.World;

/// <summary>
/// The dev reflection of the world-mutation protocol — the console surface an agent (or a deterministic test) molds a
/// running world through over stdin, reusing the SAME <see cref="WorldMutation"/> messages the editor drives. Every
/// row-valued verb takes ONE inline-JSON argument in the exact wire shape of the document section (no
/// second grammar, parsed through <see cref="WorldJsonContext"/>); a parse error echoes inline and submits
/// nothing. Every mutation verb routes <see cref="CommandRouting.Simulation"/>, so it buffers on the server and the
/// stdin barrier serializes a following <c>world.status</c> read-after-write for free; the server's own loud accept/
/// reject line is printed when the buffered edit applies at the tick boundary. <c>world.status</c> and <c>world.save</c>
/// are Immediate reads of the server's live definition and journal. This is a SEPARATE module from
/// <see cref="WorldCommandModule"/> to keep that class under its analyzer ceilings.
/// </summary>
/// <remarks>JSON arguments must be a single whitespace-free token (compact JSON): the console tokenizer that identifies
/// the verb would otherwise split the object, and the raw line the handler parses is reconstructed from the submitted
/// text. The verbs read that raw line, so quotes survive.
/// <para>Every mutation here carries the identity its ingress door stamped
/// (see <see cref="WorldPrincipalMapping"/>) — Console for a typed line, the pressing seat's own claim for a bound
/// one — and that identity is not a formality: <see cref="WorldServer"/>'s per-section <see cref="WorldCapability.Mutate"/>
/// grant check applies to EVERY submitted mutation regardless of which module produced it, so revoking a
/// principal's grant over a section refuses that principal's writes here exactly like any other's.</para></remarks>
internal sealed class WorldMutationCommandModule(WorldServer server, IServerLink link, WorldDefinitionSource definitionSource, WorldRenderSettings renderSettings, WorldScreenBinder screenBinder, Client.WorldAudioDirector audioDirector, PresentPacingControl pacing, Client.WorldBindingBarVisibility bindingBarVisibility, Client.WorldTextCatalog textCatalog) : ICommandModule {
    // Buffer a mutation over the link and return a quiet ack — the server prints the loud accept/reject line when the
    // buffered edit applies at the tick boundary, and the barrier guarantees a following world.status sees the result.
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }
    // world.load's own trailing-token grammar: <path> [force], where `force` is recognized only as the LAST
    // whitespace-separated token (a path itself never needs quoting today — see RawArgument's own remarks on why
    // this module does not tokenize paths). Empty/whitespace-only input (after stripping a trailing "force") fails.
    private static bool TryParseLoadArgs(string raw, out string path, out bool force) {
        var trimmed = raw.Trim();
        var lastSpace = trimmed.LastIndexOfAny(anyOf: [' ', '\t']);

        if (
            (lastSpace >= 0) &&
            string.Equals(
            a: trimmed[(lastSpace + 1)..],
            b: "force",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            force = true;
            path = trimmed[..lastSpace].TrimEnd();
        } else {
            force = false;
            path = trimmed;
        }

        return !string.IsNullOrWhiteSpace(value: path);
    }
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "world.kit.default",
            description: "Sets the default seat kit (by name): world.kit.default <name>. Rejected if the name matches no kit row.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return Usage(
                        form: "<name>",
                        verb: "world.kit.default"
                    );
                }

                return Submit(mutation: new WorldMutation.SetDefaultSeatKit(
                    Principal: context.ActingPrincipal(),
                    Name: args[0].ToString()
                ));
            }
        );
        yield return Simulation(
            name: "world.population.defaults",
            description: "Sets the census defaults (document-only; the live census stays the world.population verb): world.population.defaults <local> <network>, where <local> (1..4) is how many LEADING seats author 'eager' — seat 1 is always eager regardless, and every seat from <local> on authors 'onDemand'.",
            handler: (context, args) => {
                if (
                    (args.Count != 2) ||
                    !int.TryParse(
                    args[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var local
                ) ||
                    !int.TryParse(
                    args[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var network
                )
                ) {
                    return Usage(
                        form: "<local> <network>",
                        verb: "world.population.defaults"
                    );
                }

                if (
                    (local < 1) ||
                    (local > WorldBodiesLimits.LocalSeatCount)
                ) {
                    return CommandResult.Error(output: $"[world.population.defaults: local must be 1..{WorldBodiesLimits.LocalSeatCount}]");
                }

                var seatActivation = new SeatActivationPolicy[WorldBodiesLimits.LocalSeatCount];

                for (var slot = 0; (slot < seatActivation.Length); slot++) {
                    seatActivation[slot] = ((slot < local)
                        ? SeatActivationPolicy.Eager
                        : SeatActivationPolicy.OnDemand
                    );
                }

                // Preserve the document's live peer-source default (the world.population source verb owns it) and the
                // spawn policy (world.population.spawn owns it) — this verb only sets the local/network census figures.
                return Submit(mutation: new WorldMutation.SetPopulationDefaults(
                    Principal: context.ActingPrincipal(),
                    Population: (server.Definition.Population with { SeatActivationRaw = seatActivation, NetworkPlayers = network })
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.placement.get",
            description: "Reads one live placement row as its exact WorldPlacement JSON shape: world.placement.get <id>.",
            handler: (_, args) => {
                if (args.Count != 1) {
                    return Usage(
                        form: "<id>",
                        verb: "world.placement.get"
                    );
                }

                var id = args[0].ToString();

                if (WorldDefinitionRows.FindPlacement(
                    placements: server.Definition.Placements,
                    id: id
                ) is not { } placement) {
                    return CommandResult.Error(output: $"[world.placement.get: no placement row named '{id}']");
                }

                return new CommandResult(Output: JsonSerializer.Serialize(
                    value: placement,
                    jsonTypeInfo: WorldJsonContext.Default.WorldPlacement
                ));
            }
        );
        yield return Simulation(
            name: "world.grant.set",
            description: "Upserts a document-authored grant row (see WorldDefinition.Grants) — the FULL world.grant token grammar, masks included: world.grant.set <principal> <capability> <subject> [exclusive] [budget:<n>] [events:<n>] [channels:<name,...>] [ceiling:<f>] [verbs:<name,...>] [writes:<name,...>]. Each mask still obeys its own carriage rule (verbs: only on mutate section:<name> or edit state:<name>; writes: only on mutate state:<name>), refused by name at the same door a live grant hits. Additionally accepts a document:<id> principal, which world.grant refuses: a document principal is meaningless in the LIVE table (the cross-document durable-state write-back channel reads its grants off the owner's document), so this verb is the ONLY place that capability is authorable — in session, through the ordered domain and the journal, like every other document mutation. DOCUMENT-ONLY, like world.row.set addons: applies at the NEXT BOOT through the identical WorldServer.Grant path world.grant itself submits through, so an illegitimate or conflicting row (including a missing/refused budget or a missing verb mask on an untrusted mutate row) prints the same loud accept/reject line an operator would see typing it live — this verb never touches the LIVE grant table world.grant/world.revoke administer. Replaces the row matching the SAME (principal, capability, subject); a bare re-set changes only the trailing tokens actually supplied.",
            handler: (context, args) => {
                // verbsAllowed: the write-back channel's mask had no in-session author before this — the ONLY way to
                // set one was hand-editing an owned world's JSON outside the process, which the unification contract
                // refuses on its face. Both masks ride here (the row's own carriage rules, enforced identically at
                // the grant door, decide which one a given row may actually carry).
                if (!WorldGrantCommandModule.TryParseGrant(
                    args: args,
                    exclusiveAllowed: true,
                    verb: "world.grant.set",
                    grant: out var grant,
                    error: out var error,
                    channels: server.Population.Channels,
                    targets: server.Population.TargetRegisters,
                    verbsAllowed: true
                )) {
                    return error;
                }

                return Submit(mutation: new WorldMutation.UpsertGrant(
                    Principal: context.ActingPrincipal(),
                    Row: grant
                ));
            }
        );
        yield return Simulation(
            name: "world.grant.remove",
            description: "Removes a document-authored grant row by (principal, capability, subject) — exclusive is ignored, matching world.revoke's own shape: world.grant.remove <principal> <capability> <subject>. DOCUMENT-ONLY; the live grant table world.grant/world.revoke administer is untouched.",
            handler: (context, args) => {
                if (!WorldGrantCommandModule.TryParseGrant(
                    args: args,
                    exclusiveAllowed: false,
                    verb: "world.grant.remove",
                    grant: out var grant,
                    error: out var error
                )) {
                    return error;
                }

                return Submit(mutation: new WorldMutation.RemoveGrant(
                    Principal: context.ActingPrincipal(),
                    Target: grant
                ));
            }
        );
        yield return Simulation(
            name: "world.reset",
            description: "Resets the running world to its BASE — the last world.save, or the boot document if never saved (the same base world.undo replays from; world.save compacts the base): world.reset. An ordered-domain submission like any other write: buffers, applies at the tick boundary, journal clears, admitted peer CONNECTIONS survive with their admission grant re-minted, screens/bodies/population re-derive from the base document. Profiles/player documents are UNTOUCHED — player data is not world state. The accept echo names what the base actually is. Fully replay-compatible: captured on the tape, CAS-pinned by a sha256-64 hash of the base's own canonical bytes at apply time — a re-drive refuses BY NAME rather than silently reproducing a base that has moved since the recording was made.",
            handler: (context, _) => {
                link.SubmitRebuild(
                    request: new WorldRebuildRequest(
                        Kind: WorldRebuildKind.Reset,
                        Definition: null,
                        PathHint: null,
                        Force: false
                    ),
                    principal: context.ActingPrincipal()
                );

                return CommandResult.None;
            }
        );
        yield return Simulation(
            name: "world.load",
            description: "Loads a DIFFERENT world file and rebuilds from it — the loaded document becomes the new base (fully validated off disk, then re-validated at the apply boundary → swap → derived rebuild → journal RESET): world.load <path> [force]. Refused (naming world.save/world.reset/force as the outs) while the journal is DIRTY unless force is passed — a load discards unsaved work, and doing that silently is dishonest. A missing/invalid file, or the every-section Mutate hold world.load/world.undo have always needed, leaves the running world untouched and echoes a loud line naming why. Fully replay-compatible: captured on the tape, CAS-pinned by a sha256-64 hash of the exact bytes read off disk — a re-drive re-reads the same path and refuses BY NAME if the file has moved since the recording was made. The accept echo names the new origin.",
            handler: (context, args) => {
                if (!TryParseLoadArgs(
                    raw: WorldCommandArguments.Raw(
                        args: args,
                        context: context
                    ),
                    path: out var path,
                    force: out var force
                )) {
                    return Usage(
                        form: "<path> [force]",
                        verb: "world.load"
                    );
                }

                var fullPath = Path.GetFullPath(path: path);

                // The console-side validate (ApplyRebuild revalidates the SAME candidate again at the tick boundary
                // below) — both reuse server.Neighbours, the ONE live-session resolver this repository wires (a
                // document SWAP, not a per-mutation check; see WorldServer.Neighbours' own remarks).
                if (!WorldDefinitionFileSource.TryLoad(
                    path: fullPath,
                    definition: out var loaded,
                    contentHash: out var contentHash,
                    reason: out var reason,
                    neighbours: server.ResolveRebuildNeighbours(path: fullPath)
                )) {
                    return CommandResult.Error(output: $"[world.load: {reason}]");
                }

                if (!textCatalog.TryValidate(
                    definition: loaded!,
                    origin: fullPath,
                    reason: out reason
                )) {
                    return CommandResult.Error(output: $"[world.load: text assets refused: {reason}]");
                }

                link.SubmitRebuild(
                    request: new WorldRebuildRequest(
                        ContentHash: contentHash,
                        Definition: loaded!,
                        Force: force,
                        Kind: WorldRebuildKind.Load,
                        PathHint: fullPath
                    ),
                    principal: context.ActingPrincipal()
                );

                return CommandResult.None;
            }
        );
        // BINDABLE, unlike the rest of this module: it is a gesture, not a verb naming a document target (no path, no
        // row, no principal) — the artist's "re-read what is on disk" press. Binding it changes nothing about who may
        // do it: the Mutate hold is checked at dispatch under the pressing seat's principal, exactly as from stdin.
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            routing: CommandRouting.Simulation,
            name: "world.reload",
            description: "Re-reads the CURRENT document origin from disk and rebuilds from it — the artist external-edit loop: edit the JSON externally, world.reload, no restart: world.reload. The journal always clears on success (reload IS a fresh read of what is on disk right now, so there is nothing to discard-guard the way world.load does). A missing/invalid file, or a re-read that no longer validates, leaves the running world untouched and echoes a loud line naming why. Fully replay-compatible: captured on the tape, CAS-pinned by a sha256-64 hash of the exact bytes read off disk — a re-drive re-reads the same path and refuses BY NAME if the file has moved since the recording was made. The accept echo names the re-read origin.",
            handler: (context, _) => {
                var path = definitionSource.SourcePath;

                // See world.load's own remarks: reuses server.Neighbours, the one live-session resolver.
                if (!WorldDefinitionFileSource.TryLoad(
                    path: path,
                    definition: out var loaded,
                    contentHash: out var contentHash,
                    reason: out var reason,
                    neighbours: server.ResolveRebuildNeighbours(path: path)
                )) {
                    return CommandResult.Error(output: $"[world.reload: {reason}]");
                }

                if (!textCatalog.TryValidate(
                    definition: loaded!,
                    origin: path,
                    reason: out reason
                )) {
                    return CommandResult.Error(output: $"[world.reload: text assets refused: {reason}]");
                }

                link.SubmitRebuild(
                    request: new WorldRebuildRequest(
                        ContentHash: contentHash,
                        Definition: loaded!,
                        Force: false,
                        Kind: WorldRebuildKind.Reload,
                        PathHint: path
                    ),
                    principal: context.ActingPrincipal()
                );

                return CommandResult.None;
            }
        );
        yield return Simulation(
            name: "world.undo",
            description: "Undoes the last n applied mutations (default 1) by replaying the journal minus its tail through the same apply path: world.undo [n]. The journal IS the edit history; replay IS the undo engine.",
            handler: (context, args) => {
                var count = 1;

                if (
                    (args.Count >= 1) &&
                    (!int.TryParse(
                    args[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out count
                ) || (count < 1))
                ) {
                    return CommandResult.Error(output: $"[world.undo: bad count '{args[0].ToString()}' — a positive integer]");
                }

                link.SubmitUndo(
                    count: count,
                    principal: context.ActingPrincipal()
                );

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.save",
            description: "Writes a SESSION SNAPSHOT of the live world to a file in canonical form (stable member order, invariant numbers, LF newlines, one trailing newline) and compacts the journal (the saved definition becomes the new base, dirty → 0): world.save [path]. The snapshot is the live definition (mutations included) with session state folded into its document homes — the live render levers into Render, the live census + peer-source default into Population, and runtime screen inserts into the screens' Machine sources. No argument writes back to the loaded world file. A target file naming a basis stays a delta: the write is the proved minimal difference over its composed basis chain, and the echo names the preserved basis (or why the save degraded to flat).",
            handler: (context, args) => {
                var target = ((args.Count >= 1)
                    ? args.Tail(start: 0)
                    : definitionSource.SourcePath
                );

                // The same EVERY-section hold world.load and world.undo pass, and for the same two reasons rather than
                // for symmetry's sake: the file this writes IS a loadable world document carrying every section, so
                // authoring one confers exactly what loading one does; and Compact() destroys the journal that
                // world.undo is gated on. Without this, revoking a section refuses that section's mutations while a
                // save still serializes the live values of that very section to disk — the revoke would hold live and
                // leak durably.
                if (!server.Grants.AllowsAllSections(
                    principal: context.ActingPrincipal(),
                    capability: WorldCapability.Mutate,
                    deniedSection: out var savedSection,
                    denial: out var saveVerdict
                )) {
                    return CommandResult.Error(output: $"[world.save: console cannot mutate every section (section:{savedSection.ToString().ToLowerInvariant()} — {saveVerdict.DescribeDenial()}) — a save writes every section and compacts the journal; nothing written]");
                }

                try {
                    // The same completed-tick derivation WorldStateCommandModule's own read-backs use (NextInputTick
                    // is m_lastCompletedTick + 1, and Step is its one writer) — the instant every advancing state
                    // row/cell settles at in the snapshot (see WorldSessionCapture's remarks).
                    var snapshot = WorldSessionCapture.Capture(
                        definition: server.Definition,
                        render: renderSettings,
                        population: server.Population,
                        binder: screenBinder,
                        audio: audioDirector,
                        bindingBar: bindingBarVisibility,
                        pacing: pacing,
                        tick: (server.NextInputTick - 1UL)
                    );
                    var bytes = WorldDefinitionSerialization.SavePreservingBasis(
                        basisPath: out var basisPath,
                        definition: snapshot,
                        note: out var note,
                        path: target
                    );

                    server.Compact();

                    var derivation = ((basisPath is { })
                        ? $", basis: {basisPath}"
                        : ((note.Length > 0)
                            ? $", {note}"
                            : ""
                    ));

                    return new CommandResult(Output: $"[world.save: {target} ({bytes} bytes{derivation})]");
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
                    return CommandResult.Error(output: $"[world.save: could not write {target} ({exception.Message})]");
                }
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.text",
            description: "Reads the world text catalog: its default font and every named font's relative source, content pin, Unicode ranges, pixel size, distance range, padding, and preferred columns; world.text [font].",
            handler: (_, args) => {
                if (args.Count > 1) {
                    return Usage(
                        form: "[font]",
                        verb: "world.text"
                    );
                }

                var text = server.Definition.Text;

                if (text is null) {
                    return new CommandResult(Output: "[world.text: none]");
                }

                var selected = ((args.Count == 1)
                    ? args[0].ToString()
                    : null
                );
                var rows = text.Fonts.Where(predicate: row => ((selected is null) || string.Equals(
                    a: row.Name,
                    b: selected,
                    comparisonType: StringComparison.Ordinal
                ))).ToArray();

                if (rows.Length == 0) {
                    return CommandResult.Error(output: $"[world.text: no font named '{selected}']");
                }

                var formatted = string.Join(
                    separator: " | ",
                    values: rows.Select(selector: row => {
                        var options = row.ToGenerationOptions();

                        return $"{row.Name}{(string.Equals(
                            a: row.Name,
                            b: text.DefaultFont,
                            comparisonType: StringComparison.Ordinal
                        )
                            ? " (default)"
                            : string.Empty)} source:{row.Source} hash:{row.Hash} ranges:{string.Join(
                            separator: ",",
                            values: row.CodePointRanges
                        )} px:{options.FontPixelSize} distance:{options.DistanceRange.ToString(
                            format: "0.###",
                            provider: CultureInfo.InvariantCulture
                        )} padding:{options.Padding} columns:{options.Columns}";
                    })
                );

                return new CommandResult(Output: $"[world.text: {formatted}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.status",
            description: "Reports the live world definition and journal state (Immediate; the stdin barrier makes it read the settled state after any pending mutation): source path, the source file's basis (its composition template, or none — peeked from the file, the one truth for derivation), schema, row counts, the simulation rate, correction/wander/audio policy (including the mixer's half-radius curve sample), the waterline (or none), a cheap session-drift hint, and dirty = journal length. Session drift is separate from dirty: a saved-bytes-only world.save leaves the in-memory definition unchanged, so session drift honestly persists past a save.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.status: unrecognized '{args[0]}' — expected no arguments]");
                }

                var definition = server.Definition;
                var source = definitionSource.SourcePath;
                var dirty = server.JournalLength;
                var drift = WorldSessionCapture.DescribeDrift(
                    definition: definition,
                    render: renderSettings,
                    population: server.Population,
                    binder: screenBinder,
                    audio: audioDirector,
                    bindingBar: bindingBarVisibility,
                    pacing: pacing
                );
                var audioCurve = (string.Equals(
                    a: definition.Audio.DefaultCurve,
                    b: WorldAudioDefaults.CurveLinear,
                    comparisonType: StringComparison.Ordinal
                )
                    ? AudioAttenuationCurve.Linear
                    : AudioAttenuationCurve.Smoothstep
                );
                var halfRadiusGain = (((double)AudioMixer.HalfRadiusAttenuationQ16(curve: audioCurve)) / 65536.0);

                var water = ((definition.Water is { } medium)
                    ? medium.Level.ToString(
                        format: "0.###",
                        provider: CultureInfo.InvariantCulture
                    )
                    : "none"
                );
                var basis = ((WorldDefinitionFileSource.TryPeekBasis(
                    basisPath: out var basisPath,
                    path: source,
                    reason: out var unusedPeekReason
                ) && (basisPath is { }))
                    ? basisPath
                    : "none"
                );

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.status: source {source} basis {basis} schema {definition.Schema} rate {definition.SimulationRateHz}Hz kits {definition.Kits.Count} body-programs {definition.BodyMotionPrograms.Count} screens {definition.Screens.Count} cameras {definition.Cameras.Count} creations {definition.Creations.Count} placements {definition.Placements.Count} maxSmoothError {definition.Motion.MaxSmoothError:0.###} water {water} audio-curve {definition.Audio.DefaultCurve} half-radius-gain {halfRadiusGain:0.#####} session-drift {drift} dirty {dirty} undoable {dirty}]"
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.references",
            description: "Reads the references section back: each row's name -> document, or 'none' when the section is absent or declares zero rows. Authored data only — a row asserts nothing about the named document's existence or shape; resolving it is a future consumer's job, not this verb's.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.references: unrecognized '{args[0]}' — expected no arguments]");
                }

                var rows = server.Definition.References;

                if (rows is not { Count: > 0 }) {
                    return new CommandResult(Output: "[world.references: none]");
                }

                var formatted = string.Join(
                    separator: " ",
                    values: rows.Select(selector: row => $"{row.Name} -> {row.Document}")
                );

                return new CommandResult(Output: $"[world.references: {formatted}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.metadata",
            description: "Reads the metadata section back: title, description, authors, tags, and the custom bag's key/byte counts, or 'none' when the section is absent. Authored data only, boot-authored, nothing here is read by the engine.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.metadata: unrecognized '{args[0]}' — expected no arguments]");
                }

                var metadata = server.Definition.Metadata;

                if (metadata is null) {
                    return new CommandResult(Output: "[world.metadata: none]");
                }

                var title = (metadata.Title ?? "none");
                var description = (metadata.Description ?? "none");
                var authors = ((metadata.Authors is { Count: > 0 } authorRows)
                    ? string.Join(
                        separator: ",",
                        values: authorRows.Select(selector: author => ((author.Oid is { } oid)
                        ? $"{author.Name}(oid:{oid})"
                        : author.Name))
                    )
                    : "none"
                );
                var tags = ((metadata.Tags is { Count: > 0 } tagRows)
                    ? string.Join(
                        separator: ",",
                        values: tagRows
                    )
                    : "none"
                );
                var custom = ((metadata.Custom is { Count: > 0 } customBag)
                    ? string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"{customBag.Count} keys/{WorldMetadataSection.CustomUtf8ByteCount(custom: customBag)} bytes"
                    )
                    : "none"
                );

                return new CommandResult(Output: $"[world.metadata: title {title} description {description} authors {authors} tags {tags} custom {custom}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.admission",
            description: "Reads the admission section back: each row's domain/subject/mode/algorithm and its grant template count, or 'none' when the section is absent or declares zero rows (deny by default — no remote peer can verify and no traveller can arrive). The document half of the admission decision — world.peers echoes the runtime half (which bodies were admitted under which identity).",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.admission: unrecognized '{args[0]}' — expected no arguments]");
                }

                var rows = server.Definition.Admission;

                if (rows is not { Count: > 0 }) {
                    return new CommandResult(Output: "[world.admission: none — no remote peer can verify and no traveller can arrive]");
                }

                var formatted = string.Join(
                    separator: " | ",
                    values: rows.Select(selector: row => $"{row.Mode.ToString().ToLowerInvariant()} domain:{row.Domain} subject:{(row.Subject ?? "(none)")} alg:{row.Algorithm} grants:{row.Grants.Count} disclosure:{row.Tier.ToString().ToLowerInvariant()}{((row.Disclosure is null)
                    ? " (default)"
                    : string.Empty)}")
                );

                return new CommandResult(Output: $"[world.admission: {formatted}]");
            }
        );
    }

}
