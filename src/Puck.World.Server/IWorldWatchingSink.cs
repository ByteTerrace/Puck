namespace Puck.World.Server;

/// <summary>Marks a typed sink as an observer whose attention the world's host cannot read: one fed across a
/// connection to another process. While such a sink is attached the host counts the world as
/// <see cref="WorldAttention.Watched"/> (<see cref="WorldOutputHub.Watchers"/>), so a world that lapses below that
/// attention keeps stepping for an observer whose distance nobody here can measure.</summary>
public interface IWorldWatchingSink;
