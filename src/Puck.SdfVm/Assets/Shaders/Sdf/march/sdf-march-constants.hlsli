// The march, cone, tile-gap, shadow and heat-map constants the world kernels share, and the per-pixel query tally.
#ifndef MARCH_SDF_MARCH_CONSTANTS_HLSLI
#define MARCH_SDF_MARCH_CONSTANTS_HLSLI
// The primary march's step budget. KEEP IN SYNC with SdfWorldTables.PrimaryMarchSteps (the world.budget cost sheet
// quotes it against the authored far distance). There is deliberately NO far-distance constant beside it any more:
// the far plane is world data (render.farDistance), read per view through worldFarDistance.
static const int MaxSteps = 128;
static const float SurfaceEpsilon = 0.001;
static const float SphereTraceOmega = 1.2; // Keinert over-relaxation factor (1 = plain sphere tracing; [1, 2))
static const int ConeMarchSteps = 56;
static const int IndependentConeMarchSteps = 8;
static const float ConeEpsilon = 0.002;
// Four-bound teleport (Larsson "The Gunk"): after the beam cone finds the tile's ENTRY (the classic marchStart), it
// keeps marching a bounded budget to detect ONE proven-empty gap between two occupied bands — [firstExit,
// secondEntry] — that sdf-world-views teleports the fine ray across. TileGapSteps caps the extra beam cost;
// TileGapMinStep floors the through-band advance so a near-zero cone clearance can't stall the search.
static const int TileGapSteps = 16;
static const float TileGapMinStep = 0.15;
// Abandon the gap search after this many consecutive in-band, non-increasing-clearance samples. Ground/wall cones
// often descend further into an occupied half-space; continuing both gap and tail searches there adds field walks
// without finding a useful bound. A later clear span may be missed, so this is a cost heuristic, not an emptiness
// proof. Keep the entry already established and leave gap/far bounds at the far plane; the fine ray does the work.
static const int TileGapStallLimit = 3;
// F1 FAR BOUND: after the gap search resolves, a bounded TAIL phase cone-marches from the
// resolved t to prove the far bound — the depth past which the tile's cone cannot produce any footprint-accepted hit
// through the far distance. TileFarSteps caps that extra beam cost (the tail is a latency-rich single-thread march,
// per the beam kernel's design). A descending-band stall skips this phase altogether. Otherwise ten samples may
// establish a clear-to-far span; if none is proven, the tile publishes farBound = the far distance (no early exit).
static const int TileFarSteps = 10;
// Bán & Valasek 2023 auto-relaxed sphere tracing (EG short paper). The fine march tracks the field's along-ray slope
// with an EMA `m` and over-relaxes adaptively — `omega = max(1, 2/(1 - m))`, so a planar (m -> 1) approach takes a big
// step and a concave (m -> -1) one degenerates to a plain step. SlopeBeta is the paper's default;
// SlopeCap clamps `1 - m` away from 0 at tangency so `omega` stays finite (the field is stepScale-clamped to
// <= 1-Lipschitz, so the measured slope M is in [-1, 1] and m never legitimately exceeds SlopeCap).
static const float SlopeBeta = 0.3;
static const float SlopeCap = 0.8;   // omega <= 2 / (1 - 0.8) = 10
// Near-miss refinement. A primary sample inside the footprint shell (fieldDistance < hitThreshold) is accepted only
// once the ray has converged below PrimaryConvergeFraction of the threshold; until then it takes plain steps, at most
// PrimaryRefineSteps of them per ray, and accepts when that budget runs out. Step phase varies with the tile's march
// start and instance mask, so an edge decided on shell entry breaks thin lines into tile-periodic dashes; a refined
// ray that passes a feature leaves the shell and finds what lies behind it. One that escapes to sky still resolves
// the near miss through the exhaustion arm, which keeps the silhouette coverage signal. A 0.5 fraction or a 3-4 step
// budget measurably restores tile-correlated edges.
static const float PrimaryConvergeFraction = 0.25;
static const int PrimaryRefineSteps = 8;
// STRICT-MARCH fallback (SDF_STRICT_MARCH). Defining it (a build-time flip, rebuild the kernels) replaces the default
// Bán 2023 auto-relaxed marcher with a conservative Keinert marcher: fixed omega = 1.2 with a
// disjoint-sphere step-back that LATCHES omega to 1 for the rest of the ray after an overshoot — and omega is NEVER
// re-armed thereafter, not even across a four-bound teleport (the teleport jump itself still runs — it is bound-proven
// on both paths — but it does not reset the latch). It is the conservative, division-free reference marcher. Chosen
// as a compile-time #define because the world kernels are AOT-compiled by DXC in-place at build. (The engine ships one enumerable
// family of compiled Views variants — full ISA, core ops and folds, selected per program; see the
// SDF_CORE_OPS banner in field/sdf-program.hlsli — but a hand-flip parity anchor like this one still doesn't earn a shipped
// pipeline: it remains an explicit development experiment.)
// It is NOT built by default and is exercised by NO gate — a hand-flip parity anchor only. The DEFAULT auto-relaxed
// step's DIVISION is the one new cross-backend hazard (FMA contraction amplified near tangency can flip the disjoint-
// sphere fallback compare), so the divided step and that compare are pinned `precise` on both backends and the strict
// path never rides the division. The four-bound teleport rides BOTH paths (branchless, no division).
// #define SDF_STRICT_MARCH
// WorldTileSize / TileEmpty / worldTileIndex live in sdf-tile.hlsli.

