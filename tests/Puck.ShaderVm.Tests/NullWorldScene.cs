using System.Numerics;
using static Puck.ShaderVm.ShaderSdf;
using Expression = Puck.ShaderVm.ShaderExpression;

namespace Puck.ShaderVm.Tests;

// null.world.json's creations as one value graph: the four ground quadrants (two of them wallpaper-folded into
// checkerboards) and the pip avatar. Materials are flattened into one global palette because a Shader VM sample
// carries a single material id, where the document scopes its palette per creation.
public static class NullWorldScene {
    public const int BlueDeep = 3;
    public const int BluePale = 4;
    public const int Ink = 1;
    public const int Paper = 0;
    public const int PillarLamp = 11;
    public const int PillarStone = 9;
    public const int PillarTrim = 10;
    public const int PipBody = 6;
    public const int PipEye = 7;
    public const int PipPupil = 8;
    public const int PlanetoidCrust = 12;
    public const int PlanetoidRock = 13;
    public const int Sage = 2;
    public const int Stone = 5;

    // The lamp material the document marks emissive.
    public static int Emissive => PillarLamp;
    public static Vector3[] Palette => [
        Srgb(hex: 0xFFFFFF),
        Srgb(hex: 0x000000),
        Srgb(hex: 0x7A9E7E),
        Srgb(hex: 0x2E5C8A),
        Srgb(hex: 0x9DBEDF),
        Srgb(hex: 0xC8C8C8),
        Srgb(hex: 0xFF73BF),
        Srgb(hex: 0xFFFFFF),
        Srgb(hex: 0x0D0D0D),
        Srgb(hex: 0xD8D2C4),
        Srgb(hex: 0x5A5347),
        Srgb(hex: 0xFFE7B0),
        Srgb(hex: 0x8C7B6B),
        Srgb(hex: 0x5E5348),
    ];

    /// <summary>Builds the world sample at one evaluation point.</summary>
    public static Expression Build(Expression point) {
        var world = GroundNorthWest(point: point);

        world = Union(candidate: GroundNorthEast(point: point), current: world);
        world = Union(candidate: GroundSouthEast(point: point), current: world);
        world = Union(candidate: GroundSouthWest(point: point), current: world);
        world = Union(candidate: Pillars(point: point), current: world);
        world = Union(candidate: Planetoids(point: point), current: world);
        world = Union(candidate: Pip(point: point), current: world);

        return world;
    }

