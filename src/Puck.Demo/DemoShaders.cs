namespace Puck.Demo;

/// <summary>The deployment location of the demo's compiled SDF shader bytecode (deployed next to the demo by the
/// build; see the csproj's shader Content items).</summary>
internal static class DemoShaders {
    /// <summary>Gets the directory the compiled SDF shaders are deployed to next to the demo.</summary>
    internal static string SdfDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Shaders",
        "Sdf"
    );

    /// <summary>Gets the directory the demo's compiled overlay shaders are deployed to next to the demo.</summary>
    internal static string OverlayDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Shaders",
        "Overlay"
    );
}
