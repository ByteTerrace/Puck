# Authority defects — the re-runnable battery

**QUARANTINED 2026-08-06 — not a gate, and no runner lives here.** Cases 04-06
(`04-engage.txt`/`05-disengage.txt`/`06-addon-lifecycle.txt`) assumed the retired `default` world's
`screen:0` and its mounted `default` addon — the four-world charter's shipped roster
(`play`/`dive`/`kart`/`jump`) authors no `screens` or `addons` row, so those fixtures no longer boot
into the state they were written against. Repairing them in place would mean re-authoring
screen/addon furniture into one of the four shipped worlds for a battery's sake — exactly the kind
of fixture-chasing this repository's quarantine protocol (see `docs/verification/headless-boot`'s
own record) declines to do; the fix belongs in a successor that builds its own furniture instead of
borrowing a shipped world's. The rest of this document is kept as the historical record of what the
battery proved and how.

**The successor.** The acting-principal/administration contract now lives in
`tests/Puck.World.Tests`'s `AuthorityAdministrationLawTests` (a law-based test project; NOT yet
wired into `Puck.slnx` or any build gate — see that project's own README). An engage-authority law
exercising cases 04-06's ground (screen engage/disengage, addon lifecycle) with CODE-BUILT
`testPattern`-screen furniture — never borrowed from a shipped world's own document — is chartered
to follow there, closing the gap this quarantine opens.

**Validating it today.** Validation currency is RUN THE APP over stdin/stdout, owner-in-the-loop,
until the successor lands. Cases 01/02/03/07 (join/leave/confirm/assign/identity-create — no screen
or addon dependency) still describe live, checkable behavior; drive them by hand against a shipped
world and read both streams. The full historical runner logic, its two adversarial-review
discriminator proofs, and the per-case assertion sets remain in git history and in this document for
anyone who revives cases 04-06 under a rebuilt furniture set.

The "INGRESS CLOSURE IS REFUTED" finding and its two 2026-08-02 adversarial-review follow-ups
(round 1: confirm/assign/join laundering, the disengage latch/route decision; round 2: the assign
cascade's source authorization, claim/cycle's handler-constructed principals, the join precommit's
stale-reservation strand, the disengage repair's own authorization gap, two misreported
`SubmitSession` verdicts) named principal-selection defects across the player-facing command
surface. This directory WAS the durable proof they stay closed: stdin scripts
with hard-coded expected-output assertions, driven by a runner that built once and exited nonzero
the moment an assertion missed or a run crashed.

**Not a build gate** (was true before the quarantine too). Nothing here was wired into
`dotnet build`, `dotnet test`, a `puck` verb, or CI. Historically: run it by hand after touching an
authority check in `PlayerRoster.cs`, `PlayerCommandModule.cs`, `WorldEngagement.cs`,
`WorldAddonCommandModule.cs`, `WorldCommandModule.cs`, or `WorldServer.cs`'s session-request gates
(the `Join`/`Leave`/`SetIdentity` family `player.join`/`leave`/`identity` submit) — today, consult
`tests/Puck.World.Tests` instead.

## What each script proves

Every case pairs a DENIAL (the acting principal lacks the grant → a loud, named refusal, and the
state provably does not change) with a CONTROL (the identical request after the grant is restored →
it succeeds). The actor is always `console` — every stdin line dispatches through the text door,
which stamps `Console` unconditionally regardless of what the line's arguments name — and the
target is always a specific seat/body/screen/section, so actor and target are always distinct
principals; that is what makes each pair discriminate a broken check (one that consults the
target's own pre-seeded grant) from a fixed one (that consults the actor's).

