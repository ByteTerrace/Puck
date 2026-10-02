// Resource-free numeric constants shared by field, surface and sky responses.
#ifndef FIELD_SDF_NUMERIC_HLSLI
#define FIELD_SDF_NUMERIC_HLSLI
// === Shared numeric constants ========================================================================================
// Written at full double precision: each rounds to the SAME float32 the shorter literal did, so naming them is
// bytecode-identical while the digits document the exact quantity.
#define SDF_SQRT3     1.7320508075688772   // sqrt(3)
#define SDF_SQRT_HALF 0.7071067811865476   // sqrt(1/2) — the 45-degree chamfer bevel plane's normalization
#define SDF_PI        3.141592653589793
#define SDF_TAU       6.283185307179586    // 2*pi

#endif
