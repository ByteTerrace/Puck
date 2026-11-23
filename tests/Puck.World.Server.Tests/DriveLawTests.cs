using Puck.Commands;
using Xunit;

using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>drive</c> row — the anisotropic drive facets a kit authors beside the motion
/// row, read by <c>ResolveDriveFrame</c>/<c>ShapeVelocity</c>. The trace law drives one body 240 ticks through
/// every drive facet (throttle, speed-scaled steering, reversal, backward travel, a held drift stretch, a held sprint stretch,
/// a pitched frame) and pins the fixed-point result raw value for raw value; its discriminating control perturbs a
/// single drive facet and requires the trace to move. The facet law proves a drive program refuses by name against a
/// kit authoring no row, where the same kit with one is admitted.
/// </summary>
public sealed class DriveLawTests {
    private static readonly Lazy<WorldChannelTable> InputTable = new(valueFactory: () => WorldChannelTable.Compile(channels: BuildDriveDocument().Channels));

    private static int InputAddress(string name) => (InputTable.Value.TryGetOrdinal(name: name, ordinal: out var address) ? address : throw new InvalidOperationException(message: name));

    private static int BoostOrdinal => InputAddress(name: "boost");
    private static int DriftOrdinal => InputAddress(name: "drift");
    private static int ForwardOrdinal => InputAddress(name: "forward");
    private static int PitchOrdinal => InputAddress(name: "pitch");
    private static int TurnOrdinal => InputAddress(name: "turn");

