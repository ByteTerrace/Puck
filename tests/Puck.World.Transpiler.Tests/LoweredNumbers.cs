using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Tests;

/// <summary>Reads a lowered number the way the lowering's own consumers do. A lowered node holds an integer, an exact
/// decimal or a computed double, and <see cref="JsonNode.GetValue{T}"/> converts only to the type the node was built
/// from.</summary>
internal static class LoweredNumbers {
    /// <summary>Returns <paramref name="node"/> read as a number, failing when it holds none.</summary>
    /// <param name="node">A lowered node.</param>
    /// <returns>The number.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="node"/> is not a number.</exception>
    public static double AsDouble(this JsonNode? node) => (node.AsNumber() ?? throw new InvalidOperationException(message: $"'{node?.ToJsonString()}' is not a number"));
    public static double? AsNumber(this JsonNode? node) => (DocumentLowering.TryReadNumber(
        node: node,
        number: out var number
    )
        ? number
        : null
    );
}
