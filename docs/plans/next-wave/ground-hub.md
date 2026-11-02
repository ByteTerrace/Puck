# ground-hub: what exists for the hub, saves, characters and the player menu

Read-only grounding pass (brief `hub-ground`) for the design memo that
[the next wave](../next-wave.md#before-the-owners-go) queues behind
[the owner's rulings](../../decisions/play.md#the-game): the release launches
from a hub, and the player menu opens on Start. Tree: the `lead/fundamentals`
tip named in the landing commit. Paths are under `src/` unless they name
another root.

Each claim carries its kind: **[M]** measured in this pass, **[S]** read from the
source at the cited line, **[R]** recorded in a document. One headless debug
World ran (`puck_world_start`, `--devices none`, its own throwaway state
directory); everything else is [S] or [R].

## What the headless run showed [M]

One script against a fresh state root, in order:

| Line | Answer |
|---|---|
| boot stderr | `[identity] loaded 0 owned worlds from …/state/owned-worlds` |
| `identity.list` | the root, and no identities |
| `world.players` | `p1 seat1 active(none)`; `population: 1 local + 0 network` |
| `identity.show` | `p1 world=seat1 name=seat1 color=#8C8C8C move=kit` |
| `identity.fact.set visits 1` | refused: `world:seat1 is not an owned world here` |
| `identity.create ada #3366CC`, `player.identity ada` | `player 1 is now ada` |
| `identity.fact.set visits 1` | `visits=1 in world:ada` |
| the `ada` owned-world file on disk | **30,293,880 bytes**: every section of the debug world plus an `identity` section |
| `world.destinations` | `self` and `wonderland` resolve to `boot`; `garden` and `night` to instances `6~global6~garden` and `6~global5~night` |
| `world.instance.status` | `3 running: 6~global5~night, 6~global6~garden, boot` |
| `world.transfer boot 1 6~global6~garden` | `world.instance.seats` shows `garden: 1=ada`, boot seat 1 empty |
| `world.transfer 6~global6~garden 1 boot` | back home; `identity.facts` still `visits:1` |
| `world.rate pause` / `resume` | boot reads `state paused`, then `running` |
| `world.peers` | `not listening` |
| `world.save` | refused: `debug.puck names 'sheddingRack1' at $.placements.rows[167].parent, which shedding.puck declares and does not export …; nothing written` |

So a fresh debug World seats an unowned stand-in, a player becomes an owned
identity only through console verbs, that identity is a full copy of the host
world, and it survives a round trip to another world in the same process.

## 1. Player persistence today

### Identity is a world you own

- **The model.** "character, alt: a world you own"; "account: the set of worlds
  you own"; "item, inventory: durable slots on a world you own"
  ([worlds and federation](../../architecture/worlds.md#terms)) [R]. The change
  that deleted the separate player-document family (its subject: "A player is
  a world, and worlds can ask each other to change") says: "An identity is an
  ordinary world document you own, seeded from the blank world; alternate
  characters are more of them" [R, commit body].
- **The catalog.** `WorldOwnedWorlds` (`Puck.World.Server/WorldOwnedWorlds.cs:23`)
  loads every `*.world.json` carrying an `identity` section from one directory
  and seeds `playerDefaults.identities` (`WorldIdentitySeed`,
  `Puck.World.Schema/WorldPlayerDefaults.cs:56`) when it is empty (`:178-215`).
  The boot row's directory is `<state root>/owned-worlds`
  (`Puck.World/WorldOwnedWorldRegistration.cs:17`, the log line at `:41`); an
  instance's is `<state root>/instances/<name>/owned-worlds`
  (`WorldInstanceHost.OwnedWorldsDirectory`,
  `Puck.World.Server/WorldInstanceHost.cs:472-482`). The state root is
  `--state-dir`, else the per-user Puck directory's `world` folder
  (`WorldStateRoot`, `Puck.World.Server/WorldStateRoot.cs:3-11`) [S].
- **The debug world seeds none.** No `.puck` under `worlds/debug` or
  `worlds/standard` authors `playerDefaults.identities` (a search for
  `identities` finds none) [S], hence the `loaded 0 owned worlds` line [M].
- **The stand-in.** With an empty catalog and at least one local seat,
  `PlayerRoster` seats one inert identity, id and name `seat1`, the neutral
  colour and no claimed rates (`Puck.World.Client/PlayerRoster.cs:2162-2182`)
  [S]. It is a projection, not an owned document, so every identity door that
  saves refuses it (the `identity.fact.set` refusal above) [M].
- **The identity document is the host world.** `IdentityBase` returns the host
  template unchanged (`WorldOwnedWorlds.cs:617`); `Seed` copies it, swapping in
  the identity section, rate rows, empty bindings and HUD panels
  (`:691-714`). The method's introducing comment kept it "so a future minimal
  template has one door to return through" (`git log -S IdentityBase`).
  So "seeded from the blank world" no longer holds: an identity
  created in the debug world is 30 MB [M] and carries the debug world's
  placements, screens, rules and state.
- **What the identity section declares.** `WorldIdentityDefinition`
  (`WorldPlayerDefaults.cs:70`): id, name, colour, the two rate rows,
  controller slots, voice, the facts row and capacity, and `records` (selected
  capacity-one pools) [S].
- **What an identity holds live.** `WorldIdentity`
  (`Puck.World.Schema/WorldIdentity.cs:27`): bindings (`identity.bindings.save`
  folds a seat's session rebinds into an `identity` binding overlay and saves,
  `Puck.World/WorldBindingCommandModule.cs:606-655`), the private HUD panel
  (`identity.hud`), the seat look (`seatDefaults.seatCameraFeel`), the motion
  rates (`identity.motion`), the facts row, the record pools, chat log and
  inbox and the per-controller memory rows (`WorldIdentityRows`,
  `Puck.World.Schema/WorldIdentityRows.cs:9-31`) [S]. The verbs are in
  `IdentityCommandModule.GetCommands` (`Puck.World/IdentityCommandModule.cs:493-566`).
- **Durable state a world writes.** A kit's `state.identity` declarations
  compile as `ActionStateLifetime.Durable` slots
  (`FixedWorldKit.CompileActionStateFile`, `Puck.World.Schema/WorldKit.cs:145-186`).
  Each tick the server submits their outputs to the owner document
  (`Puck.World.Server/WorldTick.Step.cs:480-497`), and the owner admits a write
  only through its own `grants` (a Mutate row naming the source document, with
  a write mask): `WorldOwnedWorlds.Decide` (`WorldOwnedWorlds.cs:285-330`) [S].
  Rules write facts with `setIdentityFact`; the body's lane loads from the
  identity on bind and the persisted row follows the firing's commit
  (`Puck.World.Server/WorldRuleHost.IdentityFacts.cs:5-10`) [S]. No world
  under `worlds/` uses either; the one user is
  `experimental/worlds/rulepush/level.puck:36-43` [S].
- **When it is written.** Every identity door saves at once
  (`WorldOwnedWorlds.TrySave`, `:1126`; `Save`, `:1214`; callers in
  `ChatCommandModule`, `IdentityCommandModule`, `WorldBindingCommandModule`) [S].
  Nothing waits for quit.
- **Across machines.** `storage.push` / `storage.pull` sync whole owned worlds
  with the per-user cloud container, refusing a stale writer
  (`WorldOwnedWorldSync`, `Puck.World.Server/WorldOwnedWorldSync.cs:7-20`;
  verbs at `Puck.World/WorldStorageCommandModule.cs:135-174`) [S].

### Checkpoints and world state

- **The checkpoint exists.** `WorldServer.TryCaptureCheckpoint` /
  `WorldPersistence.RestoreCheckpoint`
  (`Puck.World.Server/WorldPersistence.Checkpoint.cs:555`, `:749`) capture a
  whole authority: document, tick, grants, population, owned worlds, escrow,
  crossings, machines [S].
- **The desktop never writes one to disk.** `WorldInstanceHost.DurableImages`
  (`Puck.World.Server/WorldInstanceHost.Stop.cs:33`) is the writer a stopping
  row's image goes through (`:227-247`), and nothing in `src/` assigns it [S].
  The checkpoint's desktop callers are in-memory: `WorldHistory` keyframes
  (`Puck.World.Server/WorldHistory.cs:586`), replay probes and tapes.
- **The silo does.** `WorldSiloHost` captures on a one-minute engine-time
  cadence (`WorldAuthorityCheckpointCadenceCounter`,
  `Puck.World.Server/WorldAuthorityCheckpointCadence.cs:24`; the counter at
  `Puck.World.Silo/WorldSiloHost.cs:63`) and on `silo.checkpoint`, retirement
  and shutdown, into its store [S, `Puck.World.Silo/README.md`].
- **`world.save` is an author's save**
  (`Puck.World/WorldMutationCommandModule.cs:364`). It rewrites the world's
  source (or a JSON delta), folds session levers in, needs every-section
  Mutate, compacts the journal, and keeps "the simulation's state … as
  authored" for a `.puck` target [S]. It is not a player save, and on the
  debug world it refuses today [M].
- **Emulated machines keep no battery save.** `MachineCreationRequest.SavePath`
  is "null for in-memory persistence"
  (`Puck.Abstractions/Machines/MachineCreationRequest.cs:22-27`), and
  `WorldMachineHost` never sets it (`Puck.World.Machines/WorldMachineHost.Instances.cs:164-171`) [S].

### Companions, crossings and journals

- What crosses a seam is `WorldIdentityProjection`: id, name, colour, the rates,
  the selected records and the facts row. Bindings, HUD, seat look, chat,
  grants and controller history stay home (`WorldIdentity.cs:9-26`;
  [joining](../../architecture/worlds.md#joining-authority-and-admission)) [S][R].
- A body's perceivable dynamic state crosses too (`WorldBodyTransferState`,
  `Puck.World.Server/WorldBodyTransferState.cs:1-30`), and its body registers:
  the Moth's `suit` register crosses because every world she can enter
  declares it (`worlds/debug/suit.puck:1-8`) [S].
- Coming home adopts the carried facts and records onto the owned identity and
  saves it after the arrival record is durable (`WorldOwnedWorlds.TryAdopt`,
  `:1341`; [joining](../../architecture/worlds.md#joining-authority-and-admission)) [S][R].
- A companion's title is custody with epochs and tickets
  (`WorldCompanionCustody`, `Puck.World.Server/WorldCompanionCustody.cs`); a
  world that does not take it sends it home
  ([companions](../../decisions/companions.md#a-world-that-does-not-take-it-sends-it-home)) [S][R].
- Arrivals are logged (`WorldCrossingRecord`,
  `Puck.World.Server/WorldCrossingRecord.cs:62`) so a restarted destination
  lands them again, and external operations are journalled
  (`WorldExternalOperationJournal`) [S]. Both matter only where an image or
  log is durable, which on the desktop it is not.

### What survives what

| State | Survives quitting | Survives a world switch | Owned by |
|---|---|---|---|
| Owned identity: name, colour, rates, bindings, HUD, seat look, chat, controller memory | yes, saved on every change | yes; only the projection travels, the rest waits at home | player (per state root, per hosting world's catalog) |
| Identity facts and records | yes | yes, carried and adopted on return | player |
| Durable `state.identity` slots | yes, when the owner's grants admit the write | they live on the owner document | player |
| The stand-in `seat1` | no, it is never saved | travels as a projection | nobody |
| Body pose, registers, momentum | no on desktop | yes, carried across the seam | world while embodied |
| World state: rows, placements, population, machines | no on desktop; yes in the silo | the source world keeps running in process | world |
| Instances behind `persisted` destinations | no on desktop: "persisted" lasts the process | yes, they keep running | world |
| Emulated machine save RAM | no | n/a | machine |
| Replays, screenshots, caches | yes (state root) | n/a | host |

So a **player save today is the owned identity document**: who you are, your
preferences and whatever durable slots and facts worlds wrote onto you. Where
you were and what the world looked like are not saved on the desktop.

## 2. Moving between worlds

- **In process, many authorities.** `WorldInstanceHost` runs the boot row and
  any number of instances on the one tick thread, each on its own rate
  (`Puck.World.Server/WorldInstanceHost.cs:13-64`) [S].
- **Destinations resolve once.** A `destinations` row
  (`WorldDestination`, `Puck.World.Schema/WorldDestinations.cs:92`) names a
  reference, durability and scope; `WorldSessionResolver`
  (`Puck.World.Schema/WorldSessionResolver.cs:6-30`) turns it into a
  generation and an instance name, idempotently [S]. The debug world names
  `garden` and `night` (`worlds/debug/debug.puck:95-120`) and `self`
  (`worlds/debug/paintings.puck:227-241`) [S], and they resolve as the table
  above shows [M].
- **Portals are composition, not a primitive**: destination, optional display,
  optional transfer, trigger
  ([portal composition](../../architecture/worlds.md#portal-composition)) [R].
  The paintings are doors whose glass shows the destination
  (`worlds/debug/paintings.puck:80-92`; [paintings](../../../worlds/debug/paintings.md)).
- **Leaving without walking.** `world.transfer <source> <seat|party|body:n>
  <destination>` moves a body between instances
  (`Puck.World/WorldInstanceCommandModule.cs:360-362`) [S][M].
  `world.instance.start <name> <path>` boots any world document beside the
  boot row (`:140-171`), and `world.instance.stop` retires one, refusing while
  it presents a local traveller (`:174-195`) [S]. All three are built with
  `WorldCommandDefinition.Simulation`, which is `Unbindable`
  (`Puck.World/WorldCommandDefinition.cs:8-14`), and an authored command "calls
  only what a binding may name" (`Puck.Launcher/Commands/TerminalCommandModule.cs:110-111`).
  So a player can leave a world by walking through a door, and an operator by
  a console line, but no binding, wheel sector or authored command can.
- **Across processes.** Federation carries the same transfer to a remote
  authority (`WorldFederatedServerLink`, `WorldFederationCodec*`), and a
  companion crosses as data under custody
  ([companions](../../decisions/companions.md#code-never-crosses-the-destination-decides-what-runs)) [S][R].
  Remote home adoption is deferred
  ([joining](../../architecture/worlds.md#joining-authority-and-admission)) [R].
- **`world.load`** (`WorldMutationCommandModule.cs:210`) swaps the boot row's
  document and resets its journal: an author's reload, not travel [S].
- **What the menu items map onto.**
  - *Swap worlds* is a transfer to a destination (an existing instance, or one
    the resolver starts), the same operation a painting performs. Nothing
    bindable reaches it.
  - *Exit to menu* has no target: no world is "the menu".
  - *Exit to desktop* is `quit`, which is unbindable "because ending the host
    process belongs to whoever runs the host"
    (`TerminalCommandModule.cs:110-119`), and closing the window. Neither
    writes world state; identities are already saved.

## 3. Hubs and launch

- **A hub is a modelled role, not a mechanism.** The model draws "hub: screens
  = joined worlds" among the worlds you own
  ([invariants](../../architecture/worlds.md#invariants)), and the planned hub
  is "owned by the platform's public-content identity" on Orleans
  ([play decisions](../../decisions/play.md#the-game)) [R]. No code names a
  hub. How another identity's world attaches to it is deferred
  ([play plan sequencing](../play.md#sequencing)) [R].
- **Prior art: the rulepush overworld.**
  `experimental/worlds/rulepush/rulepush.puck:47` declares
  `entry world hub` beside three level worlds; a level win writes an identity
  fact (`level.puck:36-43`), and the overworld opens its gate on the visitor's
  facts (`hub.puck:63-70`) [S]. The boot stages every declared world and
  starts in the entry (`WorldStaging`,
  `Puck.World.Transpiler/Composition/WorldStaging.cs`), and `--entry` picks
  another (`Puck.World/Program.cs:72-75`) [S]. That is the existing
  "hub that launches worlds and keeps progress on the player" pattern.
- **Launch.** `Puck.World` refuses to boot without `--world`
  ([no world, no boot](../../decisions/runtime-and-delivery.md)) [R]; a
  package names one entry (`worlds/debug/manifest.json`) [S].
  `Puck.Launcher` is the shared host loop and update service, not a world
  picker ([its README](../../../src/Puck.Launcher/README.md)), and
  `Puck.Launcher.Stub` starts an installed version
  ([its README](../../../src/Puck.Launcher.Stub/README.md)) [R]. Nothing
  lists worlds for a player.
- **Player-hosted worlds.** Every host can be a listen server
  ("listen / player-hosted server: the ordinary case",
  [terms](../../architecture/worlds.md#terms)); `host.listen` opens the
  door (`world.peers` reads it) [R][M]. The silo hosts declared world rows as
  grains (`Puck.World.Silo/README.md`) [R]. Destination scope `user` resolves
  to "the entering seat's owned-identity world"
  ([durability, scope](../../architecture/worlds.md#durability-scope-and-generation)),
  which is the model's player-owned place [R]. No player-driven hub exists.

## 4. Characters

- **A character is split across two owners.** The identity (an owned world)
  holds name, colour, rates, facts, records and preferences. The body's look
  and kit are the *visited world's* rows, chosen by that world's
  `looks.assignment` and `kits.assignment` (by sequence, or by a body
  register: `worlds/debug/suit.puck:42-58`, `:104-115`) [S]. An identity
  cannot claim a look or a kit; the colour is its only appearance field
  (`WorldIdentityProjection`, `WorldIdentity.cs:26`) [S].
- **Selection exists.** A joining seat is `Pending`: its avatar desaturates and
  its move stick cycles candidate identities
  (`ParticipantState`, `PlayerRoster.cs:8-17`; `PendingPicker`, `:662-680`;
  `CycleCandidate`, `:1219-1250`); `player.confirm` confirms, and
  `player.identity <name>` sets one directly (`PlayerCommandModule.cs:240-256`,
  bindable) [S]. `identity.create` makes a new one
  (`IdentityCommandModule.cs:503`) and is unbindable [S].
- **Creation does not.** No authored flow creates a character. The avatar
  studio is a room and camera layouts for judging the Moth
  (`worlds/debug/studio.puck:1-3`; [debug world](../../../worlds/debug/debug.md#avatar-studio)) [S].
- **The Moth's suit** is a body register that selects her look and kit and
  crosses with her (`suit.puck:1-8`) [S]: per-body form, not per-identity.

## 5. Pause and menus

- **Pause.** `world.rate pause|resume [instance:<name>]` holds one instance's
  accumulator without touching its authored rate
  (`Puck.World/WorldRateCommandModule.cs:167`;
  [per-world time](../../architecture/worlds.md#authored-per-world-time)) [S][R].
  It pauses the boot row by default [M], so a player standing in the garden
  is not paused by it. It is unbindable (`WorldCommandDefinition.Simulation`).
- **Step.** `world.history` seeks and steps the boot world, pausing it during
  a seek; it is bindable, and the build page binds its seat forms
  (`Puck.World.Console/WorldHistoryCommandModule.cs:8-14`) [S].
  `world.timeline` holds presentation clocks only [S].
- **Solo versus shared.** No code decides it. The inputs exist: joined local
  seats (`world.players`), admitted peers (`world.peers`), the
  `networkPlayers` cap (`Puck.World.Schema/WorldBodiesDefaults.cs:28`,
  `:61`), arrived travellers, and silo hosting [S].
- **Start today.** `start` is a composition channel "an avatar may ignore"
  (`worlds/standard/inputs.puck:70-74`), bound to Enter and the pad's Start
  (`worlds/standard/controls.puck:551-558`); only the `machine` engagement
  mode reads it (`worlds/debug/input-modes.puck:55`) [S]. The roster's
  "pad-Start gesture" (`player.cycle`, `PlayerRoster.cs:1253`) is bound
  nowhere under `worlds/` [S].
- **The wheel.** The play wheel is authored in the standard: rings, labels and
  icons as tables, held on Tab or LB (`worlds/standard/controls.puck:329-360`,
  `:760-865`) [S]. Its sectors dispatch bindable commands.
- **The UI tree is unbuilt.** One retained tree with per-seat roots,
  `world.ui` and headless drive verbs is the UI plan's step 3
  ([the UI plan](../ui.md)); no `world.ui` verb exists [S]. HUD panels and the
  bespoke overlay writers draw today.
- **An earlier menu decision.** The UI decisions add "a host menu … always
  reachable from a reserved input and from the bar … no world or mod can
  cover, restyle or intercept it (the lead's call, which the owner deferred)"
  ([UI decisions](../../decisions/ui.md#drawing-screens-and-mods)) [R]. It is
  not built.
- **Authored commands** (`commands [...]`, e.g. `controls.bar`,
  `worlds/standard/controls.puck:950-958`) are how the standard already
  authors a player action over engine mechanisms [S].

## 6. What a design must fill

1. **A minimal identity template.** Identities copy the host world
   (`WorldOwnedWorlds.cs:617`), 30 MB in the debug world [M]. One authored
   base for a character document, imported from the standard, is the door the
   method was kept for.
2. **A first identity in the release shape.** The debug world seeds none and
   seats the unsaved `seat1` (`PlayerRoster.cs:2162-2182`), so a player's
   progress has nowhere to land until someone runs `identity.create` and
   `player.identity` by hand.
3. **Character creation as a player action.** `identity.create` is an
   unbindable console verb; nothing authored invokes it, names the character
   or picks a look.
4. **Whether a look or kit belongs to the character.** Today the visited world
   chooses both and the identity carries only a colour. Deciding whether an
   identity may claim a look (as it claims rates, clamped by the destination)
   is a product question for the memo.
5. **Which catalog is the player's.** Each hosting world, and each in-process
   instance, has its own catalog directory, all under one machine-local state
   root, synced to the cloud only by explicit `storage.push`. A hub-launched
   player needs one account-level catalog that every launched world reads.
6. **What a player save holds beyond the identity.** On the desktop no world
   state survives quitting (`DurableImages` unassigned). The memo decides
   whether player-owned places persist (a durable image per `persisted` or
   `user` destination, as the silo already does), and that the save writes
   only what the player owns, unlike `world.save`.
7. **Machine save RAM.** `SavePath` is never set; a cartridge's battery save
   dies with the process.
8. **A player-reachable leave.** `world.transfer`, `world.instance.start` and
   `quit` are unbindable, so neither a menu nor an authored command can swap
   worlds or exit. A player-safe door (to a destination the release or author
   names, and to quit) is needed, gated like any other bindable command.
9. **What "the menu" is.** Exit to menu needs a target: the hub world itself,
   or an out-of-world shell. Nothing exists for either.
10. **Solo detection and a whole-session pause.** No predicate exists, and
    `world.rate pause` addresses one instance, not the one the seat stands in
    or every instance the session runs.
11. **Start routing.** The menu must take Start from the avatar's base page
    while an engaged overlay that declares Start keeps it
    ([an overlay replaces only what it declares](../../decisions/input-channels.md#an-overlay-replaces-only-what-it-declares)).
    The existing `start` channel is the hook.
12. **Reconcile the two menus.** The owner's player menu is authored in the
    standard; the earlier host-menu decision says no world can cover or
    restyle the host menu. The memo says whether these are one surface or two.
13. **The UI tree.** The menu needs step 3 of the UI plan (focus, pad
    navigation, per-seat roots) or an interim on the wheel.
14. **Hub attachment and player-driven hubs.** Deferred in the play plan; the
    rulepush overworld (`entry world`, identity-fact gates) is the prior art,
    and `user`-scoped destinations are the model's per-player place.

## Found on the way

- `world.save` on the debug world refuses: the rewrite names the module-generated
  placement `sheddingRack1` (generated by `worlds/debug/shedding.puck:145-146`)
  as a parent in `debug.puck`, and the module does not export it [M]. Cause not
  diagnosed further.
- The `player.cycle` doc calls it "the pad-Start gesture"
  (`PlayerRoster.cs:1253`), but nothing binds it [S].
- The commit that made identities worlds says one is seeded from the blank
  world; the code seeds from the host world (`WorldOwnedWorlds.cs:617`) [S].
