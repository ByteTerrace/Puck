using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The document-row verbs — <c>world.row</c>, <c>world.row.set</c>, <c>world.row.remove</c> and <c>world.row.step</c> — the one
/// door every document section is authored through: a dotted DOCUMENT MEMBER PATH (the document's own
/// camelCase JSON names, e.g. <c>kits</c>, <c>hud.panels</c>, <c>views.seatRig</c>) selects which
/// <see cref="WorldMutation"/> a row's inline JSON composes into, closing over the same section table
/// <c>puck schema</c> documents for payload shapes; <c>world.row.step</c> addresses one FIELD a level deeper (see
/// <see cref="WorldRowFieldStepper"/>) and applies a delta rather than a literal. An unknown path is refused BY NAME,
/// enumerating every admissible sibling — never a silent no-op.
/// </summary>
/// <remarks>
/// <para>ONE grammar exception: <c>properties.names</c> carries a BARE-NAME token, never JSON — the registry section
/// is a name and a toggle, so <c>world.row.set properties.names &lt;name&gt;</c> / <c>world.row.remove properties.names
/// &lt;name&gt;</c> both submit <see cref="WorldMutation.SetProperty"/>, distinguished only by its own
/// <see cref="WorldMutation.SetProperty.Remove"/> flag.</para>
/// <para>This module implements NO schema validation and issues NO schema-pointer refusals — a parse failure echoes
/// <see cref="WorldJsonPayload"/>'s own message inline and submits nothing; every semantic check (unknown id, capacity,
/// cross-row reference) still runs where it always has, at whole-document revalidation when the buffered mutation
/// applies at the tick boundary. <c>puck schema</c> is DOCUMENTATION for a payload's shape, never a gate this verb
/// consults.</para>
/// <para>Every mutation here carries the identity its ingress door stamped (see <see cref="WorldPrincipalMapping"/>) —
/// Console for a typed line — and that identity is not a formality: <see cref="WorldServer"/>'s per-section
/// <see cref="WorldCapability.Mutate"/> grant check applies to EVERY submitted mutation regardless of which module
/// produced it, so revoking a principal's grant over a section refuses that principal's writes here exactly like any
/// other's.</para>
/// <para><c>world.assign kits|looks r1 | cycle &lt;name&gt;…</c> lives here too — ONE row→entity
/// assignment-sequence primitive over both tables, the target deciding only which <see cref="WorldMutation"/> kind
/// wraps the built <see cref="WorldRowAssignment"/> and what additive offset the r1 sequence takes.</para>
/// <para><c>world.kits</c> is a plain census read-back, not a row verb — the kits section's only listing.</para>
/// </remarks>
public sealed class WorldRowCommandModule(IWorldConsoleAuthority authority, IServerLink link, WorldDeferredVerbEchoes echoes, WorldRowStepWindowGuard? stepGuard = null) : ICommandModule {
    private const string PropertiesNamesPath = "properties.names";

    // world.row.step read-your-writes guard (see WorldRowStepWindowGuard). Console-side control state, single-threaded
    // on the command pump; off every hashed simulation path. Shared with every other module that reads a row off the
    // live document and resubmits it whole (WorldSculptCommandModule) when the composition root hands both the same
    // instance — a second read-modify-whole-row write to one row inside one tick window is the same stale read
    // whichever verb issues it.
    private readonly WorldRowStepWindowGuard m_stepGuard = (stepGuard ?? new());

    // The section table — the one thing that legitimately stays as data (CLAUDE.md: a table over these rows is
    // vocabulary, not logic to duplicate per section). Built once per type: no entry closes over a particular
    // WorldServer — the four sections whose mutation reads live document state (inputHold/views.seatRig/
    // views.seatControl/playerDefaults.seatLook) take it as this delegate's own leading parameter instead, resolved
    // fresh at each invocation through IWorldConsoleAuthority.
    private static readonly IReadOnlyDictionary<string, RowSection> s_sections = BuildSections();

