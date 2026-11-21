# A hosted world on a client

A player's client can present a world that another process simulates. Today that
client draws the world's shapes and bodies and nothing else: its text, its
machines and the worlds its screens look into are blank, and the player cannot
walk. This memo settles how each of those reaches the client, at the scale of a
hub of a dozen worlds seen by a town of players, and fixes the defects that stop
a remote seat from playing. It is the design for
[a hosted world on a client](open-items.md#a-hosted-world-on-a-client), the
packet that milestone M1 rests on: the RTX 2060, running an installed release,
joins the hosted debug world and sees all of it.

It follows [the MMO foundations memo](mmo-foundations/synthesis.md)'s manner.
Each decision gives the options, the evidence, the attacks it closes, what it
costs and the ruling it rests on. Nothing here is built except the narration
described under [what ran](#what-ran-and-what-it-settled). Another family
reviewed the first draft and raised fourteen high findings, three medium and
two low. Each was rechecked against the code and is
[answered below](#the-reviews-findings-answered); the decisions here already
carry the changes.

Four words are used throughout.

- An **authority** is the process that simulates a world. A **client** is a
  process that presents a world some other authority simulates.
- A **delivery** is what an authority sends one recipient: the world's document
  and, each tick, its bodies and changed state. A **stream** is one recipient's
  deliveries from one authority, in order.
- A **pin** is a content hash that names bytes exactly (`ContentPin`).
- A **cut** is the moment a stream loses the right to read something it was
  delivered ([the withdrawal chokepoint](disclosure/withdrawal-chokepoint.md)
  defines it).

## Summary

- **The rule for every kind:** the authority sends inputs and identities, and
  the client computes the picture. Text, a machine's frames and a window's image
  are all functions of content the client can hold and inputs the authority can
  send in a few bytes. The authority never renders or encodes a picture for a
  viewer. A hosted authority has no GPU, a window is drawn from each viewer's
  own eye, and encoded video costs every viewer for every screen.
- **Content travels by pin, never by path.** A delivered document names fonts,
  sounds, voices, cartridges and creations by their pins. The client finds the
  bytes in the package it shipped with, then in its shared content store, then
  by an authenticated fetch. One resolver replaces three partial mechanisms.
- **A machine runs on the client** from the cartridge the client holds, started
  from an image of the authority's machine and fed the same inputs the
  authority's machine was fed. A viewer costs the authority a join image and a
  trickle of input bytes, only for the machines it looks at.
- **A window's world is drawn by the client from a delivery relayed by the
  authority that owns the screen.** That authority already holds the session the
  screen observes through. It serves the session's stream to the viewers who
  look at the screen, and stops when a viewer loses the screen.
- **The document travels as a manifest of pins.** A client whose package matches
  the hosted world is sent a few kilobytes where it is sent 21.9 MB today, and
  no single message grows with the world.
- **The remote seat's input is lost at the authority's input admission,** which
  was settled by running a pair. The intent stream carries a backlog of most of
  a second, the admission refuses a batch applied after the tick it was
  captured for, and one refusal refuses everything after it. The fix removes
  the backlog, re-times a remote batch at the door and never drops one
  silently.
- **Who may arrive, and as what, is authored.** The debug world authors its
  namespace, its visitor slots and separate admission rows for windows and for
  arriving bodies. The engine keeps no default that admits anyone.
- **A client simulates only what it presents.** Its own copy of the world stops
  stepping once its seat has left for the remote authority.
- **A machine's author chooses how viewers are shown it.** A client that runs a
  machine holds its whole memory, not only its picture. A machine's declaration
  says whether viewers run it, are sent its pictures, or see nothing
  ([decision 2](#decision-2-a-machine-runs-on-the-client)).

## What ran, and what it settled

The packet asked which of four places drops a remote seat's input. Reading the
code could not say, so a pair was run on this tree: two headless Worlds on one
machine, one listening and one started with `--connect`, both booted from one
staged document with one signing key. Each of the four places was given a line
of narration first (`WorldTick.NoteFederatedDrop`).

**The debug world does not get that far.** Staged as the benchmark packet
describes, its transfer is refused at the reservation. The authority's answer
embeds its whole document, and the client refuses that document for eleven
references by relative path (a HUD voice, three tunes, six patches and the
music row) and for a rule that reads a state cell the client was not sent. So
the packet's statement that patches are now sent inline does not hold on this
path: the inlining (`WorldAssetRowLoader.EmbedAudio`) runs for an observation
stream and not for a reservation's answer.

**On a small document the transfer commits and the input arrives.** The staged
document was the Moth, her controls and a floor. The client's seat submitted
every tick, the intent stream carried each batch and the authority acknowledged
each one. Three of the four places never fired. The fourth did: the authority's
input admission (`WorldInputOwner.TryAdmit`, reached through
`WorldTick.ApplyFederatedIntents`) refused batches. Whether it refuses depends
on timing, so the pair was run four times.

| Run | What was sent | What the authority did |
|---|---|---|
| 1 and 2, with scratch probes writing to both processes' error streams | One held stick | Refused the batch that carried the movement, as `input.time` and as `input.late`; the body did not move in 90 ticks |
| 3, on the committed head | One held stick | Admitted it; the body moved 14.6 units in 90 ticks |
| 4, on the committed head | Twenty stick movements, ten ticks apart | Refused 18 of the 20 as `input.time`; the body moved 1.4 units |

The refusals have three causes, and each is a defect on its own.

1. **The intent stream carries latency that never drains.** In run 4 the
   authority applied each refused batch between 21 and 46 ticks after the tick
   the client captured it for, settling at a constant 21. That is 0.7 seconds
   at this world's 30 ticks a second, on one machine. The mechanism was read in
   the code and not separately instrumented: the client sends one batch and
   waits for its acknowledgement before the next, the authority runs each batch
   on its tick thread before it acknowledges, and the client queues a batch for
   every remote tick without coalescing. The stream therefore carries at most
   one batch for each authority tick, which is the rate batches are made, so a
   stall leaves a backlog for good. Run 3 had no backlog, and its batch was
   applied in the tick it was captured for.
2. **The admission refuses a batch applied later than it was captured.** The
   authority rewrites a remote batch's tick to the tick that applies it, and
   leaves each event stamped with the earlier tick. The world authors no
   lateness allowance (`WorldInputHistory.MaximumLatenessSeconds` defaults to
   zero) and the authority keeps two earlier intervals, so an event one tick
   old is refused as late and an older one as unknown.
3. **One refused batch refuses every batch after it.** The refused batch held
   the only record that the stick moved. Later batches carry the stick's held
   position and no event, and the authority, which never took the movement,
   refuses each one as inconsistent
   (`input.image: ordered transitions disagree with final image`) until the
   stick moves again and that movement happens to be admitted.

Two smaller things showed beside them. For the first few ticks of a stream the
batches name the input epoch of the client's own world, until the client takes
the authority's published epoch; that is the ordinary start of a stream. And in
run 4 the authority detached the client's observation once for falling behind
(the benchmark packet's backpressure item), after which the client's input
profile was admitted again twice.

The existing narration of an input refusal is a bare line with no body or
actor, repeated every tick, which is why the earlier reading found that neither
process named a refusal.

The same pair confirmed two more of the trace's open points. A client started
with `--connect` keeps stepping its own copy of the world after its seat has
left: its host loop went on reporting simulation steps for the whole run. And
`body.where` for a seat whose body is on the remote authority fails with a null
reference on the client.

`puck_world_start` cannot pass a listening endpoint, a remote endpoint or a
signing key, so the pair was started by hand from the worktree's build and
driven through the operator attachment. [The build order](#the-build-order)
gives the tool those three arguments.

## Decision 1: content travels by pin

**What must be decided.** How a client finds the bytes of a font, a patch, a
voice sample set or a cartridge that a delivered document names.

**Today.** An authored row names its content by a path beside the document and
carries the content's pin (`hash`). A delivered document has no directory
(`WorldDefinition.DocumentDirectory` is never serialized), so every path in it
is unresolvable. Three mechanisms each cover part of the gap. Fonts resolve
beside a directory that a delivered document lacks (`WorldTextCatalog`), so
delivered text never draws. Patches, tunes and music are copied whole into the
document before an observation stream sends it (`EmbedAudio`). A projection's
creations travel as pins and are fetched on a miss
(`WorldProjectionPrototypeFetch`).

| Option | Cost | Fails at |
|---|---|---|
| The client resolves each path beside its own copy of the document | None on the wire | A visitor who holds another version, or no copy; and a path taken from the wire is a path an authority chose on the client's disk |
| The authority copies every asset into the document | Every font and sample in every document delivery | The document grows with its content, and is sent again when anything in it changes |
| The authority streams the finished picture (glyph images) | An encoder per screen | Text is the cheapest thing a client can draw |
| **Rows name pins; one resolver finds the bytes** | The resolver, and a pin on the row families that lack one | Nothing found |

**Favoured: the last.** One resolver answers a pin with bytes. A row's pin
names the canonical bytes of its family (`WorldAssetRowLoader` hashes the
canonical document, not the file as written), so the resolver serves canonical
bytes everywhere. It looks first in the packages this process loaded, whose
index is built from those canonical forms when the package is prepared. It
looks next in the per-user content store (`ContentAddressedStore`), which is
shared across worlds and runs. Last it fetches through the stream's
authenticated request lane and checks the bytes against the pin before it uses
them. One resolver serves the whole process: every scene shown from another
world builds its own text catalog and font cache today, and those become
readers of the one resolver and one atlas cache keyed by pin.

Four limits come with it. A voice sample set names its samples by path, so the
set's canonical form carries a pin for each sample. A machine's declaration
carries the pin of its prepared image (`WorldMachineCartridge.RomHash`),
because a player's build cannot compile a cartridge. Bytes fetched from an
authority are checked as their family's type before they are stored, and are
stored against a byte budget for that authority, so a hostile authority cannot
push other worlds' content out of the shared store. A delivered font catalog is
bounded in font bytes, glyph count and atlas pixels before any glyph is
rasterized. A delivered row is read by its pin alone, and its path is a label. The
loaders that take a directory today (the font resolver's `IAssetSource`, the
audio row loaders, `WorldVoiceSampleLoader`, the machine content folders) take
the resolver instead when the document was delivered.

This deletes `EmbedAudio` and the directory closure in `WorldTextCatalog`, and
widens the prototype fetch into a content fetch. Voice sample sets, which the
trace found carry no pin, gain one at compile as the other families have.

The presentation tier needs one more thing. A projection carries neither the
world's text catalog nor its machines (`WorldProjectionDocument` has no member
for either), and presentation is the tier a visitor is given unless a row
authors another. The projection gains both: the font catalog's rows with their
pins, and each machine's presentation half (its engine, its prepared image's
pin, its screens and outputs), without the bindings that tie a machine to state
cells.

**Evidence.** The pair's refusal list above. The resolver's three sources all
exist: the player folder ships the compiled package and prepared cartridges
(`WorldPlayerCommand`), the store keys objects by pin, and the fetch door exists
for prototypes.

**Attacks it closes.** A hostile authority cannot name a file on the client's
disk, because no path from the wire is opened. A hostile authority cannot
poison the store, because bytes that do not hash to their pin are refused. A
recipient cannot use a pin to read content it was not sent: the existing rule,
that every fetch recomposes the recipient's current delivery and requires the
pin to occur in it, covers every family.

**At MMO scale.** A dozen worlds in one hub share fonts, patches and cartridges
by pin, so each is held once. A town of viewers who installed the same release
fetch nothing. A live edit to one asset costs each viewer that asset once.

**Cost.** Medium. Most of the work is moving each loader from a directory to
the resolver.

**Ruling it rests on.** Derived products are caches whose identity is computed
from their inputs; and the engine carries what an author writes in a creation's
identified bytes.

## Decision 2: a machine runs on the client

**What must be decided.** How the picture and sound of an emulated machine
reach a client: an emulator cabinet, a television, anything a machine screen
shows.

**Today.** A machine screen asks the machine host of a world this process runs
and is given none for a remote world (`WorldScreenBinder.MachinesOf`). Nothing
about a machine crosses the wire.

| Option | Cost per viewer per machine | Fails at |
|---|---|---|
| The authority streams frames and sound | A machine's picture is the same for every viewer, so it is encoded once per watched machine and sent to each. A small handheld's frame is 5,760 bytes uncompressed, about 2.7 megabits a second before compression, plus sound | The authority runs an encoder for every watched machine, and each viewer pays the stream for as long as it watches |
| **The client runs the machine from the same inputs** | One image when it starts watching (tens to hundreds of kilobytes), then the pad changes, a few bytes when a player presses something | A machine whose state a viewer must not hold; a machine that cannot save its state |

The figures are worked from the formats and were not measured.

**Favoured: the client runs it.** A machine is already declared to be a
function of its content and its inputs: its host's replay policy is
`WorldExtensionReplayPolicy.Recomputed`, and its step takes the tick budget and
the pad lanes (`IWorldMachineHost.Advance`). The design sends exactly those.

- **What is sent.** For each authority tick, in the order the authority applied
  them, every call that changes a machine: the step with each screen's whole
  pad lane, its timed presses within the tick included (`ScreenPadSnapshot`),
  each hardware write a rule made (`IWorldMachineHost.WriteHardware`), each
  committed operation such as a cartridge swap
  (`IWorldMachineHost.TryCommitOperation`), and each change to the cables and
  the screens' selections (`IWorldMachineHost.ReconcileLinks`,
  `IWorldMachineHost.ReconcileScreens`). The record is taken at the host's
  interface, by a recorder that wraps the host where the World is composed and
  implements every member. A change that replaces the machines themselves is
  not replayed: a restore or rewind
  (`IWorldMachineCheckpointHost.RestoreCheckpoint`) and a document install that
  changes the machine declarations (`IWorldMachineHost.Commit`) each end the
  record and start a new image.
- **How a viewer starts.** The authority captures the machine's image at a tick
  boundary (`WorldMachineCheckpoint`, which the authority's own checkpoints
  already produce) and names it by pin. Viewers who start within a short window
  share one image and replay the records since it. The image and a swapped
  cartridge are not in the document, so their fetch is authorized by the
  viewer's subscription to that machine, which names them, and not by the
  document. The cartridge is content, found by pin under
  [decision 1](#decision-1-content-travels-by-pin). Two cases cannot start
  today and show the screen's fallback with a named reason until they can:
  machines coupled by a live cable, whose capture the host refuses, and an
  engine with no saved state. Capturing a coupled pair as one unit, at the
  barrier the host already steps it at, is part of this step.
- **What a viewer watches.** The unit is a machine and everything cabled to it,
  since linked machines step as one. A client asks for a unit while one of its
  screens is in sight, by the test that already decides whether a window's
  world is drawn (`WorldPortalVisibility`). It lets go a short time after the
  screen leaves sight. How long it lingers is a value of the viewer's
  nested-view table, which the standard package authors.
- **Where it runs.** The client builds a second machine host for the delivered
  world from the machine declarations it was delivered, and the screen binder
  reads it exactly as it reads a local world's. The machines step on the worker
  threads the host already uses for queued machines, never on the frame thread.
  Sound comes with it: the replica is a machine host, and a screen's audio is
  read from the host.
- **How a difference is caught.** The authority sends a digest of each watched
  machine's state every few seconds of steps. A client whose digest differs
  takes a new image, and the difference is counted and narrated. It is an
  engine defect, since the same content and inputs must give the same state on
  every machine (AGENTS.md rule 4), and it is never smoothed over.

**The invariant.** A client's machine is at the authority's state for every
step it has replayed. **What enforces it:** the recorder is the only holder of
the real host that the simulation is handed, and it implements the host's
whole interface, so a new member does not compile until its author has written
the recorder's half. Whether that half records is checked by review and caught
by the digest. The digest shows a difference
within one interval. **Where it stops:** the client's machine is behind the
authority's by the time its records take to arrive.

**The disclosure rule is authored per machine.** A client that runs a machine
holds its whole memory and every player's pad input to it, not only the
picture. For an arcade cabinet that is nothing a viewer could not see on the
screen. For a machine that keeps a secret in memory, such as a hand of cards or
a typed code, it is a leak. So a machine's declaration gains an authored member
that says how it is shown to a viewer who is not the authority: run from its
inputs, shown as pictures, or not shown. The engine's default is the first, and
the choice is the author's.

- *Run from its inputs.* A client is sent the image and inputs only while the
  document it was last delivered carries the machine's declaration, and only
  when every machine cabled to it is also run from its inputs for that client.
  A unit with one member the client may not run is not run at all.
- *Shown as pictures.* The authority encodes the machine's frames and sound
  once and sends them to each viewer. This is the first option above, taken for
  the machines that need it. It is the last step of
  [the build order](#the-build-order), since no debug world machine keeps a
  secret; until it is built such a machine shows its fallback.
- *Not shown.* The viewer sees the screen's fallback.

**Strongest case against.** The client pays the processor time of every machine
it watches. The bound is the machines in sight, and an arcade wall could exceed
what a Steam Deck can emulate. The device's quality table is the place to cap
how many machines a view runs at once; a machine past the cap shows its last
frame.

**A player at a remote cabinet** sees their press after a round trip: the press
goes to the authority, enters the pad lane, and returns in the record. Running
the player's own machine ahead of the authority and rewinding on a difference
is possible, because a machine can save and restore its state. It belongs with
the seat's own-body prediction
([replication](replication.md#decision-7-the-seats-own-body)) and is not part
of M1.

**At MMO scale.** A machine's cost to its authority no longer grows with its
audience beyond a few bytes a second per viewer. The authority runs no encoder.

**Cost.** Large: the recorder, the record on the wire, the image by pin, the
client's host and its place in the screen binder.

**Ruling it rests on.** Determinism pins the mapping; and the emulator cores are
machinery that runs authored cartridges, like any other program the engine
runs.

## Decision 3: a window's world is relayed by the authority that owns the screen

**What must be decided.** How a client draws a screen that looks into another
world: a gallery painting, a portal's glass, a sky layer that shows a moon.

**Today.** The authority of the world that holds the screen opens a session
onto the destination and is delivered what the destination's admission
discloses to it (`WorldInstanceHost.OpenScreenSession`,
`WorldServer.TryObserveAsSession`). A presentation in the same process draws
the window from that session's mirror. The sessions are held only for worlds
that process runs (`WorldInstanceHost.ScreenSession`), and no message carries a
session to a client, so every window of a remote world is blank. The authority
does hold the sessions, because the traveller's body stands in the world.

| Option | Fails at |
|---|---|
| The authority draws the window and streams the image | A window is drawn from each viewer's own eye, so nothing is shared between viewers; a hosted authority has no GPU; video per viewer per window |
| The client runs its own copy of the destination | It shows the client's private garden, not the one other players are standing in |
| The client observes the destination itself, under its own identity | The picture then depends on who looks, which one image per screen cannot show; the destination must admit every stranger; and when the viewer loses the screen, the destination does not know |
| **The authority that owns the screen relays the session's stream** | The relaying authority carries the bytes |

**Favoured: the relay.** A screen is a surface of its world. What it shows is
what that world's authority was admitted to see, and who may look at the screen
is that world's own decision. Both facts already live on the authority that
owns the screen, so that authority serves the stream.

- **The request.** A client asks, over the connection it already holds for the
  world it presents, to observe a screen by its place: a screen index or a sky
  layer's name, and for a window seen through a window, the chain of them. The
  authority finds the recipient as a traveller's observation does
  (`WorldTravelerProjection`). It checks that the recipient's current delivery
  carries that screen with a session source. It finds the session it already
  holds, and subscribes the recipient to that session's relay.
- **A relay is a projection, never the document.** A window shows a picture of
  its destination. The destination's rules, grants, admission rows and unbound
  state are no part of a picture, so a relay is composed at the presentation
  tier whatever tier the session or the recipient holds, and at nothing when
  either holds only frames.
- **One encoder for each scene, and no new connection.** The owning authority
  keeps one relay for each session scene: one sink
  (`WorldFederationProjectionSink`) on the destination for the session's own
  principal, which composes and encodes each record once. Every subscriber is
  sent the same bytes. A subscriber who joins late is sent the relay's retained
  primer, the whole projection and the latest bodies, and then what everyone
  is sent. The records travel on the observation stream the client already
  holds, each tagged with its relay, because a connection carries one stream
  and an authority admits a bounded number of connections
  (`WorldPeerHost.MaximumFederationConnections`). Subscribing and unsubscribing
  are requests on the client's existing request lane.
- **Content a relayed world names is fetched through the relay.** A fetch for a
  pin from a relayed world names the relay it came from, and the door checks
  that the pin occurs in that relay's current projection, since it does not
  occur in the owning world's.
- **What the client does with it.** The screen binder's lookup for a session
  answers, for a remote world, with a session whose mirror is fed by that
  stream. Everything after the mirror is unchanged: the binder builds the scene
  and the residency as it does for a local session.
- **Cost follows attention.** The client opens a relay while the screen's glass
  is in sight and within the nesting depth, by the test the presentation
  already uses to decide whether to draw the level
  (`WorldViewGraphHost.PortalSightOf`), and closes it after the same linger as
  a machine. A headless client opens them unread for every screen within the
  depth, as a headless authority does, so a run without a GPU can count them.
- **One relay for one scene.** Several of the debug world's paintings look into
  one world. When two sessions onto one destination are composed for one
  audience, by the audience key the authority already keeps for its policy
  table (`WorldDisclosureImages.Audience`), the authority gives both screens
  one relay. The key never leaves the authority and is decided again when the
  policy table changes, which cuts the relay. The scene proof that lets two
  windows share inside one process (`WorldSceneProof`) is issued only at the
  replica tier, so it cannot decide this. The client keeps one mirror and one
  residency for a relay, and keeps the relay while any of its screens is in
  sight. Each screen's own picture is still held under two spans, the owning
  world's and the relay's, so losing one screen withdraws that screen alone.
  The client never compares content to decide sharing.
- **A screen whose destination depends on who looks** is not served by this.
  The authority holds one session for a screen, so a relay shows every viewer
  what a presentation in the authority's own process would show. A screen that
  shows each viewer a different world needs a session for each viewer, which
  is [the multi-seat design](open-items.md#a-multi-seat-client)'s open item;
  the voucher below is how such a viewer would attach.
- **A destination on another authority** is not reachable by a session screen
  at all today. When it is ([the moon on another authority](open-items.md#the-debug-world-at-mmo-scale)),
  the same request is forwarded along the session's own route with a tier
  ceiling and a hop limit, as a traveller's observation already is
  (`IWorldForwardedAuthority.StreamProjectionAsync`).

**The invariant.** A client is delivered a window's world only while its own
current delivery of the owning world carries that screen, and never more than
the destination disclosed to the session. **What enforces it:** the relay is
opened only by the one request handler, which reads the recipient's current
view; the sink's `isCurrent` test, which every traveller projection already
passes, repeats that check at each delivery and ends the stream with a
terminal cut when it fails. **Where it stops:** at a client that keeps what it
was sent.

**Attacks it closes.** A client cannot ask for a screen it was not shown. A
client cannot widen what a session sees, because it adds no admission: it reads
a session the authority already holds. One viewer costs the authority one
stream per scene it looks at, bounded by the byte budget each stream is given
([replication](replication.md#decision-6-interest-and-priority-under-a-byte-budget)).

**Strongest case against.** When the destination runs on another authority, the
owning authority receives the stream once and sends it to every viewer. Total
traffic is the same as if each viewer connected to the destination, but it
passes through the owner. If that egress ever bounds a hub, the owner can
answer the request with a short-lived voucher that lets the viewer attach to
the destination directly as a reader of the same session. That is a change to
where the bytes flow, not to what a viewer may see, and it waits for a
measurement that asks for it.

**This narrows one statement in the replication plan.** Its decision 6 says a
player watching three worlds through portals holds three streams from three
authorities. Under the relay the player still holds one stream per world
watched and still divides its own downstream among them, and the request for a
smaller budget travels with the relay request. What changes is that the owning
authority is on the path.

**At MMO scale.** A hub's authority holds a dozen sessions whatever its
audience. Each viewer costs a stream per world in sight. Two viewers of one
scene are sent the same records, which the sink encodes once.

**Cost.** Medium: one request kind, the host-level door that finds a session
for a recipient, and the client's remote session.

**Ruling it rests on.** Disclosure is never traded for memory or bandwidth; and
structural answers come before tuning.

## Decision 4: the document travels as a manifest

**What must be decided.** What replaces a whole document in one message.

**Today.** At the replica tier the authority serializes its whole disclosed
document into one record on every document delivery. The debug world's is
21.9 MB. Every frame is read under `WorldFederationCodec.MaxFrameBytes`, which
is lower than the per-kind document cap the benchmark packet names, so the
frame limit is the one that binds. A reservation's answer and a route
description embed the same document again.

**Favoured.** The document is sent as a manifest: each root member by the
digest of its canonical bytes, and each creation by its own pin, as a
projection's prototypes already are. `WorldDefinitionMembers` already slices a
document into members with a digest each, for the reload door. The recipient
assembles the document by resolving each entry: from the document it holds,
whose members it digests the same way; from its content store; or by a fetch.
After the first manifest the authority sends only the entries that changed.
Members that came from the wire are read as untrusted input
(`WorldJsonContext.Untrusted`), which the reload door's reader does not do
today because it reads an author's own file.

A reservation's answer and a route description stop carrying a document. They
name its version, and the document arrives on the observation stream. That
leaves one door through which a document reaches a recipient, the delivery
cursor (`WorldDeliveryCursor`), which is where disclosure is applied.

**A delivered document is checked as a delivery, not as an authored source.**
The client refuses today's delivery for a rule that reads a cell the client was
not sent. A client runs no rules of a world it only presents. The validator is
already divided into partitions that each declare the parts they read
(`WorldDefinitionValidator.Partitions`), so a delivered document is checked by
the partitions whose parts the client consumes and by the bounded decoder, and
never by the partitions that compile rules or open files.

**Evidence.** The pair's refusal, and the client's own package: a player build
ships the world it joins, so for most members the client already holds the
bytes the manifest names.

**Attacks it closes.** No single message grows with the world, so the frame
limit bounds a row and stops being a limit on what an author may build. An
observer that falls behind is sent the entries it lacks, not the world again.

**At MMO scale.** A client that installed the hosted world's release is sent a
manifest and no bodies. An author's live edit costs each viewer the rows that
changed, which is
[replication's decision 9](replication.md#decision-9-documents-and-checkpoints-as-records).
This step sends member and row bytes as they are serialized today; the
generated records of that plan replace the bytes later without changing the
manifest.

**Cost.** Medium.

**Ruling it rests on.** No format is a constraint; and a derived product's
identity is computed from its inputs.

## Decision 5: a remote seat plays

Each defect is fixed at the cause that the pair or the code showed.

**Input arrives without a backlog, is re-timed at the door and is never dropped
silently.** One change to the stream, and two rules in the one place a remote
batch enters the tick (`WorldTick.ApplyFederatedIntents`).

1. *The stream does not wait.* The client writes each batch as it is made and
   does not wait for an acknowledgement; the authority's answer to an input
   already returns in its deliveries (`WorldInputAnswer`). The authority takes
   an arrived batch under its authority gate, as the path inside one process
   already does, and does not wait for its tick thread before it reads the
   next. A batch is then late by its transit and at most one tick. Batches are
   never merged or replaced: a batch can carry a pointer's movement or a
   source's return to neutral with no event to mark it, so dropping one loses
   input. The queue keeps its existing bound, past which the stream restarts as
   a lost capture.
2. *The authority decides when a remote input happened.* An event in a remote
   batch claims a capture time in the authority's ticks that the client could
   only estimate. The admission keeps a claim it can honour: one inside the
   world's lateness allowance, in an interval it still holds. Any other claim,
   late or ahead, is moved to the tick that applies it, at or after the last
   event the stream delivered, so the stream's events stay in order. A world
   that wants a remote player's timing honoured more exactly authors a larger
   allowance (`WorldInputHistory.MaximumLatenessSeconds`), and with it accepts
   that a player may act on what the allowance let them see; that trade is the
   author's. A local seat's events are stamped in the same process and keep
   today's refusal.
3. *An event is applied or its stream is restarted once, and the client is
   told.* A batch that names an epoch the authority has left is dropped; that
   is the ordinary state of a stream for a moment after any epoch change, and
   restarting for it would restart forever while batches are in flight. When
   the admission refuses a batch of the current epoch for any reason but
   permission, the door resets that input owner. The reset starts a new epoch,
   the authority publishes it, and the client continues from the published
   state, as it already does at the start of a stream.

A seat submits to a remote authority once for each tick it has observed, which
is once for each delivery. The review asked whether a world that steps several
times for each delivery loses the input between deliveries. It does not: the
pair was run on a staged document that steps eight times for each delivery,
and every stick movement was admitted. A stick movement reaches the authority
within one delivery interval of when it was made, so a push measured in ticks
can differ from the same push in one process by that much at each end.

The narration added for the pair stays: one line when a body's remote input
starts being dropped or the reason changes. The per-tick bare line from the
admission is replaced by the same once-per-change form. Saying a thing once
until its reason changes is written three times in the tree (this door, the
drive denial beside it, and the render graph's completion narration), so the
step makes it one shared type and all three use it. The client's intent
stream gains the same treatment for a worker that ends on an error it does not
handle, which today ends the stream without a word (read in the code, not
observed).

**The invariant.** Every event a remote seat sends in the current epoch is
applied once, in order, or its stream is restarted and says so. **What
enforces it:** the door is the only caller of the admission for a remote
batch, and its refusal branch for a current-epoch batch has no path that
continues without the reset. **Where it stops:** a remote event's time is the
authority's, within the allowance the world authors. Two events that arrive in
one tick after a stall are applied in that tick, so a hold shorter than the
stall can be shortened to that tick; an author who needs holds measured across
a stall authors an allowance that covers it.

**Attacks it closes.** A client cannot claim a capture time earlier than the
allowance to win a timing judgement, because the claim is moved. It cannot
wedge its own stream into a state the authority refuses forever, and a stale
batch cannot make the authority restart a stream again and again. A slow
client cannot make the authority hold a growing queue, because the queue is
bounded and its overflow restarts the stream.

**Admission is authored, and says what kind of arrival it admits.** One debug
world row serves two kinds of arrival today: a session looking in through a
window, and a traveller's body. Its `observe all` grant is right for a session
and refused for a body, so a traveller is delivered nothing. Whether a world
admits windows, visitors or both is the author's policy, so an admission row
gains an authored member that names the kinds it admits (`WorldAdmissionEntry`),
and the door matches a row to the arrival's kind. A row that authors none
admits both, as every row does today. Two rows may then name one authority for
different kinds, so the validator's rule that rows differ by domain, subject
and mode gains the kind, and each lookup that asks what an authority is
disclosed is told which kind is asking. The standard package gains a
template for a visitor's row: an exclusive `Drive` and an `Observe` over the
body the admission assigns. The debug world expands it, keeps its session row
for the paintings, and authors its visitor slots (`bodies.networkPlayers`) and
its `host.authorityNamespace`. No engine default admits anyone.

**The remote authority's name stays pinned by the client.** A `--connect` run
refuses a document that authors no namespace, because the name it expects at
the endpoint is the one its own document authors. That is a pin, and it must
stay one: when trust rests on a root that vouches for many authorities, a name
taken from the handshake would let any of them answer for the world the player
meant to join. The defect is only that the debug world authors no namespace.
It authors one, the client boots the same package, and the pin holds. The
first draft proposed reading the name from the handshake; the review showed
why that is wrong.

**A client simulates only what it presents.** A `--connect` client boots its
own copy of the world to have a body to send. Once the seat's route turns
remote, nobody stands in that copy and nothing shows it, yet it steps every
tick with its machines, its addons and the worlds its own screens observe. A
host that does not listen pauses its boot world while no seat stands in it,
nothing it presents shows it, and no transfer, custody or unsettled crossing
still refers to it: the conditions under which any other world would be
stopped (`WorldInstanceHost.ReapIfEmpty`). A paused world takes no simulation
step and still drains its administrative work, so a late settlement is not
stranded. It closes that world's sessions and resumes when a seat returns. A
host that listens keeps stepping, since others may join it.

## Decision 6: the lease after a lost connection

[The withdrawal chokepoint](disclosure/withdrawal-chokepoint.md) closes a
stream's span the moment its connection is lost, because a viewer that cannot
be told of a cut must not go on showing what it has. That blanks a neighbour's
window on every network hiccup. It leaves the remedy to the lane that next
works on remote observation, which is this one.

**Favoured.** An admission row gains a `lease`: how long a stream's last
delivery stays showable after its connection is lost. It is an author's
statement of how stale a view of their world may be. The engine's default is
none. The value travels to the observer once, with the stream's admission, and
never changes during the stream. When the connection is lost the observer's
transport waits that long on its own clock before it closes the span
(`WorldRemoteAuthority.CloseObserved`, the one caller that closes a span for a
loss). A lease covers only silence. Any word from the authority ends it at
once: a terminal cut, a notice that the projection is no longer valid, a new
session's first record, or a refusal when the observer tries to reconnect,
which it keeps doing throughout the lease. An authority that revokes a
credential or refuses a disclosure sends a terminal cut before it closes the
connection, where today it only closes it. A relayed window's lease is the
shorter of the owning world's and the destination's.

**The invariant.** A span closed for a lost connection is shown for at most the
lease its admission granted. **What enforces it:** one caller closes a span for
a loss, and the lease is a field of the stream fixed at admission. **Where it
stops:** inside the lease a viewer may show what the authority has since cut,
which the chokepoint memo already names as a limit.

**How it meets the chokepoint's second step,** which another lane is building.
That step makes content and the span it was delivered under one value and
gives a frame one latched answer about which spans are current. The lease
changes only when the close happens. It adds nothing to the span, the label or
the latch, so the two can land in either order.

The same step owes one thing to this design. A client's machine frames and
text atlases are made from delivered content, so they are pictures of the
stream that delivered their inputs. They must be made by the holder that owns
everything built from a stream's scene (the chokepoint's part 3), so that a
cut of the stream discards the machine host and the atlas with the residency.
Until that holder exists they are owned by the screen binder's per-session
objects, which the existing withdrawal releases.

## Invariants and what enforces each

| Invariant | Enforced by | A new caller that forgets |
|---|---|---|
| No path from the wire is opened | A delivered document has no directory, and the loaders take the resolver | Has no directory to join a path to |
| Fetched bytes are the pinned bytes | The resolver hashes before it uses or stores | Is handed nothing |
| A pin grants no access | The fetch door recomposes the recipient's current delivery | Is refused |
| Every change to a machine is recorded | The recorder is the only machine host the simulation holds | Has no other host to call |
| A client's machine matches the authority's | The periodic digest; a difference takes a new image and is counted | Is corrected within one interval |
| A machine's image and inputs go only to a recipient delivered its declaration | The one door that opens a machine subscription reads the recipient's current delivery | Has no other door |
| A window's world goes only to a recipient delivered the screen | The one relay request handler, and the sink's test at each delivery | Has no other producer of a relayed stream |
| Two screens share a relay only on the authority's word | The authority assigns a screen to a relay from its own audience key, which never leaves it | Has no key to compare |
| A document reaches a recipient through one door | Reservation and route answers carry no document | Has no field to put one in |
| A remote event is applied or its stream restarts | The federated door's refusal branch | Has no path past the reset |
| A span closed for a loss is shown at most its lease | One caller closes for a loss | Is not asked |

Three hold by review only, and the build names each in the code it touches. A
new member of the machine host whose recorder half forwards without recording
would go unrecorded until the digest caught it. A new
family of content row owes a pin. A new kind of arrival owes a place in the
admission row's list of kinds.

## Where the guarantee stops

- **A client keeps what it was sent.** A document, a machine's memory and a
  window's world are on the viewer's machine once delivered.
- **A machine's memory goes with its picture**
  ([decision 2](#decision-2-a-machine-runs-on-the-client)).
- **Time.** A client's machine and its windows are behind the authority by the
  delivery's delay, and a leased view may be stale by its lease.
- **What a stream is delivered** is [the disclosure plan](disclosure.md)'s.
- **A rule sent beside a cell it reads but the recipient may not.** This design
  stops the client refusing such a document. Whether a disclosed replica should
  carry that rule at all is the disclosure plan's question
  ([open questions](#open-questions-and-their-owners)).

## The review's findings, answered

Another family reviewed the first draft. Each finding was rechecked against the
code. "Taken" means the design above changed as the finding asks.

| # | Finding | Answer |
|---|---|---|
| H1 | A relay at the lower of two tiers sends a window's viewer the destination's rules and unseen state; screens scoped to a viewer break | Taken for the tier: a relay is always a projection ([decision 3](#decision-3-a-windows-world-is-relayed-by-the-authority-that-owns-the-screen)). The scoped screen is not made worse: a relay shows what the authority's own presentation shows, one session for one screen. A destination per viewer is the multi-seat design's open item, and decision 3 now says so |
| H2 | A fetch for a relayed world's content is refused, because the pin does not occur in the owning world | Taken: the fetch names its relay and is checked against the relay's projection |
| H3 | A connection per relayed stream exhausts the authority's connection limit, and a sink per viewer repeats the work | Taken. Rechecked: `WorldPeerHost.MaximumFederationConnections` bounds connections and a connection carries one stream. Relayed records ride the client's one observation stream, tagged, from one encoder for each scene |
| H4 | The presentation projection carries no text catalog and no machines | Taken. Rechecked: `WorldProjectionDocument` has no member for either. The projection gains both ([decision 1](#decision-1-content-travels-by-pin)) |
| H5 | A cabled unit leaks a partner the viewer may not run; a machine with a secret needs pictures | Taken: disclosure is authored for each machine, a unit is run only when every member may be, and pictures are a built option, last in the order |
| H6 | The recorder misses most of the host's mutating members, capture refuses a live cable, the pad lane's timed presses must travel, and an image's pin is not in the document | Taken. Rechecked: the interface has more mutating members than the three the draft named. The recorder implements the whole interface, the record carries the whole pad lane, a coupled pair is captured as a unit, and an image is authorized by the subscription |
| H7 | Restarting on every refusal restarts forever once batches are in flight | Taken: a batch of a past epoch is dropped, and only a refused batch of the current epoch restarts the stream |
| H8 | Replacing an unsent batch loses pointer movement and a source's return to neutral | Taken by removing the replacement: batches are never merged or dropped. Once the stream does not wait there is no backlog to shed |
| H9 | A seat submits once for each delivery, so a world that steps eight times for each delivery loses most of its ticks | Not taken. Rechecked two ways: the debug world steps once for each delivery (its basis authors the rate), and on a staged document that steps eight times for each delivery every stick movement was admitted and the body's travel was within a twentieth of the same script's in one process. The authority repeats the held image on each of its ticks, so the ticks between deliveries are not lost |
| H10 | Re-timing lets a player act on what the allowance let them see, shortens a hold after a stall, and can break the order of offsets | Taken for the order: a moved claim lands at or after the stream's last event. The other two are stated as where the guarantee stops: both are what an authored allowance means, and the remedy for each is the author's allowance. The suggested bound by acknowledged delivery is not taken, because [replication](replication.md#decision-4-what-a-delta-is-a-difference-against) removes that acknowledgement |
| H11 | A name read from the handshake lets any authority under a shared root answer for another | Taken: the proposal is withdrawn and the client's pin stays |
| H12 | A row's pin names canonical bytes, not the file; a voice set's samples are unpinned; a player's build cannot compile a cartridge | Taken. Rechecked: the row loader hashes the canonical document. The resolver serves canonical bytes, samples gain pins, and a machine names its prepared image |
| H13 | Members read from the wire use the authored reader; fetched bytes can evict the shared store; a font catalog can ask for unbounded work | Taken: untrusted reader, type check before storing, a byte budget for each authority, bounds on a delivered font catalog |
| H14 | A lease keeps revoked content on screen | Taken: a lease covers only silence, any word ends it, and a revoking authority sends a terminal cut |
| M1 | The kinds member has no default and collides with the rule that rows differ | Taken: none means both, and the rule gains the kind |
| M2 | Stopping the boot world strands a settlement | Taken: it is a pause, entered only when nothing unsettled refers to the world |
| M3 | The scene proof exists only at the replica tier, and a shared stream must outlive one of its screens | Taken: sharing is decided by the authority's audience key, and a relay lives while any of its screens is watched |
| L1 | The federated door allocates an array for every admitted batch | Taken in step 2 |
| L2 | A machine's picture is the same for every viewer | Taken: the options table says so, and it is why pictures are practical for the machines that need them |
| U1 | Whether every engine saves its state and is exact across processors | The step that builds machines lists them; an engine that cannot save shows its fallback with a reason, where capture throws today |
| U2 | A hostile font's outline complexity | The bounds of H13 cover bytes, glyphs and pixels. Outline complexity inside the rasterizer was not read; the step that builds the resolver reads it and bounds it there |

## The build order

Each step is one assignment's worth and closes on a check that shows it
worked. Every check is a count or a state read from the headless pair, never a
timing. Steps 1 to 4 are what M1's first remote walk needs; 5 and 6 are what
its picture needs.

1. **The pair from the tool.** `puck_world_start` takes a listening endpoint, a
   remote endpoint and a signing key file.
   *Check:* the pair below starts and stops through the tool, with no process
   started by hand.
2. **The remote seat's input.** The stream change and the two rules of
   decision 5, and the narration.
   *Check:* on the small staged pair, twenty stick movements ten ticks apart
   are all admitted, where run 4 above admitted two: `world.input.federated`
   narrates only the epoch change at the start, and the body's travel on the
   authority is the same script's travel in one process to within one delivery
   interval at each stick movement. The same holds on a staged document that
   steps eight times for each delivery. Counters read the batches applied, the
   events the door moved, the batches dropped for a past epoch and the streams
   restarted, and read no restart.
   *Built, with step 1,* and checked on the pair except for the pause under a
   held stick, which was not run. A push of ten ticks on the world that steps
   once for each delivery moved the body nine ticks' worth, the one-interval
   difference above; whether that interval can be halved by sending a seat's
   input when it changes, without waiting for the next delivery, is the next
   thing to read here.
3. **Content by pin.** The resolver shared by every scene, the loaders moved to
   it, pins on a voice set's samples, the text catalog and the machines'
   presentation half in the projection, the bounds on fetched content and
   delivered fonts, `EmbedAudio` and the text catalog's directory deleted.
   *Built in part:* the resolver (`ContentResolver`, reached as
   `WorldProjectionContent.Resolver`) answers a pin from a file a loaded world
   registered and then from the shared store; a delivered document's fonts are
   read through it by pin and its font paths are never opened; every text
   catalog shares one font cache; and the client's intent stream narrates and
   restarts when its worker fails on anything but the transport. *Not built:*
   the fetch on a miss, the other loaders (audio rows, voice sets, machine
   content), pins on a voice set's samples, the projection's text catalog and
   machines, the store's budget for each authority, the glyph and pixel bounds
   on a delivered font catalog, the resolver's counts on `world.counters`, and
   the deletion of `EmbedAudio`. The delivered text itself has not been seen:
   a headless host builds no scene for a delivered world, so the check below
   needs a rendering host.
   *Check:* with the debug world staged, the reservation is no longer refused
   for a relative path; on the client `world.nesting` reports that the world's
   text resolves; a counter reads content found in the package, found in the
   store and fetched, and reads no fetch when both processes boot one package;
   with one font removed from the client's copy it reads one fetch of that
   font's size.
4. **The manifest, and delivery validation.** Decision 4.
   *Check:* the debug world's transfer commits; the bytes of the first document
   delivery are counted per stream and are the manifest's size when both
   processes boot one package; a one-row edit (`world.row.set`) delivers bytes
   in proportion to the row; no record exceeds one row; the rule that reads a
   withheld cell no longer refuses the delivery.
5. **Authored admission and the idle boot world.** The admission row's kinds,
   the standard package's visitor template, the debug world's rows, namespace
   and visitor slots, and the boot world's pause.
   *Check:* the pair boots `worlds/debug` unmodified but for the pair's own key
   row; the traveller is delivered the world; after the route turns remote the
   client's host loop reports no simulation step and its local machines report
   no step; `body.where` for the remote seat answers.
6. **Machines run from their inputs.** Decision 2: the recorder, the record,
   the image by pin, a coupled pair captured as a unit, the client's host, and
   the authored disclosure member with its first and last values.
   *Check:* on the client `world.nesting` shows each machine screen with its
   source and no fault; counters read one image per watched unit, and replayed
   steps equal to the authority's for the same span; digest differences read
   zero across a run in which a pad is pressed at a cabinet; a cartridge swap on
   the authority shows on the client's `screen.state`; a machine authored as
   not shown opens no subscription.
7. **Windows.** Decision 3.
   *Check:* on the client `world.nesting` lists each painting's session with its
   destination; relays opened equal the distinct scenes, and the authority's
   connection count does not rise when a relay opens; a second client watching
   the same painting adds no composition on the authority, counted; revoking
   the traveller's sight of a screen ends its relay with a terminal cut,
   counted; a creation only the destination names is fetched through the relay.
8. **The lease.** Decision 6.
   *Check:* with the authority's listener stopped for less than the lease the
   client's mirror keeps its delivery, and for longer it is withdrawn; a
   reconnection inside the lease replaces the stream without a withdrawal; a
   credential revoked on the authority withdraws the client's mirror at once.
9. **Machines shown as pictures.** The authored value that sends a machine's
   frames and sound, encoded once and sent to each viewer.
   *Check:* a machine authored that way shows on the client with no image or
   input record sent, counted; a second viewer adds no encode.

Step 2 depends on nothing. Step 4 needs step 3's resolver. Step 5 needs steps 3
and 4, since the debug world's transfer does not commit without them. Steps 6
and 7 need step 5 and do not depend on each other. Step 8 needs step 7, and
step 9 needs step 6.

After step 7 the capture set the packet names (the press, an emulator cabinet,
a chess screen and a gallery painting, on the client against the same set in
process) is a windowed leg for the lead, since this lane holds no GPU grant.

## What Puck's own properties make possible

| Combination | What it rests on |
|---|---|
| A client's installed package as a prewarmed cache for the document it is delivered | Member digests, pins, and a player build that ships its world |
| One image of a machine shared by every viewer who starts watching together | The image named by pin; exact replay |
| A machine's picture as a derived product on the viewer's machine | Fixed content, recorded inputs, a digest to check them |
| Two windows onto one scene as one relay, encoded once for every viewer | The authority's audience key; one sink for each session scene |
| The same sight test deciding what is drawn and what is subscribed | `WorldPortalVisibility` |

## What would change the answer

- **A machine that cannot save its state, or whose core is not exact across
  processors.** That machine needs pictures from the authority. The design
  already leaves it dark with a named reason; step 6 lists which of the debug
  world's machines, if any, are in that class.
- **A measured relay egress that bounds a hub's authority.** Then the voucher
  of decision 3.
- **A client too weak to run the machines in view.** Then the device's quality
  table caps them, and past that the authority's pictures for the excess.
- **A world whose content the client must not hold at all.** That is the frames
  tier, where pictures are the delivery and the authority or a render host
  draws them. Nothing here blocks it, and nothing here builds it.

## Systems considered

Searched and read before proposing a mechanism. Each is cited by symbol.

- **Remote seat input.** `WorldClient.SubmitAuthorityIntents` and
  `WorldSeatInput.Publish` (the client's batch), `WorldFederatedServerLink.SubmitIntent`,
  the intent stream in `WorldRemoteAuthority`, `WorldLocalForwardedAuthority.TryApplyIntent`,
  `WorldTick.PublishFederatedIntent`, `WorldTick.ApplyFederatedIntents`,
  `WorldInputRuntime.TryAdmit` and `WorldInputOwner.TryAdmit`. The reset a lost
  capture uses, and the published epoch a client reconciles to, are the
  restart, and `WorldInputAnswer` is the acknowledgement; no new mechanism is
  added.
- **Content.** `IAssetSource` and `FileSystemAssetSource`, `FontAtlasSourceResolver`,
  `ContentAddressedStore`, `WorldProjectionPrototypeFetch`, `AssetRowFamilies`,
  `WorldAssetRowLoader.EmbedAudio`, `WorldVoiceSampleLoader`, the player folder
  (`WorldPlayerCommand`). No resolver keyed by pin across sources exists; the
  store is one of its three sources.
- **Machines.** `IWorldMachineHost` and `WorldMachineHost`,
  `WorldEngagement.BuildPadSnapshot` and `ScreenPadSnapshot`,
  `IWorldMachineCheckpointHost` and `WorldMachineCheckpoint`,
  `IMachineCheckpointRuntime`, `WorldExtensionReplayPolicy`. The replay tape
  records intents and recomputes machines, so no record of a machine's inputs
  exists to reuse.
- **Windows.** `WorldInstanceHost.OpenScreenSession`, `WorldObservationSession`,
  `WorldServer.TryObserveAsSession`, `WorldGrants.TryAdmitSession`,
  `WorldSessionMirror`, `WorldTravelerProjection`, `WorldFederationProjectionSink`,
  `IWorldForwardedAuthority.StreamProjectionAsync`, `WorldSceneProof`,
  `WorldScreenBinder.SharedSceneOf`, `WorldInstanceHost.TryWindowEndpoint`.
  The traveller's observation is the pattern the relay copies: a credential, a
  ceiling, a hop limit and a sink with a currency test.
- **The document.** `WorldDefinitionMembers` and `WorldReloadDelta` (member
  digests and a delta between revisions), `WorldDocumentBasis.Diff`,
  `WorldProjectionFeed` and `WorldProjectionHold` (a projection sent whole and
  then as deltas), `WorldDeliveryCursor`, `WorldDefinitionValidator.Partitions`.
- **Streams and cuts.** `WorldStreamStamp`, `WorldStreamIngress`,
  `IClientSink.CloseSpan`, `WorldRecipientView`.
- **Lifetime.** `WorldInstanceHost.ReapIfEmpty` and the depth walk that decides
  which worlds observe.
- **Admission.** `WorldAdmissionEntry`, `WorldAdmissionGrant`, `WorldAdmissionDoor`,
  `WorldBodiesDefaults`.

## Open questions and their owners

- **Which of the debug world's machines cannot be captured or are not exact
  across processors.** The step 6 lane reads each engine's checkpoint
  capability and runs the digest check on the pair, and lists them.
- **Which encoding a machine's pictures use.** Step 9 chooses it from what the
  tree already holds for recorded clips and measures its bytes on a debug world
  cabinet; the choice is not a product question.
- **Whether a disclosed replica should carry a rule whose reads are withheld.**
  [The disclosure plan](disclosure.md)'s owner. This design only stops the
  client refusing the document.
- **How the admission door treats an `observe all` grant on a row that admits a
  traveller.** The step 5 lane reads the door and either refuses such a row at
  validation, by name, or leaves it to the kinds member; it is not a product
  question.
- **The windowed capture set and the frame bar against a remote authority** are
  the lead's legs after step 7 ([the benchmark](benchmark.md#the-route-against-a-remote-authority)).
