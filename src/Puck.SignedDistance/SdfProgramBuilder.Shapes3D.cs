using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    public SdfProgramBuilder Box(Vector3 halfExtents, float round, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `q = abs(p) - (halfExtents - round); length(max(q, 0)) + min(max(q), 0) - round`. A negative
        // half-extent turns the box inside out, and a negative round is a corner INSET the shape has no spelling for —
        // RoundedRectangle, the 2D sibling, already clamps its corner radius to [0, min(half-extents)], which is the
        // file's settled position that a corner radius is non-negative. A round LARGER than a half-extent stays legal:
        // TryGetLocalBound deliberately adds it as bound slack "against degenerate authoring".
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A box half-extent"
        );
        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A box corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "box"
        );

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: material,
            shape: SdfShapeType.Box,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Capsule(Vector3 endpoint, float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The endpoint is a signed local-space offset (the segment's far end), so only finiteness is refused; the
        // radius closes the segment into a capsule exactly as Sphere's does — `length(...) - radius`.
        RequireFinite(
            value: endpoint,
            paramName: nameof(endpoint),
            subject: "A capsule endpoint"
        );
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A capsule radius"
        );

        // The endpoint's raw components are each individually finite, but dot(endpoint, endpoint) is not: a component
        // near float's ~1.84e19 sqrt-of-max threshold squares past float.MaxValue and overflows to +Infinity — baking
        // a silent reciprocal ZERO into derived1 below (poisoning the capsule's own distance field) while
        // SdfProgram.TryGetLocalBound's endpoint.Length() (the SAME dot, square-rooted) derives an INFINITE cull
        // bound. Refuse rather than clamp — a clamped dot would silently shorten the capsule to some other length
        // than authored.
        var dotEndpoint = Vector3.Dot(
            vector1: endpoint,
            vector2: endpoint
        );

        if (!float.IsFinite(f: dotEndpoint)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(endpoint),
                message: $"A capsule endpoint {endpoint}'s derived dot(endpoint, endpoint) is not finite. Narrow the endpoint."
            );
        }

        return Shape(
            blend: blend,
            // Data1.y carries the HOST-BAKED 1/dot(endpoint, endpoint): shapes evaluate millions of times per frame
            // while programs build once, and the shared multiply keeps both backends' shader codegen identical where a
            // per-eval divide contracted differently (KEEP IN SYNC with sdfCapsule in Assets/Shaders/Sdf/sdf-vm.hlsli).
            derived1: (1f / MathF.Max(
                x: dotEndpoint,
                y: 0.0001f
            )),
            dimensions: new Vector4(
                value: endpoint,
                w: radius
            ),
            material: material,
            shape: SdfShapeType.Capsule,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Cylinder(float radius, float halfHeight, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder subtracts both from magnitudes — `float2(length(p.xz), abs(p.y)) - float2(radius, halfHeight)` —
        // so a negative one leaves that axis with no surface, and the cull bound reads them as a right triangle's legs.
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A cylinder radius"
        );
        RequireNonNegative(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A cylinder half-height"
        );

        // Both legs are individually finite, but SdfProgram.TryGetLocalBound's Cylinder cull bound is
        // sqrt(radius² + halfHeight²) — the same dot-product-shaped overflow Capsule's endpoint has: either leg
        // squared can overflow past float.MaxValue well before the leg itself does.
        if (!float.IsFinite(f: MathF.Sqrt(x: ((radius * radius) + (halfHeight * halfHeight))))) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(halfHeight),
                message: $"A cylinder's derived bound radius (from radius {radius} and halfHeight {halfHeight}) is not finite. Narrow one or both dimensions."
            );
        }

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: radius,
                y: halfHeight,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Cylinder,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Ellipsoid(Vector3 radii, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The sign is absorbed by the Vector3.Abs clamp below (and the 1e-4 floor keeps the reciprocal finite), so
        // only NaN/infinity — which neither absorbs — are refused.
        RequireFinite(
            value: radii,
            paramName: nameof(radii),
            subject: "An ellipsoid radius"
        );

        // The degenerate-radius clamp and inverse radii are HOST-BAKED (Data1.yzw) to avoid two vector divides per
        // evaluation (KEEP IN SYNC with sdfEllipsoid in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(
            value1: Vector3.Abs(value: radii),
            value2: new Vector3(value: 0.0001f)
        );
        var inverse = (Vector3.One / clamped);

        return Shape(
            blend: blend,
            derived1: inverse.X,
            derived2: inverse.Y,
            derived3: inverse.Z,
            dimensions: new Vector4(
                value: clamped,
                w: 0f
            ),
            material: material,
            shape: SdfShapeType.Ellipsoid,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Plane(Vector3 normal, float offset, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Normalized host-side, so a zero normal packs NaN; the offset is signed by construction (it slides the plane).
        RequireDirection(
            value: normal,
            paramName: nameof(normal),
            subject: "A plane normal"
        );
        RequireFinite(
            value: offset,
            paramName: nameof(offset),
            subject: "A plane offset"
        );

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: Vector3.Normalize(value: normal),
                w: offset
            ),
            material: material,
            shape: SdfShapeType.Plane,
            smooth: smooth
        );
    }
    public SdfProgramBuilder RoundCone(float lowerRadius, float upperRadius, float height, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Both radii are sphere radii in the decoder (`length(q) - lowerRadius`, `length(q - (0, height)) - upperRadius`)
        // — the same argument as Sphere. The height must be non-negative because the slope below is baked against
        // MathF.Max(height, 0.0001f) while the decoder places the top cap at the RAW +height: a negative height puts
        // the cap below the origin with a slope constant computed for a positive one, so the two disagree.
        RequireNonNegative(
            value: lowerRadius,
            paramName: nameof(lowerRadius),
            subject: "A round-cone lower radius"
        );
        RequireNonNegative(
            value: upperRadius,
            paramName: nameof(upperRadius),
            subject: "A round-cone upper radius"
        );
        RequireNonNegative(
            value: height,
            paramName: nameof(height),
            subject: "A round-cone height"
        );

        // The slope terms are HOST-BAKED (Data0.w = b, Data1.y = a) to avoid a divide and square root per evaluation
        // (KEEP IN SYNC with sdfRoundCone in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var slope = ((lowerRadius - upperRadius) / MathF.Max(
            x: height,
            y: 0.0001f
        ));

        // The three raw inputs are each individually finite (RequireNonNegative above already refuses NaN/infinity),
        // but their RATIO is not: a huge radius difference over a near-zero height overflows a finite numerator by a
        // finite (floored) denominator into +/-Infinity, which the shape method above cannot see or clamp — it packs
        // straight into Data0.w and poisons derived1 and the program-wide Lipschitz step scale. Refuse rather than
        // clamp: a clamped slope would silently cone the shape at some other angle than authored.
        if (!float.IsFinite(f: slope)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(height),
                message: $"A round-cone's derived slope (lowerRadius {lowerRadius} minus upperRadius {upperRadius}, divided by height {height}) is not finite. Raise the height or narrow the radius difference."
            );
        }

        // A SECOND, independent derived value: SdfProgram.TryGetLocalBound's RoundCone cull bound is
        // |height/2| + max(lowerRadius, upperRadius) — the same sum-overflow class as Torus's radii, reachable even
        // when the slope above stays finite (equal enormous radii keep the slope's ratio at 0, but this sum still
        // overflows).
        var boundRadius = (MathF.Abs(x: (height * 0.5f)) + MathF.Max(
            x: lowerRadius,
            y: upperRadius
        ));

        if (!float.IsFinite(f: boundRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(height),
                message: $"A round-cone's derived bound radius (half-height {(height * 0.5f)} plus the larger end radius {MathF.Max(
                    x: lowerRadius,
                    y: upperRadius
                )}) is not finite. Narrow the radii or the height."
            );
        }

        return Shape(
            blend: blend,
            derived1: MathF.Sqrt(x: MathF.Max(
                x: (1f - (slope * slope)),
                y: 0f
            )),
            dimensions: new Vector4(
                w: slope,
                x: lowerRadius,
                y: upperRadius,
                z: height
            ),
            material: material,
            shape: SdfShapeType.RoundCone,
            smooth: smooth
        );
    }
    /// <summary>Adds a sampled distance-field brick (<see cref="SdfShapeType.SampledRegion"/>) — the settled-carve union field,
    /// pre-baked into a <paramref name="dimX"/>x<paramref name="dimY"/>x<paramref name="dimZ"/> cubic-voxel lattice at
    /// <paramref name="brickWordOffset"/> in the engine's <c>sdfBrickPool</c> buffer, sampled O(1) by manual trilinear
    /// interpolation and composed as one ordinary <see cref="SdfBlendOp.Subtraction"/> instance. The distance channel is
    /// pre-scaled c/λ (λ folded in at bake time, so this op applies no step clamp), and <paramref name="boundaryFloor"/>
    /// (= margin/λ, host-baked) is the outside-box lower-bound offset. Where the pool is not bound the shape falls back to
    /// the conservative union hull (SDF_FAR_DISTANCE — the subtraction never bites), so a brick program renders uncarved
    /// but never holes. The lane packing (Data0 = boxMin.xyz + cellSize; Data1 = smooth + packedDims + brickWordOffset +
    /// boundaryFloor) is KEEP IN SYNC with sdfSampledRegion in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="boxMin">The brick box's minimum corner in the chain's local space (voxel (0,0,0)'s cell origin).</param>
    /// <param name="cellSize">The cubic voxel edge (world units per voxel); must be finite and greater than zero.</param>
    /// <param name="dimX">Voxel count along local X, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="dimY">Voxel count along local Y, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="dimZ">Voxel count along local Z, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="brickWordOffset">The brick's base word index in the pool buffer (from the planner's slot layout); ≥ 0.</param>
    /// <param name="boundaryFloor">The host-baked outside-box lower-bound offset (margin/λ); finite and ≥ 0.</param>
    /// <param name="material">The material id the carved region shades with (unused where subtraction only removes).</param>
    /// <param name="blend">The compose against the accumulated field; <see cref="SdfBlendOp.Subtraction"/> by default (a
    /// brick carves). Smooth and chamfered carves remain analytic and must not use this sampled representation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boxMin"/> is not finite, a dim is out of range,
    /// <paramref name="cellSize"/> is not positive and finite, <paramref name="brickWordOffset"/> is negative,
    /// <paramref name="boundaryFloor"/> is negative or not finite, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder SampledRegion(Vector3 boxMin, float cellSize, int dimX, int dimY, int dimZ, int brickWordOffset, float boundaryFloor, int material, SdfBlendOp blend = SdfBlendOp.Subtraction) {
        // The one lane this method did not check: the box corner is signed by construction (it is a position) but
        // still reaches Data0.xyz raw, and TryGetLocalBound derives the brick's whole cull sphere from it.
        RequireFinite(
            value: boxMin,
            paramName: nameof(boxMin),
            subject: "A sampled-region box corner"
        );

        if (
            !float.IsFinite(f: cellSize) ||
            (cellSize <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cellSize),
                message: "A sampled-region cell size must be finite and greater than zero."
            );
        }

        if (
            (dimX < 1) ||
            (dimX > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimX),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (
            (dimY < 1) ||
            (dimY > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimY),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (
            (dimZ < 1) ||
            (dimZ > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimZ),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (brickWordOffset < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(brickWordOffset),
                message: "A sampled-region brick word offset must be non-negative."
            );
        }

        if (
            !float.IsFinite(f: boundaryFloor) ||
            (boundaryFloor < 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(boundaryFloor),
                message: "A sampled-region boundary floor must be finite and non-negative."
            );
        }

        // 3x10-bit dim pack; the two uint bit-fields (packedDims, brickWordOffset) ride the float lanes as reinterpreted
        // bits (like Glyph's PackUv) and round-trip exactly through SdfProgram's WriteVector4 — no arithmetic touches them.
        var packedDims = ((uint)dimX) | (((uint)dimY) << 10) | (((uint)dimZ) << 20);

        // Every dim is capped at MaxSampledRegionDim (1023) and cellSize is required positive and finite, but the
        // PRODUCT SdfProgram.TryGetLocalBound derives from them (dims * cellSize, the brick box's extent, feeding its
        // circumsphere radius) can still overflow float.MaxValue for a large-enough finite cellSize even though every
        // input independently passed its own check — the same overflow class Box/Cylinder/Torus/RoundCone/Capsule
        // refuse below.
        var extent = (new Vector3(
            x: dimX,
            y: dimY,
            z: dimZ
        ) * cellSize);

        if (!float.IsFinite(f: extent.Length())) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cellSize),
                message: $"A sampled-region's derived box extent (dims {dimX}x{dimY}x{dimZ} at cellSize {cellSize}) is not finite. Narrow the cell size or the dimensions."
            );
        }

        return Shape(
            blend: blend,
            derived1: BitConverter.UInt32BitsToSingle(value: packedDims),                // Data1.y = packedDims (uint bits)
            derived2: BitConverter.UInt32BitsToSingle(value: ((uint)brickWordOffset)),      // Data1.z = brickWordOffset (uint bits)
            derived3: boundaryFloor,                                                      // Data1.w = boundaryFloor
            dimensions: new Vector4(
                w: cellSize,       // Data0.w = cellSize
                x: boxMin.X,       // Data0.xyz = box min corner
                y: boxMin.Y,
                z: boxMin.Z
            ),
            material: material,
            shape: SdfShapeType.SampledRegion,
            smooth: 0f             // Data1.x = smooth: a brick composes with HARD subtraction (smooth carves stay analytic)
        );
    }
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // A screen slab IS a rounded box (sdfBox decodes both shape types), so it carries Box's argument contract.
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A screen-slab half-extent"
        );
        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A screen-slab corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "screen-slab"
        );

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: ScreenMaterialId,
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    /// <summary>Adds a screen slab whose lit face samples a bound screen source (see
    /// <c>Puck.SdfVm.SdfWorldEngine.SetScreenSource</c>) instead of the flat screen material, when one is bound this
    /// frame — a diegetic screen (an emulator's framebuffer, e.g.) on static geometry. The slab's shape/distance field
    /// is identical to the plain overload (a rounded box); only shading differs. The world-space frame maps a hit
    /// point to the slab's <c>[0,1]²</c> UV: <paramref name="worldRight"/>/<paramref name="worldUp"/> must be unit and
    /// orthogonal to each other and to the slab's local Z (its front-face normal), and should match the rigid
    /// transform (<see cref="Translate"/>/<see cref="Rotate(Quaternion)"/>) already applied to the point when this shape is
    /// declared — a mismatched frame sizes/rotates the sampled image wrong without affecting the geometry at all.</summary>
    /// <param name="halfExtents">The slab's local half-extents (as <see cref="ScreenSlab(Vector3, float, SdfBlendOp, float)"/>).</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldRight">The unit world-space axis the UV's U increases along (the slab's local +X, in world space).</param>
    /// <param name="worldUp">The unit world-space axis the UV's V increases against — V = 0 at the top (the slab's local +Y, in world space).</param>
    /// <param name="screenIndex">The screen source slot in the range 0 through <see cref="MaxScreenSurfaces"/> − 1.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius (meaningful only for a smooth <paramref name="blend"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside the supported range, this
    /// program has already declared <see cref="MaxScreenSurfaces"/> screen surfaces, a slab dimension is not finite and
    /// non-negative, the sampled face's X or Y half-extent is not positive, <paramref name="worldOrigin"/> is not
    /// finite, <paramref name="worldRight"/>/
    /// <paramref name="worldUp"/> is not finite or has zero length, <paramref name="worldRight"/> and
    /// <paramref name="worldUp"/> are not orthogonal, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Vector3 worldRight, Vector3 worldUp, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The slab geometry carries Box's contract; the UV frame's two axes are normalized host-side into the screen
        // surface record, so a zero axis would map every hit point to a NaN UV.
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A screen-slab half-extent"
        );

        // The face's X/Y half-extents become the surface frame's half-width/half-height, and sampleScreenSurface
        // resolves the UV by DIVIDING the hit's projection by each: a zero face maps every hit to a non-finite UV. The
        // slab's Z (depth) half-extent is unconstrained — nothing divides by it. KEEP IN SYNC with the SdfProgram
        // constructor's screen-extent refusal, which names the packed surface.
        if (
            !(halfExtents.X > 0f) ||
            !(halfExtents.Y > 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(halfExtents),
                message: $"An indexed screen slab's X and Y half-extents must be positive; got {halfExtents.X} and {halfExtents.Y}. They are the sampled face's half-width and half-height, which the shader divides the hit's projection by."
            );
        }

        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A screen-slab corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "screen-slab"
        );
        RequireFinite(
            value: worldOrigin,
            paramName: nameof(worldOrigin),
            subject: "A screen-slab world origin"
        );
        RequireDirection(
            value: worldRight,
            paramName: nameof(worldRight),
            subject: "A screen-slab world right axis"
        );
        RequireDirection(
            value: worldUp,
            paramName: nameof(worldUp),
            subject: "A screen-slab world up axis"
        );

        // Orthogonality subsumes the parallel case (a parallel pair has |dot| = 1) and is what the packed frame needs:
        // the UV projects onto these axes while the slab's geometry rides the rotation derived from them.
        RequireOrthogonalBasis(
            paramName: nameof(worldUp),
            right: worldRight,
            subject: "A screen-slab world right and up axis",
            up: worldUp
        );

        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

        if (m_screenSurfaces.Count >= MaxScreenSurfaces) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A program may declare at most {MaxScreenSurfaces} screen surfaces."
            );
        }

        m_screenSurfaces.Add(item: new SdfScreenSurface(
            HalfHeight: halfExtents.Y,
            HalfWidth: halfExtents.X,
            Origin: worldOrigin,
            Right: Vector3.Normalize(value: worldRight),
            ScreenIndex: screenIndex,
            Up: Vector3.Normalize(value: worldUp)
        ));

        // The screen-instance sentinel: ScreenMaterialId flags "screen shading" (as the flat-material overload), the
        // +1+screenIndex offset tells the shader WHICH declared surface (and thus which screen source) a hit belongs
        // to — decoded back by subtracting the same offset (KEEP IN SYNC with sdf-world.hlsli's screen shading).
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: ((ScreenMaterialId + 1) + screenIndex),
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    /// <summary>Adds a <see cref="ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>
    /// screen slab that derives the screen's world-space right/up axes from the slab's static orientation.</summary>
    /// <param name="halfExtents">The slab's local half-extents.</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldOrientation">The static slab orientation in world space.</param>
    /// <param name="screenIndex">The screen source slot in the range 0 through <see cref="MaxScreenSurfaces"/> − 1.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldOrientation"/> is not finite or has zero
    /// length, or the overload this forwards to refuses an argument.</exception>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Quaternion worldOrientation, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Checked HERE rather than left to the forwarded overload: a non-unit orientation would have produced two
        // zero axes, and the refusal would then have named worldRight instead of the argument the caller supplied.
        RequireRotation(
            value: worldOrientation,
            paramName: nameof(worldOrientation),
            subject: "A screen-slab orientation"
        );

        var unit = Quaternion.Normalize(value: worldOrientation);

        return ScreenSlab(
            blend: blend,
            halfExtents: halfExtents,
            round: round,
            screenIndex: screenIndex,
            smooth: smooth,
            worldOrigin: worldOrigin,
            worldRight: Vector3.Transform(
                rotation: unit,
                value: Vector3.UnitX
            ),
            worldUp: Vector3.Transform(
                rotation: unit,
                value: Vector3.UnitY
            )
        );
    }
    /// <summary>Adds a sphere centered at the current local point.</summary>
    /// <param name="radius">The sphere radius.</param>
    /// <param name="material">The material identifier.</param>
    /// <param name="blend">The blend against the accumulated field.</param>
    /// <param name="smooth">The radius used by smooth and chamfer blends.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not finite and non-negative, or
    /// <paramref name="material"/> is negative, <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Sphere(float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `length(p) - radius`, so a negative radius leaves the field strictly positive: the sphere has
        // no surface at all, while the program still spends an instruction, a cull bound (TryGetLocalBound packs
        // MathF.Abs of this very lane) and a Lipschitz reach on it. Zero is allowed — a degenerate point.
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A sphere radius"
        );

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: radius,
                y: 0f,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Sphere,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Torus(float majorRadius, float minorRadius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `length(float2(length(p.xz) - major, p.y)) - minor`: both are radii of the revolved circle,
        // and TryGetLocalBound packs MathF.Abs of each, so a negative one both mis-shapes the ring and desynchronizes
        // the shape from its own cull bound.
        RequireNonNegative(
            value: majorRadius,
            paramName: nameof(majorRadius),
            subject: "A torus major radius"
        );
        RequireNonNegative(
            value: minorRadius,
            paramName: nameof(minorRadius),
            subject: "A torus minor radius"
        );

        // Both radii are individually finite, but SdfProgram.TryGetLocalBound's Torus cull bound is their SUM (the
        // ring's farthest reach from the local origin) — two radii each well under float.MaxValue can still sum past
        // it into +Infinity, handing the packer (and any segment it merges with) an infinite bound that was never
        // authored. Refuse here, at the shape that owns the radii, rather than at the analysis pass that discovers it.
        var boundRadius = (majorRadius + minorRadius);

        if (!float.IsFinite(f: boundRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(minorRadius),
                message: $"A torus's derived bound radius (majorRadius {majorRadius} plus minorRadius {minorRadius}) is not finite. Narrow one or both radii."
            );
        }

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: majorRadius,
                y: minorRadius,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Torus,
            smooth: smooth
        );
    }
    /// <summary>Adds a vesica (lens) — the intersection of two spheres of radius <paramref name="radius"/> whose centers are
    /// 2·<paramref name="halfSeparation"/> apart — revolved into a 3D lens pointed along ±Y (a disc of radius
    /// radius−halfSeparation in XZ). <paramref name="halfSeparation"/> is clamped below <paramref name="radius"/> so
    /// the tip half-height √(r²−d²) is real; it is host-baked (skips the per-eval sqrt) — KEEP IN SYNC with sdfVesica
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="radius">The two generating spheres' radius.</param>
    /// <param name="halfSeparation">Half the distance between their centres (clamped below <paramref name="radius"/>).</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="halfSeparation"/> is
    /// not finite, the derived tip half-height (see remarks) is not finite, <paramref name="material"/> is negative,
    /// <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder Vesica(float radius, float halfSeparation, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Both signs are absorbed by the MathF.Abs pair below, so only finiteness is refused.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A vesica radius"
        );
        RequireFinite(
            value: halfSeparation,
            paramName: nameof(halfSeparation),
            subject: "A vesica half-separation"
        );

        var r = MathF.Abs(x: radius);
        var d = MathF.Min(
            x: MathF.Abs(x: halfSeparation),
            y: (r * 0.9999f)
        ); // d < r keeps b = √(r²−d²) real and positive
        var b = MathF.Sqrt(x: ((r * r) - (d * d)));

        // r and d are each individually finite, but r*r (and d*d) can overflow past float.MaxValue for a large enough
        // radius even though r itself does not — at radius == halfSeparation == float.MaxValue both squares overflow
        // to +Infinity and their difference is +Infinity − +Infinity = NaN, which sqrt propagates. b is HOST-BAKED
        // straight into Data0.z (the shape's own tip half-height) AND is what SdfProgram.TryGetLocalBound reads back
        // as part of the shape's cull radius — refuse rather than clamp, the same overflow class RoundCone's slope
        // check exists for.
        if (!float.IsFinite(f: b)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radius),
                message: $"A vesica's derived tip half-height (from radius {radius} and half-separation {halfSeparation}) is not finite. Narrow the radius or the half-separation."
            );
        }

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: r,
                y: d,
                z: b
            ),
            material: material,
            shape: SdfShapeType.Vesica,
            smooth: smooth
        );
    }
}
