using Puck.World.Protocol;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Each input bank and target-register reach has an independent capacity.</summary>
public sealed class ChannelOrdinalBudgetLawTests {
    private static WorldDefinition Document(int axes, int buttons, int registers = 0) {
        var rows = new List<WorldChannel> {
            new("forward", ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new("strafe", ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new("turn", ChannelShape.Bipolar, Role: ChannelRole.Turn),
        };

        for (var index = 3; (index < axes); index++) {
            rows.Add(item: new($"axis{index}", ChannelShape.Unipolar, Composition: true));
        }
        for (var index = 0; (index < buttons); index++) {
            rows.Add(item: new($"button{index}", ChannelShape.Binary, Composition: true));
        }
        return Fixtures.BuildDocument() with {
            ChannelsRaw = rows,
            TargetRegistersRaw = Enumerable.Range(count: registers, start: 0).Select(selector: index => new WorldTargetRegister($"target{index}", 4, 30, false)).ToArray(),
        };
    }

    [Fact]
    public void FullBanksAndTargetRegistersAreIndependent() {
        var document = Document(axes: ChannelLimits.MaxAxes, buttons: ChannelLimits.MaxButtons, registers: 64);

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
        var table = WorldChannelTable.Compile(channels: document.Channels);

        Assert.True(condition: table.TryGetOrdinal(name: "button63", ordinal: out var last));
        Assert.True(condition: table.TryGetOrdinal(name: "axis31", ordinal: out var axis));
        var intent = default(PlayerIntent).WithChannel(ordinal: last, value: FixedQ4816.One).WithChannel(ordinal: axis, value: -FixedQ4816.One);

        Assert.Equal(FixedQ4816.One, intent[last]);
        Assert.Equal(-FixedQ4816.One, intent[axis]);
        Assert.Equal((1UL << 63), intent.Channels.Buttons);
        var reach = new ChannelReachMask(default(ChannelMask).With(ordinal: last)).WithRegister(index: 63);

        Assert.True(condition: reach.Contains(ordinal: last));
        Assert.False(condition: reach.Contains(ordinal: axis));
        Assert.True(condition: reach.ContainsRegister(index: 63));
        Assert.False(condition: reach.ContainsRegister(index: 0));
    }
    [InlineData(33, 0, "numeric bank", "axis32")]
    [InlineData(3, 65, "button bank", "button64")]
    [Theory]
    public void AnOverBudgetBankNamesItsChannels(int axes, int buttons, string bank, string name) {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: Document(axes, buttons), neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: bank);
        Assert.Contains(actualString: reason, expectedSubstring: name);
    }
}
