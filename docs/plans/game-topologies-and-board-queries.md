# Game topologies and board queries

**Discrete-topology capacity constants are derived, not restated (owner
decision).** The hex radius ceiling, the document-wide board-storage budget,
the zone-sort key ceiling, and the transfer-count ceiling were each a bare
literal restating a relationship the code already knows (a topology's own
cell-count formula, `MaxTopologies × MaxCells`, the section's own row
ceiling, and the domain capacity ceiling respectively) — each now computes
from the constant it actually follows from, so the two can never drift
apart, and refusing an authored value past the bound names the derived
number rather than a hardcoded twin of it.

**A topology's opposite direction is compiled from its own vectors, never
assumed from ordinal arithmetic (owner decision).** `(direction +
DirectionCount / 2) % DirectionCount` happens to pair a Grid/Hex/Ring
topology's directions correctly because each is authored as reciprocal
pairs in that exact order; a `Box`'s 26 directions are not (they are ordered
planar, then up-shifted planar, then down-shifted planar), so the same trick
silently paired `N` with a diagonal-and-a-layer-off direction instead of `S`.
`CompiledWorldTopology.Opposite` is now a table built once at compile time by
negating each direction's own step vector and looking up the match, refusing
compilation if a direction has none.

**A topology's directions are authored content, not a fixed per-kind table
(owner decision).** The compass names above were still a closed C# switch
per `WorldTopologyKind` — a document could reach every direction a kind
carried but could never declare a narrower or differently-named vocabulary
(a 4-connected grid, orthogonal steps only; a leaper's own reach spelled by
name instead of raw `$board:offset` deltas) without a second mechanism.
`WorldStateLatticeTopology.Directions` is now an optional list of
`(name, x, y, z)` steps; unauthored, a topology compiles exactly the fixed
set it always had (Grid's eight compass points, Hex's six, Box's 26, Ring's
forward/backward — the migration is behavior-preserving by construction, so
no shipped world needed re-authoring), and an authored list replaces that
default WHOLESALE — the topology's only directions and the only names
`CompiledWorldTopology.Direction` resolves. Validation requires 1..64
entries (`MaxDirections` derived from the bit width of the `long` mask
`$match:`'s direction-mask facet packs one bit per direction into), distinct
names, distinct nonzero steps, a Z step only on a `Box`, no Y step on a
`Ring` (which has no second axis), a step magnitude under the wrapped axis'
own width or depth (a Ring always wraps X) so the modulo wrap can never fold
a step past the origin or onto itself, and — the same closure `Opposite`
already needed — every step's negation present as another entry, refused at
validation rather than left to throw mid-compile or resolve silently wrong.
The garden's 300-tick passive hash is unchanged (`0xCC2D4742992B05CC`);
`tests/Puck.World.Tests/WorldTopologyDirectionLawTests.cs` proves the
preserved default, an authored 4-connected/renamed vocabulary compiling AND a
rule compiling against it while the retired default name refuses, the
magnitude/Ring-axis refusals, and a physical field refusing a direction
vocabulary outright — each with a refusal control. The shipped garden's
`chessBoard` topology now authors its eight compass directions explicitly
(behavior-preserving, proven byte-identical on the same 300-tick hash),
demonstrating the feature rather than only proving it in isolation.

**A topology's point-group elements share one naming function; the
`$symmetry:` lattice question stays open (owner decision).**
`CompiledWorldTopology`'s point group (`BuildSymmetry`,
`WorldTopologySymmetry.cs`) named a Box element by its signed-axis spelling
(`"+x-y+z"`, where each source axis lands and with what sign) while Grid and
Hex named theirs by hand (`mirrorMain`, `mirror3`, …) — two vocabularies for
the same kind of fact. One representation now covers all three: an `AxisMap`
signed-permutation, closed by breadth-first composition over generators
(mirror each in-play axis, swap axes of equal extent) for Grid (2 planar
axes, letters `xz`) and Box (3, letters `xyz`), and — Hex's point group is
exactly the signed permutations of its cube coordinates `(q, r, s)` that
keep `q + r + s == 0`, which holds only for a bare permutation or the same
permutation negated throughout, 12 elements enumerated directly rather than
discovered by closure — for Hex (letters `qrs`). Renaming moved the
canonical name every element answers to (a 4×4 grid's old `"rot90"` is now
`"-z+x"`; `tests/Puck.World.Tests/WorldBoardSymmetryLawTests.cs` and
`WorldBoxTopologyLawTests.cs` assert the new spellings), but changed no
element's identity, closure, or image table — the garden's 300-tick hash is
unchanged because it names no element anywhere. A topology may additionally
author `elementAliases` (`WorldTopologyElementAlias`, name → canonical
name), resolved by `CompiledWorldTopology.Element` alongside the canonical
spelling — `"rot90"` for whatever axis permutation a square grid's quarter
turn actually is — while `ElementName` always answers the canonical form;
validated against the SAME bare-group enumeration (`ElementNames`, run
before any topology cell exists, so an alias can be checked without
materializing per-cell images) so an alias naming no real element refuses at
load. Separately, and still undecided: whether the fixed 240-node symmetry
lattice behind `$symmetry:` is an engine primitive or content a world could
reshape — the `$symmetry:` reserved-channel doc in
[`references/documents.md`](../../.claude/skills/puck-world/references/documents.md)
is where a session picking that up starts.

**`$board:line`/`rayCell`/`rayDistance` are retired in favour of `$match`
over a ray (owner decision).** `WorldRuleCompiler.Pattern.cs`'s board-source
`$match` already walked the identical ray `$board:rayCell`/`rayDistance` did
(`WorldServer.Patterns.cs`'s `ReadRay`); it now also answers two new facets
on one direction — `cell` (the first cell one step past the longest accepted
prefix — the first cell the pattern REJECTS — or -1 when the whole ray is
accepted) and `distance` (the step count to it) — strictly more general than
the retired queries, since the "blocker" test is any authored pattern rather
than only "not equal to the board's empty sentinel". `$board:line` (n-in-a-
row) had no caller in any shipped world; its two law-test callers in
`WorldBoxTopologyLawTests.cs` are rewritten on `$match` (a diagonal run read
with `prefix`, and an exact-run-with-no-continuation check read with the
plain accept facet over a pattern shaped `<exactly N> <never another>`).
`rayCell`/`rayDistance`'s 44 garden call sites (`tabletop-shape-rook`/
`-bishop`/`-queen`, the check/castle-transit-attack probes) are rewritten
1:1 onto a single shared pattern (`chessRayEmpty`, "zero or more empty cells")
read with `:cell`/`:distance` — the SAME cell/distance values the retired
queries answered, since the walk and the empty-run test are identical; the
garden's 300-tick passive hash is unchanged (`0xCC2D4742992B05CC`). The
chessBoard topology's `directions` are now authored explicitly in the same
change (see above). `RayCell`/`RayDistance`/`Line` are gone from
`WorldBoardQueryKind`; `Offset`'s doc no longer names a piece.

