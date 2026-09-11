namespace Puck.Shaders.Study;

/// <summary>Thrown when a study-pipeline compiler tool (<c>glslang</c>/<c>glslangValidator</c>, <c>spirv-cross</c>,
/// or <c>dxc</c>) cannot be found — a caller-supplied toolchain directory was searched and the tool was absent
/// there, or bare-name resolution through the OS's own executable search failed. Never thrown for a user-source
/// compile error; that is a <see cref="StudyDiagnostic"/> on the returned <see cref="StudyProgram"/> instead.</summary>
public sealed class StudyToolMissingException : Exception {
    public StudyToolMissingException(string tool, string? directory)
        : base(message: $"Study toolchain tool '{tool}' was not found in {(directory is null ? "the search path" : $"'{directory}'")}.") {
        Directory = directory;
        Tool = tool;
    }

    /// <summary>Gets the toolchain directory that was searched, or <see langword="null"/> when resolution fell back
    /// to the OS's own executable search path.</summary>
    public string? Directory { get; }
    /// <summary>Gets the tool name (or name-with-fallback, e.g. <c>"glslang (or glslangValidator)"</c>) that could
    /// not be resolved.</summary>
    public string Tool { get; }
}
