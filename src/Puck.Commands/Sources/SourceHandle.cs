namespace Puck.Commands;

/// <summary>What kind of image a <see cref="SourceHandle"/> names.</summary>
public enum SourceHandleKind : byte {
    /// <summary>A source instance: an image a registered producer supplies, such as an emulator, a desktop capture or a
    /// camera, whose hit ends at its pixels.</summary>
    Producer = 0,
    /// <summary>The output of another render-graph instance, such as a game camera or a nested world.</summary>
    Instance = 1,
}
/// <summary>Names the image a <see cref="SourceMapping"/> shows: a source instance a producer supplies, or a rendered
/// render-graph instance, each by its instance name, so two sources of one producer opened with different settings are
/// two handles. A hit on a rendered instance's image continues into that instance's camera; a hit on a source ends
/// there.</summary>
/// <param name="Kind">What kind of image the handle names.</param>
/// <param name="Name">The instance name.</param>
public readonly record struct SourceHandle(SourceHandleKind Kind, string Name) {
    /// <summary>Creates a handle naming a source instance a registered producer supplies.</summary>
    /// <param name="name">The source instance's name.</param>
    /// <returns>The handle.</returns>
    public static SourceHandle Producer(string name) => new(
        Kind: SourceHandleKind.Producer,
        Name: name
    );
    /// <summary>Creates a handle naming a render-graph instance's output.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The handle.</returns>
    public static SourceHandle Instance(string name) => new(
        Kind: SourceHandleKind.Instance,
        Name: name
    );
}