| Script | Defect(s) closed | Denial mechanism |
|---|---|---|
| `01-join-leave-setprofile.txt` | `player.join`/`leave`/`identity` stamped the target's own `Seat(slot)`, not the actor | `world.revoke console drive all` |
| `02-confirm.txt` | `Activate` mutated the candidate profile and state before submitting, and stamped `Seat(slot)` | `world.revoke console drive all` |
| `03-assign.txt` | `AssignDevice` moved the device before the join verdict, never checked the already-occupied ("join a team") path, and — round 2 — dissolved an orphaned SOURCE via a fabricated `Seat(source)` instead of the real actor | `world.revoke console drive all`, then a narrowed `drive body:<n>` naming only the destination |
| `04-engage.txt` | `player.engage` booted the cartridge before any check, then checked the TARGET's principal | `world.revoke console control all` |
| `05-disengage.txt` | `player.disengage` had no check at all; round 1's synthetic-grant proof could not tell a stuck latch from a clean one; round 2 — the route-without-latch repair cleared a legitimately-granted Control row with NO actor check at all | `world.revoke console control all`, plus direct `world.grant`/`world.revoke` on the latch/route pair — see below |
| `06-addon-lifecycle.txt` | `world.addon.reload`/`enable`/`disable` discarded `CommandContext` entirely | `world.revoke console mutate section:addons` |
| `07-identity-create.txt` | The defect's original surface (`profile.create` mutating grant-gated catalog state unchecked) was DELETED with the profile catalog — an identity is now an owned world the actor mints for itself, ungated by design. What remains gated is SEATING the mint: `player.identity` must consult the ACTOR's Drive over the target body, and the roster must provably stay on the boot identity through the denial (an ordered `world=amber` → `world=teal` check) | `world.revoke console drive all` |

### `03-assign.txt` — the cascade's source authorization (round 2, REFUTES R1)

Relocating a device off a slot that would then have zero devices left ORPHANS its participant,
which a dissolution cascade then removes via `Leave`. The pre-fix code authorized only the
DESTINATION body before mutating, then dissolved the SOURCE under a fabricated
`SelfProvisioned(sourceSlot)` — a principal that trivially passes its own default Drive seed
regardless of who the real actor is, so a principal holding Drive over only the destination could
delete an unrelated source body. The script's added tail: move `kbd` onto a fresh slot (making it a
Device-origin, sole-device participant — the "pad-origin source seat" shape, using the keyboard
since no physical pad is attachable in a headless run — see the note below), narrow Console to
`drive body:<destination>` only (explicitly NOT the source), attempt the relocation (denied, source
untouched, `world.devices` unchanged), then also grant `drive body:<source>` and retry
(succeeds, source dissolves).

### `05-disengage.txt` — the four latch/route combinations, and the repair's own authorization (round 2, REFUTES R4)

`WorldBody.Engaged` (the latch) and the grant table's `Control/screen:<n>` row (the route) are two
independent pieces of state `Engage` sets together and an ordinary `Disengage` clears together, but
nothing enforces that they move together in between. The script drives all four combinations, PLUS
the round-2 finding that the repair direction needs its own gate:

1. **Neither** — nothing engaged, nothing routed → the ordinary `NotEngaged` no-op.
2. **Both** (a REAL `player.engage 0` on a live-booted machine, not a synthetic grant) → the ordinary
   `Denied`/`Disengaged` pair.
3. **Route, no latch, AUTHORIZED actor** — `world.grant seat1 control screen:4` with no matching
   `Engage` call, Console still holding `control/all` → `Repaired`, and the route is cleared. Screen
   **4** on purpose: screen 0 carries the live-stepping machine from step 2, and its
   `WorldEngagement.MergedPad` call (run every simulated tick to feed the machine its pad image)
   already self-heals a route with no backing latch on its own, before this script's own
   `player.disengage` would ever see it — a real engine subtlety, not a bug, but it means this
   combination has to live on a screen with no machine consuming `MergedPad` to actually observe
   `Disengage`'s OWN repair path rather than the tick loop's.
