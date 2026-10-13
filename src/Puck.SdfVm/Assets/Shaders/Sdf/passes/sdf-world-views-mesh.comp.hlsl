// The baked-mesh path's views variant (sdf-world-views.comp.hlsl, included verbatim): every hit is a mesh pixel, whose
// material resolves from its record and its draw, so the stage's field reads (sdfViewsFieldReads) compile out and the
// kernel carries no interpreter. The mesh path's views pass records it; the debug views that read the field draw nothing.
#define SDF_VIEWS_MESH_ONLY
#include "sdf-world-views.comp.hlsl"
