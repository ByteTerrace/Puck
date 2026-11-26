// The light pass of a binned view (SdfWorldPackage.Parts.ViewsLights): the views stage over the tiles of one bin's jobs,
// as the binned views kernels run it, up to its light walk, whose sums it stores (sdf-views-light-sums.hlsli) for those
// kernels to read in place of a walk of their own. This one walks a tile whose pixels have programs: the interpreter, or
// its specialization of a program set in place of isa/sdf-shade.hlsli.
#define SDF_VIEWS_BINNED
#define SDF_VIEWS_LIGHT_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-stage.hlsli"
#include "sdf-pixel-entry.hlsli"
