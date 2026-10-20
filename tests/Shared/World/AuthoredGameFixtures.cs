namespace Puck.World.Testing;

/// <summary>The repository root the authored-game fixtures read from.</summary>
internal static partial class AuthoredGameFixtures {
    public static string Root { get; } = RepositoryPaths.RequireRoot();
}