    // Per tick: position x/y/z, planar velocity x/y/z, vertical velocity, yaw, drive pitch — each the raw
    // FixedQ4816 storage in hex. Recorded from the run this fixture describes; a deliberate correction to the drive
    // arithmetic is expected to move it and gets re-recorded with the correction.
    private static readonly string[] DriveTrace240 = [
        "0000000000000000 ffffffffffffffe3 ffffffffffffff93 0000000000000000 0000000000000000 ffffffffffff999a ffffffffffffe445 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffffa8 fffffffffffffeb9 0000000000000000 0000000000000000 ffffffffffff3334 ffffffffffffc889 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffff4f fffffffffffffd71 0000000000000000 0000000000000000 fffffffffffecccd ffffffffffffaccd 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffed9 fffffffffffffbbc 0000000000000000 0000000000000000 fffffffffffe6667 ffffffffffff9112 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffe45 fffffffffffff99a 0000000000000000 0000000000000000 fffffffffffe0000 ffffffffffff7556 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffd93 fffffffffffff70b 0000000000000000 0000000000000000 fffffffffffd999a ffffffffffff599a 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffcc4 fffffffffffff40e 0000000000000000 0000000000000000 fffffffffffd3334 ffffffffffff3dde 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffbd8 fffffffffffff0a4 0000000000000000 0000000000000000 fffffffffffccccd ffffffffffff2223 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffffacd ffffffffffffeccd 0000000000000000 0000000000000000 fffffffffffc6667 ffffffffffff0667 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff9a5 ffffffffffffe889 0000000000000000 0000000000000000 fffffffffffc0000 fffffffffffeeaab 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff860 ffffffffffffe3d8 0000000000000000 0000000000000000 fffffffffffb999a fffffffffffeceef 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff6fd ffffffffffffdeb9 0000000000000000 0000000000000000 fffffffffffb3334 fffffffffffeb334 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff57d ffffffffffffd92d 0000000000000000 0000000000000000 fffffffffffacccd fffffffffffe9778 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff3de ffffffffffffd334 0000000000000000 0000000000000000 fffffffffffa6667 fffffffffffe7bbc 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff223 ffffffffffffcccd 0000000000000000 0000000000000000 fffffffffffa0000 fffffffffffe6000 0000000000000000 0000000000000000",
        "0000000000000000 fffffffffffff049 ffffffffffffc5fa 0000000000000000 0000000000000000 fffffffffff9999a fffffffffffe4445 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffee52 ffffffffffffbeb9 0000000000000000 0000000000000000 fffffffffff93334 fffffffffffe2889 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffec3e ffffffffffffb70b 0000000000000000 0000000000000000 fffffffffff8cccd fffffffffffe0ccd 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffea0c ffffffffffffaeef 0000000000000000 0000000000000000 fffffffffff86667 fffffffffffdf112 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffe7bc ffffffffffffa667 0000000000000000 0000000000000000 fffffffffff80000 fffffffffffdd556 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffe54f ffffffffffff9d71 0000000000000000 0000000000000000 fffffffffff7999a fffffffffffdb99a 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffe2c4 ffffffffffff940e 0000000000000000 0000000000000000 fffffffffff73334 fffffffffffd9dde 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffe01c ffffffffffff8a3e 0000000000000000 0000000000000000 fffffffffff6cccd fffffffffffd8223 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffdd56 ffffffffffff8001 0000000000000000 0000000000000000 fffffffffff66667 fffffffffffd6667 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffda72 ffffffffffff7556 0000000000000000 0000000000000000 fffffffffff60000 fffffffffffd4aab 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffd771 ffffffffffff6a3e 0000000000000000 0000000000000000 fffffffffff5999a fffffffffffd2eef 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffd452 ffffffffffff5eb9 0000000000000000 0000000000000000 fffffffffff53334 fffffffffffd1334 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffd116 ffffffffffff52c7 0000000000000000 0000000000000000 fffffffffff4cccd fffffffffffcf778 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffcdbc ffffffffffff4667 0000000000000000 0000000000000000 fffffffffff46667 fffffffffffcdbbc 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffca45 ffffffffffff399a 0000000000000000 0000000000000000 fffffffffff40000 fffffffffffcc000 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffc6b0 ffffffffffff2c60 0000000000000000 0000000000000000 fffffffffff3999a fffffffffffca445 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffc2fd ffffffffffff1eb9 0000000000000000 0000000000000000 fffffffffff33334 fffffffffffc8889 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffbf2d ffffffffffff10a4 0000000000000000 0000000000000000 fffffffffff2cccd fffffffffffc6ccd 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffbb3f ffffffffffff0223 0000000000000000 0000000000000000 fffffffffff26667 fffffffffffc5112 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffb734 fffffffffffef334 0000000000000000 0000000000000000 fffffffffff20000 fffffffffffc3556 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffb30b fffffffffffee3d8 0000000000000000 0000000000000000 fffffffffff1999a fffffffffffc199a 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffaec4 fffffffffffed40e 0000000000000000 0000000000000000 fffffffffff13334 fffffffffffbfdde 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffaa60 fffffffffffec3d8 0000000000000000 0000000000000000 fffffffffff0cccd fffffffffffbe223 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffa5de fffffffffffeb334 0000000000000000 0000000000000000 fffffffffff06667 fffffffffffbc667 0000000000000000 0000000000000000",
        "0000000000000000 ffffffffffffa13f fffffffffffea223 0000000000000000 0000000000000000 fffffffffff00000 fffffffffffbaaab 0000000000000000 0000000000000000",
        "ffffffffffffffe8 ffffffffffff9c82 fffffffffffe9111 ffffffffffffe980 0000000000000000 ffffffffffefffe0 fffffffffffb8eef 0000000000000168 0000000000000000",
        "ffffffffffffffb8 ffffffffffff97a8 fffffffffffe8001 ffffffffffffd300 0000000000000000 fffffffffff00041 fffffffffffb7334 00000000000002d0 0000000000000000",
        "ffffffffffffff70 ffffffffffff92b0 fffffffffffe6ef0 ffffffffffffbc80 0000000000000000 fffffffffff00063 fffffffffffb5778 0000000000000439 0000000000000000",
        "ffffffffffffff10 ffffffffffff8d9a fffffffffffe5de0 ffffffffffffa600 0000000000000000 fffffffffff00106 fffffffffffb3bbc 00000000000005a1 0000000000000000",
        "fffffffffffffe98 ffffffffffff8867 fffffffffffe4cd1 ffffffffffff8f5f 0000000000000000 fffffffffff00167 fffffffffffb2000 000000000000070a 0000000000000000",
        "fffffffffffffe08 ffffffffffff8316 fffffffffffe3bc2 ffffffffffff78e0 0000000000000000 fffffffffff0024b fffffffffffb0445 0000000000000872 0000000000000000",
        "fffffffffffffd60 ffffffffffff7da8 fffffffffffe2ab4 ffffffffffff625f 0000000000000000 fffffffffff002ef fffffffffffae889 00000000000009db 0000000000000000",
        "fffffffffffffca0 ffffffffffff781c fffffffffffe19a7 ffffffffffff4be1 0000000000000000 fffffffffff00413 fffffffffffacccd 0000000000000b43 0000000000000000",
        "fffffffffffffbc8 ffffffffffff7272 fffffffffffe089b ffffffffffff353f 0000000000000000 fffffffffff004f5 fffffffffffab112 0000000000000cac 0000000000000000",
        "fffffffffffffad8 ffffffffffff6cab fffffffffffdf791 ffffffffffff1ee2 0000000000000000 fffffffffff0065e fffffffffffa9556 0000000000000e14 0000000000000000",
        "fffffffffffff9cf ffffffffffff66c7 fffffffffffde688 ffffffffffff0860 0000000000000000 fffffffffff00784 fffffffffffa799a 0000000000000f7c 0000000000000000",
        "fffffffffffff8af ffffffffffff60c4 fffffffffffdd580 fffffffffffef1df 0000000000000000 fffffffffff008cb fffffffffffa5dde 00000000000010e5 0000000000000000",
        "fffffffffffff777 ffffffffffff5aa4 fffffffffffdc47b fffffffffffedb82 0000000000000000 fffffffffff00a96 fffffffffffa4223 000000000000124d 0000000000000000",
        "fffffffffffff627 ffffffffffff5467 fffffffffffdb377 fffffffffffec500 0000000000000000 fffffffffff00c1d fffffffffffa2667 00000000000013b6 0000000000000000",
        "fffffffffffff4bf ffffffffffff4e0c fffffffffffda274 fffffffffffeae7e 0000000000000000 fffffffffff00dc5 fffffffffffa0aab 000000000000151e 0000000000000000",
        "fffffffffffff33f ffffffffffff4793 fffffffffffd9174 fffffffffffe9801 0000000000000000 fffffffffff00fec fffffffffff9eeef 0000000000001687 0000000000000000",
        "fffffffffffff1a7 ffffffffffff40fd fffffffffffd8076 fffffffffffe819f 0000000000000000 fffffffffff011da fffffffffff9d334 00000000000017ef 0000000000000000",
        "ffffffffffffeff8 ffffffffffff3a49 fffffffffffd6f7a fffffffffffe6b1d 0000000000000000 fffffffffff013e2 fffffffffff9b778 0000000000001958 0000000000000000",
        "ffffffffffffee30 ffffffffffff3378 fffffffffffd5e81 fffffffffffe54e2 0000000000000000 fffffffffff01676 fffffffffff99bbc 0000000000001ac0 0000000000000000",
        "ffffffffffffec50 ffffffffffff2c89 fffffffffffd4d8a fffffffffffe3e3f 0000000000000000 fffffffffff018b8 fffffffffff98000 0000000000001c29 0000000000000000",
        "ffffffffffffea59 ffffffffffff257d fffffffffffd3c96 fffffffffffe27fe 0000000000000000 fffffffffff01b2e fffffffffff96445 0000000000001d91 0000000000000000",
        "ffffffffffffe849 ffffffffffff1e53 fffffffffffd2ba5 fffffffffffe11a3 0000000000000000 fffffffffff01e1c fffffffffff94889 0000000000001ef9 0000000000000000",
        "ffffffffffffe622 ffffffffffff170b fffffffffffd1ab7 fffffffffffdfb42 0000000000000000 fffffffffff020cc fffffffffff92ccd 0000000000002062 0000000000000000",
        "ffffffffffffe3e3 ffffffffffff0fa6 fffffffffffd09cc fffffffffffde4df 0000000000000000 fffffffffff0239b fffffffffff91112 00000000000021ca 0000000000000000",
        "ffffffffffffe18c ffffffffffff0823 fffffffffffcf8e4 fffffffffffdce9e 0000000000000000 fffffffffff02692 fffffffffff8f556 0000000000002333 0000000000000000",
        "ffffffffffffdf1e ffffffffffff0082 fffffffffffce800 fffffffffffdb83c 0000000000000000 fffffffffff029a1 fffffffffff8d99a 000000000000249b 0000000000000000",
        "ffffffffffffdc97 fffffffffffef8c4 fffffffffffcd71f fffffffffffda1e2 0000000000000000 fffffffffff02d2f fffffffffff8bdde 0000000000002604 0000000000000000",
        "ffffffffffffd9f9 fffffffffffef0e9 fffffffffffcc641 fffffffffffd8bc2 0000000000000000 fffffffffff03090 fffffffffff8a223 000000000000276c 0000000000000000",
        "ffffffffffffd743 fffffffffffee8f0 fffffffffffcb568 fffffffffffd7581 0000000000000000 fffffffffff03408 fffffffffff88667 00000000000028d5 0000000000000000",
        "ffffffffffffd476 fffffffffffee0d9 fffffffffffca492 fffffffffffd5f40 0000000000000000 fffffffffff037a0 fffffffffff86aab 0000000000002a3d 0000000000000000",
        "ffffffffffffd190 fffffffffffed8a4 fffffffffffc93c0 fffffffffffd48fe 0000000000000000 fffffffffff03b57 fffffffffff84eef 0000000000002ba6 0000000000000000",
        "ffffffffffffce93 fffffffffffed053 fffffffffffc82f3 fffffffffffd32bd 0000000000000000 fffffffffff03f2d fffffffffff83334 0000000000002d0e 0000000000000000",
        "ffffffffffffcb7e fffffffffffec7e3 fffffffffffc7229 fffffffffffd1c9d 0000000000000000 fffffffffff0432e fffffffffff81778 0000000000002e76 0000000000000000",
        "ffffffffffffc852 fffffffffffebf56 fffffffffffc6164 fffffffffffd067d 0000000000000000 fffffffffff0474f fffffffffff7fbbc 0000000000002fdf 0000000000000000",
        "ffffffffffffc50e fffffffffffeb6ab fffffffffffc50a4 fffffffffffcf05d 0000000000000000 fffffffffff04b8f fffffffffff7e000 0000000000003147 0000000000000000",
        "ffffffffffffc1b3 fffffffffffeade3 fffffffffffc3fe8 fffffffffffcda5f 0000000000000000 fffffffffff04ffc fffffffffff7c445 00000000000032b0 0000000000000000",
        "ffffffffffffbe40 fffffffffffea4fd fffffffffffc2f31 fffffffffffcc41c 0000000000000000 fffffffffff0546e fffffffffff7a889 0000000000003418 0000000000000000",
        "ffffffffffffbab5 fffffffffffe9bfa fffffffffffc1e7f fffffffffffcae1f 0000000000000000 fffffffffff05919 fffffffffff78ccd 0000000000003581 0000000000000000",
        "ffffffffffffb713 fffffffffffe92d9 fffffffffffc0dd2 fffffffffffc9821 0000000000000000 fffffffffff05de3 fffffffffff77112 00000000000036e9 0000000000000000",
        "ffffffffffffb35a fffffffffffe899a fffffffffffbfd2a fffffffffffc8223 0000000000000000 fffffffffff062cc fffffffffff75556 0000000000003852 0000000000000000",
        "ffffffffffffaf89 fffffffffffe803e fffffffffffbec88 fffffffffffc6c3a 0000000000000000 fffffffffff06785 fffffffffff7399a 00000000000039ba 0000000000000000",
        "ffffffffffffaba1 fffffffffffe76c4 fffffffffffbdbeb fffffffffffc5618 0000000000000000 fffffffffff06c9e fffffffffff71dde 0000000000003b23 0000000000000000",
        "ffffffffffffa7a1 fffffffffffe6d2d fffffffffffbcb53 fffffffffffc4060 0000000000000000 fffffffffff07201 fffffffffff70223 0000000000003c8b 0000000000000000",
        "ffffffffffffa38a fffffffffffe6378 fffffffffffbbac1 fffffffffffc2a85 0000000000000000 fffffffffff07773 fffffffffff6e667 0000000000003df3 0000000000000000",
        "ffffffffffff9f5c fffffffffffe59a6 fffffffffffbaa35 fffffffffffc149a 0000000000000000 fffffffffff07caa fffffffffff6caab 0000000000003f5c 0000000000000000",
        "ffffffffffff9b16 fffffffffffe4fb6 fffffffffffb99af fffffffffffbfebe 0000000000000000 fffffffffff0825a fffffffffff6aeef 00000000000040c4 0000000000000000",
        "ffffffffffff96b9 fffffffffffe45a8 fffffffffffb8930 fffffffffffbe906 0000000000000000 fffffffffff08838 fffffffffff69334 000000000000422d 0000000000000000",
        "ffffffffffff9245 fffffffffffe3b7d fffffffffffb78b6 fffffffffffbd33e 0000000000000000 fffffffffff08dd9 fffffffffff67778 0000000000004395 0000000000000000",
        "ffffffffffff8e0c fffffffffffe3134 fffffffffffb68ba fffffffffffc09bc 0000000000000000 fffffffffff103ef fffffffffff65bbc 000000000000422e 0000000000000000",
        "ffffffffffff8a0b fffffffffffe26cd fffffffffffb593c fffffffffffc3f97 0000000000000000 fffffffffff17a54 fffffffffff64000 00000000000040b9 0000000000000000",
        "ffffffffffff8643 fffffffffffe1c49 fffffffffffb4a3d fffffffffffc74b3 0000000000000000 fffffffffff1f0ec fffffffffff62445 0000000000003f38 0000000000000000",
        "ffffffffffff82b3 fffffffffffe11a8 fffffffffffb3bbd fffffffffffca91b 0000000000000000 fffffffffff267d7 fffffffffff60889 0000000000003dab 0000000000000000",
        "ffffffffffff7f5b fffffffffffe06e9 fffffffffffb2dbc fffffffffffcdca6 0000000000000000 fffffffffff2df2a fffffffffff5eccd 0000000000003c11 0000000000000000",
        "ffffffffffff7c38 fffffffffffdfc0c fffffffffffb203b fffffffffffd0f3b 0000000000000000 fffffffffff356b7 fffffffffff5d112 0000000000003a6b 0000000000000000",
        "ffffffffffff7949 fffffffffffdf112 fffffffffffb133a fffffffffffd40ca 0000000000000000 fffffffffff3ceb5 fffffffffff5b556 00000000000038b9 0000000000000000",
        "ffffffffffff768f fffffffffffde5fa fffffffffffb06b9 fffffffffffd7178 0000000000000000 fffffffffff44729 fffffffffff5999a 00000000000036fa 0000000000000000",
        "ffffffffffff7408 fffffffffffddac4 fffffffffffafab9 fffffffffffda0e5 0000000000000000 fffffffffff4c00a fffffffffff57dde 0000000000003530 0000000000000000",
        "ffffffffffff71b2 fffffffffffdcf71 fffffffffffaef3a fffffffffffdcf2b 0000000000000000 fffffffffff53948 fffffffffff56223 0000000000003358 0000000000000000",
        "ffffffffffff6f8b fffffffffffdc401 fffffffffffae43d fffffffffffdfc20 0000000000000000 fffffffffff5b2fc fffffffffff54667 0000000000003175 0000000000000000",
        "ffffffffffff6d93 fffffffffffdb872 fffffffffffad9c3 fffffffffffe27b3 0000000000000000 fffffffffff62d0b fffffffffff52aab 0000000000002f85 0000000000000000",
        "ffffffffffff6bc9 fffffffffffdacc7 fffffffffffacfcb fffffffffffe51e2 0000000000000000 fffffffffff6a781 fffffffffff50eef 0000000000002d89 0000000000000000",
        "ffffffffffff6a29 fffffffffffda0fd fffffffffffac656 fffffffffffe7a89 0000000000000000 fffffffffff72264 fffffffffff4f334 0000000000002b81 0000000000000000",
        "ffffffffffff68b4 fffffffffffd9516 fffffffffffabd64 fffffffffffea1a6 0000000000000000 fffffffffff79db3 fffffffffff4d778 000000000000296c 0000000000000000",
        "ffffffffffff6766 fffffffffffd8912 fffffffffffab4f7 fffffffffffec715 0000000000000000 fffffffffff81966 fffffffffff4bbbc 000000000000274c 0000000000000000",
        "ffffffffffff663e fffffffffffd7cf0 fffffffffffaad0e fffffffffffeeac3 0000000000000000 fffffffffff8958e fffffffffff4a000 000000000000251e 0000000000000000",
        "ffffffffffff653a fffffffffffd70b0 fffffffffffaa5aa ffffffffffff0cae 0000000000000000 fffffffffff9121a fffffffffff48445 00000000000022e5 0000000000000000",
        "ffffffffffff6459 fffffffffffd6453 fffffffffffa9ecb ffffffffffff2cb2 0000000000000000 fffffffffff98f12 fffffffffff46889 000000000000209f 0000000000000000",
        "ffffffffffff6398 fffffffffffd57d8 fffffffffffa9872 ffffffffffff4ad6 0000000000000000 fffffffffffa0c5e fffffffffff44ccd 0000000000001e4d 0000000000000000",
        "ffffffffffff62f4 fffffffffffd4b3f fffffffffffa929f ffffffffffff66d4 0000000000000000 fffffffffffa8a12 fffffffffff43112 0000000000001bef 0000000000000000",
        "ffffffffffff626d fffffffffffd3e89 fffffffffffa8d52 ffffffffffff80cb 0000000000000000 fffffffffffb0824 fffffffffff41556 0000000000001984 0000000000000000",
        "ffffffffffff61fe fffffffffffd31b6 fffffffffffa888c ffffffffffff9890 0000000000000000 fffffffffffb8688 fffffffffff3f99a 000000000000170d 0000000000000000",
        "ffffffffffff61a7 fffffffffffd24c4 fffffffffffa844d ffffffffffffae17 0000000000000000 fffffffffffc0530 fffffffffff3ddde 000000000000148a 0000000000000000",
        "ffffffffffff6164 fffffffffffd17b6 fffffffffffa8096 ffffffffffffc138 0000000000000000 fffffffffffc842a fffffffffff3c223 00000000000011fc 0000000000000000",
        "ffffffffffff6132 fffffffffffd0a89 fffffffffffa7d67 ffffffffffffd0eb 0000000000000000 fffffffffffd0371 fffffffffff3a667 0000000000000fbf 0000000000000000",
        "ffffffffffff610d fffffffffffcfd3f fffffffffffa7abf ffffffffffffdd8f 0000000000000000 fffffffffffd82f4 fffffffffff38aab 0000000000000dd5 0000000000000000",
        "ffffffffffff60f3 fffffffffffcefd8 fffffffffffa78a0 ffffffffffffe7a1 0000000000000000 fffffffffffe029a fffffffffff36eef 0000000000000c3d 0000000000000000",
        "ffffffffffff60e2 fffffffffffce253 fffffffffffa7709 ffffffffffffefa5 0000000000000000 fffffffffffe8260 fffffffffff35334 0000000000000af7 0000000000000000",
        "ffffffffffff60d7 fffffffffffcd4b0 fffffffffffa75fa fffffffffffff612 0000000000000000 ffffffffffff0239 fffffffffff33778 0000000000000a02 0000000000000000",
        "ffffffffffff60d2 fffffffffffcc6f0 fffffffffffa7574 fffffffffffffb63 0000000000000000 ffffffffffff821e fffffffffff31bbc 0000000000000960 0000000000000000",
        "ffffffffffff60d2 fffffffffffcb912 fffffffffffa7574 0000000000000000 0000000000000000 0000000000000000 fffffffffff30000 000000000000090f 0000000000000000",
        "ffffffffffff60d5 fffffffffffcab16 fffffffffffa75e0 000000000000039f 0000000000000000 0000000000006657 fffffffffff2e445 000000000000090f 0000000000000000",
        "ffffffffffff60dd fffffffffffc9cfd fffffffffffa76ba 0000000000000772 0000000000000000 000000000000ccaa fffffffffff2c889 000000000000094f 0000000000000000",
        "ffffffffffff60ea fffffffffffc8ec7 fffffffffffa7802 0000000000000bc9 0000000000000000 00000000000132fa fffffffffff2accd 00000000000009d3 0000000000000000",
        "ffffffffffff60fc fffffffffffc8073 fffffffffffa79b6 00000000000010f0 0000000000000000 0000000000019940 fffffffffff29112 0000000000000a97 0000000000000000",
        "ffffffffffff6114 fffffffffffc7201 fffffffffffa7bd8 0000000000001738 0000000000000000 000000000001ff76 fffffffffff27556 0000000000000b9d 0000000000000000",
        "ffffffffffff6135 fffffffffffc6371 fffffffffffa7e66 0000000000001eeb 0000000000000000 0000000000026596 fffffffffff2599a 0000000000000ce5 0000000000000000",
        "ffffffffffff6160 fffffffffffc54c4 fffffffffffa8161 0000000000002861 0000000000000000 000000000002cb9e fffffffffff23dde 0000000000000e6e 0000000000000000",
        "ffffffffffff6198 fffffffffffc45fa fffffffffffa84c9 00000000000033df 0000000000000000 000000000003317d fffffffffff22223 0000000000001039 0000000000000000",
        "ffffffffffff61de fffffffffffc3712 fffffffffffa889e 00000000000041b9 0000000000000000 000000000003972e fffffffffff20667 0000000000001245 0000000000000000",
        "ffffffffffff6236 fffffffffffc280c fffffffffffa8cdf 0000000000005236 0000000000000000 000000000003fc92 fffffffffff1eaab 0000000000001493 0000000000000000",
        "ffffffffffff62a2 fffffffffffc18e9 fffffffffffa918b 00000000000065a1 0000000000000000 000000000004619d fffffffffff1ceef 0000000000001722 0000000000000000",
        "ffffffffffff6325 fffffffffffc09a8 fffffffffffa96a3 0000000000007af0 0000000000000000 000000000004c66b fffffffffff1b334 00000000000019a8 0000000000000000",
        "ffffffffffff63bb fffffffffffbfa4a fffffffffffa9bf0 0000000000008c6d 0000000000000000 000000000004f83f fffffffffff19778 0000000000001c23 0000000000000000",
        "ffffffffffff645e fffffffffffbeace fffffffffffaa13b 00000000000098a8 0000000000000000 000000000004f6dc fffffffffff17bbc 0000000000001e9a 0000000000000000",
        "ffffffffffff650e fffffffffffbdb34 fffffffffffaa685 000000000000a4e2 0000000000000000 000000000004f556 fffffffffff16000 0000000000002111 0000000000000000",
        "ffffffffffff65cb fffffffffffbcb7d fffffffffffaabce 000000000000b112 0000000000000000 000000000004f3b2 fffffffffff14445 0000000000002388 0000000000000000",
        "ffffffffffff6673 fffffffffffbbba8 fffffffffffab0aa 0000000000009dec 0000000000000000 0000000000048ee7 fffffffffff12889 0000000000002016 0000000000000000",
        "ffffffffffff6708 fffffffffffbabb6 fffffffffffab51b 0000000000008c26 0000000000000000 00000000000429db fffffffffff10ccd 0000000000001c95 0000000000000000",
        "ffffffffffff678c fffffffffffb9ba6 fffffffffffab920 0000000000007bca 0000000000000000 000000000003c499 fffffffffff0f112 0000000000001906 0000000000000000",
        "ffffffffffff6801 fffffffffffb8b78 fffffffffffabcb9 0000000000006cc5 0000000000000000 0000000000035f18 fffffffffff0d556 000000000000159f 0000000000000000",
        "ffffffffffff6866 fffffffffffb7b2d fffffffffffabfe5 0000000000005ef7 0000000000000000 000000000002f96f fffffffffff0b99a 0000000000001294 0000000000000000",
        "ffffffffffff68be fffffffffffb6ac4 fffffffffffac2a5 000000000000523a 0000000000000000 00000000000293a3 fffffffffff09dde 0000000000000fe6 0000000000000000",
        "ffffffffffff6909 fffffffffffb5a3e fffffffffffac4f8 0000000000004668 0000000000000000 0000000000022db8 fffffffffff08223 0000000000000d93 0000000000000000",
        "ffffffffffff6948 fffffffffffb499a fffffffffffac6de 0000000000003b5f 0000000000000000 000000000001c7b4 fffffffffff06667 0000000000000b9d 0000000000000000",
        "ffffffffffff697c fffffffffffb38d9 fffffffffffac857 00000000000030f9 0000000000000000 00000000000161a3 fffffffffff04aab 0000000000000a03 0000000000000000",
        "ffffffffffff69a6 fffffffffffb27fa fffffffffffac963 0000000000002712 0000000000000000 000000000000fb84 fffffffffff02eef 00000000000008c4 0000000000000000",
        "ffffffffffff69c5 fffffffffffb16fd fffffffffffaca02 0000000000001d85 0000000000000000 000000000000955c fffffffffff01334 00000000000007e2 0000000000000000",
        "ffffffffffff69db fffffffffffb05e3 fffffffffffaca35 000000000000142e 0000000000000000 0000000000002f2f ffffffffffeff778 000000000000075b 0000000000000000",
        "ffffffffffff69e7 fffffffffffaf4ab fffffffffffac9fb 0000000000000ae9 0000000000000000 ffffffffffffc902 ffffffffffefdbbc 0000000000000731 0000000000000000",
        "ffffffffffff69e8 fffffffffffae356 fffffffffffac953 000000000000018f 0000000000000000 ffffffffffff62d6 ffffffffffefc000 0000000000000761 0000000000000000",
        "ffffffffffff69e1 fffffffffffad1e3 fffffffffffac83f fffffffffffff7ff 0000000000000000 fffffffffffefcaf ffffffffffefa445 00000000000007ed 0000000000000000",
        "ffffffffffff69d3 fffffffffffac053 fffffffffffac6bd fffffffffffff383 0000000000000000 fffffffffffe9664 ffffffffffef8889 00000000000008d6 0000000000000000",
        "ffffffffffff69c0 fffffffffffaaea5 fffffffffffac4ce ffffffffffffedaf 0000000000000000 fffffffffffe3026 ffffffffffef6ccd 0000000000000a1a 0000000000000000",
        "ffffffffffff69a4 fffffffffffa9cd9 fffffffffffac273 ffffffffffffe60c 0000000000000000 fffffffffffdc9fb ffffffffffef5112 0000000000000bba 0000000000000000",
        "ffffffffffff697e fffffffffffa8af0 fffffffffffabfaa ffffffffffffdc36 0000000000000000 fffffffffffd63f7 ffffffffffef3556 0000000000000db5 0000000000000000",
        "ffffffffffff694a fffffffffffa78e9 fffffffffffabc75 ffffffffffffcfac 0000000000000000 fffffffffffcfe22 ffffffffffef199a 000000000000100d 0000000000000000",
        "ffffffffffff6908 fffffffffffa66c5 fffffffffffab8d3 ffffffffffffc1cd 0000000000000000 fffffffffffc987d ffffffffffeefdde 00000000000012c0 0000000000000000",
        "ffffffffffff68b6 fffffffffffa5483 fffffffffffab4c5 ffffffffffffb2b6 0000000000000000 fffffffffffc3302 ffffffffffeee223 00000000000015cf 0000000000000000",
        "ffffffffffff6852 fffffffffffa4223 fffffffffffab04b ffffffffffffa247 0000000000000000 fffffffffffbcdbe ffffffffffeec667 0000000000001939 0000000000000000",
        "ffffffffffff67db fffffffffffa2fa6 fffffffffffaab66 ffffffffffff906e 0000000000000000 fffffffffffb68b8 ffffffffffeeaaab 0000000000001cc8 0000000000000000",
        "ffffffffffff674f fffffffffffa1d0b fffffffffffaa615 ffffffffffff7d34 0000000000000000 fffffffffffb03ec ffffffffffee8eef 0000000000002048 0000000000000000",
        "ffffffffffff66ae fffffffffffa0a53 fffffffffffaa058 ffffffffffff689f 0000000000000000 fffffffffffa9f70 ffffffffffee7334 00000000000023bb 0000000000000000",
        "ffffffffffff65f5 fffffffffff9f77d fffffffffffa9a31 ffffffffffff52b5 0000000000000000 fffffffffffa3b37 ffffffffffee5778 0000000000002720 0000000000000000",
        "ffffffffffff6523 fffffffffff9e489 fffffffffffa939f ffffffffffff3b7e 0000000000000000 fffffffffff9d74e ffffffffffee3bbc 0000000000002a77 0000000000000000",
        "ffffffffffff6437 fffffffffff9d178 fffffffffffa8ca3 ffffffffffff22fd 0000000000000000 fffffffffff973ac ffffffffffee2000 0000000000002dc1 0000000000000000",
        "ffffffffffff6330 fffffffffff9be4a fffffffffffa853d ffffffffffff093b 0000000000000000 fffffffffff91058 ffffffffffee0445 00000000000030fd 0000000000000000",
        "ffffffffffff620c fffffffffff9aafd fffffffffffa7d6e fffffffffffeee3e 0000000000000000 fffffffffff8ad5e ffffffffffede889 000000000000342c 0000000000000000",
        "ffffffffffff60ca fffffffffff99794 fffffffffffa7535 fffffffffffed20b 0000000000000000 fffffffffff84ac1 ffffffffffedcccd 000000000000374d 0000000000000000",
        "ffffffffffff5f69 fffffffffff9840c fffffffffffa6c93 fffffffffffeb4a8 0000000000000000 fffffffffff7e869 ffffffffffedb112 0000000000003a60 0000000000000000",
        "ffffffffffff5de7 fffffffffff97067 fffffffffffa6389 fffffffffffe961d 0000000000000000 fffffffffff78677 ffffffffffed9556 0000000000003d66 0000000000000000",
        "ffffffffffff5c43 fffffffffff95ca5 fffffffffffa5a17 fffffffffffe766f 0000000000000000 fffffffffff724d8 ffffffffffed799a 000000000000405e 0000000000000000",
        "ffffffffffff5a7c fffffffffff948c5 fffffffffffa503d fffffffffffe55a3 0000000000000000 fffffffffff6c392 ffffffffffed5dde 0000000000004349 0000000000000000",
        "ffffffffffff5891 fffffffffff934c7 fffffffffffa45fb fffffffffffe33c7 0000000000000000 fffffffffff662cb ffffffffffed4223 0000000000004627 0000000000000000",
        "ffffffffffff5681 fffffffffff920ac fffffffffffa3b53 fffffffffffe10d8 0000000000000000 fffffffffff6024f ffffffffffed2667 00000000000048f7 0000000000000000",
        "ffffffffffff544a fffffffffff90c73 fffffffffffa3044 fffffffffffdecdd 0000000000000000 fffffffffff5a235 ffffffffffed0aab 0000000000004bb9 0000000000000000",
        "ffffffffffff51ec fffffffffff8f81c fffffffffffa24d0 fffffffffffdc7e1 0000000000000000 fffffffffff54284 ffffffffffeceeef 0000000000004e6e 0000000000000000",
        "ffffffffffff4f55 fffffffffff8e3a8 fffffffffffa18fb fffffffffffd91f5 0000000000000000 fffffffffff4e85f ffffffffffecd334 0000000000005054 0000000000000000",
        "ffffffffffff4c83 fffffffffff8cf16 fffffffffffa0cc6 fffffffffffd5b5f 0000000000000000 fffffffffff48e94 ffffffffffecb778 0000000000005230 0000000000000000",
        "ffffffffffff4977 fffffffffff8ba67 fffffffffffa0032 fffffffffffd242d 0000000000000000 fffffffffff43549 ffffffffffec9bbc 0000000000005402 0000000000000000",
        "ffffffffffff462e fffffffffff8a59a fffffffffff9f33f fffffffffffcec56 0000000000000000 fffffffffff3dc52 ffffffffffec8000 00000000000055cb 0000000000000000",
        "ffffffffffff42aa fffffffffff890b0 fffffffffff9e5ee fffffffffffcb3e7 0000000000000000 fffffffffff383bd ffffffffffec6445 000000000000578a 0000000000000000",
        "ffffffffffff3ee9 fffffffffff87ba8 fffffffffff9d83e fffffffffffc7ad9 0000000000000000 fffffffffff32b75 ffffffffffec4889 000000000000593f 0000000000000000",
        "ffffffffffff3aea fffffffffff86683 fffffffffff9ca31 fffffffffffc4141 0000000000000000 fffffffffff2d3a7 ffffffffffec2ccd 0000000000005aeb 0000000000000000",
        "ffffffffffff36ad fffffffffff8513f fffffffffff9bbc7 fffffffffffc0713 0000000000000000 fffffffffff27c25 ffffffffffec1112 0000000000005c8d 0000000000000000",
        "ffffffffffff3232 fffffffffff83bdf fffffffffff9acff fffffffffffbcc5c 0000000000000000 fffffffffff22505 ffffffffffebf556 0000000000005e25 0000000000000000",
        "ffffffffffff2d77 fffffffffff82660 fffffffffff99ddb fffffffffffb9118 0000000000000000 fffffffffff1ce30 ffffffffffebd99a 0000000000005fb4 0000000000000000",
        "ffffffffffff287d fffffffffff810c5 fffffffffff98e5b fffffffffffb5557 0000000000000000 fffffffffff177c9 ffffffffffebbdde 0000000000006139 0000000000000000",
        "ffffffffffff2343 fffffffffff7fb0b fffffffffff97e7f fffffffffffb191f 0000000000000000 fffffffffff121d9 ffffffffffeba223 00000000000062b5 0000000000000000",
        "ffffffffffff1dc7 fffffffffff7e534 fffffffffff96e48 fffffffffffadc66 0000000000000000 fffffffffff0cc2d ffffffffffeb8667 0000000000006427 0000000000000000",
        "ffffffffffff180b fffffffffff7cf3f fffffffffff95db5 fffffffffffa9f33 0000000000000000 fffffffffff076d6 ffffffffffeb6aab 000000000000658f 0000000000000000",
        "ffffffffffff120c fffffffffff7b92d fffffffffff94cc8 fffffffffffa6188 0000000000000000 fffffffffff021d1 ffffffffffeb4eef 00000000000066f8 0000000000000000",
        "ffffffffffff0bcc fffffffffff7a2fd fffffffffff93b81 fffffffffffa2363 0000000000000000 ffffffffffefcd1b ffffffffffeb3334 0000000000006860 0000000000000000",
        "ffffffffffff0548 fffffffffff78cb0 fffffffffff929e0 fffffffffff9e4c9 0000000000000000 ffffffffffef78c7 ffffffffffeb1778 00000000000069c8 0000000000000000",
        "fffffffffffefe82 fffffffffff77645 fffffffffff917e5 fffffffffff9a5bc 0000000000000000 ffffffffffef24d4 ffffffffffeafbbc 0000000000006b31 0000000000000000",
        "fffffffffffef777 fffffffffff75fbd fffffffffff90591 fffffffffff96639 0000000000000000 ffffffffffeed13c ffffffffffeae000 0000000000006c99 0000000000000000",
        "fffffffffffef028 fffffffffff74916 fffffffffff8f2e4 fffffffffff92633 0000000000000000 ffffffffffee7dd6 ffffffffffeac445 0000000000006e02 0000000000000000",
        "fffffffffffee895 fffffffffff73253 fffffffffff8dfde fffffffffff8e5ca 0000000000000000 ffffffffffee2afc ffffffffffeaa889 0000000000006f6a 0000000000000000",
        "fffffffffffee0bc fffffffffff71b72 fffffffffff8cc81 fffffffffff8a4d8 0000000000000000 ffffffffffedd848 ffffffffffea8ccd 00000000000070d3 0000000000000000",
        "fffffffffffed89e fffffffffff70473 fffffffffff8b8cc fffffffffff86380 0000000000000000 ffffffffffed8616 ffffffffffea7112 000000000000723b 0000000000000000",
        "fffffffffffed039 fffffffffff6ed56 fffffffffff8a4bf fffffffffff821c3 0000000000000000 ffffffffffed345d ffffffffffea5556 00000000000073a4 0000000000000000",
        "fffffffffffec78e fffffffffff6d61c fffffffffff8905c fffffffffff7df7e 0000000000000000 ffffffffffece2d7 ffffffffffea399a 000000000000750c 0000000000000000",
        "fffffffffffebe9c fffffffffff6bec5 fffffffffff87ba2 fffffffffff79cc0 0000000000000000 ffffffffffec919c ffffffffffea1dde 0000000000007675 0000000000000000",
        "fffffffffffeb562 fffffffffff6a74f fffffffffff86692 fffffffffff759a2 0000000000000000 ffffffffffec40eb ffffffffffea0223 00000000000077dd 0000000000000000",
        "fffffffffffeabe0 fffffffffff68fbd fffffffffff8512c fffffffffff715f6 0000000000000000 ffffffffffebf054 ffffffffffe9e667 0000000000007945 0000000000000000",
        "fffffffffffea215 fffffffffff6780c fffffffffff83b70 fffffffffff6d1e5 0000000000000000 ffffffffffeba03a ffffffffffe9caab 0000000000007aae 0000000000000000",
        "fffffffffffe9801 fffffffffff6603e fffffffffff82560 fffffffffff68d80 0000000000000000 ffffffffffeb50bb ffffffffffe9aeef 0000000000007c16 0000000000000000",
        "fffffffffffe8da4 fffffffffff64853 fffffffffff80efb fffffffffff6488f 0000000000000000 ffffffffffeb0162 ffffffffffe99334 0000000000007d7f 0000000000000000",
        "fffffffffffe82fd fffffffffff6304a fffffffffff7f842 fffffffffff6031f 0000000000000000 ffffffffffeab24a ffffffffffe97778 0000000000007ee7 0000000000000000",
        "fffffffffffe784a fffffffffff6183b fffffffffff7e1a9 fffffffffff5f89f 000000000000164f ffffffffffead05d ffffffffffe95bbc 0000000000007ee7 00000000000000f5",
        "fffffffffffe6d8c fffffffffff60026 fffffffffff7cb2f fffffffffff5ee42 0000000000002ca4 ffffffffffeaee5c ffffffffffe94000 0000000000007ee7 00000000000001eb",
        "fffffffffffe62c4 fffffffffff5e80c fffffffffff7b4d6 fffffffffff5e3e1 00000000000042d0 ffffffffffeb0c5a ffffffffffe92445 0000000000007ee7 00000000000002e1",
        "fffffffffffe57f0 fffffffffff5cfeb fffffffffff79e9d fffffffffff5d9a2 00000000000058d4 ffffffffffeb2a9f ffffffffffe90889 0000000000007ee7 00000000000003d7",
        "fffffffffffe4d11 fffffffffff5b7c4 fffffffffff78884 fffffffffff5cf29 0000000000006eae ffffffffffeb48d1 ffffffffffe8eccd 0000000000007ee7 00000000000004cc",
        "fffffffffffe4228 fffffffffff59f97 fffffffffff7728c fffffffffff5c4e2 000000000000845f ffffffffffeb673d ffffffffffe8d112 0000000000007ee7 00000000000005c2",
        "fffffffffffe3733 fffffffffff58763 fffffffffff75cb4 fffffffffff5baa3 00000000000099e6 ffffffffffeb85c6 ffffffffffe8b556 0000000000007ee7 00000000000006b8",
        "fffffffffffe2c33 fffffffffff56f28 fffffffffff746fd fffffffffff5b070 000000000000af45 ffffffffffeba442 ffffffffffe8999a 0000000000007ee7 00000000000007ae",
        "fffffffffffe2129 fffffffffff556e6 fffffffffff73167 fffffffffff5a658 000000000000c44d ffffffffffebc2d0 ffffffffffe87dde 0000000000007ee7 00000000000008a3",
        "fffffffffffe1614 fffffffffff53e9d fffffffffff71bf1 fffffffffff59c12 000000000000d92c ffffffffffebe19b ffffffffffe86223 0000000000007ee7 0000000000000999",
        "fffffffffffe0af4 fffffffffff5264c fffffffffff7069c fffffffffff591f8 000000000000ee3e ffffffffffec003c ffffffffffe84667 0000000000007ee7 0000000000000a8f",
        "fffffffffffdffc9 fffffffffff50df4 fffffffffff6f168 fffffffffff587f3 00000000000102f8 ffffffffffec1f1c ffffffffffe82aab 0000000000007ee7 0000000000000b85",
        "fffffffffffdf494 fffffffffff4f595 fffffffffff6dc55 fffffffffff57de2 000000000001175b ffffffffffec3e1f ffffffffffe80eef 0000000000007ee7 0000000000000c7a",
        "fffffffffffde954 fffffffffff4dd2d fffffffffff6c763 fffffffffff573d3 0000000000012bc2 ffffffffffec5d0f ffffffffffe7f334 0000000000007ee7 0000000000000d70",
        "fffffffffffdde10 fffffffffff4c4be fffffffffff6b28e fffffffffff57073 000000000001402e ffffffffffec7876 ffffffffffe7d778 0000000000007ee7 0000000000000e66",
        "fffffffffffdd2d7 fffffffffff4ac46 fffffffffff69dce fffffffffff57b13 0000000000015419 ffffffffffec8c31 ffffffffffe7bbbc 0000000000007ee7 0000000000000f5c",
        "fffffffffffdc7aa fffffffffff493c6 fffffffffff68923 fffffffffff585e1 00000000000167dc ffffffffffeca01a ffffffffffe7a000 0000000000007ee7 0000000000001051",
        "fffffffffffdbc89 fffffffffff47b3d fffffffffff6748e fffffffffff590b9 0000000000017b74 ffffffffffecb417 ffffffffffe78445 0000000000007ee7 0000000000001147",
        "fffffffffffdb173 fffffffffff462ac fffffffffff6600e fffffffffff59b98 0000000000018f39 ffffffffffecc84b ffffffffffe76889 0000000000007ee7 000000000000123d",
        "fffffffffffda669 fffffffffff44a11 fffffffffff64ba4 fffffffffff5a67c 000000000001a27d ffffffffffecdc88 ffffffffffe74ccd 0000000000007ee7 0000000000001333",
        "fffffffffffd9b6b fffffffffff4316e fffffffffff6374f fffffffffff5b178 000000000001b59b ffffffffffecf078 ffffffffffe73112 0000000000007ee7 0000000000001428",
        "fffffffffffd9078 fffffffffff418c1 fffffffffff62310 fffffffffff5bc6a 000000000001c891 ffffffffffed04aa ffffffffffe71556 0000000000007ee7 000000000000151e",
        "fffffffffffd8591 fffffffffff4000b fffffffffff60ee7 fffffffffff5c73c 000000000001dbb3 ffffffffffed191e ffffffffffe6f99a 0000000000007ee7 0000000000001614",
        "fffffffffffd7ab5 fffffffffff3e74b fffffffffff5fad3 fffffffffff5d233 000000000001ee56 ffffffffffed2d5a ffffffffffe6ddde 0000000000007ee7 000000000000170a",
        "fffffffffffd6fe5 fffffffffff3ce81 fffffffffff5e6d5 fffffffffff5dd53 00000000000200a6 ffffffffffed41bd ffffffffffe6c223 0000000000007ee7 00000000000017ff",
        "fffffffffffd6522 fffffffffff3b5ae fffffffffff5d2ec fffffffffff5e844 0000000000021323 ffffffffffed5619 ffffffffffe6a667 0000000000007ee7 00000000000018f5",
        "fffffffffffd5a6a fffffffffff39cd0 fffffffffff5bf1a fffffffffff5f38b 0000000000022577 ffffffffffed6aa2 ffffffffffe68aab 0000000000007ee7 00000000000019eb",
        "fffffffffffd4fbd fffffffffff383e8 fffffffffff5ab5d fffffffffff5fe7e 0000000000023776 ffffffffffed7f2c ffffffffffe66eef 0000000000007ee7 0000000000001ae1",
        "fffffffffffd451d fffffffffff36af5 fffffffffff597b6 fffffffffff609b7 0000000000024950 ffffffffffed939a ffffffffffe65334 0000000000007ee7 0000000000001bd6",
        "fffffffffffd3a88 fffffffffff351f8 fffffffffff58425 fffffffffff614c0 0000000000025b02 ffffffffffeda828 ffffffffffe63778 0000000000007ee7 0000000000001ccc",
        "fffffffffffd3000 fffffffffff338f0 fffffffffff570ab fffffffffff61fc7 0000000000026cb5 ffffffffffedbcdc ffffffffffe61bbc 0000000000007ee7 0000000000001dc2",
        "fffffffffffd2583 fffffffffff31fdd fffffffffff55d46 fffffffffff62afe 0000000000027e13 ffffffffffedd19c ffffffffffe60000 0000000000007ee7 0000000000001eb8",
    ];

