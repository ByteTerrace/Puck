# The partition granaries

The granaries turn the platform's user-data storage accounts into a small storeyard:
timber buildings, green roofs, sealed side bins, and a beacon over the public-content
anchor. Each building is one discovered account. The court and buildings are ordinary
authored creations; the Azure adapter knows nothing about granaries or Orleans.

## Visit the live deployment

From the repository root, with Azure CLI signed into the approved tenant:

```text
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/granaries.world.json --extensions-config-file src/Puck.World/Assets/hosting/granaries.extensions.json
```

The checked-in host configuration selects the existing `byteterrace` resource group,
lists storage accounts beginning with `bytrcstp`, and excludes the fabric account
`bytrcstp000`. It uses resource metadata reads only: no keys, blob contents, or Azure
resource mutations. The credential still needs Azure permission to list that group.
Copy the configuration and set an independent lineage UUID for another deployment;
choose `managedIdentity` for an appropriately configured host identity.

The viewing world starts at 30 Hz. Discovery begins on its first closed tick,
collects results on one-second scans, and refreshes once per minute of simulation
time. Pausing the world pauses new discovery attempts. With no extension
configuration, the court remains empty until an author adds content.

Use `view.override layout walk` for the walking camera and
`view.override layout action` to return to the overlook. The normal seat controls
use WASD, arrow keys, mouse steering, and gamepad input.

```text
world.extensions
world.state granaryNames
world.state granaryResources
world.state granaryRegions
world.state granaryKinds
world.state granarySkus
```

The five tables share account-name keys, so `granary-bytrcstp001` maps directly to
the `bytrcstp001` resource cell. `world.extensions` reports when inventory was read
and whether its projection was admitted. A read failure retains the last complete
scene; it never makes all buildings disappear. A confirmed empty collection does
remove generated buildings. Neither action changes Azure resources.

## What the metaphor means

The beacon marks `bytrcstp001` because the checked-in hosting convention fixes
public content to partition zero, whose account offset is one. Other buildings
represent potential private user-data homes. The deployment's account inventory
is not the active Orleans partition count: discovering five accounts does not
prove five partitions are serving a running host. No bin fill level, request
traffic, health, grain occupancy, or migration status is invented from inventory.

An author could later use separately measured occupancy to fill bins, a confirmed
migration to move a wagon, or a telemetry alarm to attract pests. Those events need
their own explicit sources and gameplay rules. The current scene makes only the
inventory claim it can substantiate.

## Import and reinterpret

Import `modules/granaries.world.json` from another shipped world. The main Puck
world already imports it. Move `granaryCourt` to place the whole storeyard, or
replace the three prototypes to reinterpret the same accounts. Generated buildings
are children of the court, using its position and yaw. Keep its scale at one.
The court's title uses the existing hash-pinned font at `fonts/inter-regular.ttf`,
resolved relative to the importing root world; provide that asset when relocating
the module.

Copy the host configuration, set `world` to the importing document's exact ID,
and retain or narrow its source scope and disclosure. World imports never enable
Azure access themselves. A text world can use the tables alone by omitting
`observations[].placements`. Different sources can project into different tables
and reserved placement prefixes through the same mechanism.

The example bounds inventory at 16 items and reserves 16 future placements at
boot. The court is sized for the present two rows; enlarge its authored grounds
for a larger deployment. Buildings use static per-shape rendering, with no body
simulation, per-tick cloud calls, or unchanged-snapshot rebuilds. Complete changes
are admitted as one batch. Sorting account keys fixes identity and ordering, while
membership changes can reposition buildings in the grid. Reserve `granary-` for
this source rather than placing unrelated authored rows under that prefix.

See [service composition](../../../../Puck.World.Server/ExtensionConfiguration.md)
for projection authority and lifecycle, and the
[Azure adapter](../../../../Puck.World.Azure/README.md) for query settings and bounds.
