# experimental/ — quarantined, and OFF LIMITS

**Owner ruling, 2026-08-02: no agent may read, edit, build, run, or reference
anything in this tree until the ruling is lifted.** That is broader than the
build quarantine below and it is the part that matters — the trees here are not
merely out of the build, they are out of scope. Do not open them for context,
do not cite them as precedent, do not port from them, and do not "fix" them.

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

## Nothing here is deleted, and nothing here is alive

This is removal from the build, not from history. The source is readable by a
human who deliberately comes looking. It is not a staging area, not a backlog,
and not a source of precedent — code here describes how things once worked,
which is exactly the sort of thing that reads as current to anyone who does not
already know better.

**Anything in these trees that must keep working belongs in a real project or a
`puck` verb.** Reviving something means rewriting it there, under the gate,
verified by running — never un-quarantining a tree.

## The debt this created, deliberately not paid here

Documents and skills across the repository still cite paths that moved. Those
citations are stale and will be corrected where they live — in `docs/`,
`.claude/skills/`, and the project READMEs — never by editing anything in this
tree.
