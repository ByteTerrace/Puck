Thorough adversarial review-and-fix pass in the Puck repo. Puck is a C# engine with deterministic fixed-point simulation, nested world portals and federation across authorities. Work in your review worktree on a new branch, review/portal-4, from origin/lane/portal-sources @ 886b09830. Don't commit; the session that launched you commits.

Scope is the arrival contract, four commits on top of ee3dbfba8. Diff `git diff 56de3f7c4 886b09830`, then read the merge d0d6a4c67's resolution.
- e8d15032c: local-seat arrivals go on the replay tape. A new Arrival entry, shape token 6, is landed through one WorldTransferEscrow method, and WorldServer.ArrivalTap reports it.
- 43aa45989: the full arrival contract.
  - Admission: a local seat is admitted by its arrival entry's session join. A transferred peer or entity is admitted by its own PeerAdmitted event, applied during the commit, so it sits ahead of its arrival entry.
  - Landing: one Land step for every kind (profile, appearance, accumulated turn, committed mobility, border, mapped pose and motion).
  - Outcome: every landing is reported in member order once the commit decides, with rolled-back landings undone by RollBackArrival, the same path live and re-drive use.
  - Order: arrival entries take the commit's position in the tick's authority list, ahead of the step.
- 628941c94: rolled-back peers, plus live verified peers via WorldAdmissionDoor.TryAdmitArrival. It adds the test seam WorldTransferEscrow.TestRefuseLandingOrdinal.
- 886b09830: WorldRemoteAuthority.PublishClaim publishes a seat claim and then delivers the latest observed route, both under the route gate. ObserveRoute is public.

Hunt, with special care:
- **Determinism.** Does a live run and a tape re-drive produce bit-identical state for every interleaving of arrivals, rollbacks, departures and other events in one tick? Think about two arrivals in one tick, an arrival plus departure of the same traveller, an arrival into a slot freed this tick, and generation counters.
- **Rollback completeness.** After RollBackArrival, does anything a landing touched (index, grants, census, mobility epoch, border, appearance, turn) survive? Can a rollback's ordering relative to PeerAdmitted leave a ghost?
- **Tape format.** Shape token 6 must be bumped correctly, and the codec strict: no tolerant reads, and refusal of malformed or out-of-order entries.
- **PublishClaim.** Any deadlock between the route gate and the seat and turn gates, or with the tick thread. Can a route observed before the wrapper exists still be lost?
- **The test seam.** TestRefuseLandingOrdinal must not be reachable or settable in production composition. If it is, make it a proper injected policy or restrict it structurally.
- **The laws.** Does each one record a real transfer and re-drive it, and could any pass vacuously?

Fix every real blocker with a law that fails without the fix. Build with `dotnet build Puck.slnx -c Release -m:4 -nodeReuse:false`. Run tests/Puck.World.Tests filtered to Transfer, Arrival, Replay, Federation and Escrow, plus Puck.World.Protocol.Tests. No GPU.

Report blockers only. For each: file:line, why it's wrong, how to show it fails, and what you changed.
Repo rules: no backwards compatibility, ever (never preserve old wrong behaviour; no compat aliases or shims). No InternalsVisibleTo (make a member public instead). No environment variables. One spelling per thing. LF; never raise newline issues. Docs state current behaviour in present tense with no dates or SHAs. Determinism: no wall clock, RNG or float in simulation state.

Hunt only for problems that would block the merge. For each give file:line, why it's wrong, and a concrete failing scenario. Fix it in the tree when the fix is local and clear, adding a law (test) that fails without the fix; otherwise describe it. Don't build or run tests unless a finding can't be settled any other way (one heavy command at a time); never run GPU work (no `puck canary`, `puck parity`, or Puck.World runs). Don't commit. End with a list: finding → fixed (files) or open (why). If nothing blocks, say so plainly.
