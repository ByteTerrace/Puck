# Debug world granary board

The debug world stands the platform's storage accounts on a tabletop board in the
gallery hall's board game room, the south-east table on level 2
([`shapes.md`](shapes.md#the-board-game-room)): the board's centre is at
(65, 9.905, 14.6), south of the Reversi table ([`reversi.md`](reversi.md)). Each
storage account the feed names is one small timber store dealt onto a cell of a
4 by 4 grid; its look carries the account's kind, and a lectern screen at the
table's far edge reads the piece under the seated cursor: name, region, kind and
SKU. [`granaries.puck`](granaries.puck) holds the table and its rules, and
`debug.puck` imports it whole (`import "granaries"`, ahead of `alchemy` so its
cursor takes the population's top slot). The Azure adapter knows nothing about
granaries, and the board knows nothing about Azure: it reads five text rows.

## The feed

An observation reads an approved collection into state rows and nothing else
([extension configuration](../../src/Puck.World.Server/ExtensionConfiguration.md#observe-a-collection)).
The board's rows are `granaryNames`, `granaryResources`, `granaryRegions`,
`granaryKinds` and `granarySkus`, one cell per account keyed by its name, plus
`granaryFeed.status`, which the host keeps with the feed's state:

| `granaryFeed.status` | Meaning | The lectern's feed line |
|---|---|---|
| 0 | No feed is configured, or no read has finished | `FEED  NONE  AZURE NOT CONNECTED` |
| 1 | The last read succeeded | `FEED  LIVE`, or `FEED  LIVE  NO STORAGE ACCOUNTS` for an empty group |
| 2 | The last read failed: the provider is unavailable or unauthorised | `FEED  OFFLINE  UNAVAILABLE OR UNAUTHORISED` |

A World boots with no feed and shows the first line. Three host-approved
configurations sit in [`worlds/hosting`](../hosting), outside the package, because
the file is host approval and never imported world content:

| File | Provider | Use |
|---|---|---|
| `granaries.extensions.json` | `azure.resource`, `azureCli` | The resource group `byteterrace`'s storage accounts, read live |
| `granaries.fixture.extensions.json` | `observation.fixture` | Six synthetic accounts in four kinds, for tests and headless runs |
| `granaries.offline.extensions.json` | `observation.fixture` | A provider that fails every read, to show the offline line |

The live configuration commits no subscription, tenant or credential. It names
the resource group by name; the provider signs in as the person who runs the
World (`azureCli`: `az login`) or, in a deployment, as a managed identity, and
the subscription is the one visible subscription that holds the group (a read
refuses when none or several do). The `observation.fixture` provider is built in
to every World host.

```text
dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices all --extensions-config-file worlds/hosting/granaries.extensions.json
```

That command reads the signed-in account's storage accounts every 1,800 ticks.
Headless and test runs use the fixture instead:

```text
dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices none --headless --extensions-config-file worlds/hosting/granaries.fixture.extensions.json
```

## How the board is dealt

`granaryStores` is a placement carrying a `deal` facet: a template whose children
are dealt from the keyed text row `granaryNames`, one child per cell, over the
template's own `distribution` region, here a 4 by 4 lattice of cell centres in the
table's frame (the lattice runs rows first, so slot `4 * row + column` is board
cell `4 * row + column`). A child is an ordinary placement row named
`granaryStores/<accountName>`, parented to the table at the offset it was dealt,
carrying the template's prototype and occupation volume. The template renders
nothing. `deal.variants` reads `granaryKinds` at the same key and maps the kind to
a piece: `StorageV2`, `BlobStorage`, `BlockBlobStorage` and `FileStorage` each deal
their own colours, and any other kind deals the grey plain piece.

The server's per-tick sweep (`WorldTick.SweepPlacementDeals`) re-deals only when
the source row, the variant row or the template changes. A cell that arrives takes
the lowest free `dealSlot`; a cell that leaves frees its slot and moves no
sibling; a re-authored variant re-deals that one child in place. Children land as
ordinary placement mutations under the world principal, so they journal,
`world.undo`, and replay through the one placement door.

## Reading the board

`granaryBoard` is the board facet's own row, over the grid `granaryGrid`: 1 on a
cell a piece stands on. The pieces are not bodies, so a rule finds the cell a piece
stands on from the volumes under it. Each of the 16 tiles carries influence
volumes on the channels `tile` and `bit0` to `bit3` (the bits of its own ordinal),
and a piece's reads of those channels add up to its cell; `granaryCellOf` holds
that cell plus one per account, and the occupancy row follows it.

Water and power are two providers at the board's rims, each an influence volume on
its own channel whose extents are bound to a stage row (`state.granaryIrrigationSelected`,
`state.granaryPowerSelected`). Three stages cover the corner cell, the 2 by 2 block
and the 3 by 3 block:

```text
granary.water 2
granary.power 1
world.state granaryWaterCoverage
world.state granaryPowerCoverage
```

Ordinary rules read `influence(water, granaryStores)[$each]` to address a dealt
child. The result counts distinct other providers covering the centre of the
target's first occupation volume, several matching volumes from one provider count
once, a missing target reads absent and a present target outside coverage reads
zero. The board's rules run one phase a tick in a cycle of twelve (`granaryPhase`:
clean the stale cells, find the pieces, follow the occupancy, read the water and
power coverage, count the tallies, read the piece under the cursor), because the
engine prices a tick's rule work as the dearest of rules gated on different
values of one slot; a change to the feed or a stage settles within one cycle,
0.4 s. Coverage rows write only when a value changes, and an account that leaves
has its cells cleaned on the cycle after its piece goes.

A placement's `spatial` array describes named local volumes. `Occupation` reserves
the piece's space, `Clearance` reserves access around it, and `Influence` is
nonblocking coverage on an author-defined channel. They use finite boxes or spheres
with height, local offsets, placement scale and yaw. A volume whose extents read a
state row is compiled again when the row changes. A deal's `preserve` and `reflow`
settings (not used by this board) let an instance own its transform and facets and
let a guarded, undoable batch rearrange footprints
([placement deals](../../.claude/skills/puck-world/references/documents-state.md)).

## Playing at the table

Walk within 2.5 m of the board and press the context button (E, or the pad's RB):
the Moth sits down and the camera looks over the board from her side.
`debug.go granaries` stands her at the south (`-Z`) edge, facing the board and
close enough to sit. While she sits, the controls are the shared tabletop board
page (`chess.puck`).

| Device | Move the cursor | Stand up |
|---|---|---|
| Mouse | Point at a cell | E |
| Keyboard | W A S D or the arrows | E |
| Pad | Left stick or d-pad | RB |

A blue outline marks the cursor's cell (`granaryCursor`, body 504); it starts on
cell 0. The lectern screen (screen 17) shows the feed line, the account, water and
power tallies, the cursor's cell and, for a piece on it, `granarySel`: its name,
region, kind and SKU.

| To | Console line |
|---|---|
| Go to the granary board | `debug.go granaries` |
| Put the cursor on a cell | `world.state.cell.set granarySeat cursor <0..15>` |
| Read the pieces and cells | `world.state granaryNames`, `world.state granaryCellOf`, `world.state granaryBoard` |
| Read the account under the cursor | `world.state granarySel` |
| Read the feed and the tallies | `world.state granaryFeed`, `world.state granaryNote`, `world.state granaryTally` |
| Read what the host reports about the feed | `world.extensions` |
| Stage a provider | `granary.water <0..2>`, `granary.power <0..2>` |

## Tests

[`granaries-tests.puck`](granaries-tests.puck) seeds the feed rows as the
observation would and checks the cells, the coverage, the inspected piece, a
provider shrinking back, an account leaving and the not-connected board. Run it
with `puck test worlds/debug/granaries-tests.puck --reproduce --jobs 1`.
`GranaryContentLawTests` (`tests/Puck.World.Games.Tests`) locks the authored
structure and the deal sweep's quiet tick.
