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
            { ConsoleModel.Dmg0, "BAEAEC2FD1026CCF8233852FFD5055AE30E1374F076937031AE548D369546FB0" },
            { ConsoleModel.DmgB, "7E410FC5EBE035A09162A74056FA3A0C197F64B1FA209A000E99315EADEF2837" },
            { ConsoleModel.DmgC, "7E410FC5EBE035A09162A74056FA3A0C197F64B1FA209A000E99315EADEF2837" },
            { ConsoleModel.Mgb, "EEA863755F5FBCC87B401D9D89C95F12F143966E5B4E34296A317C793FE439A6" },
            { ConsoleModel.Sgb, "DC06ADBE154E1158FED07E827E8E8C413A4F2CA472923DE67F4161ED70246247" },
            { ConsoleModel.Sgb2, "AE813532051FB65FB79B348C166BA782F18199B1CFF1C008B418045FDD3C1267" },
            { ConsoleModel.Cgb0, "A4588B2DF0EE9017AEDB4E0EF988E8162B4976EA821BDCC8348A83998575318C" },
            { ConsoleModel.CgbA, "B3BF73599D6DD52A1FA5ADBEEB616101F9CA6D076B19D1344B222A413C63C596" },
            { ConsoleModel.CgbB, "B3BF73599D6DD52A1FA5ADBEEB616101F9CA6D076B19D1344B222A413C63C596" },
            { ConsoleModel.CgbC, "B3BF73599D6DD52A1FA5ADBEEB616101F9CA6D076B19D1344B222A413C63C596" },
            { ConsoleModel.CgbD, "B3BF73599D6DD52A1FA5ADBEEB616101F9CA6D076B19D1344B222A413C63C596" },
            { ConsoleModel.CgbE, "B3BF73599D6DD52A1FA5ADBEEB616101F9CA6D076B19D1344B222A413C63C596" },
            { ConsoleModel.Agb, "FDB1F3064B6A82B9D4AA0FAD847ADFFC8C15A32FF3AB665FB61F853EC3FAFE3F" },
            { ConsoleModel.Ags, "FDB1F3064B6A82B9D4AA0FAD847ADFFC8C15A32FF3AB665FB61F853EC3FAFE3F" },
        };

    [Fact]
    public void Images_CoverEveryRevision() =>
        Assert.Equal(
            actual: Images.Count,
            expected: Enum.GetValues<ConsoleModel>().Length
        );
    [Theory]
    [MemberData(memberName: nameof(Images))]
    public void Build_MatchesTheRecordedImage(ConsoleModel model, string expected) {
        var image = BootRomBuilder.Build(model: model);
        Assert.Equal(expected: model.SupportsColor() ? BootRomBuilder.ColorLength : BootRomBuilder.MonochromeLength,
            actual: image.Length);
        var actual = Convert.ToHexString(inArray: SHA256.HashData(source: image));
        Assert.True(condition: actual == expected, userMessage: $"{model} image SHA-256: {actual}; expected: {expected}");
    }
}
