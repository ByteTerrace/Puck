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
- **Durable images make places last.** The desktop host writes the image of
  every persisted instance through `WorldInstanceHost.DurableImages`, at a
  location keyed by the destination's scope. A machine's battery RAM lives in
  the image of the world that owns the machine, and machine cores stop doing
  file I/O.

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
| I11 | A durable image is written only by the authority that runs the world, at the one location its scope key names. | `WorldInstanceHost.DurableImages` is the one writer; its location is a function of (reference, durability, scope key) (H7). | A crash loses the world state since the last cadence image; identity writes are immediate and are not lost. |
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
(`WorldInstanceHost.Stop.cs:33`, unassigned anywhere in `src/` today). One
writer places each image by its destination's scope key:
- `user`: under the account, keyed by the identity, since the identity is the
  user ([scope](../architecture/worlds.md#durability-scope-and-generation));
- `global` and `group`: under the state root's image store, the host's;
- `ephemeral`: no image.

Images are written when a row stops (the existing stop path,
`WorldInstanceHost.Stop.cs:227-247`), when the session quits (which stops every
row), and on the engine-time cadence the silo already uses
(`WorldAuthorityCheckpointCadenceCounter`), so a crash loses at most one
cadence. Resolving a persisted destination restores its image through
`WorldPersistence.RestoreCheckpoint` when one exists. The hub is resolved the
same way (gap 9), so its persistence is whatever its manifest declares.

So a player save is the account directory: the identities and the images of
the player's own places. On a desktop the player is also the host, and the
host's images of global worlds sit beside it. Nothing here rewrites a source:
`world.save` (`WorldMutationCommandModule.cs:364`) stays the author's save.

A character's progress in a world lives on the character, in that world's
package namespace. The template's visitor policy lets each package write only
durable slots and facts it qualifies with its own namespace, as an aliased
import already qualifies a state row's name; `Decide` checks the namespace
against the source document's package (H9). Worlds of one package (rulepush's
overworld and levels, `experimental/worlds/rulepush/rulepush.puck:47`) share
one namespace, which is what lets an overworld gate read its levels' wins.

The checkpoint today carries the whole compiled definition
(`WorldAuthorityCheckpoint`'s `DefinitionJson`), which the package re-derives.
An image should carry only what cannot be derived: the package's content
identity, the journal's mutations and the state. Whether that format change is
needed before H7 is measurement M1's answer.

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
checkpoint already captures machine cores (`WorldPersistence.Checkpoint.cs:543`,
`:644`), so it persists in the image of the world that owns the machine (gap
6). Each machine row authors a persistence mode, with the default in the
standard's cabinet module:
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
| M1 | Measurement for H7: checkpoint size and capture time of the debug world and the garden | 6 | The readings, and whether the image needs the derived-definition format change first | none | none |
| H7 | Durable images on the desktop: the writer, scope-keyed locations, the cadence, restore on resolve | 6 | Set a row in the garden, quit, reboot on the same root, travel to the garden: the row is restored; two characters hold two images of a `user`-scoped test place; an ephemeral row writes none; killed after a cadence image, the garden restores to it | The garden as you left it | none |
| H8 | Battery RAM in images; machine modes; cores without file I/O | 7 | A committed homebrew cartridge with battery RAM on a debug-world device writes its RAM; after quit and reboot it reads it back in `battery` and resumes in `resume`; a search finds no file I/O in the four engines' hosting; `puck test --reproduce` over a run that starts from saved RAM | A handheld game's save surviving a restart | none |
| H9 | Package namespaces for facts and durable slots in `Decide` | 6 | The night field's rule writes its namespaced fact and the hall door opens on return; a garden rule writing the night field's namespace is refused by name | The port row's fact-gated door | none |
| H10 | The avatar claim, admit policies and the arrival install | 4 | A character claiming the Moth arrives in the garden (standard `accept`) as the Moth; a test world with `substitute` seats its walker; a claimed speed above the envelope reads clamped in `identity.show`'s `moveEffective`; the garden visit's replay reproduces with the account directory removed | The Moth as a character's own avatar, carried by claim | none |
| H11 | The character place: `identity.new`, `identity.avatar`, `identity.rename`, the pavilion's plinth, pedestals and picker ring | 2, 3 | Headless: pose at the plinth and press the context button, and `identity.list` grows; at a pedestal the claim changes (`identity.show`); in the ring the stick cycles and `world.players` shows the confirmed choice | Making and choosing characters in the studio pavilion | interim (names wait for text fields) |
| H12 | The world list from three sources; the standard `hub` module; player hubs | 14 | An owned place appears as a frame in the painting wing; a hub built from the module is entered by seat 2 in process and by a second World over `host.listen` | The painting wing as the release's world list | none |
| H13 | The menu and the escape on the UI tree; the wheel ring deleted | 12, 13 | `world.ui` reads per-seat menu nodes; the drive verbs navigate to Exit to hub; in split screen one seat's menu covers only its own view | The menu as the standard's styled surface | tree |
| H14 | A character's title and custody across hosts; exit to hub as recall | 3, 8 | The custody model gains the identity title first, as the protocol's rule requires; then two Worlds on loopback, partitioned: exit to hub re-embodies at the hub and the stale copy retires on reconnect | Leaving a lost remote world without losing the character | none |
| H15 | Automatic account sync | 5 | Two state roots on one test container: progress made on one appears on the other after quit and launch; a stale push is refused by name | none | none |

H1 precedes H2, and H2 precedes every step after it. H3 precedes H4, H5 and
H11. H7 precedes H8. H10 precedes H11. H13 waits for the UI plan's steps 3 and
4, and H14 for the custody protocol's step 7
([build plan](companion-redesign.md#9-build-plan)).

## Questions for other lanes

These need more than a bounded prototype or measurement, so they go to other
lanes before the steps that need them:
- **M1** (above): how large and slow a debug-world checkpoint is, which decides
  whether images drop the derived definition before H7.
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

## Review dispositions

Fable's review findings are answered here, one by one, when the review lands.

---

[Plans](README.md) · [Play decisions](../decisions/play.md#the-game)
