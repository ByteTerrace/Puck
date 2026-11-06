using System.Numerics;

namespace Puck.SignedDistance.Tests;

/// <summary>Assembles a program from packed tables as <see cref="SdfProgramBuilder.Build"/> does: the palette and screen
/// surfaces become an <see cref="SdfAppearance"/>, which <see cref="SdfProgram.Of"/> joins to the field.</summary>
internal static class TestPrograms {
    /// <summary>Returns the program of the tables.</summary>
    /// <param name="instructions">The instructions.</param>
    /// <param name="materials">The palette.</param>
    /// <param name="instances">The instances, or none.</param>
    /// <param name="screenSurfaces">The screen surfaces, or none.</param>
    /// <param name="convexPolygonProfiles">The convex polygons' vertex lists, or none.</param>
    /// <param name="sweepCurves">The sweeps' curves, or none.</param>
    /// <param name="paths">The paths' edge tables, or none.</param>
    /// <param name="reservedPathWords">The words reserved for future path tables.</param>
    /// <param name="sampledBindings">The sampled instructions' fields, or none.</param>
    /// <returns>The program.</returns>
    public static SdfProgram Of(IReadOnlyList<SdfInstruction> instructions, IReadOnlyList<SdfMaterial> materials, IReadOnlyList<SdfInstanceRange>? instances = null, IReadOnlyList<SdfScreenSurface>? screenSurfaces = null, IReadOnlyList<(int InstructionIndex, Vector2[] Vertices)>? convexPolygonProfiles = null, IReadOnlyList<SdfSweepCurve>? sweepCurves = null, IReadOnlyList<SdfCompiledPath>? paths = null, int reservedPathWords = 0, IReadOnlyList<SdfSampledBinding>? sampledBindings = null) =>
        SdfProgram.Of(
            appearance: new SdfAppearance(materials: materials, screenSurfaces: screenSurfaces),
            convexPolygonProfiles: convexPolygonProfiles,
            instances: instances,
            instructions: instructions,
            paths: paths,
            reservedPathWords: reservedPathWords,
            sampledBindings: sampledBindings,
            sweepCurves: sweepCurves
        );
}
