namespace Puck.World.Client;

/// <summary>The view registration names cameras render under (<see cref="WorldSeatAnchors.RegistrationName"/>),
/// resolved once per camera name and seat: a seat-relative camera's generated seat view name is kept, so a caller
/// that resolves it every frame, such as a probe polling a camera's view, allocates nothing after the first.</summary>
public sealed class WorldViewRegistrationNames {
    private readonly Dictionary<(string Camera, int Seat), string> m_names = [];

    /// <summary>Returns the view registration name a camera renders under for <paramref name="seat"/>.</summary>
    /// <param name="camera">The camera row.</param>
    /// <param name="seat">The 1-based seat.</param>
    /// <returns>The camera's own name, or its seat view name for a seat-relative camera, the same instance on every
    /// call for the same camera name and seat.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="camera"/> is <see langword="null"/>.</exception>
    public string Of(WorldCamera camera, int seat) {
        ArgumentNullException.ThrowIfNull(argument: camera);

        if (!camera.IsSeatRelative) {
            return camera.Name;
        }
        if (!m_names.TryGetValue(
            key: (camera.Name, seat),
            value: out var name
        )) {
            name = WorldSeatAnchors.RegistrationName(
                camera: camera,
                seat: seat
            );
            m_names.Add(
                key: (camera.Name, seat),
                value: name
            );
        }

        return name;
    }
}
