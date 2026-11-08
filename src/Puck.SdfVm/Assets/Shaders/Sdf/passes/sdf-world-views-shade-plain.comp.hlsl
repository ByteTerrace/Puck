// The binned views kernel with no authored interpreter or stamp live across its light walk, for a tile no pixel of
// which has a program.
#define SDF_VIEWS_BINNED
#define SDF_SHADE_EMPTY
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-stage.hlsli"
#include "sdf-pixel-entry.hlsli"
