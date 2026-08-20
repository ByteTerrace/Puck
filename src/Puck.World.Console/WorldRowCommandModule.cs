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
/// The GENERAL document-row verb TRIO — <c>world.row.set</c>/<c>world.row.remove</c>/<c>world.row.step</c> — the ONE
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
public sealed class WorldRowCommandModule(IWorldConsoleAuthority authority, IServerLink link) : ICommandModule {
    private const string PropertiesNamesPath = "properties.names";

    // world.row.step read-your-writes guard within ONE tick window. A step reads a WHOLE row off the live definition,
    // mutates one field, and submits a whole-row upsert; two steps to the SAME row in one window (before the buffered
    // mutations drain) both compose from the same stale base and drain FIFO, so the later upsert reverts the earlier's
    // field — both echoing success. The window is the tick every pre-drain submission targets (server.NextInputTick);
    // the set is the rows a step has already claimed in it, cleared when the window advances. A second step against a
    // claimed row is refused by name rather than silently lost. Steps in DIFFERENT windows (a held chord repeating
    // once per tick) never collide and are never refused — the set is empty each new window. Console-side control
    // state, single-threaded on the command pump; off every hashed simulation path.
    private ulong m_stepWindow;
    private readonly HashSet<string> m_stepRowsThisWindow = new(comparer: StringComparer.Ordinal);

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
            return Submit(mutation: toMutation(
                principal,
                new WorldRowAssignment(
                    Sequence: new WorldSequence(
                        Name: WorldSequence.R1,
                        Offset: r1Offset,
                        Step: 0f
                    ),
                    Rows: []
                )
            ));
        }

        if (args.Is(
            index: 1,
            value: "cycle"
        )) {
            if (args.Count < 3) {
                return CommandResult.Error(output: $"[{verb}: cycle needs at least one name]");
            }

            return Submit(mutation: toMutation(
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
            ));
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
        RowType: typeof(WorldCreation),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCreation,
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
            info: WorldJsonContext.Default.WorldCreation,
            keyOf: static row => row.Id,
            select: static server => server.Definition.Creations
        )
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
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveTune(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldTune,
            keyOf: static row => row.Id,
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
        Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemovePatch(
            Id: id,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldPatch,
            keyOf: static row => row.Id,
            select: static server => server.Definition.Patches
        )
    ),
        ["links"] = new RowSection(
        RowType: typeof(WorldScreenLink),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldScreenLink,
            toMutation: static (principal, screenLink) => new WorldMutation.UpsertScreenLink(
                Link: screenLink,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveScreenLink(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldScreenLink,
            keyOf: static row => row.Name,
            select: static server => server.Definition.Links
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
        RowType: typeof(WorldAuthoringDefaults),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldAuthoringDefaults,
            toMutation: static (principal, authoring) => new WorldMutation.SetAuthoringDefaults(
                Authoring: authoring,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldAuthoringDefaults,
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
        // The two views sub-rows RMW the CURRENT Views row (SetViewDefaults carries the whole section) — the same
        // read-modify-write world.row.set views.seatRig/world.view.look performed before folding into this table.
        ["views.seatRig"] = new RowSection(
        RowType: typeof(WorldCameraRig),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCameraRig,
            toMutation: static (server, principal, rig) => new WorldMutation.SetViewDefaults(
                Principal: principal,
                Views: (server.Definition.Views with { SeatRig = rig })
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldCameraRig,
            select: static server => server.Definition.Views.SeatRig
        )
    ),
        ["views.seatControl"] = new RowSection(
        RowType: typeof(WorldSeatViewControl),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            toMutation: static (server, principal, control) => new WorldMutation.SetViewDefaults(
                Principal: principal,
                Views: (server.Definition.Views with { SeatControl = control })
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            select: static server => server.Definition.Views.SeatControl
        )
    ),
        ["playerDefaults.seatLook"] = new RowSection(
        RowType: typeof(WorldSeatLook),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSeatLook,
            toMutation: static (server, principal, look) => new WorldMutation.SetPlayerDefaults(
                Principal: principal,
                Defaults: (server.Definition.PlayerDefaults with { SeatLookRaw = look })
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSeatLook,
            select: static server => server.Definition.PlayerDefaults.SeatLook
        )
    ),
    };
    // world.kits: name, program, arm, and the arm's key scalars — the census this section never had.
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
                : " | ")}{kit.Name} program={kit.BodyMotionProgram} {DescribeMotionArm(motion: kit.Motion)}"
            );
        }

        return builder.Append(value: ']').ToString();
    }
    private static string DescribeMotionArm(WorldMotionModel? motion) => motion switch {
        WorldMotionModel.Grounded grounded => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"arm=grounded moveSpeed={grounded.MoveSpeed:0.###} turnSpeed={grounded.TurnSpeed:0.###} riseGravity={grounded.RiseGravity:0.###} fallGravity={grounded.FallGravity:0.###} maxFallSpeed={grounded.MaxFallSpeed:0.###}"
    ),
        WorldMotionModel.Vehicle vehicle => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"arm=vehicle topSpeed={vehicle.TopSpeed:0.###} accel={vehicle.Accel:0.###} grip={vehicle.Grip:0.###} boostMultiplier={vehicle.BoostMultiplier:0.###}"
    ),
        WorldMotionModel.Swim swim => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"arm=swim thrustSpeed={swim.ThrustSpeed:0.###} buoyancy={swim.Buoyancy:0.###} maxRiseSpeed={swim.MaxRiseSpeed:0.###} maxSinkSpeed={swim.MaxSinkSpeed:0.###}"
    ),
        _ => "arm=(none)",
    };
    private CommandResult HandleAssign(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return Usage(
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
        if (args.Count != 2) {
            return Usage(
                form: "<path> <key>",
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
            return Submit(mutation: new WorldMutation.SetProperty(
                Name: key,
                Principal: principal,
                Remove: true
            ));
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
            : Submit(mutation: outcome.Mutation!)
        );
    }
    private CommandResult HandleSet(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count < 1) {
            return Usage(
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
                return Usage(
                    form: $"{PropertiesNamesPath} <name>",
                    verb: "world.row.set"
                );
            }

            return Submit(mutation: new WorldMutation.SetProperty(
                Principal: principal,
                Name: args[1].ToString(),
                Remove: false
            ));
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

        var raw = WorldCommandArguments.RawAfter(
            args: in args,
            context: context,
            tokens: 2
        );

        if (string.IsNullOrWhiteSpace(value: raw)) {
            return Usage(
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
            : Submit(mutation: outcome.Mutation!)
        );
    }
    // rules and interactions key their Remove mutation by the validated WorldCellName type rather than a plain
    // string.
    private static Func<WorldServer, WorldPrincipal, string, RowOutcome> RemoveByCellName(Func<WorldPrincipal, WorldCellName, WorldMutation> remove) {
        return (_, principal, key) => {
            if (!WorldCellName.TryParse(
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
            if (!int.TryParse(
                s: key,
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var index
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
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
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
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.set",
            description: "Upserts ANY document row or section by its dotted MEMBER PATH — the document's own camelCase JSON names (see puck schema for payload shapes): world.row.set <path> <json>. Keyed sections (kits, cameras, screens, speakers, placements, creations, tunes, patches, links, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, groups.kinds, interactions.interactions) upsert one row addressed by its own key; keyless sections (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, views.seatControl, playerDefaults.seatLook) replace the whole row. ONE grammar exception: properties.names takes a BARE NAME token, not JSON — world.row.set properties.names <name> declares it idempotently. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary like every WorldMutation; a full-document revalidation rejects loudly. This verb performs NO schema validation of its own — a JSON parse failure echoes inline and submits nothing; every semantic check still runs at apply.",
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
            description: "Removes ONE row from a KEYED document section by its dotted MEMBER PATH and key: world.row.remove <path> <key>. Keyed sections are the same set world.row.set upserts into (kits, cameras, screens [key is the integer index], speakers, placements, creations, tunes, patches, links, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, groups.kinds, interactions.interactions), plus properties.names (BARE NAME token — world.row.remove properties.names <name>). A KEYLESS path (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, playerDefaults.seatLook) has no remove — it is refused by name. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary; rejected loudly if no row carries that key.",
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
            description: "Reports the kit census (Immediate): one segment per declared kit row — name, body motion program, the motion model arm it compiles (grounded|vehicle|swim), and that arm's key movement scalars. The kits section's own read-back (world.row.set kits/world.row.remove kits has no listing of its own otherwise).",
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
            description: "Steps ONE FIELD inside a document row or section by a delta — world.row.step <path> <delta>, one level deeper than world.row.set's own whole-row/whole-section path. <path> is <section>.<field> for a keyless section (render.sharpness) or <section>.<key>.<field> for a keyed one (creations.myrow.document.shapes[3].material) — the same section table world.row.set resolves against. Field-type semantics: a number adds delta, typed by the field's real CLR type (an integer field steps in exact integer arithmetic, a float/double field in floating point — a fractional step on a whole-numbered float lands, and an out-of-range integer step refuses by name rather than throwing); a JSON boolean toggles on any nonzero delta; a named enum (the row's own C# member spelling) cycles forward/backward by delta's sign, wrapping. A vector, a nested object, or a plain (non-enum) string refuses by name. Bindable: a chord row carries the delta as a constant Axis1D value in place of the argument; the typed form takes an explicit numeric token. Buffers and applies through the SAME section Upsert world.row.set uses, at the tick boundary; a full-document revalidation still gates the result, and the accept/reject narration arrives there (no synchronous applied-result echo). A second step against the same row in one tick window is refused by name — both would compose from the same pre-drain base and the later would revert the earlier; fence with world.wait between steps.",
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
    }
    private CommandResult HandleStep(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return Usage(
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
            return Usage(
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

        // Read-your-writes guard (see m_stepRowsThisWindow): every pre-drain submission targets NextInputTick, so it
        // is the window a same-row collision lives inside. A new window empties the claimed-row set.
        var window = server.NextInputTick;

        if (window != m_stepWindow) {
            m_stepWindow = window;
            m_stepRowsThisWindow.Clear();
        }

        // The ROW identity (section, or section.key) the field lives inside — path with its trailing field segment
        // removed. The whole-row upsert collides at this grain, not the field grain: two steps to different fields of
        // ONE row still stomp each other.
        var rowIdentity = path[..(path.Length - fieldPath.Length - 1)];

        if (m_stepRowsThisWindow.Contains(item: rowIdentity)) {
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
        _ = m_stepRowsThisWindow.Add(item: rowIdentity);
        link.SubmitWorldMutation(mutation: outcome.Mutation!);

        // A buffered mutation verb (echo model 3): no synchronous applied-result line — the whole-row upsert composes
        // and revalidates at the tick boundary, and WorldServer.EchoTap narrates the accept/reject there. Asserting
        // old -> new here would claim an outcome the drain can still reject.
        return CommandResult.None;
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
                value: prefix,
                comparisonType: StringComparison.Ordinal
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
    // string, DocumentIdentifier, WorldCellName — round-trips through ToString() the same way its Remove delegate's
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
                        value: row,
                        jsonTypeInfo: info
                    ));
                }
            }

            return RowReadOutcome.Fail(error: $"no row '{key}'");
        };
    }
    // The one section (screens) keyed by its own array POSITION rather than a stable name.
    private static Func<WorldServer, string, RowReadOutcome> ReadRowByIndex<T>(JsonTypeInfo<T> info, Func<WorldServer, IReadOnlyList<T>> select) {
        return (server, key) => {
            if (!int.TryParse(
                s: key,
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var index
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
    private sealed record RowSection(Func<WorldServer, WorldPrincipal, string, RowOutcome> Upsert, Func<WorldServer, WorldPrincipal, string, RowOutcome>? Remove, Func<WorldServer, string, RowReadOutcome> Read, Type RowType);
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
