// Depth-only primary traversal reuses the certified indirect sweep over the residency's field.
#define SDF_LIGHT_VIEW
#define SDF_PRIMARY_PASS
#include "sdf-hit-pass.hlsli"
#include "../march/sdf-primary.hlsli"
#include "../indirect/sdf-indirect-procedures.hlsli"
#include "sdf-hit-entry.hlsli"
