using System.Security.Cryptography;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>
/// The authored boot ROMs' standing gate: every revision's image assembles to the bytes recorded here. The builder
/// solves its own timing by booting the image it emitted, so a change anywhere in that loop — the emitted program, the
/// prediction tables it walks, or the machine it measures itself against — moves an image, and a moved image moves its
/// hash. A deliberate correction re-records the hash it moved in the same change; nothing else may.
/// <para>
/// The handoff comparison itself is not run here: the <c>boot-rom-handoff</c> stage owns it, and owns it alone, so a
/// divergence is reported once with the reference cartridges that pin its divider counter in scope.
/// </para>
/// </summary>
public sealed class BootRomBuilderTests {
    public static TheoryData<ConsoleModel, string> Images =>
        new() {
            { ConsoleModel.Dmg0, "A5CB52C7AECA12E32986C6781EFAED465F18FEC851C279B7F2AD9019660441D3" },
            { ConsoleModel.DmgB, "D989BB69DF96E27009774815C1074EE9832295349817848C5D39C15E01C8871D" },
            { ConsoleModel.DmgC, "D989BB69DF96E27009774815C1074EE9832295349817848C5D39C15E01C8871D" },
            { ConsoleModel.Mgb, "6C772DBF4EB99216423FDADD830C6CD3888A92D15C8C76F402EF025ACE501519" },
            { ConsoleModel.Sgb, "43504F160E478B8E1F259DFF020E54584589799ADE2FBF1267F13E0387EEAD05" },
            { ConsoleModel.Sgb2, "26CF83FB4B2C36A4C4EDFC5CD2900B1EEFE5AFA0C1CA3176BCA821FECE05BF16" },
            { ConsoleModel.Cgb0, "010A0D409DAF39959FA4C5C443B89B8E465D83A58A055F9B1F59335631578EFB" },
            { ConsoleModel.CgbA, "B7124C074511D5001A94A566DB197C9477956AD0F7E38F9C54FAEF4C6F280CD7" },
            { ConsoleModel.CgbB, "B7124C074511D5001A94A566DB197C9477956AD0F7E38F9C54FAEF4C6F280CD7" },
            { ConsoleModel.CgbC, "B7124C074511D5001A94A566DB197C9477956AD0F7E38F9C54FAEF4C6F280CD7" },
            { ConsoleModel.CgbD, "B7124C074511D5001A94A566DB197C9477956AD0F7E38F9C54FAEF4C6F280CD7" },
            { ConsoleModel.CgbE, "B7124C074511D5001A94A566DB197C9477956AD0F7E38F9C54FAEF4C6F280CD7" },
            { ConsoleModel.Agb, "6363FB849B93027A5CCEEB6511D05CFB111C67EDA9404CE969A1379CB8915832" },
            { ConsoleModel.Ags, "6363FB849B93027A5CCEEB6511D05CFB111C67EDA9404CE969A1379CB8915832" },
        };

    [Fact]
    public void Images_CoverEveryRevision() =>
        Assert.Equal(
            actual: Images.Count,
            expected: Enum.GetValues<ConsoleModel>().Length
        );
    [Theory]
    [MemberData(memberName: nameof(Images))]
    public void Build_MatchesTheRecordedImage(ConsoleModel model, string expected) =>
        Assert.Equal(
            actual: Convert.ToHexString(inArray: SHA256.HashData(source: BootRomBuilder.Build(model: model))),
            expected: expected
        );
}
