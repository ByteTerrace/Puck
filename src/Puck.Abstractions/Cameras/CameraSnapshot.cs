using System.Numerics;
using Puck.Maths;

namespace Puck.Abstractions.Cameras;

/// <summary>An immutable, finite camera basis and projection snapshot derived by <see cref="LookAt"/>.</summary>
public readonly record struct CameraSnapshot {
    private readonly Vector2 m_frustumOffset;
    private readonly float m_near;

    /// <summary>Initializes a finite camera snapshot from an already-derived basis and projection.</summary>
    public CameraSnapshot(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float TanHalfFieldOfView, float AspectRatio) {
        ValidateFinite(
            Position,
            nameof(Position)
        );
        ValidateFinite(
            Right,
            nameof(Right)
        );
        ValidateFinite(
            Up,
            nameof(Up)
        );
        ValidateFinite(
            Forward,
            nameof(Forward)
        );
        ValidateBasis(
            Right,
            nameof(Right)
        );
        ValidateBasis(
            Up,
            nameof(Up)
        );
        ValidateBasis(
            Forward,
            nameof(Forward)
        );

        if (
            (MathF.Abs(x: Vector3.Dot(
            vector1: Right,
            vector2: Up
        )) > 1e-3f) ||
            (MathF.Abs(x: Vector3.Dot(
            vector1: Right,
            vector2: Forward
        )) > 1e-3f) ||
            (MathF.Abs(x: Vector3.Dot(
            vector1: Up,
            vector2: Forward
        )) > 1e-3f)
        ) {
            throw new ArgumentException(message: "The camera basis vectors must be mutually perpendicular.");
        }

        if (
            !float.IsFinite(f: TanHalfFieldOfView) ||
            (TanHalfFieldOfView <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(TanHalfFieldOfView));
        }
        if (
            !float.IsFinite(f: AspectRatio) ||
            (AspectRatio <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(AspectRatio));
        }

        this.Position = Position;
        this.Right = Right;
        this.Up = Up;
        this.Forward = Forward;
        this.TanHalfFieldOfView = TanHalfFieldOfView;
        this.AspectRatio = AspectRatio;
    }

    /// <summary>Gets the viewport width-to-height ratio.</summary>
    public float AspectRatio { get; }
    /// <summary>Gets the normalized camera-forward basis vector.</summary>
    public Vector3 Forward { get; }
    /// <summary>Gets the off-axis frustum's tangent-space center offset: the ray through every image point gains
    /// <c>X·Right + Y·Up</c> as a trailing term, so the frustum shears without turning. Zero, the default, is a symmetric
    /// camera. A border window's camera (<c>Puck.SdfVm.Views.SdfAsymmetricFrustum</c>) carries a non-zero offset, which
    /// the SDF view pass's <c>cameraRayDirection</c>, the rasterized projection (<see cref="ViewProjection"/>) and a hit
    /// through the camera's image (<c>Puck.Commands.SourceRay.Through</c>) all read from here.</summary>
    /// <exception cref="ArgumentException">A component set at initialization is not finite.</exception>
    public Vector2 FrustumOffset {
        get => m_frustumOffset;
        init {
            if (!VectorFunctions.IsFinite(vector: value)) {
                throw new ArgumentException(
                    message: "The frustum offset must be finite.",
                    paramName: nameof(FrustumOffset)
                );
            }

            m_frustumOffset = value;
        }
    }
    /// <summary>Gets the forward distance of the camera's near plane: the image begins there, so nothing nearer along
    /// <see cref="Forward"/> is seen or hit. Zero, the default, begins the image at <see cref="Position"/>. A border
    /// window's camera (<c>Puck.SdfVm.Views.SdfAsymmetricFrustum</c>) puts it on the glass, so the window shows only
    /// what lies beyond its aperture; the SDF view pass starts its march there and a hit through the camera's image
    /// (<c>Puck.Commands.SourceRay.Through</c>) starts its ray there.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The distance set at initialization is negative or not
    /// finite.</exception>
    public float Near {
        get => m_near;
        init {
            if (
                !float.IsFinite(f: value) ||
                (value < 0f)
            ) {
                throw new ArgumentOutOfRangeException(
                    actualValue: value,
                    message: "The near-plane distance must be finite and not negative.",
                    paramName: nameof(Near)
                );
            }

            m_near = value;
        }
    }
    /// <summary>Gets the world-space camera position.</summary>
    public Vector3 Position { get; }
    /// <summary>Gets the normalized camera-right basis vector.</summary>
    public Vector3 Right { get; }
    /// <summary>Gets the tangent of half the vertical field of view.</summary>
    public float TanHalfFieldOfView { get; }
    /// <summary>Gets the normalized camera-up basis vector.</summary>
    public Vector3 Up { get; }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) => VectorFunctions.NormalizeOr(fallback: fallback, shortest: 1e-5f, value: value);
    private static void ValidateBasis(Vector3 value, string paramName) {
        if (MathF.Abs(x: (value.LengthSquared() - 1f)) > 1e-3f) {
            throw new ArgumentException(
                message: "Camera basis vectors must be normalized.",
                paramName: paramName
            );
        }
    }
    private static void ValidateFinite(Vector3 value, string paramName) {
        if (!VectorFunctions.IsFinite(vector: value)) {
            throw new ArgumentException(
                message: "All vector components must be finite.",
                paramName: paramName
            );
        }
    }

    /// <summary>Returns the ray the camera casts through an image point, summed as the SDF view pass's
    /// <c>cameraRayDirection</c> (<c>frame/sdf-viewport.hlsli</c>) sums it: the forward axis, the point scaled by the
    /// half-extents on the right and up axes, then the <see cref="FrustumOffset"/> as a trailing term.</summary>
    /// <param name="ndc">The image point in normalized device coordinates, <c>[-1, 1]²</c> across the image, up
    /// positive.</param>
    /// <returns>The unnormalized direction, whose component along <see cref="Forward"/> is one.</returns>
    public Vector3 RayThrough(Vector2 ndc) {
        var direction = (
            (Forward +
            (((ndc.X * AspectRatio) * TanHalfFieldOfView) * Right)) +
            ((ndc.Y * TanHalfFieldOfView) * Up)
        );

        return (direction + ((FrustumOffset.X * Right) + (FrustumOffset.Y * Up)));
    }
    /// <summary>Projects a world point onto the camera's image: the inverse of <see cref="RayThrough"/>.</summary>
    /// <param name="world">The world-space point.</param>
    /// <param name="ndc">The point's normalized device coordinates, up positive; <c>[-1, 1]²</c> inside the image and
    /// beyond it outside; zero when the projection fails.</param>
    /// <param name="depth">The point's distance along <see cref="Forward"/> from <see cref="Position"/>, in world
    /// units; computed whether or not the projection succeeds.</param>
    /// <returns><see langword="true"/> when the point lies ahead of the camera (a positive <paramref name="depth"/>);
    /// <see langword="false"/> for a point at or behind the eye, which has no image point.</returns>
    public bool TryProject(Vector3 world, out Vector2 ndc, out float depth) {
        var delta = (world - Position);

        depth = Vector3.Dot(
            vector1: delta,
            vector2: Forward
        );

        if (!(depth > 0f)) {
            ndc = Vector2.Zero;

            return false;
        }

        ndc = new Vector2(
            x: ((Vector3.Dot(
                vector1: delta,
                vector2: Right
            ) - (depth * FrustumOffset.X)) / ((depth * TanHalfFieldOfView) * AspectRatio)),
            y: ((Vector3.Dot(
                vector1: delta,
                vector2: Up
            ) - (depth * FrustumOffset.Y)) / (depth * TanHalfFieldOfView))
        );

        return true;
    }
    /// <summary>Returns whether any of a box may lie in the camera's frustum: its near plane, its four sides, each side
    /// widened by a fraction of the half extents, and a far plane. A box is unseen only when it lies wholly past one of
    /// those planes, so the test is conservative: a box it keeps may still be unseen behind a corner of the frustum, and a
    /// box it refuses has no point the camera's image shows nearer than the far plane.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="halfX">The box's first half axis: the center plus or minus it reaches two faces.</param>
    /// <param name="halfY">The box's second half axis.</param>
    /// <param name="halfZ">The box's third half axis. The axes need not be perpendicular, so an axis-aligned box carried
    /// through any affine matrix is tested exactly.</param>
    /// <param name="margin">The fraction the frustum's sides are widened by: zero is the exact frustum.</param>
    /// <param name="far">The forward distance of the far plane, in world units; infinity, the default, has none.</param>
    /// <param name="sweep">The displacement the box is swept along, in world units: the test is of every point the box
    /// passes moving by up to this, as a shadow caster's box swept along its light reaches the receivers it can shadow.
    /// Zero, the default, tests the box where it stands.</param>
    /// <param name="reach">The distance, in world units, every plane is moved out by: a receiver this near the frustum
    /// still counts. Zero by default.</param>
    /// <returns><see langword="false"/> when the whole box, swept, lies at or before the near plane, at or past the far
    /// plane, or past one widened side, each moved out by <paramref name="reach"/>; otherwise <see langword="true"/>.</returns>
    public bool MaySee(Vector3 center, Vector3 halfX, Vector3 halfY, Vector3 halfZ, float margin, float far = float.PositiveInfinity, Vector3 sweep = default, float reach = 0f) {
        var box = new SweptBox(Center: (center - Position), HalfX: halfX, HalfY: halfY, HalfZ: halfZ, Reach: reach, Sweep: sweep);
        Span<(Vector3 Normal, float Offset)> planes = stackalloc (Vector3, float)[FrustumPlanes];

        Planes(far: far, margin: margin, planes: planes);
        foreach (var (normal, offset) in planes) {
            if (box.Nearest(normal: normal, offset: offset) >= 0f) { return false; }
        }

        return true;
    }
    /// <summary>Returns how far a box may move before <see cref="MaySee"/>, with no sweep or reach, answers otherwise for
    /// it. A box the frustum may hold is refused once it lies wholly past any one plane, so its slack is the least, over
    /// the planes, of how far it stands from lying wholly past that plane. A refused box is held again only once it lies
    /// wholly past none, so its slack is the most, over the planes it lies wholly past, of how far past it lies. A box
    /// whose center moves by <c>d</c> and whose half axes move by <c>e</c> in all (the sum of their moves' lengths) keeps
    /// its answer while <c>d + e</c> stays under the result.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="halfX">The box's first half axis.</param>
    /// <param name="halfY">The box's second half axis.</param>
    /// <param name="halfZ">The box's third half axis.</param>
    /// <param name="margin">The fraction the frustum's sides are widened by, as <see cref="MaySee"/> takes it.</param>
    /// <param name="far">The forward distance of the far plane; infinity, the default, has none.</param>
    /// <returns>The distance in world units, never negative; infinity for a frustum with no finite plane.</returns>
    public float FrustumSlack(Vector3 center, Vector3 halfX, Vector3 halfY, Vector3 halfZ, float margin, float far = float.PositiveInfinity) {
        var box = new SweptBox(Center: (center - Position), HalfX: halfX, HalfY: halfY, HalfZ: halfZ, Reach: 0f, Sweep: default);
        Span<(Vector3 Normal, float Offset)> planes = stackalloc (Vector3, float)[FrustumPlanes];
        var inside = float.PositiveInfinity;
        var outside = 0f;
        var refused = false;

        Planes(far: far, margin: margin, planes: planes);
        foreach (var (normal, offset) in planes) {
            var length = normal.Length();
            var nearest = box.Nearest(normal: normal, offset: offset);

            if (!(length > 0f) || !float.IsFinite(f: nearest)) { continue; }
            if (nearest >= 0f) {
                refused = true;
                outside = MathF.Max(x: outside, y: (nearest / length));
            } else {
                inside = MathF.Min(x: inside, y: (-nearest / length));
            }
        }

        return (refused ? outside : inside);
    }

    /// <summary>Returns the largest angle, in radians, through which a plane of this camera's frustum stands turned from
    /// the same plane of another's (its near plane and four sides, widened alike): a turn of the camera, or a change of
    /// its field of view, aspect or frustum offset. A point at a distance <c>r</c> from the eye has its distance to such a
    /// plane moved by no more than <c>r</c> times this angle, beside the eye's own move.</summary>
    /// <param name="other">The camera to compare with.</param>
    /// <param name="margin">The fraction both frustums' sides are widened by, as <see cref="MaySee"/> takes it.</param>
    /// <returns>The angle, between zero and pi.</returns>
    public float FrustumTurn(in CameraSnapshot other, float margin) {
        Span<(Vector3 Normal, float Offset)> mine = stackalloc (Vector3, float)[FrustumPlanes];
        Span<(Vector3 Normal, float Offset)> theirs = stackalloc (Vector3, float)[FrustumPlanes];
        var turn = 0f;

        Planes(far: float.PositiveInfinity, margin: margin, planes: mine);
        other.Planes(far: float.PositiveInfinity, margin: margin, planes: theirs);
        for (var plane = 0; (plane < FrustumPlanes); plane++) {
            if (plane == 1) { continue; }

            var chord = Vector3.Distance(value1: Vector3.Normalize(value: mine[plane].Normal), value2: Vector3.Normalize(value: theirs[plane].Normal));

            turn = MathF.Max(x: turn, y: (2f * MathF.Asin(x: MathF.Min(x: 1f, y: (0.5f * chord)))));
        }

        return turn;
    }

    /// <summary>The planes <see cref="BoundingPlanes"/> writes: the near plane and the four sides.</summary>
    public const int BoundingPlaneCount = 5;

    /// <summary>Writes the planes <see cref="MaySee"/> and <see cref="FrustumSlack"/> test with no far plane, sweep or
    /// reach, in world space and at unit length, for a reader that tests many boxes against one frustum: each plane's
    /// normal points out of the frustum, and its <c>W</c> is the offset from the world origin, so a point's signed
    /// distance past it is <c>dot(normal, point) + W</c>. A box whose center is <c>c</c> and whose half extents along
    /// the world axes are <c>h</c> lies wholly past a plane when <c>dot(normal, c) + W - dot(abs(normal), h)</c> is at
    /// or past zero, and that figure is the distance <see cref="FrustumSlack"/> reads for the plane.</summary>
    /// <param name="margin">The fraction the frustum's sides are widened by, as <see cref="MaySee"/> takes it.</param>
    /// <param name="planes">Receives <see cref="BoundingPlaneCount"/> planes: the near plane, then the four sides.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="planes"/> holds fewer than
    /// <see cref="BoundingPlaneCount"/> planes.</exception>
    public void BoundingPlanes(float margin, Span<Vector4> planes) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: planes.Length, other: BoundingPlaneCount, paramName: nameof(planes));

        Span<(Vector3 Normal, float Offset)> all = stackalloc (Vector3, float)[FrustumPlanes];
        var written = 0;

        Planes(far: float.PositiveInfinity, margin: margin, planes: all);
        for (var plane = 0; (plane < FrustumPlanes); plane++) {
            // The far plane stands at infinity and refuses nothing.
            if (plane == 1) { continue; }

            var (normal, offset) = all[plane];
            var length = normal.Length();
            var unit = (normal / length);

            planes[written++] = new Vector4(value: unit, w: ((offset / length) - Vector3.Dot(vector1: unit, vector2: Position)));
        }
    }

    // The planes MaySee tests, in its order.
    private const int FrustumPlanes = 6;

    // The frustum's planes: each through the eye or at the near distance, or at the far one, its normal pointing out of
    // the frustum and its sides widened by a fraction of the half extents. A box lies past one when its nearest point
    // does (SweptBox.Nearest).
    private void Planes(float margin, float far, Span<(Vector3 Normal, float Offset)> planes) {
        var vertical = (TanHalfFieldOfView * (1f + margin));
        var horizontal = (AspectRatio * vertical);

        planes[0] = (-Forward, Near);
        planes[1] = (Forward, -far);
        planes[2] = ((Right - ((FrustumOffset.X + horizontal) * Forward)), 0f);
        planes[3] = ((((FrustumOffset.X - horizontal) * Forward) - Right), 0f);
        planes[4] = ((Up - ((FrustumOffset.Y + vertical) * Forward)), 0f);
        planes[5] = ((((FrustumOffset.Y - vertical) * Forward) - Up), 0f);
    }

    // A box relative to the eye, swept and grown by a reach, measured against one plane at a time.
    private readonly record struct SweptBox(Vector3 Center, Vector3 HalfX, Vector3 HalfY, Vector3 HalfZ, Vector3 Sweep, float Reach) {
        // The signed distance, in the plane normal's length, from the plane to the box's point nearest it: at or past
        // zero the whole box lies past the plane. The center's distance less the box's reach along the normal, less the
        // sweep's where it moves the box toward the plane, less the reach.
        public float Nearest(Vector3 normal, float offset) {
            var nearest = (((((Vector3.Dot(vector1: normal, vector2: Center) + offset) - MathF.Abs(x: Vector3.Dot(vector1: normal, vector2: HalfX))) - MathF.Abs(x: Vector3.Dot(vector1: normal, vector2: HalfY))) - MathF.Abs(x: Vector3.Dot(vector1: normal, vector2: HalfZ))) + MathF.Min(x: 0f, y: Vector3.Dot(vector1: normal, vector2: Sweep)));

            return (nearest - (Reach * normal.Length()));
        }
    }

    /// <summary>Gets the narrowest field of view a camera is built with, in radians. Half of it is a normal float, so
    /// its tangent, the snapshot's <see cref="TanHalfFieldOfView"/>, is a positive normal float that no device flushes
    /// to zero; a narrower angle lets <see cref="MathF.Tan"/> of its half underflow to zero, which no snapshot admits.</summary>
    public const float MinFieldOfViewRadians = 1e-6f;

    /// <summary>Creates a finite camera snapshot looking from <paramref name="position"/> toward <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">A position or target component is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The field of view is below <see cref="MinFieldOfViewRadians"/> or not
    /// below <see cref="MathF.PI"/>, or a viewport dimension is zero.</exception>
    public static CameraSnapshot LookAt(Vector3 position, Vector3 target, float fieldOfViewRadians, float viewportWidth, float viewportHeight) {
        ValidateFinite(
            value: position,
            paramName: nameof(position)
        );
        ValidateFinite(
            value: target,
            paramName: nameof(target)
        );

        if (
            !float.IsFinite(f: fieldOfViewRadians) ||
            (fieldOfViewRadians < MinFieldOfViewRadians) ||
            (fieldOfViewRadians >= MathF.PI)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(fieldOfViewRadians),
                fieldOfViewRadians,
                $"The field of view must be finite, at least {MinFieldOfViewRadians} and below pi radians."
            );
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: viewportHeight);

        var forward = SafeNormalize(
            fallback: -Vector3.UnitZ,
            value: (target - position)
        );
        var right = SafeNormalize(
            fallback: Vector3.UnitX,
            value: Vector3.Cross(
                vector1: forward,
                vector2: Vector3.UnitY
            )
        );
        var up = Vector3.Cross(
            vector1: right,
            vector2: forward
        );

        return new CameraSnapshot(
            Position: position,
            Right: right,
            Up: up,
            Forward: forward,
            TanHalfFieldOfView: MathF.Tan(x: (fieldOfViewRadians * 0.5f)),
            AspectRatio: (viewportWidth / viewportHeight)
        );
    }
}
