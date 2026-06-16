using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.Scene;

/// <summary>The demo's authored SDF scenes, built with the fluent <see cref="SdfProgramBuilder"/>. Each
/// is just a list of instructions + materials — proof that composing a world is composing data, not
/// editing a shader. Every scene plants a <c>ScreenSlab</c> so the future jumbotron surface is visible.</summary>
internal static class DemoSceneLibrary {
    public const string DefaultName = "blobs";

    /// <summary>Builds the named scene, or <see langword="null"/> if the name is unknown.</summary>
    public static SdfProgram? TryBuild(string name) {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToLowerInvariant() switch {
            "blobs" => BuildBlobs(),
            "pillars" => BuildPillars(),
            _ => null,
        };
    }

    private static SdfProgram BuildBlobs() {
        var builder = new SdfProgramBuilder();
        var groundMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
            x: 0.24f,
            y: 0.26f,
            z: 0.30f
        )));
        var bodyMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
            x: 0.86f,
            y: 0.36f,
            z: 0.24f
        )));
        var haloMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
            x: 0.25f,
            y: 0.62f,
            z: 0.92f
        )));

        // Ground plane.
        builder
            .ResetPoint()
            .Plane(
                blend: SdfBlendOp.Union,
                material: groundMaterial,
                normal: Vector3.UnitY,
                offset: 1f
            );
        // A smooth-blended body: two spheres merged, a smaller head, mirrored eyes carved out.
        builder
            .ResetPoint()
            .Sphere(
                blend: SdfBlendOp.Union,
                material: bodyMaterial,
                radius: 0.75f
            )
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0f,
                y: 0.7f,
                z: 0f
            ))
            .Sphere(
                blend: SdfBlendOp.SmoothUnion,
                material: bodyMaterial,
                radius: 0.5f,
                smooth: 0.35f
            );
        // A floating halo torus.
        builder
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0f,
                y: 1.35f,
                z: 0f
            ))
            .Torus(
                blend: SdfBlendOp.Union,
                majorRadius: 0.42f,
                material: haloMaterial,
                minorRadius: 0.07f
            );
        // A jumbotron screen slab floating behind the body.
        builder
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0f,
                y: 1.3f,
                z: -1.9f
            ))
            .ScreenSlab(
                halfExtents: new Vector3(
                    x: 1.5f,
                    y: 0.85f,
                    z: 0.06f
                ),
                round: 0.05f
            );

        return builder.Build();
    }
    private static SdfProgram BuildPillars() {
        var builder = new SdfProgramBuilder();
        var groundMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
            x: 0.20f,
            y: 0.22f,
            z: 0.26f
        )));
        var pillarMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
            x: 0.72f,
            y: 0.70f,
            z: 0.80f
        )));

        builder
            .ResetPoint()
            .Plane(
                blend: SdfBlendOp.Union,
                material: groundMaterial,
                normal: Vector3.UnitY,
                offset: 1f
            );
        // A repeated, limited grid of round-cone pillars rising from the ground.
        builder
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0f,
                y: -1f,
                z: 0f
            ))
            .RepeatLimited(
                limit: new Vector3(
                    x: 3f,
                    y: 0f,
                    z: 3f
                ),
                spacing: new Vector3(
                    x: 2.1f,
                    y: 0f,
                    z: 2.1f
                )
            )
            .RoundCone(
                blend: SdfBlendOp.Union,
                height: 1.6f,
                lowerRadius: 0.34f,
                material: pillarMaterial,
                upperRadius: 0.12f
            );
        builder
            .ResetPoint()
            .Translate(offset: new Vector3(
                x: 0f,
                y: 1.6f,
                z: -2.2f
            ))
            .ScreenSlab(
                halfExtents: new Vector3(
                    x: 1.7f,
                    y: 0.95f,
                    z: 0.06f
                ),
                round: 0.05f
            );

        return builder.Build();
    }
}
