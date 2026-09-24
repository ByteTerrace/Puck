namespace Puck.Abstractions;

/// <summary>
/// The one per-user directory Puck keeps state and caches under: <c>Puck</c> in the user's local application data
/// (<c>%LOCALAPPDATA%</c> on Windows, <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c> on Linux), or in the temporary
/// directory when the platform names no such folder. Each owner keeps one subdirectory of it under a lower-case name.
/// The folder comes from the platform's known-folder resolution, which on Windows ignores a <c>LOCALAPPDATA</c>
/// environment override; an owner that needs another location takes it as an explicit argument.
/// </summary>
public static class PuckUserDirectory {
    /// <summary>Gets the per-user Puck directory. It is not created here; each owner creates what it writes.</summary>
    public static string Root {
        get {
            var local = Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData);

            return Path.Join(
                path1: (string.IsNullOrEmpty(value: local)
                    ? Path.GetTempPath()
                    : local),
                path2: "Puck"
            );
        }
    }

    /// <summary>Returns one owner's subdirectory of <see cref="Root"/>.</summary>
    /// <param name="name">The subdirectory's name: one lower-case path segment, such as <c>compilations</c>.</param>
    /// <returns>The subdirectory's full path. It is not created here.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, white space, or
    /// not one lower-case path segment.</exception>
    public static string Resolve(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        if (
            (name is "." or "..") ||
            (name.IndexOfAny(anyOf: ['/', '\\', ':']) >= 0) ||
            !string.Equals(a: name, b: name.ToLowerInvariant(), comparisonType: StringComparison.Ordinal)
        ) {
            throw new ArgumentException(message: $"'{name}' is not one lower-case path segment.", paramName: nameof(name));
        }

        return Path.Join(
            path1: Root,
            path2: name
        );
    }
}