4. **Route, no latch, UNAUTHORIZED actor — the round-2 attack case.** The route can exist through a
   perfectly legitimate `world.grant` with no `Engage` behind it yet — a real, authored row, not
   always debris — and clearing it mutates the SAME per-principal `Control` subject set an ordinary
   `world.revoke` touches. The round-1 proof restored Console's `control/all` BEFORE attempting the
   repair, so this exact attack was never tried. This combination re-grants the route, revokes
   Console's `control/all`, and attempts `player.disengage` — asserted DENIED, with `world.grants
   seat1` shown to STILL carry `control/screen:4` immediately after (the runner's `OrderedContains`
   check — see below), before Console's grant is restored and the SAME repair succeeds.
5. **Latch, no route** — a REAL engage, then `world.revoke seat1 control screen:0` strips ONLY the
   route administratively (the shape an admin narrowing authority produces) → `Repaired`
   unconditionally (pure body state, no grant-table row to protect), and a SECOND `player.disengage 1`
   now reads the ordinary `NotEngaged`, proving the latch is genuinely clear and not merely reported
   clear.

The runner's `OrderedContains` assertion (distinct from the order-blind `Contains`/`MinCount`
checks) existed specifically for combination 4: it proved the grant line, the route's presence in
`world.grants`, the revoke, the DENIAL, the route's CONTINUED presence, the restore, and the
eventual repair all happened in that exact sequence — the regression this guards against
("`control/screen:4` appears somewhere in the transcript" is true whether the attack succeeded and
was later re-granted, or failed and stayed put; only the ORDER tells them apart) is real and would
pass a plain substring check.

## What this battery does NOT (and structurally cannot) exercise

Two round-2 findings — R2 (claim/cycle must authorize as the ingress-stamped SOURCE principal, never
a handler-constructed one) and R3 (a denied device-driven join must roll back the stale
`InputRouter.CommitSlot` precommit; `player.south`'s typed echo must report a real denial) — are
fixes to the PHYSICAL/bound-signal ingress path (`InputRouter.Collect`, the `ClaimCommand`/
`CycleCommand`/`SouthCommand`/`MoveCommand`/`LookCommand` bindings). Every stdin line in this battery
dispatches through the TEXT door, which stamps `Console` unconditionally and never calls
`CommitSlot` or resolves a lane through `PrincipalOf(context.Slot)` the way a bound physical signal
does — there is no way to drive either code path from a headless piped script. Both fixes are
verified by code reading (matching `CommandContext.Principal`'s own documented rule — "a handler
READS this to attribute its action; a handler that constructs a principal instead is asserting an
identity rather than carrying one" — `src/Puck.Commands/CommandContext.cs:67`) and by the full
solution building clean; they are NOT exercised by any committed, re-runnable script. Say so plainly
rather than claim coverage that does not exist.

## The synthetic cartridge

`fixtures/authority-test.gb` is a real, valid, bootable Game Boy ROM — not a stub — built entirely
from code already in this repository (`Puck.Forge.Tune.TuneRom.Build` over a minimal
`Puck.Forge.Authoring.AudioDocument`), the same mechanism `Puck.World`'s own headless tune-jukebox
host (`src/Puck.World/Audio/TuneMachineSource.cs`) boots at runtime. No `experimental/` code was
read, run, or ported to make it, and no licensed ROM content is involved. It exists because
`screen.insert` needs a real file path, and no bundled world screen ships both engageable and
carrying live content: regenerate it with

```csharp
var document = new AudioDocument(Schema: AudioDocument.CurrentSchema, Name: "AUTHTEST", Tempo: null, Patterns: null, Order: null, Effects: null);
var canonical = AudioCanonicalizer.Canonicalize(document: document);
var rom = TuneRom.Build(document: canonical.Document, title: "AUTHTEST");
File.WriteAllBytes("authority-test.gb", rom);
```

## The local owned-world catalog side effect

`07-identity-create.txt` calls `identity.create`, which both mints AND PERSISTS an owned world
into the identity catalog under the process's state root (`WorldOwnedWorlds`). The runner pointed
every child process at a fresh throwaway root via `--state-dir` (created per run, deleted in
`finally`), so a run never left a stray `teal` world in a developer's real catalog and parallel runs
could not see each other. Driving these scripts by hand gets no such protection — pass your own
`--state-dir`, not a raw `dotnet run < script.txt`, if you care about your local catalog.

## Proving the battery discriminates

A script that cannot fail is a lie, checked twice now:

- **Round 1**: the `AssignDevice`/"join a team" check was temporarily reverted to log-but-not-return
  on denial, rebuilt, and re-run — `03-assign.txt` went red (the actor-denied line and the
  `wire.errors` count both vanished, `world.devices` showed the device had moved anyway). Reverted
  back, green again.
- **Round 2**: `WorldEngagement.Disengage`'s route-without-latch actor check was temporarily removed
  (back to the unconditional self-heal R4 refuted), rebuilt, and re-run — `05-disengage.txt` went red
  (the attack case's expected denial never appeared, `wire.errors` dropped from 2 to a different
  count, and the "was not engaged" tally shifted). Reverted back, green again.

See the ledger's 2026-08-02 entries (the original pass, and the two adversarial-review corrections)
for the full transcript excerpts of both proofs.
