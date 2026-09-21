using Puck.Testing;
using Xunit;

namespace Puck.World.Schema.Tests;

public class WorldCallArgumentsLawTests {
    [Fact]
    public void ACallArmBelongsToItsDeclaredPosition() {
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(typeof(Puck.State.StateTransform), "sort"));
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(typeof(Puck.State.StateTransform.Sort), "sort"));
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(null, "sort"));
        Assert.Null(WorldCallArguments.ArmType(typeof(WorldRule), "sort"));
        Assert.Null(WorldCallArguments.ArmType(typeof(Puck.State.ActionEffect), "sort"));
    }

    [Fact]
    public void ASharedDiscriminatorRetainsEveryConcreteArm() {
        Assert.Equal(typeof(ActionPredicate.All), WorldCallArguments.ArmType(typeof(ActionPredicate.All), "all"));
        Assert.Equal(typeof(PatternNode.Both), WorldCallArguments.ArmType(typeof(PatternNode.Both), "all"));
        Assert.Equal(typeof(CellSetExpression.Everything), WorldCallArguments.ArmType(typeof(CellSetExpression.Everything), "all"));
    }

    [Fact]
    public void LookingUpUnknownAuthoredMembersDoesNotAllocateCacheEntries() {
        const int QueriesPerWindow = 1000;
        var names = Enumerable.Range(0, (QueriesPerWindow * AllocationWindow.MaximumWindows)).Select(selector: static i => $"missing_{i}").ToArray();

        _ = WorldCallArguments.Classify(owner: typeof(WorldRule), member: "mode");
        var offset = 0;
        var allocated = AllocationWindow.Least(window: () => {
            // Every window queries fresh misses so an implementation retaining them cannot pass on its next run.
            var end = (offset + QueriesPerWindow);

            for (; (offset < end); offset++) {
                _ = WorldCallArguments.Classify(owner: typeof(WorldRule), member: names[offset]);
                _ = WorldCallArguments.MemberType(owner: typeof(WorldRule), member: names[offset]);
            }
        });

        Assert.Equal(0, allocated);
    }
    [Fact]
    public void EnumWordLookupsDoNotRebuildTheEnumNames() {
        Assert.True(WorldCallArguments.IsChoiceWord(owner: typeof(WorldRule), member: "mode", word: "level"));
        var admitted = true;
        var allocated = AllocationWindow.Least(window: () => {
            for (var index = 0; (index < 1000); index++) {
                admitted &= WorldCallArguments.IsChoiceWord(owner: typeof(WorldRule), member: "mode", word: "Level");
            }
        });

        Assert.True(condition: admitted);
        Assert.Equal(0, allocated);
    }
}
