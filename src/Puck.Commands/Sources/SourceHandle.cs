namespace Puck.Commands;

/// <summary>What kind of image a <see cref="SourceHandle"/> names.</summary>
public enum SourceHandleKind : byte {
    /// <summary>An image a registered producer supplies, such as an emulator, a desktop capture or a camera.</summary>
    Producer = 0,
    /// <summary>The output of another render-graph instance, such as a game camera or a nested world.</summary>
    Instance = 1,
}
/// <summary>Names the image a <see cref="SourceMapping"/> shows: a producer by its registered id, or a render-graph
/// instance by its name. A hit on an instance's image continues into that instance's camera.</summary>
/// <param name="Kind">What kind of image the handle names.</param>
/// <param name="Name">The producer id or the instance name.</param>
public readonly record struct SourceHandle(SourceHandleKind Kind, string Name) {
    /// <summary>Creates a handle naming a registered producer.</summary>
    /// <param name="id">The producer's registered id.</param>
    /// <returns>The handle.</returns>
    public static SourceHandle Producer(string id) => new(
        Kind: SourceHandleKind.Producer,
        Name: id
    );
    /// <summary>Creates a handle naming a render-graph instance's output.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The handle.</returns>
    public static SourceHandle Instance(string name) => new(
        Kind: SourceHandleKind.Instance,
        Name: name
    );
}
