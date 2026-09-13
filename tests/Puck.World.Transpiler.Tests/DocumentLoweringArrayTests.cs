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

        existing.AppendNode(item: existingNode);
        target["values"] = existing;

        var first = new JsonObject { ["id"] = "first" };
        var second = new JsonObject { ["id"] = "second" };
        var addition = new JsonArray();

        addition.AppendNode(item: first);
        addition.AppendNode(item: null);
        addition.AppendNode(item: second);

        DocumentLowering.AssignOrExtend(
            key: "values",
            target: target,
            value: addition
        );

        Assert.Same(
            existing,
            target["values"]
        );
        Assert.Empty(collection: addition);
        Assert.Equal(
            4,
            existing.Count
        );
        Assert.Same(
            existingNode,
            existing[0]
        );
        Assert.Same(
            first,
            existing[1]
        );
        Assert.Null(@object: existing[2]);
        Assert.Same(
            second,
            existing[3]
        );
        Assert.Same(
            existing,
            existingNode.Parent
        );
        Assert.Same(
            existing,
            first.Parent
        );
        Assert.Same(
            existing,
            second.Parent
        );
    }
    [Fact]
    public void AssignOrExtendSupportsSelfArrayAliasWithoutChangingOrder() {
        var target = new JsonObject();
        var values = new JsonArray();
        var first = new JsonObject { ["id"] = "first" };
        var second = new JsonObject { ["id"] = "second" };

        values.AppendNode(item: first);
        values.AppendNode(item: null);
        values.AppendNode(item: second);
        target["values"] = values;

        DocumentLowering.AssignOrExtend(
            key: "values",
            target: target,
            value: values
        );

        Assert.Same(
            values,
            target["values"]
        );
        Assert.Equal(
            3,
            values.Count
        );
        Assert.Same(
            first,
            values[0]
        );
        Assert.Null(@object: values[1]);
        Assert.Same(
            second,
            values[2]
        );
        Assert.Same(
            values,
            first.Parent
        );
        Assert.Same(
            values,
            second.Parent
        );
    }
}
