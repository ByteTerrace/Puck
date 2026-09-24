namespace Puck.ShaderVm.Tests;

/// <summary>Where the opt-in preview harnesses write their renders and reports: <c>previews</c> beside the test
/// assembly. The harnesses are explicit tests, so a plain run never renders; run them with
/// <c>Puck.ShaderVm.Tests.exe -explicit only</c>.</summary>
internal static class PreviewOutput {
    /// <summary>Gets the preview directory, created on first use.</summary>
    public static string Directory { get; } = System.IO.Directory.CreateDirectory(path: Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "previews"
    )).FullName;
}