// Shading weights of the world's one directional-sun-plus-hemisphere model. The ambient base, its hemisphere
// gradient, the sun weight and the fog density are environment lanes (SdfEnvironment) so a world can author them;
// their pinned values live on as SdfEnvironment.Default. The fog density's pinned value lives on as
// SdfEnvironment.DefaultFogDensity.
// An unbound screen's face: dark glass with nothing behind it, tinted faintly by the sun.
static const float3 ScreenGlassColor = float3(0.02, 0.025, 0.03);
static const float ScreenGlassBase = 0.85;
static const float ScreenGlassSunTint = 0.15;
// Keeps a screen light's inverse-square attenuation finite for a surface point on the emitter's own face.
static const float ScreenLightMinDistanceSquared = 1.0e-4;
// One 8-bit display code: a silhouette's sky blend weighing less than it changes no displayed pixel.
static const float DisplayCode = (1.0 / 255.0);
// world.debug-view evals calibration: the ramp saturates at this many tallied field evaluations. Worst case for a single
// lit pixel is bounded by MaxSteps (128, primary march) + ShadowSteps (40, the soft-shadow march) + 3 (calcAO) + 4
// (the 4-tap normal fallback, worse than the 1-eval analytic default) + 1 (the coverage-AA probe) ~= 176, so 256
// leaves margin
// before saturating solid red — chosen so a typical unshadowed ambient-only hit (~30-40 evals: a short march plus
// the analytic normal and AO) reads green/yellow rather than washing out at the floor.
static const float EvalHeatmapCeiling = 256.0;
// The soft-shadow march toward the shadow light (softShadowVisibility, sdf-occlusion.hlsli): a penumbra estimate — the
// running minimum of k · d / t, where d is the field's clearance at the sample and t the distance travelled — marched
// by the field's own clearance under a distance-proportional step ceiling that keeps the samples dense enough for the
// estimate to converge. Deterministic: one ray per lit pixel, no per-frame
// sample, no history. k is the reciprocal of the shadow light's authored penumbra half-slope
// (worldShadowPenumbraSlope), so the visibility ramps across an angular band of that slope about an occluder's edge.
static const int ShadowSteps = 64;
static const int FastShadowSteps = 12;
// Short: this compute march has no acceleration structure to fast-forward to the occluder, so every unit of reach
// is marched per lit pixel. Contact/self shadows (the visual win) are near. Scaled per frame by worldShadowDistanceScale.
static const float ShadowMaxDistance = 9.0;
static const float ShadowBias = 0.02;
// A sample within this travel of the origin reads the origin surface itself — a ray skimming its own curved surface
// at grazing incidence — and is skipped. Contact occluders closer than this are not resolved.
static const float ShadowEstimateStart = 0.12;
static const float ShadowStepMin = 0.02;      // an occluder thinner than this can be stepped through
static const float ShadowStepNear = 0.08;     // the step ceiling's floor, world units
static const float ShadowStepFarSlope = 0.05; // the ceiling grows with distance: max(ShadowStepNear, slope * t)
// Fleet-scale presentation path: shorter reach and budget, a wider stride through open space. It can step through
// occluders thinner than its stride, the same trade the near-field ShadowStepMin floor already makes.
static const float FastShadowStepMax = 1.8;
static const float FastShadowStepFarSlope = 0.45;
static const float FastShadowMaxDistance = 5.0;
// The shadow-cull gather's cone (see sdfShadowGather): the half-slope of the cone whose occluders the gather must
// contain for the shadow march to be sound. The march's samples read the field within the penumbra band about the
// ray, so every occluder that can lower the estimate lies inside three penumbra half-slopes with margin; a wider cone
// is always a superset, only less selective. SdfEnvironment.MaxPenumbraSlope keeps the chord below one.
float worldShadowPenumbraChord() { return (3.0 * worldShadowPenumbraSlope()); }

// Per-pixel query tally for world.debug-view evals, including primary local-part marches and shading probes.
// Call sites in the march, the normals and the occlusion count their queries; the interpreter does not. This per-thread
// scalar follows the material-seam channel's pattern and resets at each hit stage's entry. Counting stays active
// for every view so selecting the evaluation heatmap does not change the work being measured.
static float sdfEvalCount = 0.0;
#endif
