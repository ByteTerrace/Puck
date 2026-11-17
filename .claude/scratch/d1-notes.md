# D1 continuation 2

Branch lane/d1. Base initially merged: lead/authoring 61af8b6e96.
The lead has since advanced; merge its tip before final checks.

## Invariant and enforcement

WorldGrants.IsOperator is the live predicate: Console/World structurally,
otherwise an eligible effective Operate/all hold. Remote entitlement and
generation are checked at use. WorldGrants.Authority.cs validates authority
at transaction start against the before/proposed grants, roles and ownership.
Only its PreparedAuthorityTransition installs live grant/group state. Raw
installers are private. Composition checks batch prefixes and the final graph.
Rebuild, checkpoint restoration and admission minting use the same transition.

Reuse: role-qualified membership/ownership indexes, grant payloads and masks,
WorldStateDocumentValues reference collection, CommandRegistry dispatch,
WorldDeliveryCursor, and the existing ordered PeerDisconnected event. No new
operator role system, observation stream or transport lifecycle was introduced.
The group schema currently has literal members/roles; reference collection
protects any state-backed holders in that graph as well as the literal rows.
The fold's host-locus contributor trust remains a separate mechanism.

## Evidence so far

- Release builds passed for World, CLI, Server.Tests and Silo.Tests.
- 21 OperatorGrantLawTests passed (seat/peer administration and program
  authoring, both template orders, retained command sessions, cursor reseed,
  group/batch/laundering refusals, below-Replica peers, and own-body consent).
- SiloOperatorLawTests passed: discovery/dispatch share SiloControlAdmission;
  live Operate admits operator commands and never Host; revoke/disconnect deny.
- Regenerated schema, model shape and dashboard types with the private CLI.
- First headless debug start c568509a8e2544b58401cc40121e6536 reached process
  31020, which exited on an authored seat Drive row lacking a budget. Updated
  the affected debug exhibits to explicit budgets, and removed the skirmish
  fixture's duplicate seat state grant. This was a real migration defect,
  not memory admission. The process exited; no World is running.
- CLI copy: .claude/scratch/d1-cli. Build/test logs: d1-*-build.log,
  d1-laws.log and d1-silo-laws.log in this directory. No watcher or GPU hold.

## Remaining finish work

Finish the bound peer handle and wire check, observer-INVALID run, and final
metadata/docs cleanup. Merge lead tip; rebuild touched projects/CLI, rerun
focused laws, disclosure-probe, verbs --headless-only, headless debug start/stop,
duplicates --against lead/authoring --head HEAD (act on every hit), docs links.
Final commit carries Brief: d1 done only after the finish line is observed.
