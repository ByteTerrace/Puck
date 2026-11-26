// The views pass of a binned view: the views stage over the tiles of one bin's jobs, reading the filtered occlusion and
// shadows from the filter record (sdf-views-filter.hlsli) and what its light walk added from the light sums the light pass
// stored (sdf-views-light-sums.hlsli) rather than gathering or walking them, so the kernel holds no tile, no barrier and
// no walk. This one shades a tile whose pixels have programs: the interpreter, or its specialization of a program set in
// place of isa/sdf-shade.hlsli.
#define SDF_VIEWS_BINNED
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-stage.hlsli"
#include "sdf-pixel-entry.hlsli"
