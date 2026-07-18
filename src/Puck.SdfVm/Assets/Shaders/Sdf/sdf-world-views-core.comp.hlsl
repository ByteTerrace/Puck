// The CORE-OPS compiled variant of Stage 1 (sdf-world-views.comp.hlsl — the whole kernel body is included verbatim
// below; this file adds ONLY the SDF_CORE_OPS strip flag). The tape interpreters (mapCore/mapGradCore in sdf-vm.hlsli)
// compile out every EXOTIC op case and exotic shape body, shrinking the interpreter's live register state so more
// warps reside. SdfWorldEngine selects this pipeline at UploadProgram time, and
// ONLY for a program whose instruction stream provably touches no stripped op/shape (SdfViewsKernelVariants.Select —
// selection is a pure function of program content, so every compiled-out case is UNREACHABLE and the rendered field
// is semantically identical to the full variant's; being a separate compiled binary it can still carry the usual DXC
// codegen re-roll noise class — ±1 LSB, the calibrated threshold families' signature — never a structural change).
// The beam prepass deliberately has NO core variant — see the SDF_CORE_OPS banner in sdf-vm.hlsli.
#define SDF_CORE_OPS
#include "sdf-world-views.comp.hlsl"
