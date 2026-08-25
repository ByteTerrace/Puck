// The FOLD-OPS compiled variant of Stage 1 (sdf-world-views.comp.hlsl — the whole kernel body is included verbatim
// below; this file adds ONLY the SDF_FOLD_OPS strip flag). The middle tier of the strip ladder (see the banner in
// sdf-vm.hlsli): folds, scopes, and the simple exotic shapes stay compiled; the HEAVY warp/noise family
// (twist/bends/log-sphere/cell-jitter/displace/domain-warp/noise-displace and the analytic-solve 2D shapes) compiles
// out, freeing the register pressure those cases' live state costs. SdfViewsKernelVariants.Select picks this tier for
// a program that touches a fold/scope but no heavy op, so every compiled-out case is unreachable.
#define SDF_FOLD_OPS
#include "sdf-world-views.comp.hlsl"