    // rateHz defaults to the rate the recorded trace below was authored at; a law whose loop counts ticks to mean
    // elapsed TIME names its own.
    private static WorldDefinition BuildDriveDocument(float lateral = 22f, bool authorDrive = true, int rateHz = Fixtures.RecordedTraceRateHz) {
        var channels = new WorldChannel[] {
            new(
            Name: "forward",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveAdvance
        ),
            new(
            Name: "strafe",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.MoveStrafe
        ),
            new(
            Name: "turn",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.Turn
        ),
            new(
            Name: "drift",
            Shape: ChannelShape.Binary,
            Composition: true
        ),
            new(
            Name: "boost",
            Shape: ChannelShape.Binary,
            Composition: true
        ),
            new(
            Name: "pitch",
            Shape: ChannelShape.Bipolar,
            Role: ChannelRole.Pitch
        ),
        };

        var drive = new BodyMotionProgram(
            Name: "drive",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveDriveFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );

        var kit = new WorldKit(
            Name: "kart-test",
            BodyMotionProgram: "drive",
            // The kart spelling: the motion row carries the forward speed and its held boost, the steering rate and
            // its speed-scaled authority curve and pitch rate; the speed envelope pins that speed against any seated
            // profile with min == max; gravity is the one authored hold row's own field; the shaping table carries
            // what only a drive kit has.
            Motion: new WorldMotion(
                Speed: new WorldSpeed(
                    Value: 16f,
                    Envelope: new MotionScalarEnvelope(
                        Max: 16f,
                        Min: 16f
                    ),
                    Held: new WorldSpeedHeld(
                        Channel: "boost",
                        Multiplier: 1.5f
                    )
                ),
                Turn: new WorldTurn(
                    Rate: 2.4f,
                    ReferenceSpeed: 4f,
                    Falloff: 0.55f,
                    PitchRate: 0.9f
                ),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 30f),
                        Gravity: new WorldHoldGravity(
                            Fall: 26f,
                            Rise: 14f
                        ),
                        Hold: BodyHoldKind.Gravity,
                        Name: "air"
                    ),
                ],
                Shaping: (authorDrive
            ? Shaping(lateral: lateral)
            : [])
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters>(),
            ActionsRaw: new Dictionary<string, ActionSpec>(),
            Collider: null
        );

        return Fixtures.BuildDocumentAtRate(rateHz: rateHz) with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [drive],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "kart-test",
        };
    }
    private static string Hex(FixedQ4816 value) => value.Value.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
    // The scripted intent, one span per tick range: throttle to the pinned speed, steer while moving (where lateral
    // lateral bites), back-throttle through a reversal into backward, a held drift stretch, a held sprint stretch, and a
    // pitched climb.
    private static PlayerIntent IntentAt(int tick) {
        var intent = default(PlayerIntent);

        if (tick < 40) {
            return intent.WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            );
        }
        if (tick < 88) {
            return intent
                .WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            )
                .WithChannel(
                ordinal: TurnOrdinal,
                value: FixedQ4816.One
            );
        }
        if (tick < 136) {
            return intent
                .WithChannel(
                ordinal: ForwardOrdinal,
                value: -FixedQ4816.One
            )
                .WithChannel(
                ordinal: TurnOrdinal,
                value: -FixedQ4816.One
            );
        }
        if (tick < 176) {
            return intent
                .WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            )
                .WithChannel(
                ordinal: TurnOrdinal,
                value: FixedQ4816.One
            )
                .WithChannel(
                ordinal: DriftOrdinal,
                value: FixedQ4816.One
            );
        }
        if (tick < 208) {
            return intent
                .WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            )
                .WithChannel(
                ordinal: TurnOrdinal,
                value: FixedQ4816.One
            )
                .WithChannel(
                ordinal: BoostOrdinal,
                value: FixedQ4816.One
            );
        }

        return intent
            .WithChannel(
            ordinal: ForwardOrdinal,
            value: FixedQ4816.One
        )
            .WithChannel(
            ordinal: PitchOrdinal,
            value: FixedQ4816.One
        );
    }
    // Full-pitch input held far longer than either ceiling takes to reach, so both scenarios have fully saturated
    // their own clamp by the last tick.
    private static FixedQ4816 SaturatedDrivePitch(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var intent = default(PlayerIntent)
            .WithChannel(
            ordinal: ForwardOrdinal,
            value: FixedQ4816.One
        )
            .WithChannel(
            ordinal: PitchOrdinal,
            value: FixedQ4816.One
        );

        for (var tick = 0; (tick < 75); tick++) {
            body.SubmitIntent(intent: intent);
            fixture.Step();
        }

        return body.CaptureTransferState().DrivePitch;
    }
    // The drive decomposition, spelled as two shaping rows: a held-gated drift row authored first (its own lateral
    // rate and turnScale apply while the drift channel reads held), and the ordinary row behind it (unconditional,
    // last) — the FIRST open row governs, so the drift row's own facets replace the ordinary row's whenever it
    // wins. Both rows share the same longitudinal along facet; only across.lateral and turnScale differ.
    private static WorldShaping[] Shaping(float lateral = 22f) => [
        new WorldShaping(
            When: new WorldPredicate.Held(Channel: "drift"),
            Along: new WorldShapingAlong(
                BackwardSpeed: 5f,
                Engage: 96f,
                Release: 20f,
                ReversalRate: 120f
            ),
            Across: new WorldShapingAcross(Lateral: 6f),
            TurnScale: 1.4f
        ),
        new WorldShaping(
            Along: new WorldShapingAlong(
                BackwardSpeed: 5f,
                Engage: 96f,
                Release: 20f,
                ReversalRate: 120f
            ),
            Across: new WorldShapingAcross(Lateral: lateral)
        ),
    ];
    private static string[] Trace(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var lines = new string[240];

        for (var tick = 0; (tick < lines.Length); tick++) {
            body.SubmitIntent(intent: IntentAt(tick: tick));
            fixture.Step();

            var state = body.CaptureTransferState();
            var position = body.FixedPosition;

            lines[tick] = string.Join(
                separator: ' ',
                value: [
                    Hex(value: position.X),
                    Hex(value: position.Y),
                    Hex(value: position.Z),
                    Hex(value: state.PlanarVelocity.X),
                    Hex(value: state.PlanarVelocity.Y),
                    Hex(value: state.PlanarVelocity.Z),
                    Hex(value: state.VerticalVelocity),
                    Hex(value: body.FixedYaw),
                    Hex(value: state.DrivePitch),
                ]
            );
        }

        return lines;
    }

    [Fact]
    public void ADriveProgramOnAKitAuthoringNoShapingRow_RefusesValidationNamingTheFacet() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: BuildDriveDocument(),
                reason: out var admittedReason
            ),
            userMessage: admittedReason
        );
        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: BuildDriveDocument(authorDrive: false),
                reason: out var deniedReason
            ),
            userMessage: "a drive program against a kit authoring no shaping row was expected to refuse"
        );
        Assert.Contains(
            actualString: deniedReason,
            expectedSubstring: "Shaping"
        );
    }
    [Fact]
    public void AShapingRowsOwnRatesAndDriftAreRangeCheckedByName() {
        foreach (var (mutate, token) in new (Func<WorldMotion, WorldMotion> Mutate, string Token)[] {
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { Engage = 0f } }] }, "along.engage"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { ReversalRate = -1f } }] }, "along.reversalRate"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { Release = 0f } }] }, "along.release"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Across = motion.Shaping[1].Across! with { Lateral = 0f } }] }, "across.lateral"),
            (motion => motion with { Turn = motion.Turn with { ReferenceSpeed = 0f } }, "turn.referenceSpeed"),
            (motion => motion with { Turn = motion.Turn with { Falloff = 1.5f } }, "turn.falloff"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { BackwardSpeed = -1f } }] }, "along.backwardSpeed"),
            (motion => motion with { Turn = motion.Turn with { PitchRate = -1f } }, "turn.pitchRate"),
            (motion => motion with { Turn = motion.Turn with { MaxPitch = 0f } }, "turn.maxPitch"),
            (motion => motion with { UpTurnRaw = (motion.UpTurn with { Field = 0f }) }, "upTurn.field"),
            (motion => motion with { UpTurnRaw = (motion.UpTurn with { Contact = -1f }) }, "upTurn.contact"),
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { Displacement = 0f }) }, "obstruction.displacement"),
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { IdleThreshold = -1f }) }, "obstruction.idleThreshold"),
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { GraceSeconds = 0m }) }, "obstruction.graceSeconds"),
            (motion => motion with { GroundSnap = -1f }, "groundSnap"),
            (motion => motion with { StepHeight = -0.1f }, "stepHeight"),
            (motion => motion with { StepHeight = float.Epsilon }, "stepHeight"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { When = new WorldPredicate.Held(Channel: "nope") }, motion.Shaping[1]] }, "shaping[0].when.channel"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { Across = motion.Shaping[0].Across! with { Lateral = 0f } }, motion.Shaping[1]] }, "shaping[0].across.lateral"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { TurnScale = 0f }, motion.Shaping[1]] }, "shaping[0].turnScale"),
            (motion => motion with { Turn = motion.Turn with { MaxPitch = float.Epsilon } }, "turn.maxPitch"),
            (motion => motion with { UpTurnRaw = (motion.UpTurn with { Field = float.Epsilon }) }, "upTurn.field"),
            (motion => motion with { UpTurnRaw = (motion.UpTurn with { Contact = float.Epsilon }) }, "upTurn.contact"),
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { Displacement = 0.001f }) }, "obstruction.displacement"),
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { GraceSeconds = 0.0001m }) }, "obstruction.graceSeconds"),
            (motion => motion with { GroundSnap = float.Epsilon }, "groundSnap"),
            (motion => motion with { Speed = motion.Speed with { Value = float.Epsilon } }, "speed.value"),
            (motion => motion with { Speed = motion.Speed with { Held = motion.Speed.Held! with { Multiplier = float.Epsilon } } }, "speed.held.multiplier"),
            (motion => motion with { Turn = motion.Turn with { Rate = float.Epsilon } }, "turn.rate"),
            (motion => motion with { Turn = motion.Turn with { ReferenceSpeed = float.Epsilon } }, "turn.referenceSpeed"),
            (motion => motion with { Holds = [motion.Holds![0] with { Gravity = motion.Holds[0].Gravity! with { Fall = float.Epsilon } }] }, "gravity.fall"),
            (motion => motion with { Holds = [motion.Holds![0] with { Envelope = motion.Holds[0].Envelope! with { SinkSpeed = float.Epsilon } }] }, "envelope.sinkSpeed"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { Engage = float.Epsilon } }] }, "along.engage"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { Release = float.Epsilon } }] }, "along.release"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { TurnScale = float.Epsilon }, motion.Shaping[1]] }, "shaping[0].turnScale"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Along = motion.Shaping[1].Along! with { ReversalRate = float.Epsilon } }] }, "along.reversalRate"),
            (motion => motion with { Shaping = [motion.Shaping![0], motion.Shaping[1] with { Across = motion.Shaping[1].Across! with { Lateral = float.Epsilon } }] }, "across.lateral"),
        }) {
            var document = BuildDriveDocument();
            var kits = document.Kits.ToList();

            kits[0] = (kits[0] with { Motion = mutate(kits[0].Motion!) });

            Assert.False(
                condition: WorldDefinitionValidator.TryValidateLocally(
                    definition: (document with { KitRowsRaw = kits }),
                    reason: out var reason
                ),
                userMessage: $"a shaping row failing {token} was expected to refuse"
            );
            Assert.Contains(
                actualString: reason,
                expectedSubstring: token
            );
        }
    }
    // The default kit (no maxPitch authored) climbs to exactly 1.2 radians — the engine's old hardcoded clamp, bit
    // for bit. A kit authoring a tighter ceiling climbs to its OWN bound instead, never the engine default.
    [Fact]
    public void MaxPitchClampsTheDriveFrameAtItsAuthoredCeilingNotAHardcodedOne() {
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: 1.2),
            actual: SaturatedDrivePitch(definition: BuildDriveDocument(rateHz: Fixtures.DefaultRateHz))
        );

        var kits = BuildDriveDocument(rateHz: Fixtures.DefaultRateHz).Kits.ToList();

        kits[0] = (kits[0] with { Motion = (kits[0].Motion! with { Turn = kits[0].Motion!.Turn with { MaxPitch = 0.3f } }) });
        var narrowed = (BuildDriveDocument(rateHz: Fixtures.DefaultRateHz) with { KitRowsRaw = kits });

        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: 0.3),
            actual: SaturatedDrivePitch(definition: narrowed)
        );
    }
    [Fact]
    public void TheDriveRowReproducesTheRecordedTrace_WhereChangingOneDriveFacetDiverges() {
        Assert.Equal(
            expected: DriveTrace240,
            actual: Trace(definition: BuildDriveDocument())
        );

        var perturbed = Trace(definition: BuildDriveDocument(lateral: 9f));
        var moved = 0;

        for (var tick = 0; (tick < DriveTrace240.Length); tick++) {
            if (!string.Equals(
                a: DriveTrace240[tick],
                b: perturbed[tick],
                comparisonType: StringComparison.Ordinal
            )) {
                moved++;
            }
        }

        Assert.True(
            condition: (moved > 0),
            userMessage: "a slidier drive lateral rate must move the trace, or the trace pins nothing about it"
        );
    }
}
