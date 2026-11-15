namespace Puck.Confinement;

/// <summary>The one definition of whether this platform's file system ignores case in a name. Code above
/// <c>Puck.Abstractions</c> reads it as <c>PuckPaths.Comparison</c> and <c>PuckPaths.Comparer</c>; this leaf, which
/// <c>Puck.Abstractions</c> sits above and which only the BCL may sit beneath, holds the rule itself.</summary>
public static class FileSystemCase {
    /// <summary>Gets whether this platform's file system ignores case in a name. <see langword="true"/> on Windows,
    /// <see langword="false"/> everywhere else.</summary>
    public static bool Folds { get; } = OperatingSystem.IsWindows();
    /// <summary>Gets the comparer under which two file-system names compare equal, distinct, or sorted:
    /// case-insensitive where <see cref="Folds"/>, ordinal otherwise.</summary>
    public static StringComparer Comparer { get; } = (Folds
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal
    );
    /// <summary>Gets the <see cref="StringComparison"/> equivalent of <see cref="Comparer"/>.</summary>
    public static StringComparison Comparison { get; } = (Folds
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal
    );
}