    // The one r1/cycle assignment-sequence builder both kits and looks reduce to — they differ only in which
    // WorldMutation kind wraps the built WorldRowAssignment and r1's own additive offset (the two sequences must not
    // land on the same index for the same tick, so each table keeps its own authored offset).
    private CommandResult BuildAssignment(in WireArgs args, int r1Offset, Func<WorldPrincipal, WorldRowAssignment, WorldMutation> toMutation, WorldPrincipal principal, string verb) {
        if (args.Is(
            index: 1,
            value: WorldSequence.R1
        )) {
            return link.Submit(
                mutation: toMutation(
                    principal,
                    new WorldRowAssignment(
                        Sequence: new WorldSequence(
                            Name: WorldSequence.R1,
                            Offset: r1Offset,
                            Step: 0f
                        ),
                        Rows: []
                    )
                ),
                echoes: echoes,
                verb: "world.assign"
            );
        }

        if (args.Is(
            index: 1,
            value: "cycle"
        )) {
            if (args.Count < 3) {
                return CommandResult.Error(output: $"[{verb}: cycle needs at least one name]");
            }

            return link.Submit(
                mutation: toMutation(
                    principal,
                    new WorldRowAssignment(
                        Sequence: new WorldSequence(
                            Name: WorldSequence.Index,
                            Offset: 0,
                            Step: 0f
                        ),
                        Rows: TailIdentifiers(
                            args: args,
                            start: 2
                        )
                    )
                ),
                echoes: echoes,
                verb: "world.assign"
            );
        }

        return CommandResult.Error(output: $"[{verb}: unknown sequence '{args[1].ToString()}' — r1 | cycle]");
    }
    private static IReadOnlyDictionary<string, RowSection> BuildSections() => new Dictionary<string, RowSection>(comparer: StringComparer.Ordinal) {
        // Keyed sections: set + remove + read (world.row.step's row lookup).
        ["kits"] = new RowSection(
        RowType: typeof(WorldKit),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldKit,
            toMutation: static (principal, kit) => new WorldMutation.UpsertKit(
                Kit: kit,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveKit(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldKit,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Kits
        )
    ),
        ["cameras"] = new RowSection(
        RowType: typeof(WorldCamera),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCamera,
            toMutation: static (principal, camera) => new WorldMutation.UpsertCamera(
                Camera: camera,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveCamera(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldCamera,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Cameras
        )
    ),
        ["screens"] = new RowSection(
        RowType: typeof(WorldScreen),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldScreen,
            toMutation: static (principal, screen) => new WorldMutation.UpsertScreen(
                Principal: principal,
                Screen: screen
            )
        ),
        Remove: RemoveByIndex(remove: static (principal, index) => new WorldMutation.RemoveScreen(
            Index: index,
            Principal: principal
        )),
        Read: ReadRowByIndex(
            info: WorldJsonContext.Default.WorldScreen,
            select: static server => server.Definition.Screens
        )
    ),
        ["machines"] = new RowSection(
        RowType: typeof(WorldMachine),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldMachine,
            toMutation: static (principal, machine) => new WorldMutation.UpsertMachine(principal, machine)
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveMachine(principal, name)),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldMachine,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Machines
        )
    ),
        ["speakers"] = new RowSection(
        RowType: typeof(WorldSpeaker),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSpeaker,
            toMutation: static (principal, speaker) => new WorldMutation.UpsertSpeaker(
                Principal: principal,
                Speaker: speaker
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveSpeaker(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldSpeaker,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Speakers
        )
    ),
        ["placements"] = new RowSection(
        RowType: typeof(WorldPlacement),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldPlacement,
            toMutation: static (principal, placement) => new WorldMutation.UpsertPlacement(
                Placement: placement,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemovePlacement(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldPlacement,
            keyOf: static row => row.Id,
            select: static server => server.Definition.Placements
        )
    ),
        ["creations"] = new RowSection(
        RowType: typeof(WorldPrototype),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldPrototype,
            toMutation: static (principal, creation) => new WorldMutation.UpsertCreation(
                Creation: creation,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveCreation(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldPrototype,
            keyOf: static row => row.Id,
            select: static server => server.Definition.Creations
        ),
        // The row's own "hash" is a DERIVED digest of its embedded document, recomputed at compose from the SAME
        // content a field/list edit here just changed — carrying the pre-edit digest forward would resubmit a row
        // that fails its own hash check (WorldServer.TryCanonicalizeDocument refuses a mismatch by name). Dropping
        // it before resubmission is what makes it absent, which composes exactly like the row's own whole-upsert
        // path already does for an unhashed submission: adopt the freshly recomputed canonical digest.
        DropOnEdit: ["hash"]
    ),
        ["tunes"] = new RowSection(
        RowType: typeof(WorldTune),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldTune,
            toMutation: static (principal, tune) => new WorldMutation.UpsertTune(
                Principal: principal,
                Tune: tune
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveTune(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldTune,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Tunes
        )
    ),
        ["patches"] = new RowSection(
        RowType: typeof(WorldPatch),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldPatch,
            toMutation: static (principal, patch) => new WorldMutation.UpsertPatch(
                Patch: patch,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemovePatch(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldPatch,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Patches
        )
    ),
        ["looks"] = new RowSection(
        RowType: typeof(WorldLook),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldLook,
            toMutation: static (principal, look) => new WorldMutation.UpsertLook(
                Look: look,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveLook(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldLook,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Looks
        )
    ),
        ["dynamics"] = new RowSection(
        RowType: typeof(DynamicsRow),
        Upsert: Upsert(
            info: WorldJsonContext.Default.DynamicsRow,
            toMutation: static (principal, row) => new WorldMutation.UpsertDynamics(
                Principal: principal,
                Row: row
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveDynamics(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.DynamicsRow,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Dynamics
        )
    ),
        ["curves"] = new RowSection(
        RowType: typeof(WorldCurveRow),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCurveRow,
            toMutation: static (principal, row) => new WorldMutation.UpsertCurve(
                Principal: principal,
                Row: row
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveCurve(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldCurveRow,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Curves
        )
    ),
        ["addons"] = new RowSection(
        RowType: typeof(WorldAddonRow),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldAddonRow,
            toMutation: static (principal, addon) => new WorldMutation.UpsertAddon(
                Addon: addon,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveAddon(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldAddonRow,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Addons
        )
    ),
        ["bindingOverlays"] = new RowSection(
        RowType: typeof(WorldBindingOverlay),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldBindingOverlay,
            toMutation: static (principal, overlay) => new WorldMutation.UpsertBindingOverlay(
                Overlay: overlay,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveBindingOverlay(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldBindingOverlay,
            keyOf: static row => row.Id,
            select: static server => server.Definition.BindingOverlays
        )
    ),
        ["state"] = new RowSection(
        RowType: typeof(WorldStateRow),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldStateRow,
            toMutation: static (principal, row) => new WorldMutation.UpsertStateRow(
                Principal: principal,
                Row: row
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveStateRow(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldStateRow,
            keyOf: static row => row.Name.ToString(),
            select: static server => server.Definition.State
        )
    ),
        ["rules"] = new RowSection(
        RowType: typeof(WorldRule),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldRule,
            toMutation: static (principal, rule) => new WorldMutation.UpsertWorldRule(
                Principal: principal,
                Rule: rule
            )
        ),
        Remove: RemoveByCellName(remove: static (principal, name) => new WorldMutation.RemoveWorldRule(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldRule,
            keyOf: static row => row.Name.ToString(),
            select: static server => (server.Definition.Rules ?? [])
        )
    ),
        ["hud.panels"] = new RowSection(
        RowType: typeof(WorldHudPanel),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldHudPanel,
            toMutation: static (principal, panel) => new WorldMutation.UpsertHudPanel(
                Panel: panel,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveHudPanel(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldHudPanel,
            keyOf: static row => row.Id,
            select: static server => server.Definition.Hud.Panels
        )
    ),
        ["views.layouts"] = new RowSection(
        RowType: typeof(WorldViewLayout),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldViewLayout,
            toMutation: static (principal, layout) => new WorldMutation.UpsertViewLayout(
                Layout: layout,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveViewLayout(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldViewLayout,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Views.Layouts
        )
    ),
        ["views.pipelines"] = new RowSection(
        RowType: typeof(WorldViewPipeline),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldViewPipeline,
            toMutation: static (principal, pipeline) => new WorldMutation.UpsertViewPipeline(
                Pipeline: pipeline,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveViewPipeline(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldViewPipeline,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Views.Pipelines
        )
    ),
        ["groups.kinds"] = new RowSection(
        RowType: typeof(WorldGroupKind),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldGroupKind,
            toMutation: static (principal, kind) => new WorldMutation.UpsertGroupKind(
                Kind: kind,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveGroupKind(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldGroupKind,
            keyOf: static row => row.Name,
            select: static server => (server.Definition.Groups?.Kinds ?? [])
        )
    ),
        ["interactions.interactions"] = new RowSection(
        RowType: typeof(WorldInteraction),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldInteraction,
            toMutation: static (principal, interaction) => new WorldMutation.UpsertInteraction(
                Interaction: interaction,
                Principal: principal
            )
        ),
        Remove: RemoveByCellName(remove: static (principal, name) => new WorldMutation.RemoveInteraction(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldInteraction,
            keyOf: static row => row.Name.ToString(),
            select: static server => (server.Definition.Interactions?.Interactions ?? [])
        )
    ),

        // Keyless sections: set only, plus read (world.row.step's row lookup — the whole section IS the row).
        ["motion"] = new RowSection(
        RowType: typeof(WorldMotionDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldMotionDefaults,
            toMutation: static (principal, motion) => new WorldMutation.SetMotion(
                Motion: motion,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldMotionDefaults,
            select: static server => server.Definition.Motion
        )
    ),
        ["render"] = new RowSection(
        RowType: typeof(WorldRenderDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldRenderDefaults,
            toMutation: static (principal, render) => new WorldMutation.SetRenderDefaults(
                Principal: principal,
                Render: render
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldRenderDefaults,
            select: static server => server.Definition.Render
        )
    ),
        ["audio"] = new RowSection(
        RowType: typeof(WorldAudioDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldAudioDefaults,
            toMutation: static (principal, audio) => new WorldMutation.SetAudioDefaults(
                Audio: audio,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldAudioDefaults,
            select: static server => server.Definition.Audio
        )
    ),
        ["authoring"] = new RowSection(
        RowType: typeof(WorldPlacementPolicyDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldPlacementPolicyDefaults,
            toMutation: static (principal, authoring) => new WorldMutation.SetAuthoringDefaults(
                Authoring: authoring,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldPlacementPolicyDefaults,
            select: static server => server.Definition.Authoring
        )
    ),
        ["collision"] = new RowSection(
        RowType: typeof(WorldCollision),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCollision,
            toMutation: static (principal, collision) => new WorldMutation.SetCollision(
                Collision: collision,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldCollision,
            select: static server => server.Definition.Collision
        )
    ),
        ["host"] = new RowSection(
        RowType: typeof(WorldHostDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldHostDefaults,
            toMutation: static (principal, host) => new WorldMutation.SetHostDefaults(
                Host: host,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldHostDefaults,
            select: static server => server.Definition.Host
        )
    ),
        // Authored payload is SECONDS (WorldInputHoldAuthoring), matching the document field itself; compiled to the
        // mutation's ticks wire shape against the ADDRESSED row's own current rate.
        ["inputHold"] = new RowSection(
        ReadsLiveDocument: true,
        RowType: typeof(WorldInputHoldAuthoring),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldInputHoldAuthoring,
            toMutation: static (server, principal, authoring) => new WorldMutation.SetInputHold(
                Principal: principal,
                Settings: authoring.Compile(ratePerSecond: ((uint)server.Definition.SimulationRateHz))
            )
        ),
        Remove: null,
        // Reads the AUTHORED-seconds shape back — the same type Upsert parses, matching the section's own set/read
        // symmetry (the compiled ticks form is a write-side-only derivation).
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldInputHoldAuthoring,
            select: static server => server.Definition.InputHold
        )
    ),
        ["hud.defaults"] = new RowSection(
        RowType: typeof(WorldHudDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldHudDefaults,
            toMutation: static (principal, defaults) => new WorldMutation.SetHudDefaults(
                Defaults: defaults,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldHudDefaults,
            select: static server => server.Definition.Hud.Defaults
        )
    ),
        ["spawnPoints"] = new RowSection(
        RowType: typeof(WorldSpawnPoint[]),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSpawnPointArray,
            toMutation: static (principal, spawns) => new WorldMutation.SetSpawns(
                Principal: principal,
                Spawns: spawns
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSpawnPointArray,
            select: static server => [.. server.Definition.SpawnPoints]
        )
    ),
        // A sub-row of a whole-section row submits its own field-scoped mutation, composed against the section as it
        // stands when the mutation applies — never a whole-section replacement built here from the live document,
        // which a sibling sub-row's edit queued in the same tick would revert.
        ["views.seatRig"] = new RowSection(
        RowType: typeof(WorldCameraProgram),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCameraProgram,
            toMutation: static (principal, rig) => new WorldMutation.SetViewSeatRig(
                Principal: principal,
                SeatRig: rig
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldCameraProgram,
            select: static server => server.Definition.Views.SeatRig
        )
    ),
        ["views.seatControl"] = new RowSection(
        RowType: typeof(WorldSeatViewControl),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            toMutation: static (principal, control) => new WorldMutation.SetViewSeatControl(
                Principal: principal,
                SeatControl: control
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            select: static server => server.Definition.Views.SeatControl
        )
    ),
        ["playerDefaults.seatLook"] = new RowSection(
        RowType: typeof(WorldSeatCameraFeel),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSeatCameraFeel,
            toMutation: static (principal, look) => new WorldMutation.SetPlayerSeatLook(
                Principal: principal,
                SeatLook: look
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSeatCameraFeel,
            select: static server => server.Definition.PlayerDefaults.SeatLook
        )
    ),
    };
    // world.kits: name, program, and the motion row's key scalars — the census this section never had.
    private static string DescribeKits(WorldServer server) {
        var kits = server.Definition.Kits;

        if (kits.Count == 0) {
            return "[world.kits: none declared]";
        }

        var builder = new StringBuilder(value: "[world.kits:");

        for (var index = 0; (index < kits.Count); index++) {
            var kit = kits[index];

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0)
                ? " "
                : " | ")}{kit.Name} program={kit.BodyMotionProgram} {DescribeMotion(motion: kit.Motion)} {DescribeTether(tether: kit.Tether)}"
            );
        }

        return builder.Append(value: ']').ToString();
    }
    // A kit's tether facet: the aim/rope tuning and its named channels — "tether=none" for a kit that carries no
    // rope at all (body.attach/body.detach/body.reel refuse it by name).
    private static string DescribeTether(WorldTether? tether) {
        if (tether is not { } facet) {
            return "tether=none";
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"tether(maxAnchorDistance={facet.MaxAnchorDistance:0.#####} aimHalfAngleDegrees={facet.AimHalfAngleDegrees:0.#####} lengthRate={facet.LengthRate:0.#####} minLength={facet.MinLength:0.#####} releaseVelocityScale={facet.ReleaseVelocityScale:0.#####} attach={(facet.AttachChannel ?? "none")} detach={(facet.DetachChannel ?? "none")} reel={(facet.ReelChannel ?? "none")} modeState={(facet.ModeState ?? "none")})"
        );
    }
    // A kit's shaping table: each row's mechanism in order — a named dynamics follower, the anisotropic decomposition (with
    // its own key scalars), or the whole-vector response law — echoed alongside the motion row so world.kits answers
    // "how does this kit feel" without a separate lookup.
    private static string DescribeShaping(WorldMotion motion) {
        var rows = motion.Shaping;

        if (rows is not { Count: > 0 }) {
            return "shaping=none";
        }

        var builder = new StringBuilder(value: "shaping=");

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (index > 0) {
                _ = builder.Append(value: '+');
            }

            if (row?.Dynamics is { Length: > 0 } name) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"dynamics:{name}");
            } else if ((row?.Across is { } across) && (row.Along is { } along)) {
                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"drive(engage={DescribeConvergence(value: along.Engage)} reversalRate={DescribeConvergence(value: along.ReversalRate)} release={DescribeConvergence(value: along.Release)} backwardSpeed={DescribeOptional(value: along.BackwardSpeed)} lateral={DescribeConvergence(value: across.Lateral)})"
                );
            } else if (row?.Along is { } responseAlong) {
                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"response(engage={DescribeConvergence(value: responseAlong.Engage)} release={DescribeConvergence(value: responseAlong.Release)})"
                );
            } else {
                _ = builder.Append(value: "invalid");
            }
        }

        return builder.ToString();
    }
    private static string DescribeConvergence(float? value) => (value is { } finite
        ? finite.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)
        : "instant"
    );
    // The generic "omitted" read-back for an optional authored scalar — none is a document fact worth showing
    // plainly, distinct from DescribeConvergence's own "instant" (an absent RATE, never an absent scalar).
    private static string DescribeOptional(float? value) => (value is { } finite
        ? finite.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)
        : "none"
    );
    // The ordered hold list's own kind/gravity/thrust per row — the vertical channel's whole authoring surface.
    private static string DescribeHolds(WorldMotion motion) {
        if (motion.Holds is not { Count: > 0 } holds) {
            return "holds=none";
        }

        var builder = new StringBuilder(value: "holds=(");

        for (var index = 0; (index < holds.Count); index++) {
            var hold = holds[index];

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{((index == 0) ? "" : ",")}{hold.Name}:{hold.Hold}");

            if (hold.Gravity is { } gravity) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"(rise={gravity.Rise:0.###} fall={gravity.Fall:0.###})");
            }
            if (hold.Envelope is { } envelope) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[envelope rise={DescribeOptional(value: envelope.RiseSpeed)} sink={envelope.SinkSpeed:0.###}]");
            }
            if (hold.Medium is { } medium) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[medium idleDrift={medium.IdleDrift:0.###} equilibriumOffset={medium.EquilibriumOffset:0.###} settleRate={medium.SettleRate:0.###}]");
            }
            if (hold.Thrust > 0f) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[thrust={hold.Thrust:0.###}]");
            }
        }

        return builder.Append(value: ')').ToString();
    }
    private static string DescribeMotion(WorldMotion motion) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"speed={motion.Speed.Value:0.###} turn={motion.Turn.Rate:0.###}{DescribeMaxPitch(turn: motion.Turn)} upTurn=({motion.UpTurn.Field:0.###}/{motion.UpTurn.Contact:0.###}) obstruction=({motion.Obstruction.Displacement:0.###}/{motion.Obstruction.IdleThreshold:0.###}/{motion.Obstruction.GraceSeconds:0.###}) groundStick={motion.GroundStick:0.###} {DescribeHolds(motion: motion)} {DescribeShaping(motion: motion)}"
    );
    // maxPitch is unread while pitchRate is zero (a planar drive frame), so the census omits it there rather than
    // echoing a clamp that governs nothing.
    private static string DescribeMaxPitch(WorldTurn turn) => (turn.PitchRate > 0f
        ? string.Create(provider: CultureInfo.InvariantCulture, handler: $" pitchMax={turn.MaxPitch:0.###}")
        : string.Empty
    );
    private CommandResult HandleAssign(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Usage(
                form: "kits|looks r1 | cycle <name> [<name>…]",
                verb: "world.assign"
            );
        }

        var principal = context.ActingPrincipal();
        var target = args[0].ToString();

        return target switch {
            "kits" => BuildAssignment(
            args: args,
            r1Offset: 1,
            toMutation: static (principal, assignment) => new WorldMutation.SetKitAssignment(
                Assignment: assignment,
                Principal: principal
            ),
            principal: principal,
            verb: "world.assign kits"
        ),
            "looks" => BuildAssignment(
            args: args,
            r1Offset: 129,
            toMutation: static (principal, assignment) => new WorldMutation.SetLookAssignment(
                Assignment: assignment,
                Principal: principal
            ),
            principal: principal,
            verb: "world.assign looks"
        ),
            _ => CommandResult.Error(output: $"[world.assign: unknown target '{target}' — kits|looks]"),
        };
    }
    private CommandResult HandleRemove(WorldServer server, CommandContext context, WireArgs args) {
        // The list-element form (world.row.remove <path> [<key>] <listPath> <selector>) is 3 tokens for a keyless
        // section, 4 for a keyed one — neither collides with the whole-row form's fixed 2, so the dispatch is by
        // count alone, no JSON-shape heuristic needed (every token here is a plain address, never a payload).
        if (args.Count is (3 or 4)) {
            return HandleListRemove(
                args: args,
                context: context,
                server: server
            );
        }

        if (args.Count != 2) {
            return CommandResult.Usage(
                form: "<path> <key> | <path> [<key>] <listPath> <selector>",
                verb: "world.row.remove"
            );
        }

        var path = args[0].ToString();
        var key = args[1].ToString();
        var principal = context.ActingPrincipal();

        if (string.Equals(
            a: path,
            b: PropertiesNamesPath,
            comparisonType: StringComparison.Ordinal
        )) {
            return link.Submit(
                mutation: new WorldMutation.SetProperty(
                    Name: key,
                    Principal: principal,
                    Remove: true
                ),
                echoes: echoes,
                verb: "world.row.remove"
            );
        }

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            return UnknownPath(
                path: path,
                verb: "world.row.remove"
            );
        }

        if (section.Remove is not { } remove) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: keyless (set only) — no remove]");
        }

        var outcome = remove(
            server,
            principal,
            key
        );

        return ((outcome.Error is { } error)
            ? CommandResult.Error(output: $"[world.row.remove: {path}: {error}]")
            : link.Submit(
                mutation: outcome.Mutation!,
                echoes: echoes,
                verb: "world.row.remove"
            )
        );
    }
    // world.row.remove's list-element form: locates the named list field (a plain dotted/bracketed path, resolving
    // through any intermediate selector — "document.shapes[name=forearmL].swings"), removes the ONE element
    // <selector> names, and submits the whole modified row through the SAME section Upsert the whole-row form uses.
    private CommandResult HandleListRemove(WorldServer server, CommandContext context, WireArgs args) {
        var path = args[0].ToString();

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            return UnknownPath(
                path: path,
                verb: "world.row.remove"
            );
        }

        var keyed = (section.Remove is not null);
        var expected = (keyed ? 4 : 3);

        if (args.Count != expected) {
            return CommandResult.Usage(
                form: (keyed ? $"{path} <key> <listPath> <selector>" : $"{path} <listPath> <selector>"),
                verb: "world.row.remove"
            );
        }

        var key = (keyed ? args[1].ToString() : string.Empty);
        var listPath = (keyed ? args[2].ToString() : args[1].ToString());
        var selectorText = args[(keyed ? 3 : 2)].ToString();
        var rowIdentity = RowIdentity(
            key: key,
            keyed: keyed,
            path: path
        );

        if (m_stepGuard.IsClaimed(
            rowIdentity: rowIdentity,
            window: server.NextInputTick
        )) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: row '{rowIdentity}' already has an edit buffered this tick — fence with world.wait]");
        }

        var read = section.Read(
            server,
            key
        );

        if (read.Error is { } readError) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: {readError}]");
        }

