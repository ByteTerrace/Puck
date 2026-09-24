using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>One render node a GPU work readout reports: the name it prints the node under, the node's completed work,
/// and, when the node counts the objects it creates, those lifetime counts.</summary>
/// <param name="Name">The name the readout prints the node under (<c>world</c>, <c>overlay</c>, <c>view:&lt;name&gt;</c>).</param>
/// <param name="Work">The node's completed-work source.</param>
/// <param name="Lifetime">The node's object-lifetime counters, or <see langword="null"/> when it counts none.</param>
public readonly record struct GpuWorkNode(string Name, IGpuWorkSource Work, IWorkCounterSource? Lifetime);
