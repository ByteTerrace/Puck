# Modules

A module is a `puck.world.def.v1` fragment the island imports under an alias
(`imports: [{"document": "modules/<name>.world.json", "as": "<alias>"}]`),
rooted under one court placement named `<alias>Court` at local origin and
exporting only its control rows.

## granaries

The granaries turn the platform's user-data storage accounts into a storeyard:
timber buildings, green roofs, sealed side bins, and a beacon over the
public-content anchor. Each building is one discovered account. The court and
the buildings are ordinary authored creations; the Azure adapter knows nothing
about granaries.

### How the court is dealt

`granaryStores` is a placement carrying a `deal` facet: a template whose
children are dealt from the keyed text row `granaryNames`, one child per cell,
over the template's own `distribution` region — a scatter of 16 offsets, the
row's capacity. A child is an ordinary placement row named
`granaryStores/<accountName>`, parented to the template at the offset it was
dealt, carrying the template's prototype, `solid`, `grip`, `region`, and
`emission`. The template itself renders nothing and collides with nothing.

`deal.variants` reads the same row at the same key and maps the cell's text to
a prototype: `bytrcstp001` deals `granaryAnchor` (the beacon), every other
account deals `granaryStore`.

The server's per-tick sweep (`WorldServer.SweepPlacementDeals`) re-deals only
when the row moves. A cell that arrives takes the lowest free offset; a cell
that leaves frees its offset and moves no sibling; a re-authored variant re-deals
that one child in place. Children land as ordinary placement mutations under the
world principal, so they journal, `world.undo`, and replay through the one
placement door.

Read back:

```text
world.placements          # 'granaryStores' … dealt from granaryNames (<n> of 16); 'granaryStores/<name>' … dealt by granaryStores
world.budget              # placements … 16 dealt offset(s) over 1 template(s)
world.state granaryNames
world.state.cell.set granaryNames bytrcstp004 bytrcstp004   # deals a fourth building on the next tick
world.state.cell.remove granaryNames bytrcstp004            # removes it, the others stay put
```

### Visit the live deployment

From the repository root, with Azure CLI signed into the approved tenant:

```text
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/granaries.world.json --extensions-config-file src/Puck.World/Assets/hosting/granaries.extensions.json
```

The checked-in host configuration selects the `byteterrace` resource group,
lists storage accounts beginning with `bytrcstp`, excludes the fabric account
`bytrcstp000`, and projects `name`/`id`/`location`/`kind`/`sku` into the five
`granary*` text rows. Resource metadata reads only: no keys, blob contents, or
Azure resource mutations. Set `world` to the importing document's exact id and
name the rows as the importing document spells them (an aliased import
prefixes them `<alias>_`). Copy the configuration and set an independent
lineage UUID for another deployment; choose `managedIdentity` for an
appropriately configured host identity.

Discovery begins on the first closed tick, collects on one-second scans, and
refreshes once per minute of simulation time; pausing the world pauses
discovery. With no extension configuration the court stays empty until a row
is written. A read failure retains the last complete rows; a confirmed empty
collection empties them, and the sweep removes every dealt building.

### What the metaphor means

The beacon marks `bytrcstp001` because the hosting convention fixes public
content to partition zero, whose account offset is one. Other buildings are
potential private user-data homes. The account inventory is not the active
Orleans partition count: discovering five accounts does not prove five
partitions are serving. No bin fill level, traffic, health, grain occupancy, or
migration status is invented from inventory. A later reading of the platform
enters the same way — an observation writing a row a placement, body, or rule
reads.

### Import and reinterpret

Move `granaryCourt` (restate its position and yaw from the importing document)
to place the whole storeyard; keep its scale at one. Replace the three
prototypes to reinterpret the same accounts. The court's title uses the
hash-pinned font at `fonts/inter-regular.ttf`, resolved relative to the
importing root world. The module exports its five rows as reads, spawns at
`granaries-arrival` on the court's near edge, and walks the `granaryYard`
surface domain.
