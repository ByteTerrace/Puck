using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.World.Schema.Tests;

public class WorldCallArgumentsLawTests {
    [Fact]
    public void ACallArmBelongsToItsDeclaredPosition() {
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(baseType: typeof(Puck.State.StateTransform), discriminator: "sort"));
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(baseType: typeof(Puck.State.StateTransform.Sort), discriminator: "sort"));
        Assert.Equal(typeof(Puck.State.StateTransform.Sort), WorldCallArguments.ArmType(baseType: null, discriminator: "sort"));
        Assert.Null(@object: WorldCallArguments.ArmType(baseType: typeof(WorldRule), discriminator: "sort"));
        Assert.Null(@object: WorldCallArguments.ArmType(baseType: typeof(Puck.State.ActionEffect), discriminator: "sort"));
    }
    [Fact]
    public void ASharedDiscriminatorRetainsEveryConcreteArm() {
        Assert.Equal(typeof(ActionPredicate.All), WorldCallArguments.ArmType(baseType: typeof(ActionPredicate.All), discriminator: "all"));
        Assert.Equal(typeof(PatternNode.Both), WorldCallArguments.ArmType(baseType: typeof(PatternNode.Both), discriminator: "all"));
        Assert.Equal(typeof(CellSetExpression.Everything), WorldCallArguments.ArmType(baseType: typeof(CellSetExpression.Everything), discriminator: "all"));
    }
    [Fact]
    public void LookingUpUnknownAuthoredMembersDoesNotAllocateCacheEntries() {
        const int QueriesPerWindow = 1000;
        var names = Enumerable.Range(count: (QueriesPerWindow * AllocationWindow.MaximumWindows), start: 0).Select(selector: static i => $"missing_{i}").ToArray();

        _ = WorldCallArguments.Classify(member: "mode", owner: typeof(WorldRule));
        var offset = 0;
        var allocated = AllocationWindow.Least(window: () => {
            // Every window queries fresh misses so an implementation retaining them cannot pass on its next run.
            var end = (offset + QueriesPerWindow);

            for (; (offset < end); offset++) {
                _ = WorldCallArguments.Classify(owner: typeof(WorldRule), member: names[offset]);
                _ = WorldCallArguments.MemberType(owner: typeof(WorldRule), member: names[offset]);
            }
        });

        Assert.Equal(actual: allocated, expected: 0);
    }
    [Fact]
    public void EnumWordLookupsDoNotRebuildTheEnumNames() {
        Assert.True(condition: WorldCallArguments.IsChoiceWord(member: "mode", owner: typeof(WorldRule), word: "level"));
        var admitted = true;
        var allocated = AllocationWindow.Least(window: () => {
            for (var index = 0; (index < 1000); index++) {
                admitted &= WorldCallArguments.IsChoiceWord(member: "mode", owner: typeof(WorldRule), word: "Level");
            }
        });

        Assert.True(condition: admitted);
        Assert.Equal(actual: allocated, expected: 0);
    }
}
