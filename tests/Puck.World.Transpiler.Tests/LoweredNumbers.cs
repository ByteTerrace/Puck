using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Tests;

/// <summary>Reads a lowered number the way the lowering's own consumers do. A lowered node holds an integer, an exact
/// decimal or a computed double, and <see cref="JsonNode.GetValue{T}"/> converts only to the type the node was built
/// from.</summary>
internal static class LoweredNumbers {
    public static double? AsNumber(this JsonNode? node) => (DocumentLowering.TryReadNumber(
        node: node,
        number: out var number
    )
        ? number
        : null
    );
}
