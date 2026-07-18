# Moldable-state code review — 2026-07-18

Active review ledger for `claude/moldable-state-plan-a86832` against
`claude/world-moldable-state-plan-a37566`. The supplied merge base,
`35d745b1bf9959811718bd2d2f03f6320f764fbc`, is also the tip of
`features/it-starts`, so this is the requested source-branch comparison.

The review inspected the full branch diff, checked the branch's plan and
handoff against the implementation, and built `src/Puck.World/Puck.World.csproj`
in Release with zero warnings and errors. These findings are behavioral and
maintainability gaps that compilation does not detect.

## Triage

| ID | Priority | Area | Finding | Status |
|---|---|---|---|---|
| CR-1 | P1 | Grants | An `exclusive` grant does not exclude ordinary holders. | **Closed (2nd pass) 2026-07-18** — exclusive wildcards rejected outright |
| CR-2 | P1 | Player document | Three advertised `SetPlayerSection` variants always reject. | **Closed 2026-07-18** |
| CR-3 | P1 | Screen lifecycle | Removing a screen leaves its live binder slot running. | **Closed (2nd pass) 2026-07-18** — camera views released with the last wired slot |
| CR-4 | P2 | Source hygiene | Two C# files contain literal NUL bytes and appear binary to Git. | **Closed and independently verified 2026-07-18** |
| CR-5 | P1 | Player bindings | Raw binding-section edits persist but do not update active seat mappings. | **Closed 2026-07-18** — seats sharing the profile refresh on ack |

## Resolution — second pass, 2026-07-18 (re-audit findings closed, full suite green)

- **CR-1b:** the review's first resolution taken — `WorldGrants.Conflicts` rule (0) rejects any
  exclusive grant whose subject is the wildcard, in both orders and on a fresh table
  ("an exclusive `<cap>` reservation must name a concrete subject"). Concrete-exclusive semantics
  and the ordinary-wildcard acquisition exemption are unchanged; the type doc records the rule.
  Proof: `grants` gained exclusive-all-rejected on a fresh table AND after a concrete ordinary hold.
- **CR-3b:** `ReconcileScreens`'s removal pass records removed View names and
  `ReleaseOrphanedCameraViews` recomputes the wired set per camera: no surviving slot →
  `ViewStack.Release(name)` (already present in Puck.SdfVm.Views; disposes the SdfCameraView and
  its offscreen SdfWorldEngine — no engine seam needed) + the local registration drops so a later
  re-add rebuilds fresh; survivors → wired set re-narrowed. Witness: the no-arg
  `world.view-refresh` echo now reports the registered camera-view count (the one deliberate
  status-echo addition; no honest witness existed). Proof: `screens` removes a View screen and
  asserts the count drops 2 → 1.
- **CR-5:** after an acknowledged bindings-section edit, the new shared `RefreshSeatsBoundTo`
  recomposes + reloads every active seat whose selected profile was edited; `profile.save` now
  routes through the same helper (dedup — and it now refreshes couch co-op seats sharing the
  profile, which the old single-seat inline refresh missed). Proof: `bindings` asserts a raw
  `profile.section … bindings …` edit shows in `player.bindings` immediately, no reseat.

## Independent re-audit of closure commit `3d0a53f`

The closure commit builds cleanly and its `grants`, `bindings`, and `screens`
proof stages pass. The two edited binding source files contain no `0x00` bytes
and Git classifies both as ordinary LF text. Those checks verify CR-4 and the
specific positive cases added for CR-1 through CR-3, but the audit found three
scenarios outside those cases:

- **CR-1 remains open for an exclusive wildcard.** `WorldGrants.Conflicts`
  scans ordinary holders only when the incoming exclusive subject is concrete
  (`grant.Subject.Kind != All`). The public command grammar accepts `all`, so
  `world.grant seat2 drive body:11` followed by
  `world.grant addon:domain drive all exclusive` accepts both grants. The new
  enforcement override then denies every seat's concrete drive access. The
  reverse order rejects the concrete grant, so the advertised both-orders
  invariant is still false for a supported subject. Either reject exclusive
  wildcards explicitly or check every overlapping concrete ordinary hold when
  acquiring one, while retaining the deliberate ordinary-wildcard exemption
  for a concrete exclusive acquisition.
- **CR-3 remains open for View screens.** Removal drops `ScreenSlot.View`, but
  the corresponding camera content remains registered in `ViewStack`.
  `RegisterCameraView` registers it without an `isLive` predicate, and
  `ViewStack.RenderFrame` continues resolving every registered budgeted entry.
  Removing the last screen wired to a camera therefore leaves its offscreen SDF
  engine consuming refresh budget and GPU work until binder shutdown. After a
  removal, recompute the wired set and release a camera view when no remaining
  slot references it (or gate registration with live slot membership).
