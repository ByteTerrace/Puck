namespace Puck.Shaders.Study;

/// <summary>Resolves the study pipeline's compiler tools from an explicit directory, or leaves bare names for the
/// OS's own executable search when none is given. Never reads an environment variable — the directory comes only
/// from a caller (a CLI option or a document field); <see langword="null"/> means the process launcher resolves
/// each bare name itself, and a tool missing from that search surfaces as a launch failure the caller translates.</summary>
public sealed class StudyToolchain(string? directory) {
    /// <summary>Gets the directory this toolchain resolves against, or <see langword="null"/> for bare-name
    /// resolution through the OS's own executable search path.</summary>
    public string? Directory { get; } = (string.IsNullOrWhiteSpace(value: directory) ? null : Path.GetFullPath(path: directory));

    /// <summary>Resolves <paramref name="name"/> to an invocable path, trying <paramref name="fallbackName"/> when
    /// given and <paramref name="name"/> is absent. With no directory this returns <paramref name="name"/>
    /// unchecked; with a directory, both candidates are checked against it and a miss on both throws
    /// <see cref="StudyToolMissingException"/> immediately, before any process is spawned.</summary>
    public string Resolve(string name, string? fallbackName = null) {
        if (Directory is null) { return name; }
        if (Find(directory: Directory, name: name) is { } found) { return found; }
        if ((fallbackName is not null) && (Find(directory: Directory, name: fallbackName) is { } fallbackFound)) { return fallbackFound; }

        throw new StudyToolMissingException(directory: Directory, tool: (fallbackName is null ? name : $"{name} (or {fallbackName})"));
    }

    private static string? Find(string directory, string name) {
        var bare = Path.Combine(path1: directory, path2: name);

        if (File.Exists(path: bare)) { return bare; }

        var exe = Path.Combine(path1: directory, path2: $"{name}.exe");

        return (File.Exists(path: exe) ? exe : null);
    }
}
