#define SDF_DYNAMIC_TRANSFORMS
#define SDF_INSTANCE_MASKS
#define SDF_SAMPLED_REGIONS
#define SDF_TAPE_BUILD
#define SDF_VM_DISABLE_PART_PROGRAMS
#include "sdf-world.hlsli"
#include "../field/sdf-tape-build.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);
    if (viewIndex >= passGroup.viewportCount || id.x >= passGroup.tileGrid.x || id.y >= passGroup.tileGrid.y) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    ViewportData view = worldView();
    uint tile = worldTileIndex(viewIndex, id.xy, passGroup.tileGrid);
    float2 extent = float2(worldViewDims(view));
    float2 minimum = float2(id.xy) * float(WorldTileSize);
    float2 maximum = min(minimum + float(WorldTileSize), extent);
    TileCone cone = buildTileCone(view, minimum / extent, maximum / extent);
    sdfBuildTileTape(tile, worldInstanceMaskBase(tile), view.position.xyz, cone.centerDirection, cone.chord,
        tiles[worldTileMarchStartIndex(tile)], tiles[worldTileFarBoundIndex(tile)]);
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
