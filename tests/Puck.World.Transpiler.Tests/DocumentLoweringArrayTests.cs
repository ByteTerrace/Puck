using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class DocumentLoweringArrayTests {
    [Fact]
    public void AssignOrExtendMovesNodesInOrderAndPreservesNullsAndParents() {
        var target = new JsonObject();
        var existing = new JsonArray();
        var existingNode = new JsonObject { ["id"] = "existing" };
        existing.AppendNode(existingNode);
        target["values"] = existing;

        var first = new JsonObject { ["id"] = "first" };
        var second = new JsonObject { ["id"] = "second" };
        var addition = new JsonArray();
        addition.AppendNode(first);
        addition.AppendNode(null);
        addition.AppendNode(second);

        DocumentLowering.AssignOrExtend(target, "values", addition);

        Assert.Same(existing, target["values"]);
        Assert.Empty(addition);
        Assert.Equal(4, existing.Count);
        Assert.Same(existingNode, existing[0]);
        Assert.Same(first, existing[1]);
        Assert.Null(existing[2]);
        Assert.Same(second, existing[3]);
        Assert.Same(existing, existingNode.Parent);
        Assert.Same(existing, first.Parent);
        Assert.Same(existing, second.Parent);
    }

    [Fact]
    public void AssignOrExtendSupportsSelfArrayAliasWithoutChangingOrder() {
        var target = new JsonObject();
        var values = new JsonArray();
        var first = new JsonObject { ["id"] = "first" };
        var second = new JsonObject { ["id"] = "second" };
        values.AppendNode(first);
        values.AppendNode(null);
        values.AppendNode(second);
        target["values"] = values;

        DocumentLowering.AssignOrExtend(target, "values", values);

        Assert.Same(values, target["values"]);
        Assert.Equal(3, values.Count);
        Assert.Same(first, values[0]);
        Assert.Null(values[1]);
        Assert.Same(second, values[2]);
        Assert.Same(values, first.Parent);
        Assert.Same(values, second.Parent);
    }
}