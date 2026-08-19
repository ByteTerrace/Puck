using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Describes one camera and its normalized output region for an SDF frame.</summary>
/// <param name="Camera">The camera used to render the view.</param>
/// <param name="Region">The view's normalized output region.</param>
public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region) {
    /// <summary>The off-axis (asymmetric) frustum's tangent-space center offset — <c>(0, 0)</c> (the default) is the
    /// ordinary symmetric camera every view used before this member existed, byte-identical: the shader adds it as a
    /// trailing term (see sdf-world.hlsli's <c>cameraRayDirection</c>), and adding exactly zero changes no rounding.
    /// A non-zero value shears the frustum so a fixed rectangular aperture (a border-window face) maps 1:1 to the
    /// render regardless of where the camera's own eye sits relative to that aperture — see
    /// <see cref="Puck.SdfVm.Views.SdfAsymmetricFrustum"/>, the one producer of a non-zero offset. Rides the packed
    /// render-scale row's two always-zero spare lanes (KEEP IN SYNC with <c>SdfWorldEngine.PackViewports</c> and
    /// sdf-world.hlsli's <c>ViewportData.renderScale</c>) — no row growth.</summary>
    public Vector2 AsymmetricFrustumOffset { get; init; }
    /// <summary>The view's internal render scale in (0, 1]: Stage 1 renders the view at this fraction of its output
    /// region (an integer-derived extent — see the shader's <c>worldRenderDims</c>) and Stage 2 upsamples it back.
    /// 1 (the default) renders native through a bit-exact copy path, so an unset frame is byte-identical to a build
    /// without the lever. Presentation-only: hosts drop it during camera transitions / for mostly-hidden views.</summary>
    public float RenderScale { get; init; } = 1f;
    /// <summary>The reduced-resolution reconstruction blend in [0, 1]: 0 keeps the four-tap bilinear fast path; 1 uses
    /// clamped Catmull-Rom bicubic reconstruction; values between blend continuously. Ignored by the native exact-copy
    /// path. Presentation-only, quantized to one byte in Stage 2's push constants.</summary>
    public float UpscaleSharpness { get; init; }
}
/// <summary>Contains the scene program and presentation state consumed by one SDF render frame.</summary>
/// <param name="Program">The SDF program to render.</param>
/// <param name="ProgramChanged">Whether the renderer must upload <paramref name="Program"/> for this frame.</param>
/// <param name="Views">The camera views to render and composite.</param>
/// <param name="Time">The presentation time in seconds.</param>
/// <param name="WarpAmount">The presentation warp amount supplied to the compositor.</param>
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time,
    float WarpAmount
) {
    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Must supply
    /// at least the program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> entries (the render frame
    /// throws otherwise — a dynamic slot silently rendering at identity is a bug, not a default); empty is therefore
    /// valid only for a program with no dynamic slots (the renderer then binds a single identity slot the program
    /// never references). Updating this list is how entities move — the program (binding 1) is uploaded once and left
    /// untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];
    /// <summary>A per-frame scale on the world path's ambient term (default 1 = unchanged). Below 1 dims the room so
    /// the diegetic screen glow dominates — the overworld sets it low for mood; other scenes leave the default.</summary>
    public float AmbientScale { get; init; } = 1f;
    /// <summary>A per-frame scale on the world path's sun (directional) term (default 1 = unchanged). Pairs with
    /// <see cref="AmbientScale"/> to darken the room for the overworld mood.</summary>
    public float SunScale { get; init; } = 1f;
    /// <summary>The scene's directional sun, as a unit vector pointing from the surface toward the light. Rides the
    /// screen-light buffer's sun rows, so a day/night cycle is a per-frame value rather than a shader rebuild.</summary>
    /// <remarks>The default is the exact float32 triple the shaders pinned as <c>SdfSunDirection</c> before the sun
    /// became per-frame data, so a frame that never sets it renders bit-identically to the pinned era. The area-light
    /// shadow estimator needs a frame, not just a direction; the engine derives the two tangents from this vector in
    /// double precision and rounds once (<c>SdfWorldEngine.PackSunFrame</c>) — which reproduces the pinned tangent and
    /// bitangent literals exactly, where a float32 <see cref="Vector3.Normalize"/> lands one ulp off in the
    /// bitangent's Z. Derive it any other way and the default sun stops being a no-op.</remarks>
    public Vector3 SunDirection { get; init; } = DefaultSunDirection;
    /// <summary>The sun's linear RGB color (default white). Multiplies the directional term, so a sunset is a warm
    /// color here rather than a second code path.</summary>
    public Vector3 SunColor { get; init; } = Vector3.One;
    /// <summary>The sun's diffuse weight — the shaders' pinned <c>SunWeight</c> as per-frame data.</summary>
    public float SunWeight { get; init; } = DefaultSunWeight;
    /// <summary>The ambient term's linear RGB color (default white).</summary>
    public Vector3 AmbientColor { get; init; } = Vector3.One;
    /// <summary>The ambient term's constant floor — the shaders' pinned <c>AmbientBase</c> as per-frame data.</summary>
    public float AmbientBase { get; init; } = DefaultAmbientBase;
    /// <summary>The ambient term's hemisphere gradient, scaling surface normal Y (sky above, darker below) — the
    /// shaders' pinned <c>AmbientHemisphere</c> as per-frame data.</summary>
    public float AmbientHemisphere { get; init; } = DefaultAmbientHemisphere;
    /// <summary>The object grid's reference frame orientation (the lattice renders in this frame's coordinates).</summary>
    public Quaternion GridObjectFrame { get; init; } = Quaternion.Identity;

    /// <summary>The slice debug view's plane selector: 0 (the default) = camera-locked (the plane through the world
    /// origin with normal = camera forward), 1/2/3 = a world-axis-aligned plane (X/Y/Z normal) at
    /// <see cref="DebugSliceOffset"/> along that axis. Rides the screen-light buffer's environment entry's two spare
    /// lanes (KEEP IN SYNC with sdf-world.hlsli's <c>sdfScreenLights</c> env decode and
    /// <c>SdfWorldEngine.PackScreenLights</c>) — no new upload plumbing. Read only by debug view mode 7 (slice);
    /// every other mode ignores it, so the default demo is byte-unchanged.</summary>
    public float DebugSliceAxis { get; init; }
    /// <summary>The axis-aligned slice plane's signed offset along the <see cref="DebugSliceAxis"/> axis (world
    /// units). Ignored while <see cref="DebugSliceAxis"/> is 0 (camera-locked).</summary>
    public float DebugSliceOffset { get; init; }
    /// <summary>Engine-bench lever: skips <c>calcAO</c>'s normal-ladder ambient occlusion (occlusion is forced to 1, so
    /// creases read brighter). Default <see langword="false"/> = AO on. Isolates the AO map() evals per lit pixel for
    /// the <c>sdf.ao</c> bench toggle. Rides the bench-params screen-light row's <c>.y</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldAoDisabled</c>); an unset frame uploads 0
    /// and AO stays on.</summary>
    public bool DisableAmbientOcclusion { get; init; }
    /// <summary>A/B lever for the beam-published per-tile far bound. Default
    /// <see langword="false"/> keeps the far bound active — the shipped behavior: the fine march exits at
    /// <c>traveled &gt;= farBound</c> (plane 3), where the tile's cone provably cannot produce any footprint-accepted hit
    /// through MaxDistance, so the pixel is output-identical to a full march but pays fewer steps. Set
    /// <see langword="true"/> to push the far bound out of reach so the march runs to MaxDistance exactly as without
    /// it — the paired-run "off" side. Rides a dedicated far-field screen-light row's <c>.x</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldFarBoundDisabled</c> / <c>SdfFarFieldParams</c>);
    /// an unset frame uploads 0 and the far bound stays on.</summary>
    public bool DisableFarBound { get; init; }
    /// <summary>Engine-bench lever: skips the per-screen area-light loop (the diegetic CRTs stop spilling colored light
    /// into the room). Default <see langword="false"/> = screen lights on. Directly measures the lit CRTs' cost for the
    /// <c>sdf.screen-lights</c> bench toggle. Rides the bench-params screen-light row's <c>.w</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldScreenLightsDisabled</c>); an unset frame
    /// uploads 0 and screen lights stay on.</summary>
    public bool DisableScreenLights { get; init; }
    /// <summary>A/B lever (and kill switch) for temporal accumulation of the area-light shadow estimator. Default
    /// <see langword="false"/> keeps accumulation on — the shipped behavior: each frame's
    /// <c>ShadowSamplesPerPixel</c>-sample estimate is folded into the reprojected previous value by an integer moving
    /// average, which is what turns a three-level stochastic estimate into a smooth penumbra. Set
    /// <see langword="true"/> to shade the raw per-frame estimate with no history read and no history write.</summary>
    /// <remarks>
    /// The "off" side is deliberately noisy — it is the paired-run A/B side and what a gate pins when it needs a frame
    /// to be a pure function of its own inputs rather than of the frames before it. It is not a quality tier.
    /// </remarks>
    public bool DisableShadowAccumulation { get; init; }
    /// <summary>Disables the soft-shadow grid cull (default <see langword="false"/> = the cull is on). With the cull on
    /// the world lit path gathers each lit pixel's shadow-ray grid neighborhood and marches only those instances —
    /// bit-identical to the flat all-instances shadow but far cheaper on spread scenes. Setting this <see langword="true"/> forces
    /// the flat all-instances march: the ground-truth reference for the cull, and the A/B lever's off state (the
    /// <c>sdf.shadowcull</c> verb) — cull-equals-flat parity is checked by flipping the verb. Rides the screen-light
    /// buffer's grid-object-params row's reserved <c>.w</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c>
    /// and sdf-world.hlsli's <c>worldShadowCullEnabled</c>); an unset frame uploads 0 and the cull stays on.</summary>
    public bool DisableShadowCull { get; init; }
    /// <summary>A/B lever for the shadow light-side escape exit. Default <see langword="false"/> keeps the exit
    /// active — the shipped behavior: each of <c>areaShadowVisibility</c>'s binary rays stops the moment its
    /// de-scaled clearance exceeds the remaining reach, at which point the field's along-ray 1-Lipschitz bound proves
    /// no occluder can lie ahead. Set <see langword="true"/> to run the full shadow step budget/reach — the paired-run
    /// "off" side.</summary>
    /// <remarks>
    /// The exit is bit-identical, not march-path: a binary visibility test has two outputs and the Lipschitz bound
    /// rules the occluded one out exactly, so flipping this lever must not move a single pixel. Rides the far-field
    /// row's <c>.y</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldShadowEscapeExitDisabled</c> / <c>SdfFarFieldParams</c>); an unset frame uploads 0 and the exit stays on.
    /// </remarks>
    public bool DisableShadowEscapeExit { get; init; }
    /// <summary>Engine-bench lever: skips the whole soft-shadow sun march (the sun goes unshadowed; the ambient term is
    /// untouched, so shadowed regions read brighter). Default <see langword="false"/> = shadows on. Isolates the single
    /// most expensive shading term for the <c>sdf.soft-shadows</c> bench toggle. Rides the bench-params screen-light
    /// row's <c>.x</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldSoftShadowsDisabled</c>); an unset frame uploads 0 and shadows stay on.</summary>
    public bool DisableSoftShadows { get; init; }
    /// <summary>Enables the cadence gate: a presentation-only frame-graph optimization where a
    /// frame whose render-consumed inputs are byte-for-byte unchanged from the last rendered frame skips the
    /// mask/beam/cull-args/views compute passes and re-composites from the retained views output — pixel-identical to a
    /// full re-render of the same inputs, at a fraction of the GPU cost. Built on change signatures (the packed
    /// per-frame byte spans the skipped passes consume, plus a program/decal revision), never wall-clock heuristics, so
    /// a camera ease — any input change at all — re-renders. The engine additionally forces a render whenever a live
    /// screen source is bound or a carve bake is in progress (their content changes without touching a packed span).
    /// Default <see langword="false"/> = the gate is off and every frame renders fully — byte-identical to a build
    /// without the gate. Presentation-only: never involves simulation state, and a skipped frame's simulation is
    /// unaffected.</summary>
    public bool EnableCadenceGate { get; init; }
    /// <summary>Engine-bench lever (PATH B): when <see langword="true"/>, the soft-shadow march skips Subtraction-family
    /// carve instances (host-flagged shadow-transparent) and marches the pre-carve union hull — the carve cavities stop
    /// letting sun through (a carved tunnel stays shadowed), collapsing the O(cluster) shadow re-march on dense-carve
    /// scenes to O(few). Default <see langword="false"/> = off (the full occluder set, byte-identical): shadows still
    /// evaluate every carve. Conservative when on — a skipped carve can only make the field more solid, so shadows go
    /// darker, never light-leak. The <c>sdf.shadow-proxy</c> bench toggle. Rides a dedicated shadow-proxy screen-light
    /// row's <c>.x</c> lane (SdfBenchParams's four lanes are full — KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c>
    /// and sdf-world.hlsli's <c>worldShadowProxyEnabled</c> / <c>SdfShadowProxyParams</c>); an unset frame uploads 0 and
    /// the proxy stays off.</summary>
    public bool EnableShadowProxy { get; init; }
    /// <summary>The grid-lock overlay flags (bit0 = draw the world floor grid, bit1 = draw the object grid). Rides
    /// the screen-light buffer's grid rows 9..12 (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and
    /// sdf-world.hlsli's <c>SdfGridWorld..SdfGridObjParams</c> decode). Default 0 = no overlay, so a frame that never
    /// sets it uploads the same zeros as before.</summary>
    public uint GridFlags { get; init; }
    /// <summary>The floor plane height the world grid draws on (the overlay gates on the surface being near this Y).</summary>
    public float GridFloorY { get; init; }
    /// <summary>The object grid's reference frame origin (world space).</summary>
    public Vector3 GridObjectOrigin { get; init; }
    /// <summary>The object grid's finite-patch radius (reference-local units); 0 disables the object grid.</summary>
    public float GridObjectPatchRadius { get; init; }
    /// <summary>The object grid's per-axis in-plane pitch (reference-local X/Z).</summary>
    public Vector2 GridObjectPitch { get; init; }
    /// <summary>The world floor grid's per-axis lattice pitch on X/Z (world units); 0 disables the grid on that axis.</summary>
    public Vector2 GridWorldPitch { get; init; }
    /// <summary>The area-light shadow estimator's sample index: which point of the digital net every pixel draws this
    /// frame.</summary>
    /// <remarks>
    /// <para>
    /// This must be fed from the deterministic tick clock — <c>WorldSimulation.ElapsedTicks</c> — and never from
    /// <see cref="Time"/>, which is a presentation-clock accumulation that advances by wall-clock deltas. The sampler
    /// is stateless and seekable precisely so that a replay at tick N draws the identical set of sun-disc directions;
    /// sourcing this from a wall-clock quantity throws that away and makes the shadows a per-run quantity.
    /// </para>
    /// <para>
    /// It is folded into the engine's frame signature, so the cadence gate can never skip a frame whose sample index
    /// moved.
    /// </para>
    /// </remarks>
    public uint SampleIndex { get; init; }
    /// <summary>Engine-bench lever: scales the soft-shadow reach (both the <c>sdfShadowGather</c> cull cone and the
    /// march ceiling — one shared length, or the cull set would be unsound for the ray) for the <c>sdf.shadow-distance</c>
    /// bench toggle. <c>0</c> (the default) means the full 1.0 reach — an unset frame uploads 0 and behavior is
    /// unchanged; set 0.5/0.25 to shorten far shadows. Rides the bench-params screen-light row's <c>.z</c> lane (KEEP IN
    /// SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldShadowDistanceScale</c>).</summary>
    public float ShadowDistanceScale { get; init; }
    /// <summary>Uses the already-computed camera-tile instance mask for soft-shadow rays instead of running the
    /// correctness-complete per-pixel shadow-grid gather. This is an explicit performance approximation for dense
    /// real-time crowds: it can omit an occluder outside the camera tile whose shadow reaches into the tile, but avoids
    /// paying a grid traversal for every sun-facing pixel. Default <see langword="false"/> keeps the exact gathered
    /// mask. Rides the shadow-proxy params row's reserved <c>.y</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseCameraTileShadowMask</c>).</summary>
    public bool UseCameraTileShadowMask { get; init; }
    /// <summary>Uses the one-sample contact-AO approximation instead of the three-rung quality ladder. This is an
    /// explicit presentation approximation for dense real-time scenes; the default <see langword="false"/> retains
    /// the quality path. Rides the shadow-proxy params row's reserved <c>.w</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseFastAmbientOcclusion</c>).</summary>
    public bool UseFastAmbientOcclusion { get; init; }
    /// <summary>Uses the bounded-cost soft-shadow marcher: fewer samples, wider open-space advances, and a sub-visible
    /// darkness early-out. This is an explicit presentation approximation for dense real-time scenes; the default
    /// <see langword="false"/> retains the exact 48-step quality path. Rides the shadow-proxy params row's reserved
    /// <c>.z</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldUseFastSoftShadowMarch</c>).</summary>
    public bool UseFastSoftShadowMarch { get; init; }
    /// <summary>Selects the four-tap finite-difference surface normal instead of the default analytic forward-mode
    /// gradient dual. The default <see langword="false"/> uses analytic normals (one dual field evaluation at
    /// the hit — exact through the transform chain, immune to finite-difference cancellation). Rides the screen-light
    /// buffer's grid-object-params row's reserved <c>.z</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseTapNormals</c>); a frame that never sets
    /// it uploads 0 and shades with analytic normals.</summary>
    public bool UseFiniteDifferenceNormals { get; init; }
    /// <summary>Gets a value indicating whether the procedural sky (three-stop gradient, sun disc, star field) is
    /// active. The default <see langword="false"/> takes the shader's pinned two-stop gradient through an
    /// unconditional branch — the identical instructions in the identical order the shader held before this member
    /// existed — so a frame that never sets it renders bit-identically. Rides the sky-horizon row's <c>.w</c> lane
    /// (KEEP IN SYNC with <c>SdfWorldEngine.PackSkyFrame</c> and sdf-world.hlsli's <c>worldSkyEnabled</c>).</summary>
    public bool SkyEnabled { get; init; }

    /// <summary>The sky gradient's straight-up (zenith) color. Read only while <see cref="SkyEnabled"/>.</summary>
    public Vector3 SkyZenithColor { get; init; } = DefaultSkyZenithColor;
    /// <summary>The sky gradient's horizon-band color — the gradient's middle stop. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public Vector3 SkyHorizonColor { get; init; } = DefaultSkyHorizonColor;
    /// <summary>The sky gradient's straight-down (nadir/ground) color. Read only while <see cref="SkyEnabled"/>.</summary>
    public Vector3 SkyGroundColor { get; init; } = DefaultSkyGroundColor;
    /// <summary>The exponential distance-fog density fading toward the sky color — the shaders' pinned
    /// <c>FogDensity</c> constant as per-frame data. Read every frame regardless of <see cref="SkyEnabled"/> (fog and
    /// the gradient are independent levers); the default reproduces the retired constant's exact float32 value, so an
    /// unset frame's fog term is bit-identical.</summary>
    public float SkyFogDensity { get; init; } = DefaultSkyFogDensity;
    /// <summary>The visible sun disc's angular half-radius in radians, in (0, π/2]. Read only while
    /// <see cref="SkyEnabled"/>; the engine host-bakes it into a <c>pow()</c> exponent
    /// (<c>SdfWorldEngine.PackSkyFrame</c>) rather than deriving one per pixel.</summary>
    public float SkySunDiscRadians { get; init; } = DefaultSkySunDiscRadians;

    /// <summary>The visible sun disc's peak additive brightness. Zero (the default) draws no disc. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkySunDiscIntensity { get; init; }

    /// <summary>The star field's cell count per octahedral sky-projection axis. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyStarDensity { get; init; } = DefaultSkyStarDensity;

    /// <summary>The star field's peak per-star brightness. Zero (the default) draws no stars. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyStarBrightness { get; init; }
    /// <summary>The star field's per-cell hash seed. Read only while <see cref="SkyEnabled"/>.</summary>
    public uint SkyStarSeed { get; init; }
    /// <summary>The fraction of stars that twinkle, in <c>[0, 1]</c>. Zero (the default) twinkles none. Read only
    /// while <see cref="SkyEnabled"/>.</summary>
    public float SkyStarTwinkleShare { get; init; }
    /// <summary>How far a twinkling star dips below its steady brightness, in <c>[0, 1]</c>. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyStarTwinkleDepth { get; init; }

    /// <summary>The fundamental scintillation rate in hertz — each twinkling star runs at a small harmonic and its own
    /// phase of it, on the deterministic tick clock (<see cref="SampleIndex"/>). Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyStarTwinkleRate { get; init; } = DefaultSkyStarTwinkleRate;
    /// <summary>The cloud layer's colour (linear RGB). Read only while <see cref="SkyEnabled"/>.</summary>
    public Vector3 SkyCloudColor { get; init; } = Vector3.One;

    /// <summary>The fraction of the sky the cloud layer covers, in <c>[0, 1]</c>. Zero (the default) draws no
    /// clouds. Read only while <see cref="SkyEnabled"/>.</summary>
    public float SkyCloudCoverage { get; init; }

    /// <summary>The width of a cloud's edge in the noise's unit range, in <c>(0, 1]</c>. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyCloudSoftness { get; init; } = DefaultSkyCloudSoftness;
    /// <summary>The size of one cloud cell in layer units (the layer sits at unit height). Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public float SkyCloudScale { get; init; } = DefaultSkyCloudScale;

    /// <summary>The cloud lattice's hash seed. Read only while <see cref="SkyEnabled"/>.</summary>
    public uint SkyCloudSeed { get; init; }
    /// <summary>The cloud layer's wind in layer units per second along world X and Z, integrated on
    /// <see cref="SampleIndex"/> (the deterministic tick clock) by the host before upload. Read only while
    /// <see cref="SkyEnabled"/>.</summary>
    public Vector2 SkyCloudDrift { get; init; }
    /// <summary>The cloud layer's rotation about the zenith in radians per second, integrated on
    /// <see cref="SampleIndex"/> by the host. Read only while <see cref="SkyEnabled"/>.</summary>
    public float SkyCloudSpin { get; init; }
    /// <summary>The Coriolis twist: the layer's winding about the zenith in radians at 45° elevation, falling off
    /// toward the horizon and the zenith. Read only while <see cref="SkyEnabled"/>.</summary>
    public float SkyCloudCurl { get; init; }
    /// <summary>The shaping field's wind relative to the cloud field, in layer units per second, integrated on
    /// <see cref="SampleIndex"/> by the host. Read only while <see cref="SkyEnabled"/>.</summary>
    public Vector2 SkyCloudShear { get; init; }

    /// <summary>The default fundamental scintillation rate: 1 Hz.</summary>
    public const float DefaultSkyStarTwinkleRate = 1f;
    /// <summary>The default cloud edge width: 0.25.</summary>
    public const float DefaultSkyCloudSoftness = 0.25f;
    /// <summary>The default cloud cell size: 2 layer units.</summary>
    public const float DefaultSkyCloudScale = 2f;

    /// <summary>Gets the pinned default sun direction — the exact float32 triple the shaders held as
    /// <c>SdfSunDirection</c> before the sun became per-frame data. See <see cref="SunDirection"/>'s remarks for why
    /// it must be reproduced exactly, not merely approximately.</summary>
    public static Vector3 DefaultSunDirection { get; } = new(
        x: 0.51343602f,
        y: 0.79349202f,
        z: 0.32673201f
    );
    /// <summary>Gets the pinned default sun diffuse weight.</summary>
    public static float DefaultSunWeight { get; } = 0.85f;
    /// <summary>Gets the pinned default ambient floor.</summary>
    public static float DefaultAmbientBase { get; } = 0.25f;
    /// <summary>Gets the pinned default ambient hemisphere gradient.</summary>
    public static float DefaultAmbientHemisphere { get; } = 0.25f;
    /// <summary>Gets the pinned default sky zenith color — the shaders' retired two-stop gradient's top color.</summary>
    public static Vector3 DefaultSkyZenithColor { get; } = new(
        x: 0.10f,
        y: 0.13f,
        z: 0.20f
    );
    /// <summary>Gets the default sky horizon color, read only while <see cref="SkyEnabled"/> — the midpoint between
    /// <see cref="DefaultSkyGroundColor"/> and <see cref="DefaultSkyZenithColor"/>.</summary>
    public static Vector3 DefaultSkyHorizonColor { get; } = new(
        x: 0.07f,
        y: 0.09f,
        z: 0.135f
    );
    /// <summary>Gets the pinned default sky ground (nadir) color — the shaders' retired two-stop gradient's bottom
    /// color.</summary>
    public static Vector3 DefaultSkyGroundColor { get; } = new(
        x: 0.04f,
        y: 0.05f,
        z: 0.07f
    );
    /// <summary>Gets the pinned default fog density — the shaders' retired <c>FogDensity</c> constant.</summary>
    public static float DefaultSkyFogDensity { get; } = 0.015f;
    /// <summary>Gets the default sun-disc angular half-radius in radians, read only while <see cref="SkyEnabled"/>.</summary>
    public static float DefaultSkySunDiscRadians { get; } = 0.05f;
    /// <summary>Gets the default star-field cell density, read only while <see cref="SkyEnabled"/>.</summary>
    public static float DefaultSkyStarDensity { get; } = 48f;
}