        if (!TryResolveListContainer(
            array: out var array,
            error: out var containerError,
            listPath: listPath,
            root: read.Row!,
            verb: "world.row.remove"
        )) {
            return containerError;
        }

        var containerName = ContainerName(listPath: listPath);

        if (!WorldRowFieldPath.TryParseSelector(
            containerName: containerName,
            error: out var selectorParseError,
            segment: out var selector,
            text: selectorText
        )) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: {selectorText}: {selectorParseError}]");
        }

        if (!WorldRowFieldPath.TryResolveArrayElement(
            array: array!,
            error: out var selectorError,
            index: out var index,
            path: listPath,
            segment: selector
        )) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: {selectorError}]");
        }

        array!.RemoveAt(index: index);

        DropEditArtifacts(
            row: read.Row!,
            section: section
        );

        var principal = context.ActingPrincipal();
        var outcome = section.Upsert(
            server,
            principal,
            read.Row!.ToJsonString()
        );

        if (outcome.Error is { } upsertError) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: {listPath}: {upsertError}]");
        }

        m_stepGuard.Claim(rowIdentity: rowIdentity);

        return link.Submit(
            mutation: outcome.Mutation!,
            echoes: echoes,
            verb: "world.row.remove"
        );
    }
    private CommandResult HandleAdd(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count < 3) {
            return CommandResult.Usage(
                form: "<path> [<key>] <listPath> <json> [after=<selector>]",
                verb: "world.row.add"
            );
        }

        var path = args[0].ToString();

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            return UnknownPath(
                path: path,
                verb: "world.row.add"
            );
        }

        var keyed = (section.Remove is not null);
        var addressTokens = (keyed ? 3 : 2);

        if (args.Count <= addressTokens) {
            return CommandResult.Usage(
                form: (keyed ? $"{path} <key> <listPath> <json> [after=<selector>]" : $"{path} <listPath> <json> [after=<selector>]"),
                verb: "world.row.add"
            );
        }

        var key = (keyed ? args[1].ToString() : string.Empty);
        var listPath = (keyed ? args[2].ToString() : args[1].ToString());
        var leadingTokens = (addressTokens + 1);
        var lastIndex = (args.Count - 1);
        var hasAfter = ((lastIndex >= addressTokens) && args[lastIndex].StartsWith(value: "after=", comparisonType: StringComparison.OrdinalIgnoreCase));
        var afterText = (hasAfter ? args[lastIndex][6..].ToString() : null);
        // RawBetween carries no preserveQuotes escape hatch (unlike RawAfter): an after= clause's element JSON that is
        // itself a single bare quoted string loses its quotes there. Every list this door reaches holds record
        // elements (an object literal), never a bare string, so this is inert in practice.
        var json = (hasAfter
            ? WorldCommandArguments.RawBetween(
                args: in args,
                context: context,
                leadingTokens: leadingTokens,
                trailingTokens: 1
            )
            : WorldCommandArguments.RawAfter(
                args: in args,
                context: context,
                preserveQuotes: true,
                tokens: leadingTokens
            )
        );

        if (string.IsNullOrWhiteSpace(value: json)) {
            return CommandResult.Error(output: $"[world.row.add: {path}: {listPath}: no element JSON given]");
        }

        var rowIdentity = RowIdentity(
            key: key,
            keyed: keyed,
            path: path
        );

        if (m_stepGuard.IsClaimed(
            rowIdentity: rowIdentity,
            window: server.NextInputTick
        )) {
            return CommandResult.Error(output: $"[world.row.add: {path}: row '{rowIdentity}' already has an edit buffered this tick — fence with world.wait]");
        }

        var read = section.Read(
            server,
            key
        );

        if (read.Error is { } readError) {
            return CommandResult.Error(output: $"[world.row.add: {path}: {readError}]");
        }

        if (!TryResolveListContainer(
            array: out var array,
            error: out var containerError,
            listPath: listPath,
            root: read.Row!,
            verb: "world.row.add"
        )) {
            return containerError;
        }

        JsonNode? element;

        try {
            element = JsonNode.Parse(json: json);
        } catch (JsonException exception) {
            return CommandResult.Error(output: $"[world.row.add: {path}: {listPath}: {exception.Message}]");
        }

        if (element is null) {
            return CommandResult.Error(output: $"[world.row.add: {path}: {listPath}: the JSON parsed to null]");
        }

        var insertAt = array!.Count;

        if (hasAfter) {
            var containerName = ContainerName(listPath: listPath);

            if (!WorldRowFieldPath.TryParseSelector(
                containerName: containerName,
                error: out var selectorParseError,
                segment: out var afterSegment,
                text: afterText!
            )) {
                return CommandResult.Error(output: $"[world.row.add: {path}: after={afterText}: {selectorParseError}]");
            }

            if (!WorldRowFieldPath.TryResolveArrayElement(
                array: array,
                error: out var selectorError,
                index: out var afterIndex,
                path: listPath,
                segment: afterSegment
            )) {
                return CommandResult.Error(output: $"[world.row.add: {path}: {selectorError}]");
            }

            insertAt = (afterIndex + 1);
        }

        array.Insert(
            index: insertAt,
            item: element
        );

        DropEditArtifacts(
            row: read.Row!,
            section: section
        );

        var principal = context.ActingPrincipal();
        var outcome = section.Upsert(
            server,
            principal,
            read.Row!.ToJsonString()
        );

        if (outcome.Error is { } upsertError) {
            return CommandResult.Error(output: $"[world.row.add: {path}: {listPath}: {upsertError}]");
        }

        m_stepGuard.Claim(rowIdentity: rowIdentity);

        return link.Submit(
            mutation: outcome.Mutation!,
            echoes: echoes,
            verb: "world.row.add"
        );
    }
    // Resolves listPath's OWN array node off root — every segment (including intermediate selectors, e.g. the
    // "shapes[name=forearmL]" in "document.shapes[name=forearmL].swings") is walked, landing ON the list field
    // itself rather than one level short of it, which is what world.row.add/.remove operate over.
    private static bool TryResolveListContainer(JsonNode root, string listPath, string verb, out JsonArray? array, out CommandResult error) {
        array = null;

        if (!WorldRowFieldPath.TryParse(
            error: out var parseError,
            path: listPath,
            segments: out var segments
        )) {
            error = CommandResult.Error(output: $"[{verb}: {parseError}]");

            return false;
        }

        if (!WorldRowFieldPath.TryNavigate(
            container: out var node,
            error: out var navError,
            path: listPath,
            root: root,
            segments: segments
        )) {
            error = CommandResult.Error(output: $"[{verb}: {navError}]");

            return false;
        }

        if (node is not JsonArray resolved) {
            error = CommandResult.Error(output: $"[{verb}: '{listPath}' is not a list]");

            return false;
        }

        array = resolved;
        error = default;

        return true;
    }
    // The list field's own name — the last dotted component of listPath, bracket stripped — the same discriminator
    // TryResolveArrayElement's refusal quotes ("no element of 'shapes' has …").
    private static string ContainerName(string listPath) {
        var dot = listPath.LastIndexOf(value: '.');
        var last = ((dot < 0) ? listPath : listPath[(dot + 1)..]);
        var bracket = last.IndexOf(value: '[');

        return ((bracket < 0) ? last : last[..bracket]);
    }
    private CommandResult HandleSet(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count < 1) {
            return CommandResult.Usage(
                form: "<path> <json>",
                verb: "world.row.set"
            );
        }

        var path = args[0].ToString();
        var principal = context.ActingPrincipal();

        if (string.Equals(
            a: path,
            b: PropertiesNamesPath,
            comparisonType: StringComparison.Ordinal
        )) {
            if (args.Count != 2) {
                return CommandResult.Usage(
                    form: $"{PropertiesNamesPath} <name>",
                    verb: "world.row.set"
                );
            }

            return link.Submit(
                mutation: new WorldMutation.SetProperty(
                    Principal: principal,
                    Name: args[1].ToString(),
                    Remove: false
                ),
                echoes: echoes,
                verb: "world.row.set"
            );
        }

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            return UnknownPath(
                path: path,
                verb: "world.row.set"
            );
        }

        // The literal-field form (world.row.set <path> <key> <fieldPath> <json> for a keyed section, <path>
        // <fieldPath> <json> for a keyless one) shares this verb name with the whole-row form above. Every RowType
        // is a record — its whole-row payload is always a JSON object or array literal — so the SECOND token's own
        // first character discriminates the two grammars without ambiguity: a bare key or a dotted field path never
        // starts with '{' or '['.
        if (
            (args.Count >= 2) &&
            !LooksLikeJsonContainer(token: args[1])
        ) {
            return HandleLiteralSet(
                args: args,
                context: context,
                path: path,
                section: section,
                server: server
            );
        }

        var raw = WorldCommandArguments.RawAfter(
            args: in args,
            context: context,
            tokens: 2
        );

        if (string.IsNullOrWhiteSpace(value: raw)) {
            return CommandResult.Usage(
                form: $"{path} <json>",
                verb: "world.row.set"
            );
        }

        var outcome = section.Upsert(
            server,
            principal,
            raw
        );

        return ((outcome.Error is { } error)
            ? CommandResult.Error(output: $"[world.row.set: {path}: {error}]")
            : link.Submit(
                mutation: outcome.Mutation!,
                echoes: echoes,
                verb: "world.row.set"
            )
        );
    }
    // The whole-row form's own discriminator: a RowSection's RowType is always a record, so its inline-JSON payload
    // is always an object or array literal — never how a bare key or a dotted field path is spelled.
    private static bool LooksLikeJsonContainer(ReadOnlySpan<char> token) => ((token.Length > 0) && ((token[0] == '{') || (token[0] == '[')));
    // world.row.set's literal-field form: reads the addressed row, replaces ONE field in place (creating an absent
    // optional member; an index/selector segment must already exist — see WorldRowFieldPath.TrySetLeaf), and submits
    // the whole modified row through the SAME section Upsert the whole-row form uses — so the spliced field crosses
    // the row's own JsonTypeInfo exactly once, at reparse, which is where its declared shape (including a bindable
    // field's "state.row.key" string arm) is actually validated.
    private CommandResult HandleLiteralSet(WorldServer server, CommandContext context, WireArgs args, string path, RowSection section) {
        var keyed = (section.Remove is not null);

        if (
            keyed &&
            (args.Count < 3)
        ) {
            return CommandResult.Usage(
                form: $"{path} <key> <fieldPath> <json>",
                verb: "world.row.set"
            );
        }

        var key = (keyed ? args[1].ToString() : string.Empty);
        var fieldPath = (keyed ? args[2].ToString() : args[1].ToString());
        var leadingTokens = (keyed ? 4 : 3);
        // preserveQuotes: a literal field value is often a bare JSON string (an enum name, a "state.row.key" binding,
        // a text field) — the whole tail IS then exactly one quoted token, which RawAfter's default unwrap would
        // strip into invalid JSON (the payload the whole-row form always carries, an object or array literal, is
        // never a single quoted token, so it never hit this).
        var json = WorldCommandArguments.RawAfter(
            args: in args,
            context: context,
            preserveQuotes: true,
            tokens: leadingTokens
        );

        if (string.IsNullOrWhiteSpace(value: json)) {
            return CommandResult.Usage(
                form: (keyed ? $"{path} <key> <fieldPath> <json>" : $"{path} <fieldPath> <json>"),
                verb: "world.row.set"
            );
        }

        var rowIdentity = RowIdentity(
            key: key,
            keyed: keyed,
            path: path
        );

        if (m_stepGuard.IsClaimed(
            rowIdentity: rowIdentity,
            window: server.NextInputTick
        )) {
            return CommandResult.Error(output: $"[world.row.set: {path}: row '{rowIdentity}' already has an edit buffered this tick — a second edit composes from the same pre-drain base and would revert the first; fence with world.wait, or compose one JSON row with world.row.set {path} <json>{(keyed ? " (the key rides inside the JSON)" : string.Empty)}]");
        }

        var read = section.Read(
            server,
            key
        );

        if (read.Error is { } readError) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {readError}]");
        }

        JsonNode? replacement;

        try {
            replacement = JsonNode.Parse(json: json);
        } catch (JsonException exception) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {fieldPath}: {exception.Message}]");
        }

        if (!WorldRowFieldPath.TryParse(
            error: out var parseError,
            path: fieldPath,
            segments: out var segments
        )) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {parseError}]");
        }

        var last = segments[^1];

        if (!WorldRowFieldPath.TryNavigate(
            container: out var container,
            error: out var navError,
            path: fieldPath,
            root: read.Row!,
            segments: segments.AsSpan(start: 0, length: (segments.Length - 1))
        )) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {navError}]");
        }

        if (!WorldRowFieldPath.TrySetLeaf(
            container: container!,
            error: out var setError,
            last: last,
            path: fieldPath,
            replacement: replacement
        )) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {setError}]");
        }

        DropEditArtifacts(
            row: read.Row!,
            section: section
        );

        var principal = context.ActingPrincipal();
        var outcome = section.Upsert(
            server,
            principal,
            read.Row!.ToJsonString()
        );

        if (outcome.Error is { } upsertError) {
            return CommandResult.Error(output: $"[world.row.set: {path}: {fieldPath}: {upsertError}]");
        }

        m_stepGuard.Claim(rowIdentity: rowIdentity);

        return link.Submit(
            mutation: outcome.Mutation!,
            echoes: echoes,
            verb: "world.row.set"
        );
    }
    // The row-edit window guard's own identity string — "path.key" for a keyed section, bare "path" for a keyless
    // one — the SAME spelling world.row.step derives from its own combined address, so a step and a literal edit to
    // the same row in one tick window collide under the identical guard.
    private static string RowIdentity(string path, string key, bool keyed) => (keyed ? $"{path}.{key}" : path);
    // rules and interactions key their Remove mutation by the validated CellName type rather than a plain
    // string.
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> RemoveByCellName(Func<WorldPrincipal, CellName, WorldMutation> remove) {
        return (_, principal, key) => {
            if (!CellName.TryParse(
                candidate: key,
                name: out var name,
                reason: out var reason
            )) {
                return RowOutcome.Fail(error: $"'{key}' {reason}");
            }

            return RowOutcome.Ok(mutation: remove(
                arg1: principal,
                arg2: name
            ));
        };
    }
    // screens is the one section keyed by an integer index rather than a string name.
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> RemoveByIndex(Func<WorldPrincipal, int, WorldMutation> remove) {
        return (_, principal, key) => {
            if (!CommandArgs.TryParseInt(
                text: key,
                value: out var index
            )) {
                return RowOutcome.Fail(error: $"bad index '{key}' — an integer");
            }

            return RowOutcome.Ok(mutation: remove(
                arg1: principal,
                arg2: index
            ));
        };
    }
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> RemoveByName(Func<WorldPrincipal, string, WorldMutation> remove) {
        return (_, principal, key) => RowOutcome.Ok(mutation: remove(
            arg1: principal,
            arg2: key
        ));
    }
    // Materializes the trailing tokens from <paramref name="start"/> onward as the assignment's identifier array —
    // the ONE place world.assign needs each row name separately rather than the joined free-text tail RawAfter gives.
    private static DocumentIdentifier[] TailIdentifiers(in WireArgs args, int start) {
        var count = args.Count;

        if (start >= count) {
            return [];
        }

        var identifiers = new DocumentIdentifier[(count - start)];

        for (var index = start; (index < count); index++) {
            identifiers[(index - start)] = new DocumentIdentifier(value: args[index].ToString());
        }

        return identifiers;
    }
    // Every admissible path, sections plus the one bare-name exception, sorted for a stable, greppable refusal.
    private static CommandResult UnknownPath(string verb, string path) {
        var admissible = string.Join(
            separator: ", ",
            values: s_sections.Keys.Append(element: PropertiesNamesPath).OrderBy(
                keySelector: static name => name,
                comparer: StringComparer.Ordinal
            )
        );

        return CommandResult.Error(output: $"[{verb}: unknown path '{path}' — {admissible}]");
    }

    /// <summary>Composes the <c>world.row.set</c> mutation for one dotted path and raw JSON tail WITHOUT submitting
    /// it — the seam the routed twin (<c>player.row.set</c>, which follows a crossed seat's authority route) reuses,
    /// so the routed grammar and the local grammar can never drift. A section whose mutation composes against the
    /// addressed world's own live document is refused by name (a routed write's document lives at the destination),
    /// and so is the bare-name <c>properties.names</c> exception, which carries no JSON row.</summary>
    /// <param name="path">The dotted document member path (the <c>world.row.set</c> vocabulary).</param>
    /// <param name="json">The raw JSON row tail.</param>
    /// <param name="principal">The composing principal. Informational for a routed submission — the destination
    /// re-stamps the envelope with the traveler's own transfer principal before admission.</param>
    /// <param name="mutation">The composed mutation, on success.</param>
    /// <param name="error">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the mutation composed.</returns>
    public static bool TryComposeRoutedSet(string path, string json, WorldPrincipal principal, out WorldMutation? mutation, out string error) {
        mutation = null;

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            error = $"unknown path '{path}' — {string.Join(
                separator: ", ",
                values: s_sections.Keys.Where(predicate: static key => !s_sections[key].ReadsLiveDocument).Order(comparer: StringComparer.Ordinal)
            )}";

            return false;
        }

        if (section.ReadsLiveDocument) {
            error = $"'{path}' composes against the addressed world's own live document, which a routed write cannot read — author it on the destination's console";

            return false;
        }

        var outcome = section.Upsert(
            arg1: null!,
            arg2: principal,
            arg3: json
        );

        if (outcome.Mutation is not { } composed) {
            error = (outcome.Error ?? "the row did not parse");

            return false;
        }

        mutation = composed;
        error = string.Empty;

        return true;
    }

    /// <summary>Composes the <c>world.row.set</c> mutation for one dotted path and one row read off the live document
    /// and edited in place — the read-modify-whole-row shape the literal field/list doors above take, exposed for a
    /// module that composes such rows outside this one (<c>creation.sculpt</c>, which resubmits every row a sculpt's
    /// patch touched). Strips the section's edit artifacts (<c>creations</c>' derived <c>hash</c>) exactly as those
    /// doors do, and composes under <paramref name="principal"/> — the caller passes the identity its own ingress
    /// stamped (<c>context.ActingPrincipal()</c>), never one it constructed. Composing is not submitting: the caller
    /// still owns the <see cref="WorldRowStepWindowGuard"/> claim and the link submission.</summary>
    /// <param name="server">The addressed row's server (the handful of sections whose mutation reads its live document).</param>
    /// <param name="path">The dotted document member path (the <c>world.row.set</c> vocabulary).</param>
    /// <param name="row">The edited row, as read off the live document and modified.</param>
    /// <param name="principal">The acting principal the mutation carries.</param>
    /// <param name="mutation">The composed mutation, on success.</param>
    /// <param name="error">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the mutation composed.</returns>
    public static bool TryComposeEditedRow(WorldServer server, string path, JsonNode row, WorldPrincipal principal, out WorldMutation? mutation, out string error) {
        mutation = null;

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            error = UnknownPath(
                path: path,
                verb: "world.row.set"
            ).Output;

            return false;
        }

        DropEditArtifacts(
            row: row,
            section: section
        );

        var outcome = section.Upsert(
            arg1: server,
            arg2: principal,
            arg3: row.ToJsonString()
        );

        if (outcome.Mutation is not { } composed) {
            error = (outcome.Error ?? "the row did not parse");

            return false;
        }

        mutation = composed;
        error = string.Empty;

        return true;
    }
    /// <summary>Composes the whole-row <c>world.row.remove</c> mutation for one keyed section and key without
    /// submitting it — the remove twin of <see cref="TryComposeEditedRow"/>. A keyless section (set only) and an
    /// unknown path are refused by name exactly as the verb refuses them.</summary>
    /// <param name="server">The addressed row's server.</param>
    /// <param name="path">The dotted document member path.</param>
    /// <param name="key">The row key.</param>
    /// <param name="principal">The acting principal the mutation carries.</param>
    /// <param name="mutation">The composed mutation, on success.</param>
    /// <param name="error">The named refusal, on failure.</param>
    /// <returns><see langword="true"/> when the mutation composed.</returns>
    public static bool TryComposeRemove(WorldServer server, string path, string key, WorldPrincipal principal, out WorldMutation? mutation, out string error) {
        mutation = null;

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            error = UnknownPath(
                path: path,
                verb: "world.row.remove"
            ).Output;

            return false;
        }

        if (section.Remove is not { } remove) {
            error = $"{path}: keyless (set only) — no remove";

            return false;
        }

        var outcome = remove(
            arg1: server,
            arg2: principal,
            arg3: key
        );

        if (outcome.Mutation is not { } composed) {
            error = (outcome.Error ?? "the key did not parse");

            return false;
        }

        mutation = composed;
        error = string.Empty;

        return true;
    }
    /// <summary>The row-edit window guard's identity for one row — <c>path.key</c> for a keyed section, the bare
    /// <c>path</c> for a keyless one — the spelling every read-modify-whole-row door here claims under, so another
    /// module's whole-row resubmission of the same row collides with a pending literal/list/step edit by name.</summary>
    /// <param name="path">The dotted document member path.</param>
    /// <param name="key">The row key, or null for a keyless section.</param>
    public static string RowIdentityOf(string path, string? key) => RowIdentity(
        key: (key ?? string.Empty),
        keyed: (key is not null),
        path: path
    );

    // Type-erased upsert factory (server-agnostic form): parses <paramref name="info"/>'s shape from the raw JSON
    // tail and hands the parsed value to <paramref name="toMutation"/> — the ONE generic seam most of the section
    // table closes over.
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> Upsert<T>(JsonTypeInfo<T> info, Func<WorldPrincipal, T, WorldMutation> toMutation) {
        return (_, principal, raw) => {
            if (!WorldJsonPayload.TryParse(
                error: out var error,
                info: info,
                json: raw,
                value: out var value
            )) {
                return RowOutcome.Fail(error: error);
            }

            return RowOutcome.Ok(mutation: toMutation(
                arg1: principal,
                arg2: value
            ));
        };
    }
    // The server-reading form: for the handful of sections whose mutation composes against the addressed row's own
    // live document state (inputHold's rate, the two views sub-rows, playerDefaults.seatLook).
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> Upsert<T>(JsonTypeInfo<T> info, Func<WorldServer, WorldPrincipal, T, WorldMutation> toMutation) {
        return (server, principal, raw) => {
            if (!WorldJsonPayload.TryParse(
                error: out var error,
                info: info,
                json: raw,
                value: out var value
            )) {
                return RowOutcome.Fail(error: error);
            }

            return RowOutcome.Ok(mutation: toMutation(
                arg1: server,
                arg2: principal,
                arg3: value
            ));
        };
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.set",
            description: "Upserts ANY document row or section by its dotted MEMBER PATH — the document's own camelCase JSON names (see puck schema for payload shapes): world.row.set <path> <json>. Keyed sections (kits, cameras, screens, speakers, placements, creations, tunes, patches, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, views.pipelines, groups.kinds, interactions.interactions) upsert one row addressed by its own key; keyless sections (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, views.seatControl, playerDefaults.seatLook) replace the whole row. ONE grammar exception: properties.names takes a BARE NAME token, not JSON — world.row.set properties.names <name> declares it idempotently. A SECOND, LITERAL form sets ONE field inside a row instead of the whole thing: world.row.set <path> <key> <fieldPath> <json> for a keyed section, world.row.set <path> <fieldPath> <json> for a keyless one — discriminated from the whole-row form by the second token's own shape (a bare key/field path never starts with '{' or '['). <fieldPath> is the same dotted/bracketed grammar world.row.step and world.row read — a numeric index (shapes[3]) or a name/id-addressed selector (shapes[name=forearmL], palette[1].specular), refused by name when a selector matches none or more than one element, listing the candidates. Composes the modified row and submits it through the SAME Upsert the whole-row form uses, so a field's own declared type (a DocumentVector3's [x,y,z]-or-'state.row.key' binding-string arm, a nullable field's JSON null to clear) is validated at reparse exactly as it always is. Two edits to the SAME row in one tick window collide — the second composes from the same pre-drain base and would revert the first — and are refused by name; fence with world.wait, or compose one JSON row with the whole-row form. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary like every WorldMutation; a full-document revalidation rejects loudly. This verb performs NO schema validation of its own — a JSON parse failure echoes inline and submits nothing; every semantic check still runs at apply.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.row.set"
                )) {
                    return error;
                }

                return HandleSet(
                    args: args,
                    context: context,
                    server: server
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.remove",
            description: "Removes ONE row from a KEYED document section by its dotted MEMBER PATH and key: world.row.remove <path> <key>. Keyed sections are the same set world.row.set upserts into (kits, cameras, screens [key is the integer index], speakers, placements, creations, tunes, patches, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, views.pipelines, groups.kinds, interactions.interactions), plus properties.names (BARE NAME token — world.row.remove properties.names <name>). A KEYLESS path (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, playerDefaults.seatLook) has no remove — it is refused by name. A SECOND form removes ONE ELEMENT of a list field instead of a whole row: world.row.remove <path> <key> <listPath> <selector> for a keyed section, world.row.remove <path> <listPath> <selector> for a keyless one (discriminated by argument count — 4 vs 3 — never by JSON shape, since neither token carries a payload). <listPath> is the dotted/bracketed path TO the list field (document.shapes, document.shapes[name=forearmL].swings); <selector> is a bare 0-based index or field=value, refused by name when it names none or more than one element, listing the candidates. Composes and submits a whole-row upsert through the SAME section table the row-level form uses; two edits to the same row in one tick window collide and are refused by name — fence with world.wait. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary; rejected loudly if no row carries that key.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.row.remove"
                )) {
                    return error;
                }

                return HandleRemove(
                    args: args,
                    context: context,
                    server: server
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.kits",
            description: "Reports the kit census (Immediate): one segment per declared kit row — name, body motion program, the motion row's key movement scalars, holds, and planar shaping, and the kit's tether facet ('tether=none' for a kit that carries no rope). The kits section's own read-back (world.row.set kits/world.row.remove kits has no listing of its own otherwise).",
            handler: (context, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: "[world.kits: no arguments — reports the kit census]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.kits"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeKits(server: server));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.assign",
            description: "Sets a row→entity assignment sequence, keyed by which table it targets: world.assign kits|looks r1 | cycle <name> [<name>…]. r1 selects from every row of that table; cycle walks the named row view by index.",
            handler: (context, args) => HandleAssign(
                args: args,
                context: context
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.row.step",
            description: "Steps ONE FIELD inside a document row or section by a delta — world.row.step <path> <delta>, one level deeper than world.row.set's own whole-row/whole-section path. <path> is <section>.<field> for a keyless section (render.sharpness) or <section>.<key>.<field> for a keyed one (creations.myrow.document.shapes[3].material, creations.myrow.document.shapes[name=forearmL].rounding) — the same section table world.row.set resolves against, and the same [n]/[field=value] list-selector grammar world.row.set's literal form and world.row read (a selector refused by name when it matches none or more than one element, listing the candidates). Field-type semantics: a number adds delta, typed by the field's real CLR type (an integer field steps in exact integer arithmetic, a float/double field in floating point — a fractional step on a whole-numbered float lands, and an out-of-range integer step refuses by name rather than throwing); a JSON boolean toggles on any nonzero delta; a named enum (the row's own C# member spelling) cycles forward/backward by delta's sign, wrapping. A vector, a nested object, or a plain (non-enum) string refuses by name. Bindable: a chord row carries the delta as a constant Axis1D value in place of the argument; the typed form takes an explicit numeric token. Buffers and applies through the SAME section Upsert world.row.set uses, at the tick boundary; a full-document revalidation still gates the result, and the accept/reject narration arrives there (no synchronous applied-result echo; a drain rejection additionally prints a per-verb [world.row.step: …] line). A second step against the same row in one tick window is refused by name — both would compose from the same pre-drain base and the later would revert the earlier; fence with world.wait between steps.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.row.step"
                )) {
                    return error;
                }

                return HandleStep(
                    args: args,
                    context: context,
                    server: server
                );
            },
            routing: CommandRouting.Simulation,
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.add",
            description: "Inserts ONE element into a LIST field inside a row: world.row.add <path> <key> <listPath> <json> [after=<selector>] for a keyed section, world.row.add <path> <listPath> <json> [after=<selector>] for a keyless one. <listPath> is the dotted/bracketed path TO the list field (document.shapes, document.palette, document.shapes[name=forearmL].swings — the same grammar world.row.set's literal form and world.row.step resolve a field through, walked all the way to the list itself); <json> is the new element's inline JSON. Omitting after= appends; after=<n> inserts after that 0-based index; after=<field>=<value> inserts after the element whose own field equals value, refused by name when it names none or more than one, listing the candidates. Composes and submits a whole-row upsert through the SAME section table world.row.set uses; two edits to the same row in one tick window collide and are refused by name — fence with world.wait. Buffers and applies at the tick boundary; a full-document revalidation rejects loudly.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.row.add"
                )) {
                    return error;
                }

                return HandleAdd(
                    args: args,
                    context: context,
                    server: server
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row",
            description: "Reads back a row, or one field inside it, as canonical JSON (Immediate): world.row <path> <key> [<fieldPath>] for a keyed section, world.row <path> [<fieldPath>] for a keyless one. <fieldPath> is the same dotted/bracketed grammar world.row.set's literal form and world.row.step resolve a field through (document.shapes[name=forearmL].rounding), and may end at a bare list field — world.row echoes '[world.row <index>: <name-or-id> <json>]' one line per element instead of one JSON blob. Every set/add/remove echoes its own 'old -> new' outcome at the tick boundary the same way world.row.step does; this verb reads the CURRENT value directly, with no mutation. An unknown path/key/field is refused by name.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.row"
                )) {
                    return error;
                }

                return HandleRead(
                    args: args,
                    server: server
                );
            }
        );
    }
    private CommandResult HandleRead(WorldServer server, WireArgs args) {
        if (args.Count < 1) {
            return CommandResult.Usage(
                form: "<path> [<key>] [<fieldPath>]",
                verb: "world.row"
            );
        }

        var path = args[0].ToString();

        if (!s_sections.TryGetValue(
            key: path,
            value: out var section
        )) {
            return UnknownPath(
                path: path,
                verb: "world.row"
            );
        }

        var keyed = (section.Remove is not null);
        string key;
        string? fieldPath;

        if (keyed) {
            if (args.Count < 2) {
                return CommandResult.Usage(
                    form: $"{path} <key> [<fieldPath>]",
                    verb: "world.row"
                );
            }

            key = args[1].ToString();
            fieldPath = ((args.Count >= 3) ? args[2].ToString() : null);
        } else {
            key = string.Empty;
            fieldPath = ((args.Count >= 2) ? args[1].ToString() : null);
        }

        var read = section.Read(
            server,
            key
        );

        if (read.Error is { } readError) {
            return CommandResult.Error(output: $"[world.row: {path}: {readError}]");
        }

        var node = read.Row!;

        if (fieldPath is not { Length: > 0 }) {
            // The whole-row echo is what the whole-row set form accepts back with a field changed, so it omits the
            // same derived self-digest a field edit strips (a creation's hash, recomputed from the content it would
            // no longer match); the digest itself stays readable by field path.
            DropEditArtifacts(
                row: node,
                section: section
            );
        } else {
            if (!WorldRowFieldPath.TryParse(
                error: out var parseError,
                path: fieldPath,
                segments: out var segments
            )) {
                return CommandResult.Error(output: $"[world.row: {path}: {parseError}]");
            }

            var last = segments[^1];

            if (!WorldRowFieldPath.TryNavigate(
                container: out var container,
                error: out var navError,
                path: fieldPath,
                root: node,
                segments: segments.AsSpan(start: 0, length: (segments.Length - 1))
            )) {
                return CommandResult.Error(output: $"[world.row: {path}: {navError}]");
            }

            if (!WorldRowFieldPath.TryGetLeaf(
                container: container!,
                error: out var leafError,
                last: last,
                leaf: out var leaf,
                path: fieldPath
            )) {
                return CommandResult.Error(output: $"[world.row: {path}: {leafError}]");
            }

            node = leaf!;
        }

        if (node is JsonArray listing) {
            return new CommandResult(Output: DescribeListing(
                array: listing,
                fieldPath: fieldPath,
                key: (keyed ? key : null),
                path: path
            ));
        }

        var keySuffix = (keyed ? $" {key}" : string.Empty);
        var fieldSuffix = ((fieldPath is { Length: > 0 }) ? $".{fieldPath}" : string.Empty);

        return new CommandResult(Output: $"[world.row: {path}{keySuffix}{fieldSuffix} = {node.ToJsonString()}]");
    }
    // A list field's own read-back: one summary line per element (its own compact JSON — the same text
    // world.row.set's literal form would accept back), headed by a discriminator (name, else id, else "-") so a
    // long list's elements can be told apart without reading every field.
    private static string DescribeListing(string path, string? key, string? fieldPath, JsonArray array) {
        var keySuffix = ((key is { Length: > 0 }) ? $" {key}" : string.Empty);
        var fieldSuffix = ((fieldPath is { Length: > 0 }) ? $".{fieldPath}" : string.Empty);
        var builder = new StringBuilder(value: $"[world.row: {path}{keySuffix}{fieldSuffix}: {array.Count} element(s)]");

        for (var index = 0; (index < array.Count); index++) {
            var element = array[index];

            _ = builder.Append(value: Environment.NewLine);
            _ = builder.Append(value: $"[world.row {index}: {DescribeDiscriminator(element: element)} {(element?.ToJsonString() ?? "null")}]");
        }

        return builder.ToString();
    }
    private static string DescribeDiscriminator(JsonNode? element) {
        if (element is not JsonObject obj) {
            return "-";
        }

        if (
            obj.TryGetPropertyValue(propertyName: "name", jsonNode: out var name) &&
            (name is not null)
        ) {
            return name.ToJsonString();
        }

        if (
            obj.TryGetPropertyValue(propertyName: "id", jsonNode: out var id) &&
            (id is not null)
        ) {
            return id.ToJsonString();
        }

        return "-";
    }

    private CommandResult HandleStep(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Usage(
                form: "<path> <delta>",
                verb: "world.row.step"
            );
        }

        var path = args[0].ToString();
        float delta;

        if (args.Count == 2) {
            if (!args.TryFloat(
                index: 1,
                value: out delta
            )) {
                return CommandResult.Error(output: $"[world.row.step: could not parse delta '{args[1].ToString()}' as a finite number]");
            }
        } else if (context.Origin == CommandOrigin.Binding) {
            delta = context.Value.AsAxis1D;
        } else {
            return CommandResult.Usage(
                form: "<path> <delta>",
                verb: "world.row.step"
            );
        }

        if (!TryResolveStepTarget(
            error: out var resolveError,
            fieldPath: out var fieldPath,
            key: out var key,
            path: path,
            section: out var section
        )) {
            return CommandResult.Error(output: $"[world.row.step: {resolveError}]");
        }

        // The ROW identity (section, or section.key) the field lives inside — path with its trailing field segment
        // removed. The whole-row upsert collides at this grain, not the field grain: two steps to different fields of
        // ONE row still stomp each other. Every pre-drain submission targets NextInputTick, so it is the window a
        // same-row collision lives inside.
        var rowIdentity = path[..((path.Length - fieldPath.Length) - 1)];

        if (m_stepGuard.IsClaimed(
            rowIdentity: rowIdentity,
            window: server.NextInputTick
        )) {
            return CommandResult.Error(output: $"[world.row.step: {path}: row '{rowIdentity}' already has a step buffered this tick — a second step composes from the same pre-drain base and would revert the first; fence with world.wait, or use world.row.set for the final value]");
        }

        var read = section.Read(
            server,
            key
        );

        if (read.Error is { } readError) {
            return CommandResult.Error(output: $"[world.row.step: {path}: {readError}]");
        }

        if (!WorldRowFieldStepper.TryStep(
            delta: delta,
            error: out var stepError,
            fieldPath: fieldPath,
            newText: out _,
            oldText: out _,
            root: read.Row!,
            rowType: section.RowType
        )) {
            return CommandResult.Error(output: $"[world.row.step: {path}: {stepError}]");
        }

        DropEditArtifacts(
            row: read.Row!,
            section: section
        );

        var principal = context.ActingPrincipal();
        var outcome = section.Upsert(
            server,
            principal,
            read.Row!.ToJsonString()
        );

        if (outcome.Error is { } upsertError) {
            return CommandResult.Error(output: $"[world.row.step: {path}: {upsertError}]");
        }

        // Claim the row for this window only once the upsert is genuinely buffered — a step that refused above never
        // blocks a later well-formed one.
        m_stepGuard.Claim(rowIdentity: rowIdentity);

        // A buffered mutation verb (echo model 3): no synchronous applied-result line — the whole-row upsert composes
        // and revalidates at the tick boundary, where WorldServer.EchoTap narrates the accept/reject. Asserting
        // old -> new here would claim an outcome the drain can still reject. A drain REJECTION additionally prints a
        // per-verb "[world.row.step: …]" line through the registered correlation, so a script can account the refusal
        // against the verb that submitted it.
        return link.Submit(
            mutation: outcome.Mutation!,
            echoes: echoes,
            verb: "world.row.step"
        );
    }
    // Resolves a step path against the SAME section table world.row.set uses, one level deeper: the longest
    // section-key prefix (dot-boundary match) wins, so a dotted section name (hud.panels, views.seatRig) is never
    // shadowed by a shorter one. A keyed section's remainder splits at its first dot into (rowKey, fieldPath); a
    // keyless section's whole remainder IS the field path. An exact section-key match (no remainder) has no field to
    // step — the whole row, not a field — and is refused the same as an unknown path.
    private static bool TryResolveStepTarget(string path, out RowSection section, out string key, out string fieldPath, out string? error) {
        section = null!;
        key = string.Empty;
        fieldPath = string.Empty;

        string? bestKey = null;
        RowSection? best = null;

        foreach (var (candidateKey, candidateSection) in s_sections) {
            if (string.Equals(
                a: path,
                b: candidateKey,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var prefix = (candidateKey + ".");

            if (
                !path.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: prefix
            ) ||
                ((bestKey is not null) && (candidateKey.Length <= bestKey.Length))
            ) {
                continue;
            }

            bestKey = candidateKey;
            best = candidateSection;
        }

        if (
            (bestKey is null) ||
            (best is null)
        ) {
            error = UnknownStepPath(path: path);

            return false;
        }

        var remainder = path[(bestKey.Length + 1)..];

        if (best.Remove is null) {
            if (remainder.Length == 0) {
                error = $"'{path}': no field to step — a bare section path steps nothing";

                return false;
            }

            section = best;
            fieldPath = remainder;
            error = null;

            return true;
        }

        var dot = remainder.IndexOf(value: '.');

        if (
            (dot < 0) ||
            (dot == 0) ||
            (dot == (remainder.Length - 1))
        ) {
            error = $"'{path}': a keyed section needs a row key and a field — {bestKey}.<key>.<field>";

            return false;
        }

        section = best;
        key = remainder[..dot];
        fieldPath = remainder[(dot + 1)..];
        error = null;

        return true;
    }
    // Every admissible step section, sorted for a stable, greppable refusal — properties.names is NOT included
    // (it is a bare-name registry toggle, not a document row a field lives inside).
    private static string UnknownStepPath(string path) {
        var admissible = string.Join(
            separator: ", ",
            values: s_sections.Keys.OrderBy(
                keySelector: static name => name,
                comparer: StringComparer.Ordinal
            )
        );

        return $"unknown path '{path}' — {admissible}";
    }
    // The keyless-section reader: the whole row IS the section, read fresh off the live definition.
    private static Func<WorldServer, string, RowReadOutcome> ReadRow<T>(JsonTypeInfo<T> info, Func<WorldServer, T> select) {
        return (server, _) => ToReadOutcome(node: JsonSerializer.SerializeToNode(
            value: select(server),
            jsonTypeInfo: info
        ));
    }
    // The keyed-section reader: a linear scan by the row's own stable key text (every keyed section's key type —
    // string, DocumentIdentifier, CellName — round-trips through ToString() the same way its Remove delegate's
    // plain-string key already does).
    private static Func<WorldServer, string, RowReadOutcome> ReadRowByKey<T>(JsonTypeInfo<T> info, Func<WorldServer, IReadOnlyList<T>> select, Func<T, string> keyOf) {
        return (server, key) => {
            foreach (var row in select(server)) {
                if (string.Equals(
                    a: keyOf(row),
                    b: key,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return ToReadOutcome(node: JsonSerializer.SerializeToNode(
                        jsonTypeInfo: info,
                        value: row
                    ));
                }
            }

            return RowReadOutcome.Fail(error: $"no row '{key}'");
        };
    }
    // The one section (screens) keyed by its own array POSITION rather than a stable name.
    private static Func<WorldServer, string, RowReadOutcome> ReadRowByIndex<T>(JsonTypeInfo<T> info, Func<WorldServer, IReadOnlyList<T>> select) {
        return (server, key) => {
            if (!CommandArgs.TryParseInt(
                text: key,
                value: out var index
            )) {
                return RowReadOutcome.Fail(error: $"bad index '{key}' — an integer");
            }

            var rows = select(server);

            if (((uint)index) >= ((uint)rows.Count)) {
                return RowReadOutcome.Fail(error: $"index {index} out of range (0..{(rows.Count - 1)})");
            }

            return ToReadOutcome(node: JsonSerializer.SerializeToNode(
                value: rows[index],
                jsonTypeInfo: info
            ));
        };
    }
    private static RowReadOutcome ToReadOutcome(JsonNode? node) => ((node is null)
        ? RowReadOutcome.Fail(error: "serialized to null")
        : RowReadOutcome.Ok(row: node)
    );

    // One entry of the section table: a path's upsert (always present), remove (null for a keyless section), and
    // read (world.row.step's row lookup — the whole row for a keyed section, the whole section for a keyless one) —
    // each given the addressed row's own WorldServer as their leading parameter. RowType is the row's own CLR type
    // (typeof(T), the SAME T every other member closes over) — the reflection root WorldRowFieldStepper walks an
    // enum leaf's vocabulary through, since a JsonNode carries no type of its own.
    // ReadsLiveDocument marks the entries whose mutation composes against the ADDRESSED world's own live document
    // (the server-reading Upsert overload) — the routed composer refuses them by name, since a routed write's
    // document lives at the destination. DropOnEdit names top-level row properties a field/list edit (step, the
    // literal set form, add, remove) strips before resubmitting the modified row — a derived self-digest the row
    // itself carries (see the "creations" entry), never author data a field edit should preserve.
    private sealed record RowSection(Func<WorldServer, WorldPrincipal, string, RowOutcome> Upsert, Func<WorldServer, WorldPrincipal, string, RowOutcome>? Remove, Func<WorldServer, string, RowReadOutcome> Read, Type RowType, bool ReadsLiveDocument = false, IReadOnlyList<string>? DropOnEdit = null);
    // Strips section.DropOnEdit's named top-level properties from a row before it is resubmitted whole — every
    // field/list edit's shared last step ahead of ToJsonString().
    private static void DropEditArtifacts(JsonNode row, RowSection section) {
        if (
            (section.DropOnEdit is not { Count: > 0 } drop) ||
            (row is not JsonObject obj)
        ) {
            return;
        }

        foreach (var property in drop) {
            _ = obj.Remove(propertyName: property);
        }
    }
    // A parsed-and-built mutation, or the reason building one failed — the ONE outcome shape every section entry
    // returns, so the two verb handlers stay generic over which section answered.
    private readonly record struct RowOutcome(WorldMutation? Mutation, string? Error) {
        public static RowOutcome Fail(string error) => new(
            Error: error,
            Mutation: null
        );
        public static RowOutcome Ok(WorldMutation mutation) => new(
            Error: null,
            Mutation: mutation
        );
    }
    // The row-read outcome world.row.step's lookup answers with — the row's live JSON node, or the reason none
    // resolved (no such key, a malformed index).
    private readonly record struct RowReadOutcome(JsonNode? Row, string? Error) {
        public static RowReadOutcome Fail(string error) => new(
            Error: error,
            Row: null
        );
        public static RowReadOutcome Ok(JsonNode row) => new(
            Error: null,
            Row: row
        );
    }
}
/// <summary>
/// The read-your-writes guard for <c>world.row.step</c> within one tick window. A step reads a WHOLE row off the live
/// definition, mutates one field, and submits a whole-row upsert; two steps to the SAME row inside one window (before
/// the buffered mutations drain) both compose from the same pre-drain base and drain FIFO, so the later upsert reverts
/// the earlier's field — both would echo success. The window is the tick every pre-drain submission targets
/// (<see cref="Server.WorldServer.NextInputTick"/>); the guard remembers which rows a step has already claimed in the
/// current window and refuses a second claim on one of them, emptying its set the moment the window advances. Steps in
/// DIFFERENT windows (a held chord repeating once per tick) never collide — the set is empty each new window. Not
/// thread-safe by design: the command pump is single-threaded, and this is console-side control state off every hashed
/// simulation path.
/// </summary>
public sealed class WorldRowStepWindowGuard {
    private readonly HashSet<string> m_claimed = new(comparer: StringComparer.Ordinal);

    private ulong m_window;
    private bool m_seenWindow;

    /// <summary>Gets a value indicating whether a step to <paramref name="rowIdentity"/> collides with one already
    /// buffered in <paramref name="window"/> — advancing to (and emptying) a new window first. The whole-row upsert
    /// stomps at the row grain, so the addressed field is not part of the identity.</summary>
    /// <param name="window">The tick every pre-drain submission targets (<see cref="Server.WorldServer.NextInputTick"/>).</param>
    /// <param name="rowIdentity">The row a step addresses (a section path, or a section path plus row key).</param>
    /// <returns><see langword="true"/> when the row already has a step buffered this window; otherwise <see langword="false"/>.</returns>
    public bool IsClaimed(ulong window, string rowIdentity) {
        if (!m_seenWindow || (window != m_window)) {
            m_seenWindow = true;
            m_window = window;
            m_claimed.Clear();
        }

        return m_claimed.Contains(item: rowIdentity);
    }
    /// <summary>Records <paramref name="rowIdentity"/> as buffered in the current window — called only once a step's
    /// upsert is genuinely submitted, so a step refused for any other reason never blocks a retry.</summary>
    /// <param name="rowIdentity">The row the submitted step addresses.</param>
    public void Claim(string rowIdentity) => _ = m_claimed.Add(item: rowIdentity);
}
