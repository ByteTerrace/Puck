using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>The root size sampled by authority contact for one solid placement.</summary>
public readonly record struct WorldSolidScale(string Placement, FixedQ4816 Scale);
