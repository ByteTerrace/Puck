# experimental/ — quarantined for work, open for reading

**Owner ruling, 2026-08-08, superseding the 2026-08-02 blanket ban.** The
quarantine governs *work*, not *reading*. You are expected to READ the source
here and CITE it as prior art, and to DELETE code once live code has eclipsed
it. You may NOT improve it, fix it, build it, run it, or run its tests. Expect
its builds to break as deletions land — that is the intended outcome, not a
regression to repair.

## What is here, and why

| Tree | Was | Quarantined |
|---|---|---|
| `Puck.Demo` | `src/Puck.Demo` | 2026-08-01 |
| `Puck.Post` | `src/Puck.Post` | 2026-08-02 |
| `tools` | `tools/` | 2026-08-02 |
| `scripts/world` | `src/Puck.World/scripts/` | 2026-08-02 |
| `scripts/recording` | `src/Puck.Recording/scripts/` | 2026-08-02 |
| `Puck.BareMetal` | (never in the build) | — |

None are in `Puck.slnx`; none are reached by the root build. Each carries a
**firewall pair** — `Directory.Build.props` AND `Directory.Build.targets`,
because MSBuild discovers the two separately and one half alone is a firewall
with a door in it. That is what makes the quarantine structural rather than a
path filter someone has to maintain: a filter that happens to exclude the right
directories reads identically to one that excludes the wrong ones.

Do not trust a green restore in `Puck.Demo`: its `ProjectReference`s are
relative paths written when it lived under `src/`, and they all dangle here.
`dotnet restore` exits 0 while silently discarding every one of them
(warning-level "Skipping project ... because it was not found"), and a build
then fails as a flood of `CS0234` errors pointing at source files rather than
at the dangling edges.

## Nothing here is deleted, and nothing here is alive

This is removal from the build, not from history. Read it the way you read
git history: evidence of how a problem was solved once, never a precedent that
binds, and never something to revive in place.

**Anything in these trees that must keep working belongs in a real project or a
`puck` verb.** Reviving something means rewriting it there, under the gate,
verified by running — never un-quarantining a tree.

**Retiring eclipsed code.** The deletion rides in the same squash as the
landing that eclipses it, so the evidence sits beside the removal and every
deletion line stays accounted for. "Eclipsed" is a claim that needs a
mechanical check behind it, not an impression — bring it to the lead for a
decision rather than deciding alone.

## The debt this created, deliberately not paid here

Documents and skills across the repository still cite paths that moved. Those
citations are stale and will be corrected where they live — in `docs/`,
`.claude/skills/`, and the project READMEs — never by editing anything in this
tree.
