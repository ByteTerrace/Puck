namespace Puck.Demo;

/// <summary>The deployment location of the demo's compiled SDF/overlay shader bytecode (deployed next to the demo by
/// the build; see the csproj's shader Content items). <see cref="SdfDirectory"/> is byte-for-byte the same path as
/// <see cref="Puck.SdfVm.SdfWorldKernels.DefaultDirectory"/> — kept here too (rather than deleted) because two
/// coupling-ceilinged consumers (<c>OverworldRenderNode</c>, <c>OverlayComposition</c>) already reference
/// <see cref="DemoShaders"/> for <see cref="OverlayDirectory"/>, so reading <see cref="SdfDirectory"/> off the SAME
/// already-counted type costs them nothing, where naming <c>SdfWorldKernels</c> directly would not.</summary>
internal static class DemoShaders {
    /// <summary>Gets the directory the compiled SDF shaders are deployed to next to the demo.</summary>
    internal static string SdfDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Sdf"
    );

    /// <summary>Gets the directory the demo's compiled overlay shaders are deployed to next to the demo.</summary>
    internal static string OverlayDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Overlay"
    );
}
