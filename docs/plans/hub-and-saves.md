# The hub, player saves and the player menu

This memo designs the release's shape from the player's side: where a session
starts, what a player keeps, how a character moves between worlds, and how
Start reaches a menu. It answers the [fourteen gaps](#the-fourteen-gaps) between what Puck persists
today and what the release needs, under
[the owner's rulings](../decisions/play.md#the-game): the release launches from
a hub, the player menu opens on Start, a character claims and a world clamps,
exit to menu goes to the hub world, and there are two menu layers. It is design
only; a builder re-reads every citation before building from it, since
`file:line` references describe today's tree.

The owner asked that the design reason abstractly, never from the one world at
hand. So each section states the general shape (any release, any world, any
number of players and hosts), and the build order says what the debug world,
the proving ground, shows of each step.

## The shape in one page

- **A player save is the account, and the account is a directory of worlds
  the player owns.** The model already says so ("account: the set of worlds
  you own", [terms](../architecture/worlds.md#terms)). There is no save file
  beside it and no account document above it. It holds identities (characters)
  and the durable images of places the player owns.
- **A character is a minimal owned world**, seeded from the standard's identity
  template rather than from whatever world is running.
- **A character claims, a world clamps.** The identity claims an avatar (its
  looks, kits and body registers) and motion rates. The world it enters admits
  them through an authored admit policy, as it already clamps rates against a
  kit's envelope.
- **The hub is the release package's entry world**, bound to the reserved
  destination `hub` that every session can resolve locally. Booting is a
  travel into `hub` from nowhere, and exit to menu is a travel to it.
- **Travel is one transfer.** A player-safe verb addresses only the pressing
  seat, the world it stands in and a destination that world offers, and it
  queues the same transfer a painting or `world.transfer` does.
- **Two menu layers.** The Start menu is standard data on the resting page, so
  an engaged overlay that declares Start keeps it. Beneath it is the host
  escape, answered by the host's always-active input plane before any page,
  drawn in the engine's neutral theme. Apart from closing the window, it is
  the only door to quitting.
- **Pause is derived, never latched:** every instance this host runs holds
  while a menu asks for it and the session is solo, and the hold lifts the
  frame either condition fails.
- **Durable images make places last.** A persisted world's durable identity
  is its resolved scope key and its referenced document, never a door's name
  or a process-local instance name. The desktop host writes one image per
  identity at a location that is a pure function of it, restores it onto the
  current source through one door, and keeps what still fits when the source
  changed ([durable images](#durable-images)). A machine's battery RAM lives
  in the image of the world that owns the machine, and machine cores stop
  doing file I/O.

## Invariants

Each invariant names the one place that enforces it and where its guarantee
stops. Where nothing enforces one yet, the build step that adds it is named.

| # | Invariant | Enforced by | Where the guarantee stops |
|---|---|---|---|
| I1 | An identity document holds only what the identity template authors plus what the player and admitted worlds write to it; no host world's sections. | `WorldOwnedWorlds.Seed` takes its template from the account's configured template only; the `IdentityBase` door that returns the host world is deleted, so the host world is not in scope where an identity is seeded (step H1). | An author can still write a large template. |
| I2 | One account store per host process; every authority in it reads and writes identities through that one object. | `WorldInstanceHost` is handed the one `WorldOwnedWorlds` for the state root; `WorldInstanceHost.OwnedWorldsDirectory` is deleted, so no second catalog path exists to open (H2). Across processes, an OS file lock on the account directory: a second process opens it read-only and says so. | Two machines offline on one cloud account each write their own copy; the existing stale-writer refusal of `WorldOwnedWorldSync` refuses the second push, and the player chooses. |
| I3 | An identity is embodied by at most one seat at a time. | One claim door on the account (the roster's identity assignment calls it, and so does an arrival). Across hosts, the identity's title and custody records follow [the custody protocol](companion-redesign.md#1-vocabulary), whose rule is the engine's for any owned traveller (H14). | Until H14, a partition between hosts is not covered: a remote custodian unreachable holds the character until it answers. |
| I4 | A foreign document writes an identity only through the identity's own grants, and only in its own package's namespace. | `WorldOwnedWorlds.Decide`, the one door every durable write and delivery passes (`WorldOwnedWorlds.cs:284`); the namespace check joins it (H9). | Reads are not namespaced: any world the projection reaches sees every fact the identity carries, as today. |
| I5 | A binding moves only the seat that pressed it, out of the world it stands in, to a destination that world offers or to `hub`. | `player.travel` resolves its one argument against the current authority's `destinations` rows marked as offered, plus the reserved `hub`; it takes no source, traveller or path (H3). | A world may still move its own inhabitants anywhere its own rules and portals reach; that is the world's authority (invariant 6 of [the model](../architecture/worlds.md#invariants)). |
| I6 | Nothing a document authors ends the host process. | `quit` stays `CommandBindability.Unbindable`, and `CommandRegistry.CheckAuthored` keeps authored commands to bindable verbs; the authored menu reaches quitting only by opening the host escape (H3, H5). | Closing the window remains the host's. |
| I7 | The host escape opens from every input state. | The host's always-active plane (`IAlwaysActiveInputBindings`) answers the reserved gestures before page resolution; the binding vocabulary gate refuses a document that names the reserved pair as a chord (H5). | A hardware or OS overlay that swallows the input first (a platform's own guide menu) is outside Puck. |
| I8 | Start opens the menu unless an engaged overlay declares Start. | The existing overlay resolution ([an overlay replaces only what it declares](../decisions/input-channels.md#an-overlay-replaces-only-what-it-declares)); no new routing code (H4). | An overlay that declares Start and offers no way out still leaves the host escape. |
| I9 | Only a solo session holds; a hold never outlives solo or the menu that asked for it. | One derived value per frame in `WorldInstanceHost`: held exactly when a local seat's menu requests a hold and `Solo` holds; no latched flag (H6). | A solo player's own in-process services that count wall time (none in simulation) are not held. |
| I10 | A hold never changes simulation state. | The hold acts only on each instance's accumulator, through the lever `world.rate pause` already uses; ticks are counted in steps, so a held span inserts none (H6). | Presentation clocks are held by policy, not by this invariant. |
| I11 | A durable image is written only by the authority that runs the world, at the one location its durable identity names, independent of activation order, of the door it was reached through and of the filesystem's case rules. | The row carries its `WorldDurableIdentity` (scope key, referenced document identity), assigned once at `TryStartCore` or `AdmitBoot`; the store derives the path from the identity's content hash alone and refuses an image whose header names another identity (H7a). | A crash loses the world state since the last cadence image; identity writes are immediate and are not lost. |
| I15 | Restoring an image never replaces the current source's definition: the row runs the document its source composes, and the image supplies state. | The image's embedded documents reach no install path: `RestoreContinuation` takes the row's live definition and compilation and verifies the continuation key before it applies anything (H7b); H7e removes the embedded documents from the codec, so nothing can install them. | A source whose edit changes the compiled layout drops the exact continuation to durable rows; the durable rows are what survives any source change. |
| I16 | A restore that cannot apply leaves the row as its source built it, sets the image aside and starts the row through the same door every activation uses. | One private ladder, `RestoreImage` on `WorldInstanceHost`, whose only callers are `TryStartCore` and `AdmitBoot`; its tiers cannot half-apply because the restore core is two-phase, preparing everything that can refuse before it applies anything (H7b). | A refusal the core throws after its apply point is a defect the fault-injection law catches, never a silent half-restore. |
| I17 | Saved state reaches a changed source only through the arena's own admission door, row by row, never by coercion. | `StateArena.TryValidateLoad` and `TryLoad` decide every carried row; a row the current catalog does not declare, or whose declaration refuses a carried cell, is dropped and named (H7c). | A row the source still declares with an unchanged declaration keeps its saved cells, including over an author's re-tuned default value; `world.image.discard` is the author's reset. |
| I12 | Battery RAM enters a machine only at power-on, as a configuration input pinned in the image a run starts from; machine cores do no file I/O. | `MachineCreationRequest` carries the battery image as bytes in place of `SavePath`, so a core has no path to open (H8). | `.sav` import from a real cartridge is an operator verb, outside simulation. |
| I13 | A visitor's claimed avatar enters a world only through that world's admit policy, installed by a taped mutation at arrival. | The arrival handler is the one door; kit and look tables change only through `WorldMutation` (H10). | Taste is not checked: a look within every envelope can still be unwelcome; moderation is a platform policy outside this memo. |
| I14 | The account is an identity's only truth: restoring a world's image, seeking its history or replaying it never writes or rolls back a character. | Today every checkpoint embeds the instance's owned-world documents and restores them (`WorldAuthorityCheckpointCodec.EncodeOwnedWorlds`; `Host.Profiles.Restore`, `WorldPersistence.Checkpoint.cs:975`). H2 removes that section from world images, so no image holds a character to restore; a replay or history host binds identities pinned from the tape and holds no account to write. | A world's own rows that mirror a fact (a gate opened by it) rewind with the world, as any world state does. |

## The fourteen gaps

Each gap gives the design, what it rejects, which part is engine mechanism and
which is authored standard data, and what determinism and multiplayer
authority require of it.

### 1. A minimal identity template

**Design.** The standard authors `worlds/standard/identity.puck`: a world
fragment holding the identity section's defaults (the facts row and capacity,
the two rate rows with no claim, the record pools declared empty, the HUD
defaults with no panels) and the visitor policy of gap 6, and nothing else: no
placements, views, rules or screens. The player defaults name it as the
identity template, so every world that imports the standard inherits it, and a
release that wants another template overrides it by id. `WorldOwnedWorlds.Seed`
reads that template; `IdentityBase` (`WorldOwnedWorlds.cs:617`), which returns
the booted world unchanged, is deleted. Today an identity created in the debug
world is 30,293,880 bytes (measured on a headless debug World); under the template its
size is what the template authors plus what is written to it.

An identity's preferences (bindings, the seat look) name the standard's input
vocabulary, not one world's rows, and travel into worlds that may not declare
every channel. A preference row naming something a world lacks is inert there
and reported by `identity.show`, never a refusal: the identity is valid
everywhere, and each world applies what it can. Rebinds made for one world's
own channels are stored on the identity under that world's package namespace.

**Rejected.** Copying the host world and stripping sections is a denylist that
leaks each section added later, and `Seed`'s own comment shows the copy was
kept only because the template's camera programs read its state rows. A
template built in C# is a second implementation of an authored design. An
identity as a record outside the world model would lose the grants door,
durability, sync and the projection machinery that already work.

**Engine and standard.** Engine: `Seed` and the template reference, and the
inert-preference rule. Standard: the template itself.

**Determinism.** Seeding is account work outside every simulation. An identity
enters a simulation only when a seat binds it or a body arrives with its
projection, and the replay snapshot already pins the bound identity's rates
(`WorldReplaySnapshot.cs:1341`, `:1382`).

**Authority.** The host holding the account is the only writer of the seeded
document.

### 2. A first identity in the release shape

**Design.** The release shape seats no unsaved stand-in. An account with no
identity mints one from the template on first launch, named by default and
claiming the release's first offered avatar, and the session's first arrival
is at the hub's character place (gap 3), where the player renames and dresses
it. After that, a joining seat is `Pending` and its stick cycles the account's
identities, as the picker already does (`PlayerRoster.cs:662-680`,
`:1219-1250`), preselecting by the identity's controller memory rows. The
stand-in projection (`PlayerRoster.cs:2162-2182`) is deleted: every seated
identity is an owned document, so every save door has somewhere to land. A
world that authors `playerDefaults.identities` (a test cast) still seeds them.

**Rejected.** Keeping the stand-in until the player creates someone keeps two
kinds of seated identity, one of which every save door refuses (`identity.fact.set`
answers that the stand-in "is not an owned world here"). Holding the player out of the world until a
character exists is a shell before the hub, which the owner ruled out.

**Engine and standard.** Engine: mint on an empty account, delete the stand-in.
Standard: the template, the default name and the default avatar offer.

**Determinism.** Minting is account work; the seat binds the minted identity
like any other.

**Authority.** Local only. A remote peer arrives with an identity from its own
account; an anonymous seat still refuses a `user`-scoped destination by name
([scope](../architecture/worlds.md#durability-scope-and-generation)).

### 3. Character creation as a player action

**Design.** Three bindable verbs, each acting only on the pressing seat and its
account:
- `identity.new` mints a character from the template, under an account
  capacity the hub's player defaults author. The id is generated; the name
  starts at the default.
- `identity.avatar <avatar>` sets the seat's identity's avatar claim to one of
  the avatars the current world offers (an authored offer list, gap 4).
- `identity.rename <name>` renames the seat's own identity.

Selection uses what exists: `player.identity` (bindable,
`PlayerCommandModule.cs:252`) and the pending picker.

The character place is the debug world's avatar studio, the lawn pavilion
([composition](debug-world-composition.md)) that is already a photo area. A
plinth makes a new character, a pedestal per offered avatar dresses the
current one, and standing in the picker's ring returns the seat to `Pending`
so the stick cycles the account's characters in full colour. Each is wired by
mechanisms the debug world already uses: a rule sets a body register while the
body stands in a reach box (the lift's call plates, `worlds/debug/hall.puck`),
an input-mode family selects on that register, and a binding overlay keyed on
it declares the context button as the verb, with static text. No world rule
calls an account verb: the seat's own press does, under its own principal.

**Rejected.** Making `identity.create` bindable would let a binding name a
document id and a colour of its choosing. A rule effect that creates or
selects characters would let a world choose who a player is without the
player's press. A creation screen outside the world is the shell the owner
ruled out.

**Engine and standard.** Engine: the three verbs. Standard: the offer list's
shape and the walker offer. Debug world: the pavilion's plinth, pedestals and
ring, and the Moth offer.

**Determinism.** The verbs are taped seat commands. Their account effect is
outside simulation; their simulation effect is a seat binding a different
identity or an avatar install (gap 4), each taped.

**Authority.** Local seats only: a remote peer's or an addon's command cannot
create or rename on this host's account.

Text entry needs the UI plan's grapheme-safe fields (its step 4). Until then a
new character keeps its default name, and the console's `identity.rename`
renames it.

### 4. A character claims a look and a kit; a world clamps

**Design.** The identity gains an avatar claim: one avatar module named by
content identity (its package's content hash and the module's export), which
carries the looks, kits, assignment and body registers the module exports, as
the walker and the Moth do (`worlds/standard/walker.puck`,
`worlds/debug/suit.puck`). The existing rate claims stay; they refine the
claimed kit and are clamped by its envelope in `ResolveMoveSpeed`, as now.

Each world authors an admit policy over claimed avatars, evaluated in order:
- `accept` installs the claimed rows under the world's envelopes (speed, jump,
  collider extent, and cost: the look's bake work and bytes from creation
  stats);
- `restyle` keeps the kit and replaces or re-materials the look by an authored
  rule (a painterly world recolours every visitor);
- `substitute` gives the visitor the world's own avatar.

A world never refuses a player for the avatar: a policy that admits nothing
substitutes. The standard authors `accept` with envelopes sized to its walker,
so a world importing the standard takes characters as they are, the same
choice it makes for companions
([accept by policy](../decisions/companions.md#a-world-may-accept-visiting-drivers-by-policy)).

At arrival the policy is a pure function of the claim, the policy and the
destination's own measurement of the claim's cost. The result is installed by
a taped mutation that adds the module's rows to the world's kit and look
tables, namespaced by content hash, deduplicated so a hundred visitors with one
avatar add one set, and the arriving body is assigned the installed kit. The
body registers the assignment selects by (the Moth's `suit`) already cross
with the body (`WorldBodyTransferState`). Meshes for the look bake at the
destination from the creation by content identity: derived products, cached,
never carried.

**Rejected.** The visited world choosing everything (today) contradicts the
ruling. Carrying only a colour is too weak to be a look. Carrying baked meshes
makes a derived product the truth. Carrying a driver's code is refused for the
reason [companions](../decisions/companions.md#code-never-crosses-the-destination-decides-what-runs)
gives; motion programs and creations are data and travel.

**Engine and standard.** Engine: the claim field, the admit evaluation, the
install mutation and the cost measurement. Standard: the policy vocabulary's
default (`accept`), the envelopes and the walker. Debug world: the Moth.

**Determinism.** The install is taped, and the arrival record pins the resolved
avatar's content hash, so a replay reads the installed rows from its tape or
checkpoint and never the account. Clamps are fixed-point (`FixedMotionScalarEnvelope.Clamp`).
Look choice is presentation; the kit is simulation and enters at one boundary.

**Authority.** The destination decides, from what it can verify (the cap-trust
rule): it measures the creation's cost itself rather than trusting a claimed
figure.

### 5. The account catalog every launched world reads

**Design.** The state root's `owned-worlds` directory is the account, opened
once per process by one `WorldOwnedWorlds` that the boot row and every
instance share. Today each instance has its own directory
(`WorldInstanceHost.OwnedWorldsDirectory`, `WorldInstanceHost.cs:472-482`);
that path is deleted, so a character's facts written in the garden are in the
same document the hub reads, without an adoption on return. Adoption
(`WorldOwnedWorlds.TryAdopt`) remains for a body coming home from another host.
A second process on the same root takes the account read-only and says so.
Because the account is shared, a world's checkpoint stops embedding the
owned-world documents it embeds today (`WorldAuthorityCheckpointCodec.EncodeOwnedWorlds`),
so restoring the garden's image or seeking its history cannot roll back a
character the hub also reads (I14).
The catalog is derived by enumerating the directory, as now; it keeps no index
file to drift from it. Cloud sync (`storage.push`, `storage.pull`) becomes
automatic: pull at launch, push after quit's final write (H15), keeping its
stale-writer refusal.

**Rejected.** Per-instance catalogs with copies made on transfer keep two
copies of one character, and copies drift. An account document above the
worlds contradicts the model, which has no tier above them. A server-side
account service would break offline play.

**Engine and standard.** Engine only: storage is mechanism.

**Determinism.** The catalog never enters a simulation directly. A world sees
an identity's projection at bind or arrival, and a hub that shows the
account's characters or places receives them as a taped submission (gaps 3 and
14).

**Authority.** The host holding the account holds every identity's title. A
world on another host is a custodian whose writes are submissions admitted by
`Decide`; adopting them on a remote return is the deferred half of
[joining](../architecture/worlds.md#joining-authority-and-admission), built in
H14.

### 6. What a player save holds beyond the identity

**Design.** The desktop host assigns `WorldInstanceHost.DurableImages`
(unassigned anywhere in `src/` today). One writer places each image by the
world's durable identity, under a root its scope key chooses:
- `user`: under the account, beside the identity's document, since the
  identity is the user ([scope](../architecture/worlds.md#durability-scope-and-generation));
- `global` and `group`: under the state root's image store, the host's;
- `ephemeral`: no image.

Images are written when a row stops (the existing stop path,
`WorldInstanceHost.AdvanceStop`), when the session quits, and on the
engine-time cadence the silo already uses
(`WorldAuthorityCheckpointCadenceCounter`), so a crash loses at most one
cadence. Activating a persisted destination restores its image onto the row
its current source built. The identity, the location, what an image holds,
the restore and the writes are designed in [durable images](#durable-images),
which a builder reads before H7. The hub is resolved the same way (gap 9), so
its persistence is whatever its manifest declares.

So a player save is the account directory: the identities and the images of
the player's own places. On a desktop the player is also the host, and the
host's images of global worlds sit beside it. Nothing here rewrites a source:
`world.save` (`WorldMutationCommandModule`) stays the author's save.

A character's progress in a world lives on the character, in that world's
package namespace. The template's visitor policy lets each package write only
durable slots and facts it qualifies with its own namespace, as an aliased
import already qualifies a state row's name; `Decide` checks the namespace
against the source document's package (H9). Worlds of one package (rulepush's
overworld and levels, `experimental/worlds/rulepush/rulepush.puck:47`) share
one namespace, which is what lets an overworld gate read its levels' wins.

The checkpoint today carries the whole compiled definition
(`WorldServerCheckpoint.DefinitionJson`) and its undo base, which the source
re-derives. An image carries what cannot be derived: the source's content
identity, the journal's mutations, the continuation and the durable rows.
Measurement M1 settled that the format change is needed and in which step
([what an image holds](#what-an-image-holds)).

**Rejected.** A separate "save game" snapshot of chosen rows is a second
persistence mechanism beside the checkpoint. Persisting through `world.save`
rewrites sources and refuses on the debug world today. Persisting only the
identity (today) loses every player-owned place.

**Engine and standard.** Engine: the writer, its locations, the cadence and
restore, and the namespace check. Standard: the visitor policy in the identity
template, and each world's durability declarations.

**Determinism.** An image is a checkpoint, and a restore is the existing
restore. A replay of a session that began from an image starts from it as its
keyframe, named by content hash.

**Authority.** Only the authority writes a world's image. A `user`-scoped place
on someone else's host is persisted by that host, keyed by your platform user
id; it is not on your machine.

### 7. Machine save RAM

**Design.** A machine's battery RAM is part of the machine's state, and the
checkpoint already captures machine cores (`WorldPersistence.TryCaptureCheckpoint`,
`IWorldMachineCheckpointHost`), so it persists in the image of the world that
owns the machine (gap 6): the whole core in the image's exact continuation,
and the battery alone in its durable layer, keyed by machine name, so a save
outlives an edit to the world around the cabinet
([batteries](#batteries-h8)). Each machine row authors a persistence mode,
with the default in the standard's cabinet module:
- `resume` restores the whole machine from the image (a save state);
- `battery` restores the image, then power-cycles the machine keeping only its
  battery RAM and persistent clock, as a real cartridge does;
- `none` starts fresh on every activation.

`MachineCreationRequest.SavePath` (`MachineCreationRequest.cs:22-27`) becomes
the battery image as bytes in, and the host reads the core's external RAM at
capture. The cores' own file I/O (`HumbleGamingBrickCore.FlushSave` and
`LoadBatterySave`, `HumbleGamingBrickCore.cs:234-315`, and whatever the
other three engines that take `savePath` do with it) is deleted, and with it
the wall-clock stamp `FlushSave` writes and the load already ignores. Operator
verbs import and export a `.sav` for a real cartridge, unbindable.

Who owns a save follows who owns the world: an arcade cabinet in a global
world is the host's, and a console in the player's own place is the player's.
A handheld the character carries between worlds is an identity record; its
RAM rides with the record, a follow-up once records carry machine state.

**Rejected.** Setting `SavePath` to a file per machine adds a second store
beside images, keeps file I/O and a wall clock inside a deterministic core,
and tears on a crash mid-flush. A per-player save for every machine is wrong
for a shared cabinet, which has one RAM.

**Engine and standard.** Engine: the modes, the byte-carrying request, the
capture. Standard: the cabinet module's default mode.

**Determinism.** The battery image enters at power-on as a configuration input
(the core already names it one, `HumbleGamingBrickCore.cs:277-279`), pinned by
the checkpoint the run starts from.

**Authority.** The world's authority steps the machine and writes the image;
replicas never write.

### 8. A player-reachable leave and quit

**Design.** `player.travel <destination>` is bindable. Its traveller is the
pressing seat's body and its source is the instance that body stands in; it
takes neither as an argument. Its destination is a name the source world
offers or `hub`. It queues the same transfer `world.transfer` queues
(`WorldInstanceCommandModule.cs:360-362`), so Drive over the body, reservation
before detach, admission, companions and crossings all apply unchanged, and
each refusal is named.

A world offers a destination by marking its `destinations` row as offered to
players. The hub may mark rows as offered session-wide: every world the
session launched then lists them in its menu, unless that world clamps
session offers, so the release claims swapping and each world clamps it.

Quitting stays with the host. `player.escape [hub|desktop]` is bindable and
only opens the host escape (gap 12) with that item focused; the escape alone
exits to the desktop, by stopping every row (writing their images) and then
exiting. So the authored menu's Exit to desktop is one more press in a surface
no world draws, and a visited world that binds every control to
`player.escape` cannot quit anyone's game.

The gates are the ones every bindable command passes: the vocabulary gate, the
pressing seat's principal, Drive over its own body, the destination's
admission, and, for `player.escape`, a local seat.

**Rejected.** Making `world.transfer` bindable would let a binding move any
body from any instance to any path. Making `quit` bindable would let a world
document end the host process (I6). Leaving only by doors fails the ruling,
which puts exit in the menu.

**Engine and standard.** Engine: the two verbs, the offer marks and the clamp.
Standard: the menu's items. Each world: what it offers.

**Determinism.** `player.travel` is a taped seat command, and transfer
handshakes are already checkpointed host state. The escape and quitting are
outside simulation.

**Authority.** The source authority departs the body and the destination
admits it. If the source is on another host and unreachable, exit to hub is
the player's own recall under the custody protocol
([recall and release](companion-redesign.md#55-recall-and-release)): the
identity re-embodies at the hub from its last committed image at a new epoch,
the player is told what was lost, and the stale embodiment retires when its
custodian next hears from home (H14).

### 9. What "the menu" is: the hub

**Design.** The package manifest's entry world is the hub, bound to the
reserved destination `hub`. It resolves like any destination, with the
durability and scope the manifest declares, and always locally: the hub's
document ships in the release package, so no network is needed to reach it. A
shared online hub (the silo's instance owned by the platform's public-content
identity, [play decisions](../decisions/play.md#the-game)) is a `global`
destination the local hub offers, not the session's `hub`. Booting is a travel
into `hub` from nowhere, so the boot row is a resolved instance and boot and
exit share one path. `--entry` (`src/Puck.World/Program.cs:72-75`) stays an author's tool for
booting another world of a composition.

In the debug world, the debug world is the hub: its lobby is the arrival, the
painting wing is the first world list (the garden and night paintings), and
the studio pavilion is the character place.

**Rejected.** A shell outside every world, which the owner ruled out. Binding
`hub` to the online instance, which makes exit depend on a network. A menu room
in every world, one copy per world.

**Engine and standard.** Engine: the reserved name and boot as travel. The
release: its hub document.

**Determinism.** The hub is an ordinary world, and arrivals in it are taped.

**Authority.** The local host is the authority of the hub `hub` names.

### 10. Solo detection and true pause

**Design.** `Solo` holds when, across every instance this host runs, exactly
one embodied participant is a person, no network peer is admitted
(`world.peers`), no traveller has arrived from another host, the seat's world
is locally authoritative, and no external participant (an MCP Participant) is
embodied. It is computed each frame from the population, never latched.
Companions and other authored bodies do not count: they are not players
([companions](../decisions/companions.md#the-owners-rulings)).

The hold is derived: every instance this host runs holds its accumulator
exactly while a local seat's menu requests a hold and `Solo` holds. It uses
the lever `world.rate pause` arms (`WorldRateCommandModule.cs:167`) as a
second hold reason beside the operator's, under one stated rule: an instance
steps when no reason holds it. Presentation clocks (`world.timeline`) and
audio follow the hold. If a peer connects or a second seat joins while the
menu is open, `Solo` fails and the hold lifts that frame; admission never
waits on a pause.

Outside solo, the menu opens while the world runs. The menu's overlay releases
the input it declares, so the body stands idle rather than walking on a held
stick ([switching releases input](../decisions/input-channels.md#switching-releases-input)).

**Rejected.** Pausing only the seat's own instance leaves the rest of the
session (the hub behind a painting, a companion's world) running in a session
that is supposed to be paused. A flag set on open goes stale when a peer
joins. Stopping the tick thread stops network and loading work the host still
owes. A per-world pause policy reopens what the owner ruled.

**Engine and standard.** Engine: `Solo`, the hold and its refusal outside
solo. Standard: whether the menu requests a hold, so an authored menu that
should never pause (a rhythm game's) simply does not ask.

**Determinism.** A hold inserts no step, so state at every tick is unchanged.
The tape already records the boot row's pause and resume as ordered
rate-lever events (`WorldRateCommandModule.cs:167`); the menu hold rides the
same event, and H6 checks the state hash with and without a pause.

**Authority.** The hold is refused whenever another person shares any world
this host runs, so no one's world stops for someone else's menu.

### 11. Start routing

**Design.** The standard's resting page (the `bare` overlay) binds the pad's
Start and the keyboard's Escape to open the menu. Every overlay that declares
Start keeps it under the existing rule, with no new routing code. The standard
authors a `machine` engagement overlay that declares `start` and `select` as
channels, so an engaged machine keeps both. Today the channels reach a machine
only through the engagement family's eligibility list
(`worlds/debug/input-modes.puck:55`), and that family is the debug world's;
engagement is generic, so the family moves into the standard with its
overlay. A board's page does not declare Start (`worlds/debug/chess.puck:542-575`),
so Start opens the menu while seated. An unmapped pad's Start still joins it to
the roster; the `player.cycle` comment that calls it "the pad-Start gesture"
(`PlayerRoster.cs:1253`) is corrected.

**Rejected.** An engine-routed Start with a special case for machines is a
second routing rule beside the overlay rule. A dedicated menu button does not
exist on every pad.

**Engine and standard.** Standard only, apart from the corrected comment.

**Determinism.** Binding resolution is input-side; channels and commands land
in each tick's `CommandSnapshot` as today.

**Authority.** Per local seat.

### 12. The two menus

**Design.** Layer one is the authored Start menu: standard data, styled by the
skin, refined by id or replaced by any author. Layer two is the host escape,
and it is the same surface as the UI decisions' "host menu"
([UI decisions](../decisions/ui.md#drawing-screens-and-mods)), now an owner's
ruling: it offers exit to the hub, exit to the desktop and return, and later
the Mods surface and settings recovery that decision lists. The engine draws it
in the neutral theme (`WorldThemeSection.Default`), so no world restyles it and
no one world's look enters it.

Its reserved gestures are answered by the host's always-active plane, which
today answers only the console key (`TerminalInputBindings`):
- on a keyboard, the reserved chord `SourceFocus` already holds (Control and
  Alt held, Escape pressed), which already means "back to the host" while a
  passthrough source has focus;
- on a pad, View and Menu (Select and Start) held together for one second,
  recognised by the existing chord tracker (`BindingChordTracker`) inside the
  plane rather than by a second hold detector.

A page may still bind Start or Select alone; only the held pair is reserved.
The pad's guide button is rejected because the platform overlays (Steam,
Game Bar) take it first.

**Rejected.** One surface that is both authored and uncoverable is a
contradiction. Letting the standard style the escape lets a world make it
unreadable. Escape alone is a key worlds already bind (the board's cancel,
`worlds/debug/chess.puck:596`).

**Engine and standard.** Engine: the plane's gestures, the escape surface and
its items. Standard: everything in layer one.

**Determinism.** Outside simulation; its travel is `player.travel hub`, taped
like a bound press.

**Authority.** The escape acts only for the local seat that opened it.

### 13. The UI tree, and the interim

**Interim.** The standard authors the menu as a wheel held open on Start, as
the play wheel is held on LB (`worlds/standard/controls.puck:760-865`). Its
sectors dispatch `player.travel hub`, `player.escape desktop` and the swap
sectors a world refines by id, and the menu requests the hold while the wheel
is open. The host escape draws through the engine's bespoke overlay path, as
the console and toasts do, in the neutral theme.

**On the tree.** Once the UI plan's steps 3 and 4 land (the tree, runtime
collections, focus and pad navigation, [the UI plan](ui.md)), the menu becomes
a tree document whose swap list is a runtime collection of offered
destinations, and the host escape becomes a subtree under a host root that no
world or mod namespace reaches. The wheel ring is deleted in that change.

### 14. Hub attachment and player-driven hubs

**Design.** Everything the hub launches is a destination row in the hub, from
three sources:
- the release's worlds, authored in the hub document;
- the account's owned places, which reach the hub as destination rows through
  a taped mutation at arrival and on change (the account enters a simulation
  only so);
- other players' hubs, reference rows naming a federated address, from an
  invite now and from the finder later.

A player hub is a world the player owns, built from a standard `hub` module (a
gallery whose frames are portal compositions, display plus transfer,
[portal composition](../architecture/worlds.md#portal-composition)), edited in
build mode, and hosted by its owner's host as a listen server (`host.listen`)
or by the silo. Visiting another player's hub is a transfer like any other;
its offers are its owner's, and exit to hub still returns you to your own
session's hub. A gate in a hub reads the visitor's facts, as the rulepush
overworld's does (`experimental/worlds/rulepush/hub.puck:63-70`), and the
[port inventory](port-experimental.md#2-seamless-topology-and-carried-identity) already names the
debug world's first such gate: the night field awards a fact and a hall door
opens on it.

**Rejected.** A world list drawn outside the world contradicts the ruling that
it is a place in the hub. An engine list of release worlds bakes a design into
code. A player hub as a different kind of thing from a world needs a second
hosting path.

**Engine and standard.** Engine: owned places as taped destination rows,
federated references. Standard: the `hub` module. Debug world: its painting
wing as the release list.

**Determinism.** Destination rows enter as taped mutations; remote visits are
federated transfers with taped departures and arrivals.

**Authority.** The owner's host is the authority of the owner's hub. Visiting
grants no Mutate; building in someone else's hub is a grant its owner issues.

## Durable images

This section decides what a durable image is, where it lives, what it holds,
how it is restored onto a source that may have changed, and when it is
written. It replaces the image half of gap 6, settles measurement M1 and
expands H7 into the steps at the end. The first build keyed images by instance
name and installed the image's own definition; its review found five highs,
answered one by one [below](#the-first-builds-review-answered). A builder
reads this section and the code it names, and builds the steps without
re-deciding anything here.

### The decision in one page

- **A persisted world's durable identity is `WorldDurableIdentity(scopeKey,
  document)`**: the resolved scope key (`WorldSessionResolver.GlobalScopeKey`,
  `user:<id>` or `group:<id>`) and the referenced document's canonical
  identity, the same string the host already threads into the resolver
  (`WorldInstanceHost.CanonicalDocumentIdentity`). A door's name is the source
  document's name for the door, not the world's identity: two doors to one
  document in one scope reach one world, whether it is running (adoption) or
  not (one image). Durability is implied; an ephemeral or raw-started row has
  no durable identity.
- **The image's location is a pure function of the identity**: the store root
  the scope kind chooses, then a directory named by the content hash of the
  identity's key (`sha256-64`, sixteen lowercase hex digits). No author-spelled
  name reaches the filesystem, so case folding cannot collide two worlds, and
  the header inside the image repeats the identity, so a read refuses an image
  that names another world.
- **An image holds a header, a continuation and durable rows**, never a
  definition to install. The continuation is the authority checkpoint, exact
  against the source and the build it was captured under. The durable rows
  are the arena's own section form, the shape `world.save` folds and a boot
  seeds from, and they are what survives a changed source.
- **Restore is one ladder behind one door.** The row is built from its
  current source; then the exact continuation applies when its key matches,
  else the durable rows apply through the arena's admission door, else
  nothing. Every refusal sets the image aside and continues down; nothing
  half-applies, because the restore core prepares everything that can refuse
  before it applies anything.
- **Images are written** when a stop is accepted and when it completes, at
  quit for every row whose final image has not landed, and on the engine-time
  cadence. Only the authority writes.
- **The boot row carries its identity from `AdmitBoot`** and keeps no image
  until H3 makes boot a travel into `hub` and the manifest declares the entry
  world's durability.

### Identity and location

The row's `Durable` property on `WorldInstance` is its `WorldDurableIdentity`,
or `null` for a row no persisted destination reaches. `TryStartCore` assigns it
from the resolution that starts the row, `AdmitBoot` assigns the boot row's
(the boot document's identity, `GlobalScopeKey`), and nothing reassigns it.
The identity's key is netstring-composed, as `WorldSessionResolver.MintInstanceName`
composes an instance name, so two identities never spell one key.

The row's identity, not the resolver's cache, is what the store and the host
read:

- **Adoption generalizes "return means home".** The origin scan
  (`WorldInstanceHost.TryFindRunningInstanceByOrigin`) matches a running row by
  document path alone, so a global door and a user-scoped door to one document
  adopt each other's row today. It becomes a lookup by durable identity over
  each row's `Durable` identity: a persisted resolution whose identity a running row
  already carries adopts that row (`WorldSessionResolver.TryAdopt` installs the
  door's cache entry as now), and otherwise starts one. The resolver keeps its
  per-door cache and generations; the host keeps the one map from identity to
  row.
- **Retirement touches the resolver only.** `Retire` still calls
  `NotifyInstanceRetired` for the door's cache; the store never asks the
  resolver whether a row keeps an image, so a failed activation that retires
  and restarts under the same identity keeps writing.
- **The seed ladder's instance rung is the identity's key** for a persisted
  row (`WorldDefinitionLoader.TryLoadFileForAdmission`'s `instanceIdentity`),
  so the document's draws are a function of (document, durable identity, draw
  history) and the same world drawn through the boot row and through a minted
  row is one document.

Store roots, by scope kind:

- `global` and `group`: `<state root>/images/<hash>/`, the host's.
- `user`: `<account>/<identity-id>.images/<hash>/`, beside the identity's own
  `<identity-id>.world.json` in the account directory (`WorldDocumentName.For`;
  the suffix is within `SafeName.MaxSuffixLength`), outside the catalog's
  `*.world.json` glob, so an identity's places travel, sync and are disposed
  with it. The disposal and quarantine paths of `WorldOwnedWorlds` move the
  images directory with the document (H7a).
- `ephemeral`: no store.

Each directory holds `image`, written to a sibling and moved into place whole
(`File.Move` with overwrite, as the first build did; a crash mid-write leaves
the previous image), and at most one `image.refused`, the last image a
restore set aside, kept as evidence and overwritten by the next set-aside.
`world.instance.status` echoes a row's identity, hash and path, and
`world.image.discard <instance>` sets a row's image aside by hand, so an
author who wants a clean start has a door that is not deleting files.

The document identity in the key is the canonical absolute path the resolver
already uses. It is deliberately machine-local during development: two
worktrees sharing the per-user state root keep separate images of their own
gardens. Account sync (H15) maps the package root when it pushes and pulls,
and the header carries the components it needs for that.

### What an image holds

The file is one envelope, versioned by `WorldAuthorityCheckpointCodec.SupportedVersion`
as the checkpoint is, with three parts:

- **The header**: the durable identity; the source hash, which is the chain
  content hash the loader computes for the composed source
  (`TryLoadFileForAdmission`'s `contentHash`, which `TryStartCore` discards
  today); the continuation key; the authority identity the row ran under; the
  completed tick and engine ticks; whether an explicit stop was in progress;
  and the row counts, so a listing needs no decode.
- **The continuation**: the `WorldAuthorityCheckpoint` the codec encodes,
  with the row's host slice (`WorldInstanceHost.CaptureRow`) and without the
  owned-worlds section (I14; H2 lands first). In H7a to H7d the embedded
  `DefinitionJson` and `BaseDefinitionJson` remain in the bytes as witnesses
  of what the continuation was captured against, and no restore path installs
  them (I15); H7e removes them from the codec for every consumer.
- **The durable rows**: a `WorldStateSection` made by the fold `world.save`
  already uses (`WorldSessionCapture.CaptureState`: `StateArena.ToRows`,
  `ToPools`, `ToPairPools`), with every cell's clock cleared so a timed cell
  restarts at the activation it is loaded into rather than at a tick of the
  run that wrote it, plus each machine's battery image keyed by machine name
  ([batteries](#batteries-h8)).

The continuation key is (source hash, the module version id of the assembly
that owns the checkpoint codec, the machine catalog fingerprint). Under the
SDK's deterministic compilation the module version id is a pure function of
the build's inputs, so "the same build" needs no list of which engine changes
alter a compiled layout; a rebuilt engine drops to the durable rows, which is
the loss a development loop can afford, and a shipped build matches itself
run after run.

**Measurement M1, settled.** A probe over the lane's Release assemblies (a
scratch project, not committed; its method, command and output are in the
commit message that lands this section) booted each world
in-process, stepped it 120 ticks, captured and encoded its checkpoint, and
measured the parts by encoding the same checkpoint with a part emptied:

| World | Image | Embedded definition | Undo base identical | Arena section | Population entries | Addon bytes |
|---|---|---|---|---|---|---|
| `worlds/debug/garden.puck` | 886,298 | 353,491 | yes | 14,115 | 32 | 0 |
| `worlds/debug/night.puck` | 323,333 | 310,314 | yes | 10,186 | 0 | 0 |
| `worlds/debug/debug.puck` | 35,106,108 | 13,017,151 | no (a second 13 MB) | 706,342 | 557 | 1,360,199 |

The derived documents are 40% of the garden's image, 96% of the night's and
74% of the debug world's, which is also the boot row's cadence write once H3
makes it a persisted row. The probe also showed that the live definition's
compact bytes equal the loaded source's for the garden and the night field
but not for the debug world, with an empty journal in every case: the live
document is rewritten by state-derived projections outside the journal
(`WorldDocument.InstallPlacementProjection`, `WorldServer.BindArenaDocument`,
`WorldServer.MachineOperation`'s candidate definition), so the live bytes are
not a derivable pin and the continuation key pins the source and the build
instead. Capture time is a timing question for the lead's quiet window; it
does not decide the format.

The durable-row carrier was checked the same way. The garden's 131 stored
cells across 25 rows, the night field's 110 across 15 and the debug world's
14,025 across 469 all loaded into a fresh server of their own source through
`StateArena.TryLoad` with zero cell, cursor or phase differences, clocks
stripped or kept. Loaded into a fresh garden, the night field's 15 rows (the
standard's shared rows) were all admitted, and the debug world's 469 rows
were partitioned by the same door into 15 kept and 454 dropped, each dropped
row named by the arena's own refusal ("row 'solitaireKlondikeCards' is not a
document row this arena's catalog declares"). A whole-list load refuses on
the first such row, which is why the ladder admits row by row.

### The restore ladder

`RestoreImage(row)` on `WorldInstanceHost` is one private method with two callers,
`TryStartCore` after `Admit` and `AdmitBoot`, over a row its source has just
built and nothing has stepped. Its tiers, in order:

0. **Read.** No image: the row starts clean. An image that does not decode,
   whose header names another identity, or whose envelope version is not the
   codec's: set aside, clean start, one narrated line naming the reason.
1. **Exact continuation**, when the header's continuation key equals the
   row's (the source hash the load just computed, this build, this catalog).
   The journal's mutations re-apply in order through
   `WorldPersistence.TryApplyJournalTailMutation`, the door the silo's tail
   recovery already uses; a refusal names the entry and falls to tier 2. Then
   `RestoreContinuation(checkpoint)` restores every section over the row's
   live definition and compilation, with the source's definition as the undo
   base, and `RestoreRow` installs the host slice. The deal memos are reset
   (`WorldTick.ResetPlacementDeals`) so the first sweep re-derives every
   projection from the restored rows, and `EndUnobservedSessions` lets this
   activation's screens observe afresh. The row resumes the authority
   identity the header names, as the silo's activation does
   (`WorldPersistence.FromCheckpoint` with no override), so its forwarding and
   in-doubt records still name their authority and its incarnations stay
   unique through the restored population generations.
2. **Durable rows**, when tier 1 does not apply or refused. Each row of the
   section is offered alone to `StateArena.TryValidateLoad`; the admitted set
   loads once through `TryLoad`, and every dropped row is narrated with the
   arena's reason. Batteries apply by machine name ([H8](#batteries-h8)). The
   journal, population, escrow, grants and host slice do not apply: they are
   keyed by body index, kit index and generation, which a changed source does
   not preserve. Custody the image holds (titles abroad, in-doubt transfers,
   forwarding records) is narrated as lost, by incarnation and custodian; the
   custody protocol's recall is the recovery
   ([recall and release](companion-redesign.md#55-recall-and-release), H14).
   The row takes a fresh `LifetimeAuthority` name, since the earlier lifetime's
   incarnations may still be abroad.
3. **Clean.** Nothing applies; the row runs its source.

Every refusal inside tier 1 or 2 sets the image aside and continues to the
next tier through the same code path; there is no second fallback door and no
recursion into `TryStartCore`. The one exception filter lists the refusals
the restore doors document, `InvalidDataException` included, because
`WorldDefinitionSerialization.DeserializeForAdmission` wraps admission
refusals in it and it is an `IOException`, which the first build's filter
missed. Addon attachment and machine restore sit inside the same tier, so
their refusals take the same path.

What makes a refused tier harmless is the restore core. Today
`WorldPersistence.RestoreCheckpointCore` validates most sections before it
mutates and still throws after `AdoptDefinition` for a retained-turn, replay
scope or search refusal, which would leave a live row half-restored. H7b
splits it into a prepare phase that returns every refusal by name and an
apply phase that cannot fail, the shape `StateArena.TryLoad` already has
("every refusal is decided before one byte moves"). A history seek
(`WorldHistory.Seek`) inherits the same atomicity.

### Writes

- **Stop**: unchanged in shape. The accepted stop writes an image whose host
  slice says `Stopping`, so a restart resumes the stop; the final image is
  written once the row is frozen for retirement, and `AdvanceStop` marks the
  row's final image as landed immediately before `Retire`. That mark has one
  writer.
- **Quit**: every row that keeps an image and whose final image has not
  landed writes, frozen or not. A frozen row whose final write failed
  transiently therefore gets its final image at quit instead of being skipped
  for being retired.
- **Cadence**: each row that keeps an image counts its own steps on a
  `WorldAuthorityCheckpointCadenceCounter`, as the silo's rows do; an armed
  request is honoured at the next boundary and stays armed across a refusal,
  which narrates once per distinct reason.
- A write captures under the authority gate: `TryCaptureCheckpoint` for the
  continuation, the `world.save` fold for the rows, the battery export for
  each machine; then encodes and moves the file into place. The boot row's
  write path is the same method; only the identity's `KeepsImage` flag gates it until H3.

### User scope

A `user`-scoped image belongs to the identity named in its scope key and
lives beside that identity's document, so the account directory is the
player's whole save: characters and their places. Two characters hold two
images of one `user`-scoped destination because their identities differ in
the key. A second process on the same account takes it read-only (I2) and
writes no image under it.

### Batteries (H8)

The machine host captures each named machine's battery bytes beside its
runtime image, and the durable rows carry them keyed by machine name, engine
identity and content hash of the cartridge. `MachineCreationRequest` takes
the battery as bytes in place of `SavePath`; `HumbleGamingBrickCore.LoadBatterySave`
and `FlushSave` and their counterparts in the other engines go, with the wall
clock `FlushSave` stamps. The modes of gap 7 then read: `resume` applies the
whole core from the exact continuation when tier 1 applies and the battery
alone otherwise; `battery` applies the battery alone in either tier and
power-cycles; `none` applies nothing. A battery whose machine name, engine or
cartridge hash no longer matches is dropped and narrated like a row.

### Rejected

- **Keying by instance name** (the first build): the name depends on which
  door minted it and on adoption, and it is an author's spelling, so a
  case-folding filesystem collides two of them.
- **Keying by destination name**: two doors to one document would be two
  worlds while stopped and one while running, since adoption already resolves
  by document.
- **Installing the image's definition**: a retargeted destination or an
  edited source is silently replaced by the saved document, and a derived
  product becomes the truth ([decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
- **Pinning the live definition's bytes** for exactness: measured not
  reproducible from the source and journal (projections rewrite the live
  document outside the journal).
- **A layout key enumerating the compiled shapes** (arena descriptors, kit
  order, field regions, machine inventory) in place of the build identity: a
  list every layout-affecting change must remember to extend, which is an
  invariant kept by convention ([hazards](../development/hazards.md#an-invariant-kept-by-convention)).
- **Rebasing the index-keyed sections** (population by kit index and slot,
  escrow and forwarding by body index) onto a changed source: they have no
  stable identities to rebase by, and a wrong rebase seats a body in the
  wrong kit silently.
- **Carrying cells with their clocks** across a source change: a clock names
  a tick of the run that wrote it, and a fresh activation starts at zero.
- **A separate save-game format**: a second persistence mechanism beside the
  checkpoint and the arena's section form.

### The first build's review, answered

The adversarial review of the first build (kept in the lead's scratch as
`durability-review.md`) raised seven findings. Each is closed by a named
mechanism above; the deal-gate finding is outside this design and stays in
the open items.

1. *Distinct destinations overwrite one image on Windows.* The directory is
   the identity's content hash in lowercase hex, never an author-spelled
   name, and the header repeats the identity for the read to check (H7a).
2. *Adoption makes the durable path depend on activation order; the boot
   instance is skipped.* The location is a function of the row's `Durable` identity,
   assigned once and carried by the row whichever door reached it; the boot
   row carries its identity from `AdmitBoot` and the same write path, gated
   by a declared durability rather than by its name (H7a, H3).
3. *An image is not bound to its source.* The continuation applies only under
   a key of the source's content hash and the build; the row always runs the
   document its source composes; a changed source keeps the durable rows
   through the arena's own door, row by row, with every drop named (H7b,
   H7c).
4. *The deal gate admits a solid variant the load validator refuses.* Not an
   image defect; it stays filed beside the deal gate in the open items.
5. *Reactivating a stopped forwarding row rejects its image and drops
   persistence.* Tier 1 resumes the authority identity the header names, so
   the forwarding records' `SourceAuthority` matches; the store reads the
   row's identity, not the resolver's cache, so retiring a failed activation
   cannot stop later writes (H7a, H7b).
6. *Some restore refusals bypass set-aside and clean fallback.* One ladder
   with one filter that includes `InvalidDataException`, and a two-phase
   restore core, so every refusal lands in the same place and leaves the row
   as its source built it (H7b).
7. *Quit skips a frozen row whose final image failed.* Quit writes every row
   whose final image has not landed; the mark has one writer, set immediately
   before `Retire` (H7d).

### Steps

Each step lands with the mechanical check named, headless, on a fresh
`--state-dir` unless the check is about reusing one. H2 precedes H7a (the
owned-worlds section leaves the checkpoint first).

| Step | Delivers | Check |
|---|---|---|
| H7a | `WorldDurableIdentity`, the row's `Durable` identity, adoption by identity, the seed rung, the store (`IWorldImageStore`, the directory store, header, set-aside, `world.image.discard`), the status echo, the images directory moving with an identity | A test document authoring persisted global `room` and `ROOM` to two documents writes two image directories on Windows; a law over the host starts the garden's document as `manual`, resolves persisted `garden` (adopted), quits, and on a second host resolves `garden` first: `world.instance.status` names the same hash both times; a global and a user-scoped door to one document start two rows; the same document loaded under two instance names of one identity hashes to one definition |
| H7b | The two-phase restore core; `RestoreImage` with tiers 0, 1 and 3; `RestoreContinuation` over the live definition with the source as base; authority resume; journal re-application; deal-memo reset | A fault-injection law makes each named refusal fire (decode, header, admission `InvalidDataException`, `RestoreCheckpointCore` prepare, `RestoreRow`, addon attachment): the row's state hash equals a clean boot's and `image.refused` exists; the garden set at tick N, quit, relaunched on the same root and travelled to reads N; a stopped forwarding row reactivates with its record intact and `KeepsImage` still true; a world with dealt children lists the same placements after a restore as before quit |
| H7c | Tier 2: the rows fold at capture, row-by-row admission, clock stripping, loss narration | The garden's source gains a placement between two runs on one root: the row set in run one reads back in run two and the narration names zero dropped rows; a unit law loads the debug document's rows into a garden arena and reports 15 kept and 454 dropped by name; a cell with a clock restarts at the activation tick |
| H7d | Cadence per row; quit writes for unlanded final images | A World killed after its first cadence image restores the garden at that cadence tick; a store fault refuses a frozen row's final image, the fault is removed, and quit lands it (`ImageWrites` shows the final write) |
| H7e | The embedded documents leave the checkpoint codec; the tape, the history, the silo and the release transition each supply their definition | `puck test --reproduce` over a recorded tape; a history seek across a live edit; the silo's activation law; the release metadata transition law; the garden's image is under 540,000 bytes (532,809 measured without the documents) |
| H8 | Batteries in the durable rows; `MachineCreationRequest` takes bytes; the cores' file I/O deleted; the three modes | As in the build order: a committed homebrew cartridge with battery RAM on a debug-world device writes its RAM; after quit and reboot it reads it back in `battery` and resumes in `resume`; a search finds no file I/O in the four engines' hosting; `puck test --reproduce` over a run that starts from saved RAM; an edited source around the cabinet keeps the battery |

A World a tool or a scenario starts passes its own `--state-dir`
(`WorldTestBatch`, `WorldLaunch`'s state directory, the live World's start), so
no image crosses from one lane's build to another's, and every check above
that reuses a root says so.

## What is the player's and what is the world's

| State | Owner | Lives in | Crosses a seam |
|---|---|---|---|
| Name, colour, avatar claim, rate claims | the character | the identity | yes, in the projection |
| Bindings, HUD panel, seat look, chat, controller memory | the character | the identity | no; stays home |
| Facts and durable slots a package writes | the character, in that package's namespace | the identity | facts and selected records, as today |
| Records (inventory) | the character | the identity | the selected pools, as today |
| A `user`-scoped place's state, its machines' RAM included | the character | an image under the account | no; it stays where its authority runs |
| A global world's state, its machines' RAM included | the world's host | an image in the host's store | no |
| Body pose, momentum, registers | the world embodying it | the world | yes, as in-flight state |
| An ephemeral world's state | nobody | memory only | no |

## Build order

Each step lands on a working branch with the mechanical check named, and shows
its result in the debug world. "Interim" means the step works before the UI
plan's tree; "tree" means it waits for the UI plan's steps 3 and 4.

| Step | Delivers | Gaps | Check | Shown in the debug world | UI |
|---|---|---|---|---|---|
| H1 | The standard's identity template; `Seed` reads it; `IdentityBase` deleted | 1 | Headless, fresh `--state-dir`: `identity.create ada #3366CC`, then the file's size is under 256 KiB (30,293,880 bytes today); `world.transfer` to the garden and back keeps `visits:1` | A character made in the debug world carries none of it | none |
| H2 | One account per process; the stand-in deleted; the read-only second process | 2, 5 | Fresh state root: `identity.list` shows one minted identity and `world.players` no `seat1`; `world.instance.status` names one owned-worlds directory for the boot row and the garden; a fact set in the garden reads back in the boot row with no adoption; after a rule in the boot world writes a second fact, a `world.history` seek of the boot world to before that write leaves the fact on the character; a second World on the root reports read-only | The garden and the hub read one character | none |
| H3 | `player.travel`, `player.escape`, offered destinations, the reserved `hub`, boot as travel | 8, 9 | Headless: a bound `player.travel garden` refuses until the row is offered, then lands; `player.travel hub` from the garden lands in the boot world; an authored command naming `quit` still refuses at `CheckAuthored`; an addon principal's `player.escape` refuses | Leaving the garden without walking back through its frame | interim |
| H4 | Start opens the menu wheel; the engagement family and the `machine` overlay move into the standard | 11, 13 | Injected `gamepad.start` on the resting page opens the `menu` ring (wheel read-back); engaged at a device, the same press reaches the machine's start and no ring opens; an unmapped pad's Start joins | Start at a device starts the game; Start elsewhere opens the menu | interim |
| H5 | The host escape and its reserved gestures | 12 | With the board overlay engaged and a test overlay binding every pad source, View and Menu held one second opens the escape (read-back) and its Exit to hub lands in the boot world; Control, Alt and Escape does the same; a document naming the reserved pair as a chord is refused at the vocabulary gate | Exit to the hub from a seated board game | interim |
| H6 | `Solo` and the derived hold | 10 | Solo: the menu open holds every instance (`world.instance.status`); `player.join 2` lifts it with the menu still open; a second World joined over `host.listen` never holds; a scenario with a mid-run menu pause gives the same state hash as without (`puck test --reproduce`) | The debug world freezes behind the menu alone, never in company | none |
| H7 | Durable images on the desktop, in five steps H7a to H7e ([durable images](#steps)): identity and store, the restore ladder over a two-phase restore core, durable rows across a changed source, cadence and quit, the embedded documents out of the codec | 6 | The checks of each step, and together: set a row in the garden, quit, reboot on the same root, travel to the garden: the row is restored; edit the garden's source between the runs and it still is; two characters hold two images of a `user`-scoped test place; an ephemeral row writes none; killed after a cadence image, the garden restores to it | The garden as you left it, through an edit | none |
| H8 | Battery RAM in images; machine modes; cores without file I/O ([batteries](#batteries-h8)) | 7 | A committed homebrew cartridge with battery RAM on a debug-world device writes its RAM; after quit and reboot it reads it back in `battery` and resumes in `resume`; a search finds no file I/O in the four engines' hosting; `puck test --reproduce` over a run that starts from saved RAM; an edited source around the cabinet keeps the battery | A handheld game's save surviving a restart | none |
| H9 | Package namespaces for facts and durable slots in `Decide` | 6 | The night field's rule writes its namespaced fact and the hall door opens on return; a garden rule writing the night field's namespace is refused by name | The port row's fact-gated door | none |
| H10 | The avatar claim, admit policies and the arrival install | 4 | A character claiming the Moth arrives in the garden (standard `accept`) as the Moth; a test world with `substitute` seats its walker; a claimed speed above the envelope reads clamped in `identity.show`'s `moveEffective`; the garden visit's replay reproduces with the account directory removed | The Moth as a character's own avatar, carried by claim | none |
| H11 | The character place: `identity.new`, `identity.avatar`, `identity.rename`, the pavilion's plinth, pedestals and picker ring | 2, 3 | Headless: pose at the plinth and press the context button, and `identity.list` grows; at a pedestal the claim changes (`identity.show`); in the ring the stick cycles and `world.players` shows the confirmed choice | Making and choosing characters in the studio pavilion | interim (names wait for text fields) |
| H12 | The world list from three sources; the standard `hub` module; player hubs | 14 | An owned place appears as a frame in the painting wing; a hub built from the module is entered by seat 2 in process and by a second World over `host.listen` | The painting wing as the release's world list | none |
| H13 | The menu and the escape on the UI tree; the wheel ring deleted | 12, 13 | `world.ui` reads per-seat menu nodes; the drive verbs navigate to Exit to hub; in split screen one seat's menu covers only its own view | The menu as the standard's styled surface | tree |
| H14 | A character's title and custody across hosts; exit to hub as recall | 3, 8 | The custody model gains the identity title first, as the protocol's rule requires; then two Worlds on loopback, partitioned: exit to hub re-embodies at the hub and the stale copy retires on reconnect | Leaving a lost remote world without losing the character | none |
| H15 | Automatic account sync | 5 | Two state roots on one test container: progress made on one appears on the other after quit and launch; a stale push is refused by name | none | none |

H1 precedes H2, and H2 precedes every step after it. H3 precedes H4, H5 and
H11. H7a to H7d land in order; H7e follows H7d, and H8 follows H7c. H10
precedes H11. H13 waits for the UI plan's steps 3 and 4, and H14 for the
custody protocol's step 7 ([build plan](companion-redesign.md#9-build-plan)).

## Questions for other lanes

These need more than a bounded prototype or measurement, so they go to other
lanes before the steps that need them:
- **P1**, before H11: a prototype of the reach-box register, the input-mode
  family on it and the overlay keyed on that family, showing that a static
  context-button command fires only inside the box and releases on leaving.
- **P2**, before H10: the cold bake time of a visiting avatar's look at its
  first arrival, which decides whether the admit policy's cost envelope must
  also bound bake time and whether the first arrival waits on a background
  bake.

## Owner calls

- **Local co-op.** Several local seats with no remote player is not solo under
  the ruling's words, so the menu would not pause. Many games pause for the
  whole couch. The memo follows the words until the owner says otherwise.
- **The first character.** The memo mints a default character on first launch
  and sends the player to the character place to change it. The alternative
  is to require creation before the first world.
- **The reserved gestures.** View and Menu held one second on a pad, and
  Control, Alt and Escape on a keyboard, are the memo's choice of feel.
- **Saved state over an edited source.** The memo carries a saved row over an
  edit that keeps the row's declaration, so a player's score survives an
  author re-tuning the row's default, and the author resets with
  `world.image.discard`. The alternative is a clean start on any source
  change, with the image set aside.
- **Custody an inexact image holds.** When the exact continuation cannot
  apply and the image holds titles abroad or transfers in doubt, the memo
  starts the row from its durable rows and narrates the loss by name, leaving
  recovery to the custody protocol's recall. The alternative is to refuse the
  activation until the author discards the image or restores the source.

## Review dispositions

Fable's review findings are answered here, one by one, when the review lands.
The durability review of the first image build is answered in
[the first build's review, answered](#the-first-builds-review-answered).

---

[Plans](README.md) · [Play decisions](../decisions/play.md#the-game)
