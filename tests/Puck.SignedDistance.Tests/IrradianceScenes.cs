using System.Numerics;

using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance.Tests;

// The fixture worlds the illumination laws share. Each builds an SdfProgram the CPU evaluator interprets, so the
// reference and the cache model read the same field.
internal static class IrradianceScenes {
    // A closed spherical shell centred at the origin: free space inside the inner radius and outside the outer one.
    public static IrradianceField Shell(double inner, double thickness) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(material: material, radius: ((float)(inner + thickness)));
        _ = builder.Sphere(blend: SdfBlendOp.Subtraction, material: material, radius: ((float)inner));

        return new IrradianceField(program: builder.Build());
    }
    // A closed shell with a small solid ball at its centre, the receiver a sealed hall holds.
    public static IrradianceField HallWithReceiver(double inner, double thickness, double receiver) {
        var builder = new SdfProgramBuilder();
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var ball = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(material: wall, radius: ((float)(inner + thickness)));
        _ = builder.Sphere(blend: SdfBlendOp.Subtraction, material: wall, radius: ((float)inner));
        _ = builder.ResetPoint();
        _ = builder.Sphere(material: ball, radius: ((float)receiver));

        return new IrradianceField(program: builder.Build());
    }
    // A sealed box room: walls, floor and ceiling of one thickness around an interior box, centred at `center`; an
    // optional doorway cut through the wall at the interior's least X, from the floor up.
    public static IrradianceField Room(Double3 center, Double3 interiorHalf, double thickness, double doorWidth = 0.0, double doorHeight = 0.0) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var position = ToVector(value: center);
        var inner = ToVector(value: interiorHalf);

        _ = builder.Translate(offset: position);
        _ = builder.Box(halfExtents: (inner + new Vector3(value: ((float)thickness))), material: material, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: position);
        _ = builder.Box(blend: SdfBlendOp.Subtraction, halfExtents: inner, material: material, round: 0f);

        if (doorWidth > 0.0) {
            var doorCenter = (position + new Vector3(x: ((float)(-interiorHalf.X - (0.5 * thickness))), y: ((float)((doorHeight * 0.5) - interiorHalf.Y)), z: 0f));

            _ = builder.ResetPoint();
            _ = builder.Translate(offset: doorCenter);
            _ = builder.Box(
                blend: SdfBlendOp.Subtraction,
                halfExtents: new Vector3(x: ((float)thickness), y: ((float)(doorHeight * 0.5)), z: ((float)(doorWidth * 0.5))),
                material: material,
                round: 0f
            );
        }

        return new IrradianceField(program: builder.Build());
    }
    // A sealed spherical room: a shell of one thickness around an interior sphere centred at `center`.
    public static IrradianceField SphereRoom(Double3 center, double radius, double thickness) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: ToVector(value: center));
        _ = builder.Sphere(material: material, radius: ((float)(radius + thickness)));
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: ToVector(value: center));
        _ = builder.Sphere(blend: SdfBlendOp.Subtraction, material: material, radius: ((float)radius));

        return new IrradianceField(program: builder.Build());
    }
    // A table in an emissive hall: a closed shell whose inside (material 0) emits, a floor slab whose top is y = 0, and
    // a floating square table top above it (both material 1).
    public static IrradianceField TableHall(double height, double halfWidth, double thickness) {
        var builder = new SdfProgramBuilder();
        var shell = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var solid = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(material: shell, radius: 6.2f);
        _ = builder.Sphere(blend: SdfBlendOp.Subtraction, material: shell, radius: 6.0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0f, y: -0.05f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 5f, y: 0.05f, z: 5f), material: solid, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0f, y: ((float)height), z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: ((float)halfWidth), y: ((float)(thickness * 0.5)), z: ((float)halfWidth)), material: solid, round: 0f);

        return new IrradianceField(program: builder.Build());
    }
    // Uniform surfaces: every material reflects `albedo` and emits `emission`, with no direct light and a uniform sky.
    public static IrradianceSurfaces Uniform(double albedo, double emission, double sky) => new(
        albedo: _ => new Double3(X: albedo, Y: albedo, Z: albedo),
        emission: _ => new Double3(X: emission, Y: emission, Z: emission),
        sky: _ => new Double3(X: sky, Y: sky, Z: sky)
    );
    public static Vector3 ToVector(Double3 value) => new(x: ((float)value.X), y: ((float)value.Y), z: ((float)value.Z));
    // The furnace's normalized incident irradiance after n feedback bounces: e(1 + ρ + … + ρ^n).
    public static double Furnace(double albedo, double emission, int bounces) {
        var sum = 0.0;
        var term = emission;

        for (var bounce = 0; (bounce <= bounces); bounce++) {
            sum += term;
            term *= albedo;
        }

        return sum;
    }
}
