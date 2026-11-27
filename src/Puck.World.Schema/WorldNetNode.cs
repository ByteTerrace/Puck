using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>One node of an authority net as a tick left it: the placement carrying the net, the node's place in its grid
/// (row by row) and its offset from its rest position, in metres. A snapshot carries the nodes that moved since the one
/// before it, and every node of every net when it primes a reader.</summary>
/// <param name="Placement">The placement carrying the net.</param>
/// <param name="Node">The node, row by row.</param>
/// <param name="Offset">The node's offset from rest, in metres.</param>
public readonly record struct WorldNetNode(string Placement, int Node, FixedVector3 Offset);
