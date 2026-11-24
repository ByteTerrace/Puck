namespace Puck.Cli.Automation;

/// <summary>The paths inside the world-silo image (<c>src/Puck.World.Silo/Dockerfile</c>) that a step running one of its
/// containers names. The Dockerfile is the other side of this coupling: a path moved there moves here.</summary>
public static class WorldSiloImageLayout {
    /// <summary>The directory the image keeps its composed hosted documents in, inside the authored asset layout it ships
    /// beside them, and the image's working directory: the directory a hosted document's relative machine content and
    /// assets resolve beside, which every silo document run in the image names as its <c>documentDirectory</c>.</summary>
    public const string WorldsDirectory = "/app/worlds";
    /// <summary>The entry assembly of the Puck CLI the image carries beside the silo.</summary>
    public const string CliAssembly = "/puck-cli/Puck.Cli.dll";
    /// <summary>The silo's entry assembly, which the image's own entry point runs with <c>dotnet</c>.</summary>
    public const string SiloAssembly = "/app/Puck.World.Silo.dll";

    /// <summary>Returns the arguments that follow the image in <c>docker run --entrypoint dotnet</c> to run the hosted MCP
    /// shape: the image's CLI running <c>puck mcp</c>, which composes the installed extensions over the silo it hosts.</summary>
    /// <param name="silo">The silo document's path inside the container.</param>
    /// <param name="http">The remote MCP configuration's path inside the container.</param>
    /// <returns>The command arguments, in order.</returns>
    public static string[] McpArguments(string silo, string http) => [CliAssembly, "mcp", "--silo", silo, "--http", http];
}
