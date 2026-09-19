using Puck.Transpiler.Rewriting;

namespace Puck.Cli.Transpiler;

/// <summary>The migrations <c>puck migrate</c> can run.</summary>
/// <remarks>A migration is registered here for as long as the reshape it serves has sources left to rewrite, and
/// removed with the reshape's own landing. An empty registry is the resting state, not a defect: <c>migrate</c> with
/// any name then lists nothing and exits non-zero.</remarks>
internal static class PuckMigrations {
    /// <summary>Gets the registered migrations, ordered by name.</summary>
    public static IReadOnlyList<PuckMigration> Registered { get; } = [];

    /// <summary>Returns the registered migration named <paramref name="name"/>.</summary>
    /// <param name="name">The name to resolve.</param>
    /// <param name="migrations">The migrations to search, or <see langword="null"/> for <see cref="Registered"/>.</param>
    /// <returns>The migration, or <see langword="null"/> when no registered migration carries that name.</returns>
    public static PuckMigration? Resolve(string name, IReadOnlyList<PuckMigration>? migrations = null) =>
        (migrations ?? Registered).FirstOrDefault(predicate: migration => string.Equals(
            a: migration.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));
}
