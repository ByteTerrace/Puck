namespace Puck.SdfVm;

/// <summary>Identifies an SDF VM instruction operation. Values must match the <c>SDF_OP_*</c> definitions in
/// <c>Assets/Shaders/Sdf/sdf-vm.hlsli</c>; reserved gaps preserve the packed wire format.</summary>
public enum SdfOp : uint {
    ResetPoint = 0,
    Translate = 1,
    Rotate = 2,
    Scale = 3,
    /// <summary>A rigid transform (translation + orientation) read at evaluation time from a per-frame dynamic-transform
    /// buffer slot (Data0.x = slot index) rather than from immediate instruction data: element <c>2*slot</c> is the
    /// position (xyz), <c>2*slot+1</c> the orientation quaternion. Lets a moving entity (player, enemy, carried screen)
    /// be repositioned each frame by updating a small buffer, without re-uploading the static scene program. Honored only
    /// by shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c> (the world path); a no-op elsewhere.</summary>
    TransformDynamic = 4,
    /// <summary>Bends space about the local X axis: the XY plane rotates by <c>rate * x</c> radians (Data0.x = rate).
    /// Not an isometry (space stretches tangentially) — keep rates moderate so the march stays stable.</summary>
    BendX = 5,
    /// <summary>Bends the XY plane by <c>rate * y</c> radians (Data0.x = rate).</summary>
    BendY = 6,
    /// <summary>Bends the YZ plane by <c>rate * y</c> radians (Data0.x = rate). Quirk, kept deliberately: like
    /// <see cref="BendY"/> it keys on the local Y coordinate (not Z).</summary>
    BendZ = 7,
    /// <summary>Elongates the shape that follows by clamping the point into a box (Data0.xyz = extents): the shape's
    /// cross-section is swept over <c>±extents</c> — the classic capsule-from-sphere operator.</summary>
    Elongate = 8,
    ShapeBlend = 9,
    Repeat = 11,
    RepeatLimited = 12,
    // 13–15 (the axis-aligned SymmetryX/Y/Z folds) were collapsed into SymmetryPlane (id 26), which reproduces each
    // bit-for-bit with an axis normal; the builder keeps SymmetryX()/Y()/Z() as sugar that emits it. The ids stay
    // retired (the ISA numbering is non-sequential — never reuse them).
    /// <summary>Shells the entire field accumulated so far: <c>d = abs(d) − thickness</c> (Data0.x = thickness) turns
    /// solids into hollow skins. A field op, not a point op — order objects so it follows everything it should shell.</summary>
    Onion = 16,
    /// <summary>Inflates the entire field accumulated so far by a radius: <c>d −= radius</c> (Data0.x = radius) rounds
    /// and fattens everything before it. A field operation, not a point operation.</summary>
    Dilate = 17,
    /// <summary>Folds the evaluation point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry
    /// group (all 17 IUC groups; square/rectangular lattices plus the equilateral hex lattice for P3 and up). The
    /// lattice reduction is <see cref="RepeatLimited"/> restricted to two axes (P1 is bit-identical to it); the
    /// per-cell stage composes mirrors/rotations keyed on the lattice parity. Every branch is an isometry, so
    /// distances are preserved. Instruction lanes: Shape = <see cref="SdfWallpaperGroup"/>, Blend =
    /// <see cref="SdfWallpaperPlane"/>, Material = the parity-material stride (the cell key — checker parity or hex
    /// 3-coloring — strides the material id of later shape wins in the chain; 0 keeps the fold purely geometric).
    /// Data0.xy = cell extents (hex: pitch = x, y must equal it), Data1.xy = RepeatLimited-style cell limits,
    /// Data1.z = the symmetry-LOD distance threshold (0 = off): past it the lattice keeps its copies but the in-cell
    /// folds are skipped — upright copies, cheaper and shimmer-free at range.</summary>
    WallpaperFold = 18,
    /// <summary>Twists space about the local Y axis: the XZ plane rotates by <c>rate * y</c> radians (Data0.x = rate).
    /// It is not an isometry; keep rates moderate.</summary>
    TwistY = 20,
    /// <summary>Log-spherical domain warp: tiles space into infinite self-similar "Droste" shells by folding the
    /// radial log-coordinate to the nearest shell — a translation along <c>log(radius)</c> becomes a uniform scaling
    /// in Cartesian space, so one authored prototype shell repeats outward/inward as scaled copies from a handful of
    /// instructions. Data0.x = w (the log cell size = <c>ln(shellRatio)</c>, host-baked); Data0.y = twist (radians of
    /// per-shell Z-spin — the isometric Droste spiral; 0 = concentric shells); Data0.z = 1/w (host-baked reciprocal).
    /// Not an isometry (space scales by ~r), so the runtime folds the r/density correction into the per-candidate
    /// <c>distanceScale</c> (exactly like <see cref="Scale"/>) and <c>AnalyzeLipschitz</c> bakes a conservative
    /// <c>exp(w/2)</c> step clamp so the over-relaxed march cannot tunnel through a shell boundary. Folds only the
    /// radial coordinate (theta/phi are preserved, so there is no polar pinching); like <see cref="Repeat"/>, prototype
    /// content should respect the shell cell (radii within a factor of <c>shellRatio</c>).</summary>
    LogSphere = 21,
    /// <summary>Stochastic domain-repeat fold: tiles space into cells like <see cref="Repeat"/>, then per cell applies a
    /// hashed position displacement, an optional hashed orientation ("tumble"), and an optional hashed material variant —
    /// scattering one prototype into a jittered field from a single instruction. Both the displacement and the tumble are
    /// isometries (translate + rotate), so distances are preserved: the field stays 1-Lipschitz, no cull bound changes,
    /// and <c>AnalyzeLipschitz</c> needs only one conservative reach line (the jitter half-amplitude). Instruction lanes:
    /// Shape = seed (uint), Blend = <see cref="SdfNoiseFlavor"/> (how the position offset is distributed — White = 0 the
    /// byte-identical default, Blue, Gaussian; tumble and material variant are unaffected, and every flavor shares the same
    /// offset bound so no Lipschitz change), Material = materialVariants (0 = geometric only). Data0.xyz = spacing
    /// (world units/axis), Data0.w = jitter (peak-to-peak position displacement), Data1.xyz = 1/spacing (host-baked, like
    /// <see cref="Repeat"/>), Data1.w = tumble in [0,1] (0 = no rotation; 1 = up to ±π about a random axis). jitter,
    /// tumble, and materialVariants each default to an exact identity (amplitude 0 / count 0), so an unused-and-zeroed
    /// instruction leaves the point byte-identical on both backends. The per-cell hash is integer-only (canonical PCG3D
    /// on the two's-complement cell index xored with the seed), so it is bit-identical across DXC's SPIR-V and DXIL
    /// targets — only the final uint→float and the tumble trig carry the usual ±1 LSB warp noise. Like <see cref="Repeat"/>,
    /// keep the in-cell content clear of the <c>round()</c> boundary: jitter/2 + prototype radius ≤ min(spacing)/2.</summary>
    CellJitter = 22,
    /// <summary>Angular domain-repeat fold: folds the plane perpendicular to a chosen axis into <c>count</c> equal
    /// sectors, so one authored prototype repeats rotationally around the axis (gears, wheels, columns of a rotunda,
    /// clock ticks, flower petals) from a single instruction — the rotational sibling of the linear <see cref="Repeat"/>
    /// and the lattice <see cref="WallpaperFold"/>. The fold is a rotation into the base sector (and, when the mirror
    /// flag is set, a reflection of each sector across its bisector — the kaleidoscope fold): both branches are
    /// isometries, so distances are preserved — the field stays 1-Lipschitz (factor 1, no step clamp, exactly like
    /// <see cref="Repeat"/>) and no cull bound changes. Instruction lanes: Shape = <see cref="SdfPolarAxis"/> (the
    /// rotation axis), Blend = the mirror flag (0 = plain repeat, 1 = kaleidoscope), Material = the per-sector palette
    /// stride (the sector index 0..count-1 strides the material id of a later shape win; 0 keeps the fold purely
    /// geometric). Data0.x = the sector angle <c>2π/count</c> (host-baked), Data0.y = <c>count/(2π)</c> = 1/angle
    /// (host-baked), Data0.z = count, Data0.w = 1/count (host-baked); Data1 is reserved. The fold uses <c>atan2</c>/
    /// <c>floor</c> (floats), so a point exactly on a sector seam carries the usual ±1 LSB warp noise (geometry only;
    /// the optional per-sector material can flip at a seam exactly as <see cref="WallpaperFold"/>'s can). Like
    /// <see cref="Repeat"/>, keep the prototype clear of the sector walls (the two radial half-planes through the axis)
    /// — content that overspills a wall is clipped by the neighbouring sector.</summary>
    RepeatPolar = 23,
    /// <summary>Adds a bounded sinusoidal displacement to the field accumulated so far — surface relief (bumps,
    /// corrugation, a rippled skin), the SDF-native answer to height/parallax mapping (the relief is real geometry, so
    /// it shadows and self-occludes correctly). A field op (like <see cref="Onion"/>/<see cref="Dilate"/>), evaluated at
    /// the current folded point: order it after the shapes it should displace. Data0.xyz = per-axis angular frequency,
    /// Data0.w = amplitude; the basis is the separable product <c>amp·sin(fx·x)·sin(fy·y)·sin(fz·z)</c> (deterministic
    /// float trig — ±1 LSB like the twist/bend warps, cross-backend-parity-safe without a hashed noise table). Not
    /// 1-Lipschitz: the added relief's gradient reaches <c>amp·‖freq‖</c>, so the field can overestimate true distance by
    /// up to <c>1 + amp·‖freq‖</c> — <c>AnalyzeLipschitz</c> bakes that as a conservative step clamp (reach-independent,
    /// folded like the log-spherical product). amp = 0 is an exact identity (byte-identical).</summary>
    Displace = 24,
    /// <summary>Warps the sample point by a bounded, cross-coupled sinusoidal field before the shapes evaluate — organic
    /// bulging / wobble / terrain. A point op (like the fold ops). Data0.xyz = per-axis angular frequency, Data0.w =
    /// amplitude; the point moves by <c>amp·(sin(fx·y), sin(fy·z), sin(fz·x))</c> — cross-coupled (each axis driven by the
    /// next axis's coordinate) so the warp is non-separable, and deterministic float trig (±1 LSB). Not an isometry: the
    /// Jacobian is <c>I</c> plus a perturbation of spectral norm ≤ <c>amp·‖freq‖</c>, so the metric stretches by up to
    /// <c>1 + amp·‖freq‖</c> — <c>AnalyzeLipschitz</c> bakes that step clamp (reach-independent) and folds the point's max
    /// travel (<c>amp·√3</c>) into a downstream twist/bend's reach. amp = 0 is an exact identity (byte-identical).</summary>
    DomainWarp = 25,
    /// <summary>Reflection fold across an arbitrary plane — the general-normal superset of the axis-aligned
    /// <c>SymmetryX</c>/<c>SymmetryY</c>/<c>SymmetryZ</c> builder methods:
    /// everything on the plane's negative side
    /// is mirrored onto its positive side, so one authored half repeats mirror-imaged (a kaleidoscope leaf, a bilateral
    /// body, the reflect atom of a KIFS fold). Data0.xyz = the unit plane normal (host-normalized), Data0.w = the plane
    /// offset; the fold is <c>p -= 2·min(dot(p, n) + offset, 0)·n</c> — for <c>n = x̂, offset = 0</c> this is
    /// <c>abs(p.x)</c> to the bit, so it is an exact superset of <c>SymmetryX</c>. A reflection is an isometry,
    /// so distances are preserved: the field stays 1-Lipschitz (factor 1, no step clamp) and no cull bound changes. Like
    /// the axis symmetries, keep authored content on the plane's positive side (the kept half); content straddling the
    /// plane is folded onto itself.</summary>
    SymmetryPlane = 26,
    /// <summary>Opens a scoped field accumulator — the first half of the <see cref="PushField"/>/<see cref="PopField"/>
    /// pair (<see cref="SdfProgramBuilder.PushField"/>). Saves the running nearest-surface distance into a one-deep slot
    /// and reseeds a fresh accumulator (<c>SDF_FAR_DISTANCE</c>), so every accumulator-reading op emitted until the
    /// matching <see cref="PopField"/> — the intersection family, and the <see cref="Onion"/>/<see cref="Dilate"/>/
    /// <see cref="Displace"/> field ops — acts on this scope's field alone, not on everything emitted before it. That is
    /// the whole cure for the flat accumulator's "a field op shells the entire scene" bug: an <c>Onion</c> inside a scope
    /// shells only the scope's own shapes. A scope touches the field, never the evaluation point (localPosition /
    /// distanceScale / the wallpaper material delta), so <see cref="ResetPoint"/> semantics are unchanged and per-shape
    /// cull bounds after the Push in the same chain stay sound. Depth is capped at
    /// <see cref="SdfProgramBuilder.MaxFieldScopeDepth"/> (a shader-constant + validator rule, not part of the packed
    /// layout). Op-unused (scope-free) programs are byte-identical. KEEP IN SYNC with SDF_OP_PUSH_FIELD in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    PushField = 27,
    /// <summary>Closes the scope opened by the matching <see cref="PushField"/> and composes the scope's accumulated
    /// field back into the saved parent accumulator as a single candidate — reusing the shape blend tail (a pop is just
    /// another candidate), so it costs no second copy of the blend switch. The compose blend + smooth radius are carried
    /// on the pop instruction (Blend lane + Data1.x, <see cref="SdfProgramBuilder.PushField"/>'s arguments): a Union
    /// compose makes the whole scope far-neutral (and so maskable / segment-eligible again — the culling payoff), while an
    /// intersection-family compose composes the scope globally (unmaskable, exactly like an unscoped intersection). The
    /// scope's candidate is already in world units (its shapes were distance-scaled as they blended in) and must not be
    /// re-scaled or take the point material delta (the fusion trap). Its material tie-break matches shape's (strict
    /// compare — the parent keeps its material on a tie). A chamfer compose is the one non-1-Lipschitz case: it folds a
    /// conservative √2 into <see cref="SdfProgram.StepScale"/>. KEEP IN SYNC with SDF_OP_POP_FIELD in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    PopField = 28,
}
