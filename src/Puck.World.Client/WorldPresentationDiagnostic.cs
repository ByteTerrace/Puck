namespace Puck.World.Client;

/// <summary>The console presentation of the same structured domain transitions the inspector consumes.</summary>
internal static class WorldPresentationDiagnostic {
    public static void Report(WorldValueDomainDiagnostic diagnostic) => Console.Error.WriteLine(value: $"[world.presentation: {diagnostic}]");
}
