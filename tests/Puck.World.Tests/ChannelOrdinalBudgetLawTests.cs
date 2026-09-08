using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Channels and target registers share one Drive-reach ordinal space bounded by
/// <see cref="ChannelLimits.MaxChannels"/>. The whole-document validator refuses an over-budget table by name at every
/// count, ahead of any fold that would index a per-ordinal array with an authored count.</summary>
public sealed class ChannelOrdinalBudgetLawTests {
    private static WorldDefinition Document(int channels, int registers) {
        var rows = new WorldChannel[channels];

        rows[0] = new WorldChannel(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance);
        rows[1] = new WorldChannel(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe);
        rows[2] = new WorldChannel(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn);

        for (var index = 3; (index < channels); index++) {
            rows[index] = new WorldChannel(Name: $"filler{index}", Shape: ChannelShape.Unipolar, Composition: true);
        }

        var registerRows = new WorldTargetRegister[registers];

        for (var index = 0; (index < registers); index++) {
            registerRows[index] = new WorldTargetRegister(
                Name: $"register{index}",
                MaximumRange: 4f,
                MaximumHalfAngleDegrees: 30f,
                RequiresLineOfSight: false
            );
        }

        return (Fixtures.BuildDocument() with {
            ChannelsRaw = rows,
            TargetRegistersRaw = registerRows,
        });
    }
    private static bool TryValidate(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );

    /// <summary>A table over budget only once its registers are counted refuses by name, exactly as an over-budget
    /// channel list alone does — never as an unnamed index fault out of a per-ordinal array.</summary>
    [Fact]
    public void ChannelsPlusRegistersPastTheCeiling_Refuses_ControlAtTheCeilingClean() {
        Laws.RefusalWithControl(
            lawId: "channels.shared-ordinal-budget",
            deniedOutcome: static () => TryValidate(definition: Document(channels: ChannelLimits.MaxChannels, registers: 2)),
            controlOutcome: static () => TryValidate(definition: Document(channels: (ChannelLimits.MaxChannels - 2), registers: 2))
        );
    }
    /// <summary>The refusal names the ceiling rather than surfacing as an index fault, at any over-budget count.</summary>
    [Theory]
    [InlineData(17, 0)]
    [InlineData(16, 1)]
    [InlineData(16, 8)]
    [InlineData(64, 64)]
    public void EveryOverBudgetCount_NamesTheCeiling(int channels, int registers) {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(channels: channels, registers: registers),
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"the maximum is {ChannelLimits.MaxChannels}"
        );
    }
}