    // A ground plane at y = 0.001, wallpaper-folded on a unit cell so the P1 two-colouring strides paper against ink,
    // then cut back to the +x/-z quadrant by two subtracted half-spaces.
    private static Expression GroundNorthEast(Expression point) {
        var folded = WallpaperFoldP1(cell: Expression.Constant(x: 1f, y: 1f), cellIndex: out var cellIndex, point: point);
        var surface = Sample(
            distance: Plane(normal: Up, offset: -0.001f, point: folded),
            material: (Paper + WallpaperMaterial(cellIndex: cellIndex, stride: 1f))
        );

        return Quadrant(keepNegativeX: true, keepPositiveZ: true, point: point, surface: surface);
    }
    private static Expression GroundSouthEast(Expression point) => Quadrant(
        keepNegativeX: true,
        keepPositiveZ: false,
        point: point,
        surface: Sample(distance: Plane(normal: Up, offset: -0.001f, point: point), material: Sage)
    );
    private static Expression GroundSouthWest(Expression point) {
        var folded = WallpaperFoldP1(cell: Expression.Constant(x: 2f, y: 2f), cellIndex: out var cellIndex, point: point);
        var surface = Sample(
            distance: Plane(normal: Up, offset: -0.001f, point: folded),
            material: (BlueDeep + WallpaperMaterial(cellIndex: cellIndex, stride: 1f))
        );

        return Quadrant(keepNegativeX: false, keepPositiveZ: false, point: point, surface: surface);
    }
    // The one quadrant the document leaves uncut: it underlies the whole plane and the other three sit above it.
    private static Expression GroundNorthWest(Expression point) => Sample(
        distance: Plane(normal: Up, offset: 0f, point: point),
        material: Stone
    );
    // The document spells each cut as a Subtraction of a rotated Plane; a quarter turn about Z carries the plane's
    // +Y normal to -X, and a quarter turn about X carries it to +Z.
    private static Expression Quadrant(Expression surface, Expression point, bool keepNegativeX, bool keepPositiveZ) {
        var alongX = Sample(
            distance: Plane(normal: Expression.Constant(x: (keepNegativeX ? -1f : 1f), y: 0f), offset: 0f, point: point),
            material: 0f
        );
        var alongZ = Sample(
            distance: Plane(normal: Expression.Constant(x: 0f, y: 0f, z: (keepPositiveZ ? 1f : -1f)), offset: 0f, point: point),
            material: 0f
        );

        return Subtraction(candidate: alongZ, current: Subtraction(candidate: alongX, current: surface));
    }
    // Four corner pillars from one authored shape: two symmetry folds carry the (+x, +z) column into all four
    // quadrants. Each stage composes into the running accumulator with its own blend radius, so the shaft melts into
    // the plinth and the lamp into the capital.
    private static Expression Pillars(Expression point) {
        var folded = SymmetryPlane(
            normal: Expression.Constant(x: 0f, y: 0f, z: 1f),
            offset: 0f,
            point: SymmetryPlane(normal: Expression.Constant(x: 1f, y: 0f), offset: 0f, point: point)
        );
        var plinth = Sample(
            distance: Cylinder(halfHeight: 0.3f, point: Translate(offset: Expression.Constant(x: 6f, y: 0.3f, z: 6f), point: folded), radius: 0.8f),
            material: PillarTrim
        );
        var shaft = Sample(
            distance: Cylinder(halfHeight: 2.9f, point: Translate(offset: Expression.Constant(x: 6f, y: 3.3f, z: 6f), point: folded), radius: 0.45f),
            material: PillarStone
        );
        var capital = Sample(
            distance: Cylinder(halfHeight: 0.3f, point: Translate(offset: Expression.Constant(x: 6f, y: 6.35f, z: 6f), point: folded), radius: 0.8f),
            material: PillarTrim
        );
        var lamp = Sample(
            distance: Sphere(point: Translate(offset: Expression.Constant(x: 6f, y: 7f, z: 6f), point: folded), radius: 0.45f),
            material: PillarLamp
        );
        var column = SmoothUnion(candidate: shaft, current: plinth, radius: 0.25f);

        column = SmoothUnion(candidate: capital, current: column, radius: 0.25f);

        return SmoothUnion(candidate: lamp, current: column, radius: 0.1f);
    }
    // Five placements of one creation. A placement is a translate and a uniform scale, so the point divides by the
    // scale going in and the distance multiplies by it coming out.
    private static Expression Planetoids(Expression point) {
        var world = Planetoid(point: point, position: Expression.Constant(x: 6f, y: 17f, z: 6f), scale: 1f);

        foreach (var (x, y, z, scale) in (((float, float, float, float)[])[(-6f, 17f, 6f, 1f), (6f, 17f, -6f, 1f), (-6f, 17f, -6f, 1f), (0f, 24f, 0f, 1.8f)])) {
            world = Union(candidate: Planetoid(point: point, position: Expression.Constant(x: x, y: y, z: z), scale: scale), current: world);
        }

        return world;
    }
    // A core sphere with six outcrops repeated around the Y axis by the polar fold, each melting into the core.
    private static Expression Planetoid(Expression point, Expression position, float scale) {
        var local = (Translate(offset: position, point: point) / scale);
        var core = Sample(distance: Sphere(point: local, radius: 1.5f), material: PlanetoidCrust);
        var folded = RepeatPolarY(count: 6f, point: local, sector: out _);
        var outcrop = Sample(
            distance: Sphere(point: Translate(offset: Expression.Constant(x: 1.32f, y: 0f, z: 0f), point: folded), radius: 0.3f),
            material: PlanetoidRock
        );
        var blended = SmoothUnion(candidate: outcrop, current: core, radius: 0.3f);

        return Sample(distance: (Distance(sample: blended) * scale), material: Material(sample: blended));
    }
    // The avatar: a capsule body, then eyes and pupils mirrored across the sagittal plane by a symmetry fold, which
    // is how the document authors one eye and gets two.
    private static Expression Pip(Expression point) {
        var body = Sample(
            distance: Capsule(
                endpoint: Expression.Constant(x: 0f, y: 0.9f, z: 0f),
                point: Translate(offset: Expression.Constant(x: 0f, y: 0.25f, z: 0f), point: point),
                radius: 0.35f
            ),
            material: PipBody
        );
        var mirrored = SymmetryPlane(normal: Expression.Constant(x: 1f, y: 0f), offset: 0f, point: point);
        var eye = Sample(
            distance: Sphere(point: Translate(offset: Expression.Constant(x: 0.2f, y: 1.1f, z: 0.42f), point: mirrored), radius: 0.14f),
            material: PipEye
        );
        var pupil = Sample(
            distance: Sphere(point: Translate(offset: Expression.Constant(x: 0.2f, y: 1.1f, z: 0.54f), point: mirrored), radius: 0.055f),
            material: PipPupil
        );

        return Union(candidate: pupil, current: Union(candidate: eye, current: body));
    }

    private static Expression Up => Expression.Constant(x: 0f, y: 1f, z: 0f);

    private static Vector3 Srgb(uint hex) => new(
        x: MathF.Pow(x: (((hex >> 16) & 0xFFu) / 255f), y: 2.2f),
        y: MathF.Pow(x: (((hex >> 8) & 0xFFu) / 255f), y: 2.2f),
        z: MathF.Pow(x: ((hex & 0xFFu) / 255f), y: 2.2f)
    );
}
