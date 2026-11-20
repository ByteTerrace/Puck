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
}