- **CR-5: `profile.section ... bindings ...` is not live for active seats.**
  The server updates `WorldProfile.Bindings`, but `WorldSeatBindings` stores a
  separately compiled per-seat profile layer. `profile.save` refreshes that
  cache explicitly after acknowledgement; the new generic `SectionHandler`
  does not. It reports success and persists the edit while currently seated
  players keep their old controls until a reseat or restart. Refresh every
  active seat sharing the edited profile after an acknowledged binding edit,
  and prove the raw verb changes `player.bindings` immediately.

Verification performed during this re-audit:

- `dotnet build src/Puck.World/Puck.World.csproj -c Release --no-restore -p:EmitCompilerGeneratedFiles=false`
  — passed with zero warnings and errors.
- `dotnet run src/Puck.World/scripts/proof.cs -- grants --no-build` — passed.
- `dotnet run src/Puck.World/scripts/proof.cs -- bindings --no-build` — passed
  (with its AppData backup/restore access enabled).
- `dotnet run src/Puck.World/scripts/proof.cs -- screens --no-build` — passed.
- Manual console reproduction accepted the conflicting wildcard sequence above
  and immediately emitted Drive denials for seats 1 through 4.

## Resolution attempt — 2026-07-18 (commit `3d0a53f`)

- **CR-1:** `WorldGrants` now rejects conflicting grants in BOTH orders (an exclusive reservation
  blocks any different principal's overlapping grant; an incoming exclusive is blocked by a
  different principal's ordinary hold of the same concrete subject). The seeded wildcard (`all`)
  is deliberately exempt at acquisition — so the addon keystone flow still acquires exclusivity
  while console holds `Drive/all` — and exclusivity is made honest at ENFORCEMENT instead:
  `Allows` answers only for the exclusive holder while a reservation stands, so an exclusively
  held body has exactly one principal whose intent passes `WorldServer.Step` (the console's
  wildcard command is denied, proven). Semantic documented at the type. Proof: `grants` gained
  ordinary-then-exclusive, exclusive-then-ordinary, and the wildcard-override denial.
- **CR-2:** `WorldProfiles.ApplySection` implements all four declared sections. Each parses its
  exact payload, validates a candidate document through `WorldPlayerDocumentValidator`, updates
  the live `WorldProfile` handle (identity edits refresh cached name/color; seated snapshot color
  refreshes via `WorldPopulation.RefreshSeatColor`), persists, and bumps `Revision`. `profile.set`
  now routes through `SetPlayerSection(motion)` (one persist path), and the new raw
  `profile.section <id> <section> <json>` verb is the direct protocol reflection. Proof:
  `bindings` gained positive + malformed cases per section, revision/restart/live-refresh
  assertions.
- **CR-3:** `WorldScreenBinder.ReconcileScreens` reconciles removals first: vanished indices are
  collected to a scratch list, engaged players are disengaged via the new
  `WorldEngagement.DisengageScreen` (latch cleared, avatar resumes intent), slot-OWNED
  machine/pattern/capture state is disposed (`ScreenSlot.DisposeOwned`; the shared webcam session
  and boot-sized view pool are dereferenced, never disposed), and the slot leaves
  `m_slots`/`m_sources`/`m_lights`. Proof: `screens` removes a live, engaged machine screen and
  asserts state/peek/insert report the index absent and the avatar resumes.
- **CR-4:** the raw U+0000 bytes in `WorldBindingComposer.cs` and `WorldBindingCommandModule.cs`
  are now the textual `\0` escape (compiled separator unchanged). Note on the closure criterion:
  a diff AGAINST the pre-fix blob still reports binary (one side contains the NUL); diffs between
  post-fix revisions are ordinary text.

## CR-1. Exclusive grants do not exclude ordinary holders

**Evidence:** `src/Puck.World/Server/WorldGrants.cs`, `TryGrant`, especially
the `if (grant.Exclusive)` block. The reverse index is consulted only when the
incoming grant is itself exclusive.

**Failure:** an existing non-exclusive grant does not block an exclusive
acquisition. Conversely, after an exclusive grant lands, another principal can
receive the same capability and subject through a non-exclusive grant because
that path never reads `m_exclusive`. For a body, both principals can therefore
submit allowed intents despite the body being reported as exclusively held.
Wildcard grants need an explicit overlap rule as well: a `Drive/all` holder
already has effective access to every `Drive/body:n` subject.

**Action:** make every incoming grant check conflicting exclusive reservations,
and make exclusive acquisition check existing effective holders before it is
recorded. Define and enforce wildcard overlap in the same place. Add proof cases
for both grant orders: ordinary-then-exclusive and exclusive-then-ordinary.

**Closure:** the second conflicting grant is rejected in either order, and an
exclusive body has exactly one principal whose intent passes `WorldServer.Step`.

## CR-2. Most player-section messages are nonfunctional

**Evidence:** `src/Puck.World/Protocol/SessionRequest.cs` declares
`WorldPlayerSection.Identity`, `Motion`, `Bindings`, and `Preferences`; the
plan and `docs/reviews/2026-07-18-world-moldable-state-handoff.md` promise those
four section messages. `src/Puck.World/Server/WorldProfiles.cs`,
`ApplySection`, implements only `Bindings`; its default arm rejects every other
declared value as "not editable this arc."

**Failure:** editor or network clients can successfully send only one quarter
of the advertised durable profile-edit protocol. Identity, motion, and open
preferences writes are always negatively acknowledged, even though they are
part of `puck.world.player.v1` and the handoff tells the next arc to build on
this seam.

**Action:** parse the exact JSON payload for every declared section, validate a
candidate profile/document through `WorldPlayerDocumentValidator`, update the
live `WorldProfile` handle, then persist and acknowledge it. Identity changes
also require the runtime handle to update its cached name/color representation;
do not mutate storage while leaving seated participants on stale cached data.
If those variants are intentionally deferred, delete them from the current
protocol and handoff instead of exposing inert messages.

**Closure:** all four section kinds have positive and malformed-payload proof
cases, successful edits bump `Revision`, survive restart, and update any seated
participant that shares the edited profile.

## CR-3. Removed screens remain live in the binder

**Evidence:** `src/Puck.World/WorldScreenBinder.cs`, `ReconcileScreens`, loops
only over the incoming screen list. It updates or reports added indices but
never compares `m_slots` against the incoming index set. `AdvanceMachines`,
`Publish`, `State`, insert/eject, and engagement collection continue to operate
over those retained slots.

**Failure:** after `WorldMutation.RemoveScreen` applies, the SDF slab disappears
from the rebuilt program but the old machine/feed remains allocated and can
keep advancing and publishing. `screen.state`, insert, and eject still treat
the removed index as declared, and a player may remain engaged with an invisible
machine while their avatar is held idle.

**Action:** reconcile removals before additions/updates: collect incoming
indices, clear engagement routes for missing screens, dispose every removed
slot's owned machine/pattern/capture state, and remove its entries from
`m_slots`, `m_sources`, and `m_lights`. Mutate dictionaries from a scratch list,
not while enumerating them.

**Closure:** removing a live machine screen disposes/stops it, makes all screen
commands report the index absent, clears engaged routes, and leaves no provider
entry for the removed index.

## CR-4. Literal NUL bytes suppress source diffs

**Evidence:** `src/Puck.World/WorldBindingCommandModule.cs` in the composite
`seen` key and `src/Puck.World/WorldBindingComposer.cs` in `PageKey` each embed
an actual U+0000 byte in the source file. `git diff --numstat` consequently
reports `- -` and `git diff` says the new `.cs` files are binary, despite the
new `*.cs diff=csharp` attribute.

**Failure:** reviewers and future agents cannot inspect ordinary line diffs for
either binding implementation, so subsequent changes can evade normal review
and blame tooling.

**Action:** replace each literal source byte with the textual C# escape `\0`.
The compiled separator remains a NUL, so runtime key behavior is unchanged
while the repository files become normal UTF-8 text.

**Closure:** both files contain zero byte value `0x00` occurrences on disk and
`git diff --numstat` reports numeric added/deleted line counts.

## Verification notes

- `dotnet build src/Puck.World/Puck.World.csproj -c Release --no-restore -p:EmitCompilerGeneratedFiles=false`
  passed with zero warnings and errors.
- A full solution build reached the changed projects, then stopped on pre-existing
  unwritable CsWin32 generated files under `src/Puck.Platform/obj`; this was an
  environment/output-tree issue rather than evidence about the reviewed diff.
- The existing `grants` proof verifies that an addon moves after an exclusive
  grant, but does not exercise a competing ordinary holder.
- The existing `bindings` proof exercises only the bindings section.
- No existing proof removes a running machine screen and then checks binder,
  engagement, and command state.
