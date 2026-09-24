namespace Puck.Abstractions;

/// <summary>
/// The one place Puck decides whether two file-system paths name the same file, and the one place a full path is
/// normalized to Puck's on-disk and wire form.
/// <para>Two paths name the same file case-insensitively on Windows, where the file system itself ignores case, and
/// exactly (ordinally) everywhere else. This is a file-system rule, not a text rule: it says nothing about comparing
/// a document name, a URL, or any other string that happens to look like a path. <c>Puck.Assets.WorldDocumentName</c>
/// compares document names with its own comparer for exactly that reason — a document name is case-insensitive on
/// every platform, on purpose, because it is a name and not a file-system path.</para>
/// </summary>
public static class PuckPaths {
    /// <summary>Gets whether this platform's file system ignores case in a path. <see langword="true"/> on Windows,
    /// <see langword="false"/> everywhere else.</summary>
    public static bool FoldsCase { get; } = OperatingSystem.IsWindows();
    /// <summary>Gets the comparer two file-system paths compare equal, distinct, or sorted under: case-insensitive
    /// where <see cref="FoldsCase"/>, ordinal otherwise.</summary>
    public static StringComparer Comparer { get; } = (FoldsCase
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal
    );
    /// <summary>Gets the <see cref="StringComparison"/> equivalent of <see cref="Comparer"/>, for
    /// <see cref="string.Equals(string?, string?, StringComparison)"/>, <see cref="string.StartsWith(string, StringComparison)"/>,
    /// and similar overloads that take a comparison rather than a comparer.</summary>
    public static StringComparison Comparison { get; } = (FoldsCase
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal
    );

    /// <summary>Returns <paramref name="path"/> resolved to a full path with <c>/</c> as its only separator, per
    /// Puck's file-path convention: authored paths, configuration, stored path identities, and path output spell a
    /// separator as <c>/</c> on every platform.</summary>
    /// <param name="path">A relative or full path, spelled with either separator.</param>
    /// <returns>The full path, resolved against the current directory when <paramref name="path"/> is relative, with
    /// every <see cref="Path.DirectorySeparatorChar"/> replaced by <c>/</c>.</returns>
    public static string Normalize(string path) => Path.GetFullPath(path: path).Replace(
        newChar: '/',
        oldChar: Path.DirectorySeparatorChar
    );
    /// <summary>Returns the full path of a file this build ships beside the executable: the one resolver for the
    /// engine's own content (the default world, the shipped fonts, shaders and probe kinds), which the build copies
    /// under <see cref="AppContext.BaseDirectory"/>. A path a document authors never resolves here; it resolves beside
    /// the document that authored it.</summary>
    /// <param name="relativePath">The path relative to the executable's directory, spelled with <c>/</c>.</param>
    /// <returns>The full, forward-slashed path.</returns>
    public static string Shipped(string relativePath) => Normalize(path: Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: relativePath
    ));
}
