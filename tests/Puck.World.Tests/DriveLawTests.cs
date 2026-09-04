using Xunit;

using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>drive</c> row — the anisotropic drive facets a kit authors beside the motion
/// row, read by <c>ResolveDriveFrame</c>/<c>ShapeVelocity</c>. The trace law drives one body 240 ticks through
/// every drive facet (throttle, speed-scaled steering, reversal, backward travel, a held drift stretch, a held sprint stretch,
/// a pitched frame) and pins the fixed-point result raw value for raw value; its discriminating control perturbs a
/// single drive facet and requires the trace to move. The facet law proves a drive program refuses by name against a
/// kit authoring no row, where the same kit with one is admitted.
/// </summary>
public sealed class DriveLawTests {
    private const int BoostOrdinal = 4;
    private const int DriftOrdinal = 3;
    private const int ForwardOrdinal = 0;
    private const int PitchOrdinal = 5;
    private const int TurnOrdinal = 2;

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
        "ffffffffffffffe8 ffffffffffff9c82 fffffffffffe9112 ffffffffffffe980 0000000000000000 fffffffffff00000 fffffffffffb8eef 0000000000000168 0000000000000000",
        "ffffffffffffffb8 ffffffffffff97a8 fffffffffffe8001 ffffffffffffd300 0000000000000000 fffffffffff00040 fffffffffffb7334 00000000000002d0 0000000000000000",
        "ffffffffffffff70 ffffffffffff92b0 fffffffffffe6ef0 ffffffffffffbc80 0000000000000000 fffffffffff00080 fffffffffffb5778 0000000000000439 0000000000000000",
        "ffffffffffffff10 ffffffffffff8d9a fffffffffffe5de0 ffffffffffffa600 0000000000000000 fffffffffff00100 fffffffffffb3bbc 00000000000005a1 0000000000000000",
        "fffffffffffffe98 ffffffffffff8867 fffffffffffe4cd1 ffffffffffff8f60 0000000000000000 fffffffffff00180 fffffffffffb2000 000000000000070a 0000000000000000",
        "fffffffffffffe08 ffffffffffff8316 fffffffffffe3bc2 ffffffffffff78e0 0000000000000000 fffffffffff00240 fffffffffffb0445 0000000000000872 0000000000000000",
        "fffffffffffffd60 ffffffffffff7da8 fffffffffffe2ab4 ffffffffffff6260 0000000000000000 fffffffffff00300 fffffffffffae889 00000000000009db 0000000000000000",
        "fffffffffffffca0 ffffffffffff781c fffffffffffe19a7 ffffffffffff4be0 0000000000000000 fffffffffff00400 fffffffffffacccd 0000000000000b43 0000000000000000",
        "fffffffffffffbc8 ffffffffffff7272 fffffffffffe089c ffffffffffff3540 0000000000000000 fffffffffff00500 fffffffffffab112 0000000000000cac 0000000000000000",
        "fffffffffffffad8 ffffffffffff6cab fffffffffffdf791 ffffffffffff1ee0 0000000000000000 fffffffffff00640 fffffffffffa9556 0000000000000e14 0000000000000000",
        "fffffffffffff9cf ffffffffffff66c7 fffffffffffde688 ffffffffffff0860 0000000000000000 fffffffffff00780 fffffffffffa799a 0000000000000f7c 0000000000000000",
        "fffffffffffff8af ffffffffffff60c4 fffffffffffdd581 fffffffffffef1e0 0000000000000000 fffffffffff008e0 fffffffffffa5dde 00000000000010e5 0000000000000000",
        "fffffffffffff777 ffffffffffff5aa4 fffffffffffdc47b fffffffffffedb80 0000000000000000 fffffffffff00a80 fffffffffffa4223 000000000000124d 0000000000000000",
        "fffffffffffff627 ffffffffffff5467 fffffffffffdb377 fffffffffffec500 0000000000000000 fffffffffff00c20 fffffffffffa2667 00000000000013b6 0000000000000000",
        "fffffffffffff4bf ffffffffffff4e0c fffffffffffda274 fffffffffffeae80 0000000000000000 fffffffffff00de0 fffffffffffa0aab 000000000000151e 0000000000000000",
        "fffffffffffff33f ffffffffffff4793 fffffffffffd9174 fffffffffffe9800 0000000000000000 fffffffffff00fe0 fffffffffff9eeef 0000000000001687 0000000000000000",
        "fffffffffffff1a7 ffffffffffff40fd fffffffffffd8076 fffffffffffe81a0 0000000000000000 fffffffffff011e0 fffffffffff9d334 00000000000017ef 0000000000000000",
        "ffffffffffffeff8 ffffffffffff3a49 fffffffffffd6f7b fffffffffffe6b20 0000000000000000 fffffffffff01400 fffffffffff9b778 0000000000001958 0000000000000000",
        "ffffffffffffee30 ffffffffffff3378 fffffffffffd5e81 fffffffffffe54e0 0000000000000000 fffffffffff01660 fffffffffff99bbc 0000000000001ac0 0000000000000000",
        "ffffffffffffec50 ffffffffffff2c89 fffffffffffd4d8b fffffffffffe3e40 0000000000000000 fffffffffff018c0 fffffffffff98000 0000000000001c29 0000000000000000",
        "ffffffffffffea59 ffffffffffff257d fffffffffffd3c97 fffffffffffe2800 0000000000000000 fffffffffff01b40 fffffffffff96445 0000000000001d91 0000000000000000",
        "ffffffffffffe849 ffffffffffff1e53 fffffffffffd2ba6 fffffffffffe11a0 0000000000000000 fffffffffff01e00 fffffffffff94889 0000000000001ef9 0000000000000000",
        "ffffffffffffe622 ffffffffffff170b fffffffffffd1ab7 fffffffffffdfb40 0000000000000000 fffffffffff020c0 fffffffffff92ccd 0000000000002062 0000000000000000",
        "ffffffffffffe3e3 ffffffffffff0fa6 fffffffffffd09cc fffffffffffde4e0 0000000000000000 fffffffffff023a0 fffffffffff91112 00000000000021ca 0000000000000000",
        "ffffffffffffe18c ffffffffffff0823 fffffffffffcf8e5 fffffffffffdcea0 0000000000000000 fffffffffff026a0 fffffffffff8f556 0000000000002333 0000000000000000",
        "ffffffffffffdf1e ffffffffffff0082 fffffffffffce800 fffffffffffdb840 0000000000000000 fffffffffff029c0 fffffffffff8d99a 000000000000249b 0000000000000000",
        "ffffffffffffdc97 fffffffffffef8c4 fffffffffffcd71f fffffffffffda1e0 0000000000000000 fffffffffff02d20 fffffffffff8bdde 0000000000002604 0000000000000000",
        "ffffffffffffd9f9 fffffffffffef0e9 fffffffffffcc642 fffffffffffd8bc0 0000000000000000 fffffffffff03080 fffffffffff8a223 000000000000276c 0000000000000000",
        "ffffffffffffd743 fffffffffffee8f0 fffffffffffcb568 fffffffffffd7580 0000000000000000 fffffffffff03400 fffffffffff88667 00000000000028d5 0000000000000000",
        "ffffffffffffd476 fffffffffffee0d9 fffffffffffca492 fffffffffffd5f40 0000000000000000 fffffffffff037a0 fffffffffff86aab 0000000000002a3d 0000000000000000",
        "ffffffffffffd190 fffffffffffed8a4 fffffffffffc93c1 fffffffffffd4900 0000000000000000 fffffffffff03b60 fffffffffff84eef 0000000000002ba5 0000000000000000",
        "ffffffffffffce93 fffffffffffed053 fffffffffffc82f3 fffffffffffd32c0 0000000000000000 fffffffffff03f40 fffffffffff83334 0000000000002d0e 0000000000000000",
        "ffffffffffffcb7e fffffffffffec7e3 fffffffffffc722a fffffffffffd1ca0 0000000000000000 fffffffffff04340 fffffffffff81778 0000000000002e76 0000000000000000",
        "ffffffffffffc852 fffffffffffebf56 fffffffffffc6165 fffffffffffd0680 0000000000000000 fffffffffff04760 fffffffffff7fbbc 0000000000002fdf 0000000000000000",
        "ffffffffffffc50e fffffffffffeb6ab fffffffffffc50a4 fffffffffffcf060 0000000000000000 fffffffffff04ba0 fffffffffff7e000 0000000000003147 0000000000000000",
        "ffffffffffffc1b3 fffffffffffeade3 fffffffffffc3fe9 fffffffffffcda60 0000000000000000 fffffffffff05000 fffffffffff7c445 00000000000032b0 0000000000000000",
        "ffffffffffffbe40 fffffffffffea4fd fffffffffffc2f32 fffffffffffcc420 0000000000000000 fffffffffff05480 fffffffffff7a889 0000000000003418 0000000000000000",
        "ffffffffffffbab5 fffffffffffe9bfa fffffffffffc1e80 fffffffffffcae20 0000000000000000 fffffffffff05920 fffffffffff78ccd 0000000000003581 0000000000000000",
        "ffffffffffffb713 fffffffffffe92d9 fffffffffffc0dd3 fffffffffffc9820 0000000000000000 fffffffffff05de0 fffffffffff77112 00000000000036e9 0000000000000000",
        "ffffffffffffb35a fffffffffffe899a fffffffffffbfd2b fffffffffffc8220 0000000000000000 fffffffffff062c0 fffffffffff75556 0000000000003852 0000000000000000",
        "ffffffffffffaf89 fffffffffffe803e fffffffffffbec89 fffffffffffc6c40 0000000000000000 fffffffffff067a0 fffffffffff7399a 00000000000039ba 0000000000000000",
        "ffffffffffffaba1 fffffffffffe76c4 fffffffffffbdbeb fffffffffffc5640 0000000000000000 fffffffffff06cc0 fffffffffff71dde 0000000000003b22 0000000000000000",
        "ffffffffffffa7a1 fffffffffffe6d2d fffffffffffbcb54 fffffffffffc4060 0000000000000000 fffffffffff07200 fffffffffff70223 0000000000003c8b 0000000000000000",
        "ffffffffffffa38a fffffffffffe6378 fffffffffffbbac2 fffffffffffc2a80 0000000000000000 fffffffffff07760 fffffffffff6e667 0000000000003df3 0000000000000000",
        "ffffffffffff9f5c fffffffffffe59a6 fffffffffffbaa36 fffffffffffc14a0 0000000000000000 fffffffffff07cc0 fffffffffff6caab 0000000000003f5c 0000000000000000",
        "ffffffffffff9b16 fffffffffffe4fb6 fffffffffffb99b0 fffffffffffbfec0 0000000000000000 fffffffffff08260 fffffffffff6aeef 00000000000040c4 0000000000000000",
        "ffffffffffff96ba fffffffffffe45a8 fffffffffffb8930 fffffffffffbe900 0000000000000000 fffffffffff08820 fffffffffff69334 000000000000422d 0000000000000000",
        "ffffffffffff9246 fffffffffffe3b7d fffffffffffb78b7 fffffffffffbd340 0000000000000000 fffffffffff08de0 fffffffffff67778 0000000000004395 0000000000000000",
        "ffffffffffff8e0c fffffffffffe3134 fffffffffffb68bb fffffffffffc09be 0000000000000000 fffffffffff103f7 fffffffffff65bbc 000000000000422e 0000000000000000",
        "ffffffffffff8a0b fffffffffffe26cd fffffffffffb593d fffffffffffc3f99 0000000000000000 fffffffffff17a5d fffffffffff64000 00000000000040b9 0000000000000000",
        "ffffffffffff8644 fffffffffffe1c49 fffffffffffb4a3e fffffffffffc74b5 0000000000000000 fffffffffff1f0f6 fffffffffff62445 0000000000003f38 0000000000000000",
        "ffffffffffff82b4 fffffffffffe11a8 fffffffffffb3bbe fffffffffffca91e 0000000000000000 fffffffffff267e1 fffffffffff60889 0000000000003dab 0000000000000000",
        "ffffffffffff7f5b fffffffffffe06e9 fffffffffffb2dbd fffffffffffcdca8 0000000000000000 fffffffffff2df33 fffffffffff5eccd 0000000000003c11 0000000000000000",
        "ffffffffffff7c38 fffffffffffdfc0c fffffffffffb203c fffffffffffd0f3d 0000000000000000 fffffffffff356c0 fffffffffff5d112 0000000000003a6b 0000000000000000",
        "ffffffffffff794a fffffffffffdf112 fffffffffffb133b fffffffffffd40cc 0000000000000000 fffffffffff3cebf fffffffffff5b556 00000000000038b9 0000000000000000",
        "ffffffffffff7690 fffffffffffde5fa fffffffffffb06ba fffffffffffd717a 0000000000000000 fffffffffff44732 fffffffffff5999a 00000000000036fa 0000000000000000",
        "ffffffffffff7408 fffffffffffddac4 fffffffffffafaba fffffffffffda0ff 0000000000000000 fffffffffff4c018 fffffffffff57dde 000000000000352f 0000000000000000",
        "ffffffffffff71b2 fffffffffffdcf71 fffffffffffaef3c fffffffffffdcf2e 0000000000000000 fffffffffff5395a fffffffffff56223 0000000000003358 0000000000000000",
        "ffffffffffff6f8c fffffffffffdc401 fffffffffffae43f fffffffffffdfc24 0000000000000000 fffffffffff5b30e fffffffffff54667 0000000000003175 0000000000000000",
        "ffffffffffff6d94 fffffffffffdb872 fffffffffffad9c4 fffffffffffe27b6 0000000000000000 fffffffffff62d1d fffffffffff52aab 0000000000002f85 0000000000000000",
        "ffffffffffff6bc9 fffffffffffdacc7 fffffffffffacfcc fffffffffffe51e6 0000000000000000 fffffffffff6a794 fffffffffff50eef 0000000000002d89 0000000000000000",
        "ffffffffffff6a2a fffffffffffda0fd fffffffffffac657 fffffffffffe7a8c 0000000000000000 fffffffffff72277 fffffffffff4f334 0000000000002b81 0000000000000000",
        "ffffffffffff68b4 fffffffffffd9516 fffffffffffabd66 fffffffffffea1a9 0000000000000000 fffffffffff79dc5 fffffffffff4d778 000000000000296c 0000000000000000",
        "ffffffffffff6766 fffffffffffd8912 fffffffffffab4f9 fffffffffffec718 0000000000000000 fffffffffff81978 fffffffffff4bbbc 000000000000274b 0000000000000000",
        "ffffffffffff663f fffffffffffd7cf0 fffffffffffaad10 fffffffffffeeac6 0000000000000000 fffffffffff895a0 fffffffffff4a000 000000000000251e 0000000000000000",
        "ffffffffffff653b fffffffffffd70b0 fffffffffffaa5ac ffffffffffff0cb0 0000000000000000 fffffffffff9122c fffffffffff48445 00000000000022e5 0000000000000000",
        "ffffffffffff645a fffffffffffd6453 fffffffffffa9ecd ffffffffffff2cb5 0000000000000000 fffffffffff98f24 fffffffffff46889 000000000000209f 0000000000000000",
        "ffffffffffff6398 fffffffffffd57d8 fffffffffffa9874 ffffffffffff4ad8 0000000000000000 fffffffffffa0c70 fffffffffff44ccd 0000000000001e4d 0000000000000000",
        "ffffffffffff62f5 fffffffffffd4b3f fffffffffffa92a1 ffffffffffff66e2 0000000000000000 fffffffffffa8a25 fffffffffff43112 0000000000001bee 0000000000000000",
        "ffffffffffff626d fffffffffffd3e89 fffffffffffa8d54 ffffffffffff80cd 0000000000000000 fffffffffffb0838 fffffffffff41556 0000000000001984 0000000000000000",
        "ffffffffffff61ff fffffffffffd31b6 fffffffffffa888e ffffffffffff9892 0000000000000000 fffffffffffb869c fffffffffff3f99a 000000000000170d 0000000000000000",
        "ffffffffffff61a8 fffffffffffd24c4 fffffffffffa8450 ffffffffffffae19 0000000000000000 fffffffffffc0544 fffffffffff3ddde 000000000000148a 0000000000000000",
        "ffffffffffff6165 fffffffffffd17b6 fffffffffffa8099 ffffffffffffc141 0000000000000000 fffffffffffc843f fffffffffff3c223 00000000000011fb 0000000000000000",
        "ffffffffffff6133 fffffffffffd0a89 fffffffffffa7d69 ffffffffffffd0ed 0000000000000000 fffffffffffd0387 fffffffffff3a667 0000000000000fbf 0000000000000000",
        "ffffffffffff610e fffffffffffcfd3f fffffffffffa7ac2 ffffffffffffdd90 0000000000000000 fffffffffffd830a fffffffffff38aab 0000000000000dd5 0000000000000000",
        "ffffffffffff60f4 fffffffffffcefd8 fffffffffffa78a3 ffffffffffffe7a2 0000000000000000 fffffffffffe02b0 fffffffffff36eef 0000000000000c3d 0000000000000000",
        "ffffffffffff60e2 fffffffffffce253 fffffffffffa770c ffffffffffffefa6 0000000000000000 fffffffffffe8276 fffffffffff35334 0000000000000af7 0000000000000000",
        "ffffffffffff60d8 fffffffffffcd4b0 fffffffffffa75fd fffffffffffff613 0000000000000000 ffffffffffff024f fffffffffff33778 0000000000000a02 0000000000000000",
        "ffffffffffff60d3 fffffffffffcc6f0 fffffffffffa7577 fffffffffffffb64 0000000000000000 ffffffffffff8234 fffffffffff31bbc 0000000000000960 0000000000000000",
        "ffffffffffff60d3 fffffffffffcb912 fffffffffffa7577 0000000000000000 0000000000000000 0000000000000000 fffffffffff30000 000000000000090f 0000000000000000",
        "ffffffffffff60d6 fffffffffffcab16 fffffffffffa75e3 000000000000039f 0000000000000000 0000000000006657 fffffffffff2e445 000000000000090f 0000000000000000",
        "ffffffffffff60de fffffffffffc9cfd fffffffffffa76bd 0000000000000773 0000000000000000 000000000000ccaa fffffffffff2c889 0000000000000950 0000000000000000",
        "ffffffffffff60ea fffffffffffc8ec7 fffffffffffa7805 0000000000000bc9 0000000000000000 00000000000132fa fffffffffff2accd 00000000000009d3 0000000000000000",
        "ffffffffffff60fc fffffffffffc8073 fffffffffffa79b9 00000000000010f0 0000000000000000 0000000000019940 fffffffffff29112 0000000000000a97 0000000000000000",
        "ffffffffffff6115 fffffffffffc7201 fffffffffffa7bdb 0000000000001738 0000000000000000 000000000001ff76 fffffffffff27556 0000000000000b9d 0000000000000000",
        "ffffffffffff6136 fffffffffffc6371 fffffffffffa7e69 0000000000001eeb 0000000000000000 0000000000026596 fffffffffff2599a 0000000000000ce5 0000000000000000",
        "ffffffffffff6161 fffffffffffc54c4 fffffffffffa8165 0000000000002861 0000000000000000 000000000002cb9e fffffffffff23dde 0000000000000e6e 0000000000000000",
        "ffffffffffff6199 fffffffffffc45fa fffffffffffa84cd 00000000000033df 0000000000000000 000000000003317d fffffffffff22223 0000000000001039 0000000000000000",
        "ffffffffffff61df fffffffffffc3712 fffffffffffa88a1 00000000000041b9 0000000000000000 000000000003972e fffffffffff20667 0000000000001245 0000000000000000",
        "ffffffffffff6236 fffffffffffc280c fffffffffffa8ce2 0000000000005236 0000000000000000 000000000003fc92 fffffffffff1eaab 0000000000001493 0000000000000000",
        "ffffffffffff62a3 fffffffffffc18e9 fffffffffffa918e 00000000000065a1 0000000000000000 000000000004619d fffffffffff1ceef 0000000000001722 0000000000000000",
        "ffffffffffff6326 fffffffffffc09a8 fffffffffffa96a6 0000000000007af0 0000000000000000 000000000004c66b fffffffffff1b334 00000000000019a8 0000000000000000",
        "ffffffffffff63bc fffffffffffbfa4a fffffffffffa9bf3 0000000000008c6e 0000000000000000 000000000004f844 fffffffffff19778 0000000000001c24 0000000000000000",
        "ffffffffffff645e fffffffffffbeace fffffffffffaa13f 00000000000098a8 0000000000000000 000000000004f6dc fffffffffff17bbc 0000000000001e9a 0000000000000000",
        "ffffffffffff650e fffffffffffbdb34 fffffffffffaa689 000000000000a4e2 0000000000000000 000000000004f556 fffffffffff16000 0000000000002111 0000000000000000",
        "ffffffffffff65cb fffffffffffbcb7d fffffffffffaabd1 000000000000b112 0000000000000000 000000000004f3b2 fffffffffff14445 0000000000002388 0000000000000000",
        "ffffffffffff6674 fffffffffffbbba8 fffffffffffab0ad 0000000000009dec 0000000000000000 0000000000048ee7 fffffffffff12889 0000000000002016 0000000000000000",
        "ffffffffffff6709 fffffffffffbabb6 fffffffffffab51e 0000000000008c26 0000000000000000 00000000000429db fffffffffff10ccd 0000000000001c95 0000000000000000",
        "ffffffffffff678d fffffffffffb9ba6 fffffffffffab923 0000000000007bca 0000000000000000 000000000003c499 fffffffffff0f112 0000000000001906 0000000000000000",
        "ffffffffffff6801 fffffffffffb8b78 fffffffffffabcbc 0000000000006cc5 0000000000000000 0000000000035f18 fffffffffff0d556 000000000000159f 0000000000000000",
        "ffffffffffff6867 fffffffffffb7b2d fffffffffffabfe8 0000000000005ef7 0000000000000000 000000000002f970 fffffffffff0b99a 0000000000001295 0000000000000000",
        "ffffffffffff68be fffffffffffb6ac4 fffffffffffac2a8 000000000000523a 0000000000000000 00000000000293a4 fffffffffff09dde 0000000000000fe6 0000000000000000",
        "ffffffffffff6909 fffffffffffb5a3e fffffffffffac4fb 0000000000004668 0000000000000000 0000000000022db9 fffffffffff08223 0000000000000d93 0000000000000000",
        "ffffffffffff6949 fffffffffffb499a fffffffffffac6e1 0000000000003b5e 0000000000000000 000000000001c7b5 fffffffffff06667 0000000000000b9d 0000000000000000",
        "ffffffffffff697d fffffffffffb38d9 fffffffffffac85a 00000000000030f8 0000000000000000 00000000000161a4 fffffffffff04aab 0000000000000a03 0000000000000000",
        "ffffffffffff69a7 fffffffffffb27fa fffffffffffac966 0000000000002711 0000000000000000 000000000000fb85 fffffffffff02eef 00000000000008c4 0000000000000000",
        "ffffffffffff69c6 fffffffffffb16fd fffffffffffaca06 0000000000001d84 0000000000000000 000000000000955d fffffffffff01334 00000000000007e2 0000000000000000",
        "ffffffffffff69dc fffffffffffb05e3 fffffffffffaca38 000000000000142d 0000000000000000 0000000000002f30 ffffffffffeff778 000000000000075b 0000000000000000",
        "ffffffffffff69e7 fffffffffffaf4ab fffffffffffac9fe 0000000000000ae8 0000000000000000 ffffffffffffc903 ffffffffffefdbbc 0000000000000731 0000000000000000",
        "ffffffffffff69e9 fffffffffffae356 fffffffffffac957 000000000000018e 0000000000000000 ffffffffffff62d7 ffffffffffefc000 0000000000000761 0000000000000000",
        "ffffffffffff69e1 fffffffffffad1e3 fffffffffffac842 fffffffffffff7fe 0000000000000000 fffffffffffefcb0 ffffffffffefa445 00000000000007ed 0000000000000000",
        "ffffffffffff69d4 fffffffffffac053 fffffffffffac6c0 fffffffffffff383 0000000000000000 fffffffffffe9665 ffffffffffef8889 00000000000008d6 0000000000000000",
        "ffffffffffff69c0 fffffffffffaaea5 fffffffffffac4d1 ffffffffffffedaf 0000000000000000 fffffffffffe3027 ffffffffffef6ccd 0000000000000a1a 0000000000000000",
        "ffffffffffff69a5 fffffffffffa9cd9 fffffffffffac276 ffffffffffffe60c 0000000000000000 fffffffffffdc9fc ffffffffffef5112 0000000000000bba 0000000000000000",
        "ffffffffffff697f fffffffffffa8af0 fffffffffffabfad ffffffffffffdc31 0000000000000000 fffffffffffd63f8 ffffffffffef3556 0000000000000db6 0000000000000000",
        "ffffffffffff694b fffffffffffa78e9 fffffffffffabc78 ffffffffffffcfac 0000000000000000 fffffffffffcfe23 ffffffffffef199a 000000000000100d 0000000000000000",
        "ffffffffffff6909 fffffffffffa66c5 fffffffffffab8d6 ffffffffffffc1cd 0000000000000000 fffffffffffc987e ffffffffffeefdde 00000000000012c0 0000000000000000",
        "ffffffffffff68b6 fffffffffffa5483 fffffffffffab4c9 ffffffffffffb2b6 0000000000000000 fffffffffffc3303 ffffffffffeee223 00000000000015cf 0000000000000000",
        "ffffffffffff6852 fffffffffffa4223 fffffffffffab04f ffffffffffffa247 0000000000000000 fffffffffffbcdbf ffffffffffeec667 0000000000001939 0000000000000000",
        "ffffffffffff67db fffffffffffa2fa6 fffffffffffaab69 ffffffffffff906e 0000000000000000 fffffffffffb68b9 ffffffffffeeaaab 0000000000001cc8 0000000000000000",
        "ffffffffffff6750 fffffffffffa1d0b fffffffffffaa618 ffffffffffff7d34 0000000000000000 fffffffffffb03ed ffffffffffee8eef 0000000000002048 0000000000000000",
        "ffffffffffff66ae fffffffffffa0a53 fffffffffffaa05c ffffffffffff689f 0000000000000000 fffffffffffa9f71 ffffffffffee7334 00000000000023bb 0000000000000000",
        "ffffffffffff65f5 fffffffffff9f77d fffffffffffa9a34 ffffffffffff52b5 0000000000000000 fffffffffffa3b38 ffffffffffee5778 0000000000002720 0000000000000000",
        "ffffffffffff6524 fffffffffff9e489 fffffffffffa93a3 ffffffffffff3b7e 0000000000000000 fffffffffff9d74f ffffffffffee3bbc 0000000000002a77 0000000000000000",
        "ffffffffffff6438 fffffffffff9d178 fffffffffffa8ca6 ffffffffffff22fc 0000000000000000 fffffffffff973ad ffffffffffee2000 0000000000002dc1 0000000000000000",
        "ffffffffffff6331 fffffffffff9be4a fffffffffffa8540 ffffffffffff093a 0000000000000000 fffffffffff9105a ffffffffffee0445 00000000000030fd 0000000000000000",
        "ffffffffffff620d fffffffffff9aafd fffffffffffa7d71 fffffffffffeee3e 0000000000000000 fffffffffff8ad5f ffffffffffede889 000000000000342c 0000000000000000",
        "ffffffffffff60cb fffffffffff99794 fffffffffffa7538 fffffffffffed20b 0000000000000000 fffffffffff84ac2 ffffffffffedcccd 000000000000374d 0000000000000000",
        "ffffffffffff5f69 fffffffffff9840c fffffffffffa6c96 fffffffffffeb4a8 0000000000000000 fffffffffff7e86a ffffffffffedb112 0000000000003a60 0000000000000000",
        "ffffffffffff5de7 fffffffffff97067 fffffffffffa638c fffffffffffe961c 0000000000000000 fffffffffff78678 ffffffffffed9556 0000000000003d66 0000000000000000",
        "ffffffffffff5c44 fffffffffff95ca5 fffffffffffa5a1a fffffffffffe766f 0000000000000000 fffffffffff724da ffffffffffed799a 000000000000405e 0000000000000000",
        "ffffffffffff5a7d fffffffffff948c5 fffffffffffa5040 fffffffffffe55a4 0000000000000000 fffffffffff6c394 ffffffffffed5dde 0000000000004349 0000000000000000",
        "ffffffffffff5892 fffffffffff934c7 fffffffffffa45ff fffffffffffe33c8 0000000000000000 fffffffffff662cd ffffffffffed4223 0000000000004627 0000000000000000",
        "ffffffffffff5682 fffffffffff920ac fffffffffffa3b56 fffffffffffe10d8 0000000000000000 fffffffffff60251 ffffffffffed2667 00000000000048f7 0000000000000000",
        "ffffffffffff544b fffffffffff90c73 fffffffffffa3048 fffffffffffdecde 0000000000000000 fffffffffff5a236 ffffffffffed0aab 0000000000004bb9 0000000000000000",
        "ffffffffffff51ed fffffffffff8f81c fffffffffffa24d3 fffffffffffdc7e3 0000000000000000 fffffffffff54285 ffffffffffeceeef 0000000000004e6e 0000000000000000",
        "ffffffffffff4f56 fffffffffff8e3a8 fffffffffffa18fe fffffffffffd91f7 0000000000000000 fffffffffff4e860 ffffffffffecd334 0000000000005054 0000000000000000",
        "ffffffffffff4c84 fffffffffff8cf16 fffffffffffa0cc9 fffffffffffd5b61 0000000000000000 fffffffffff48e94 ffffffffffecb778 0000000000005230 0000000000000000",
        "ffffffffffff4977 fffffffffff8ba67 fffffffffffa0035 fffffffffffd242f 0000000000000000 fffffffffff43549 ffffffffffec9bbc 0000000000005402 0000000000000000",
        "ffffffffffff462f fffffffffff8a59a fffffffffff9f342 fffffffffffcec58 0000000000000000 fffffffffff3dc51 ffffffffffec8000 00000000000055cb 0000000000000000",
        "ffffffffffff42ab fffffffffff890b0 fffffffffff9e5f1 fffffffffffcb3e9 0000000000000000 fffffffffff383bd ffffffffffec6445 000000000000578a 0000000000000000",
        "ffffffffffff3eea fffffffffff87ba8 fffffffffff9d842 fffffffffffc7adc 0000000000000000 fffffffffff32b76 ffffffffffec4889 000000000000593f 0000000000000000",
        "ffffffffffff3aeb fffffffffff86683 fffffffffff9ca34 fffffffffffc4144 0000000000000000 fffffffffff2d3a8 ffffffffffec2ccd 0000000000005aeb 0000000000000000",
        "ffffffffffff36ae fffffffffff8513f fffffffffff9bbca fffffffffffc0716 0000000000000000 fffffffffff27c26 ffffffffffec1112 0000000000005c8d 0000000000000000",
        "ffffffffffff3233 fffffffffff83bdf fffffffffff9ad02 fffffffffffbcc5f 0000000000000000 fffffffffff22506 ffffffffffebf556 0000000000005e26 0000000000000000",
        "ffffffffffff2d78 fffffffffff82660 fffffffffff99dde fffffffffffb911b 0000000000000000 fffffffffff1ce31 ffffffffffebd99a 0000000000005fb4 0000000000000000",
        "ffffffffffff287e fffffffffff810c5 fffffffffff98e5e fffffffffffb555a 0000000000000000 fffffffffff177ca ffffffffffebbdde 000000000000613a 0000000000000000",
        "ffffffffffff2343 fffffffffff7fb0b fffffffffff97e82 fffffffffffb1922 0000000000000000 fffffffffff121da ffffffffffeba223 00000000000062b5 0000000000000000",
        "ffffffffffff1dc8 fffffffffff7e534 fffffffffff96e4b fffffffffffadc68 0000000000000000 fffffffffff0cc2f ffffffffffeb8667 0000000000006427 0000000000000000",
        "ffffffffffff180c fffffffffff7cf3f fffffffffff95db9 fffffffffffa9f35 0000000000000000 fffffffffff076d9 ffffffffffeb6aab 000000000000658f 0000000000000000",
        "ffffffffffff120d fffffffffff7b92d fffffffffff94ccc fffffffffffa618a 0000000000000000 fffffffffff021d3 ffffffffffeb4eef 00000000000066f8 0000000000000000",
        "ffffffffffff0bcd fffffffffff7a2fd fffffffffff93b84 fffffffffffa2364 0000000000000000 ffffffffffefcd1d ffffffffffeb3334 0000000000006860 0000000000000000",
        "ffffffffffff0549 fffffffffff78cb0 fffffffffff929e3 fffffffffff9e4c9 0000000000000000 ffffffffffef78c9 ffffffffffeb1778 00000000000069c9 0000000000000000",
        "fffffffffffefe82 fffffffffff77645 fffffffffff917e8 fffffffffff9a5bd 0000000000000000 ffffffffffef24d5 ffffffffffeafbbc 0000000000006b31 0000000000000000",
        "fffffffffffef778 fffffffffff75fbd fffffffffff90594 fffffffffff96639 0000000000000000 ffffffffffeed13e ffffffffffeae000 0000000000006c99 0000000000000000",
        "fffffffffffef029 fffffffffff74916 fffffffffff8f2e7 fffffffffff92632 0000000000000000 ffffffffffee7dd8 ffffffffffeac445 0000000000006e02 0000000000000000",
        "fffffffffffee896 fffffffffff73253 fffffffffff8dfe2 fffffffffff8e5c9 0000000000000000 ffffffffffee2afe ffffffffffeaa889 0000000000006f6a 0000000000000000",
        "fffffffffffee0bd fffffffffff71b72 fffffffffff8cc84 fffffffffff8a4d7 0000000000000000 ffffffffffedd84a ffffffffffea8ccd 00000000000070d3 0000000000000000",
        "fffffffffffed89f fffffffffff70473 fffffffffff8b8cf fffffffffff8637f 0000000000000000 ffffffffffed8618 ffffffffffea7112 000000000000723b 0000000000000000",
        "fffffffffffed03a fffffffffff6ed56 fffffffffff8a4c3 fffffffffff821c1 0000000000000000 ffffffffffed345f ffffffffffea5556 00000000000073a4 0000000000000000",
        "fffffffffffec78f fffffffffff6d61c fffffffffff8905f fffffffffff7df7d 0000000000000000 ffffffffffece2d8 ffffffffffea399a 000000000000750c 0000000000000000",
        "fffffffffffebe9d fffffffffff6bec5 fffffffffff87ba5 fffffffffff79cbf 0000000000000000 ffffffffffec919d ffffffffffea1dde 0000000000007675 0000000000000000",
        "fffffffffffeb563 fffffffffff6a74f fffffffffff86695 fffffffffff759a1 0000000000000000 ffffffffffec40ec ffffffffffea0223 00000000000077dd 0000000000000000",
        "fffffffffffeabe0 fffffffffff68fbd fffffffffff8512f fffffffffff715f5 0000000000000000 ffffffffffebf055 ffffffffffe9e667 0000000000007946 0000000000000000",
        "fffffffffffea216 fffffffffff6780c fffffffffff83b74 fffffffffff6d1e4 0000000000000000 ffffffffffeba03b ffffffffffe9caab 0000000000007aae 0000000000000000",
        "fffffffffffe9802 fffffffffff6603e fffffffffff82563 fffffffffff68d7e 0000000000000000 ffffffffffeb50bc ffffffffffe9aeef 0000000000007c16 0000000000000000",
        "fffffffffffe8da5 fffffffffff64853 fffffffffff80efe fffffffffff6488d 0000000000000000 ffffffffffeb0162 ffffffffffe99334 0000000000007d7f 0000000000000000",
        "fffffffffffe82fd fffffffffff6304a fffffffffff7f845 fffffffffff6031d 0000000000000000 ffffffffffeab24a ffffffffffe97778 0000000000007ee7 0000000000000000",
        "fffffffffffe784b fffffffffff6183b fffffffffff7e1ac fffffffffff5f89d 000000000000164f ffffffffffead05d ffffffffffe95bbc 0000000000007ee7 00000000000000f5",
        "fffffffffffe6d8d fffffffffff60026 fffffffffff7cb33 fffffffffff5ee3f 0000000000002ca4 ffffffffffeaee5c ffffffffffe94000 0000000000007ee7 00000000000001eb",
        "fffffffffffe62c4 fffffffffff5e80c fffffffffff7b4da fffffffffff5e3de 00000000000042d0 ffffffffffeb0c5b ffffffffffe92445 0000000000007ee7 00000000000002e1",
        "fffffffffffe57f1 fffffffffff5cfeb fffffffffff79ea1 fffffffffff5d99d 00000000000058d4 ffffffffffeb2aa0 ffffffffffe90889 0000000000007ee7 00000000000003d7",
        "fffffffffffe4d12 fffffffffff5b7c4 fffffffffff78888 fffffffffff5cf24 0000000000006eae ffffffffffeb48d2 ffffffffffe8eccd 0000000000007ee7 00000000000004cc",
        "fffffffffffe4228 fffffffffff59f97 fffffffffff77290 fffffffffff5c4dd 000000000000845f ffffffffffeb673e ffffffffffe8d112 0000000000007ee7 00000000000005c2",
        "fffffffffffe3734 fffffffffff58763 fffffffffff75cb8 fffffffffff5ba9f 00000000000099e6 ffffffffffeb85c7 ffffffffffe8b556 0000000000007ee7 00000000000006b8",
        "fffffffffffe2c34 fffffffffff56f28 fffffffffff74701 fffffffffff5b06b 000000000000af45 ffffffffffeba443 ffffffffffe8999a 0000000000007ee7 00000000000007ae",
        "fffffffffffe212a fffffffffff556e6 fffffffffff7316a fffffffffff5a653 000000000000c44d ffffffffffebc2d0 ffffffffffe87dde 0000000000007ee7 00000000000008a3",
        "fffffffffffe1615 fffffffffff53e9d fffffffffff71bf4 fffffffffff59c0e 000000000000d92c ffffffffffebe199 ffffffffffe86223 0000000000007ee7 0000000000000999",
        "fffffffffffe0af4 fffffffffff5264c fffffffffff7069f fffffffffff591f3 000000000000ee3e ffffffffffec003b ffffffffffe84667 0000000000007ee7 0000000000000a8f",
        "fffffffffffdffca fffffffffff50df4 fffffffffff6f16b fffffffffff587ee 00000000000102f8 ffffffffffec1f1b ffffffffffe82aab 0000000000007ee7 0000000000000b85",
        "fffffffffffdf494 fffffffffff4f595 fffffffffff6dc58 fffffffffff57ddc 000000000001175b ffffffffffec3e1d ffffffffffe80eef 0000000000007ee7 0000000000000c7a",
        "fffffffffffde954 fffffffffff4dd2d fffffffffff6c766 fffffffffff573cd 0000000000012bc2 ffffffffffec5d0c ffffffffffe7f334 0000000000007ee7 0000000000000d70",
        "fffffffffffdde10 fffffffffff4c4be fffffffffff6b291 fffffffffff57070 000000000001402e ffffffffffec7871 ffffffffffe7d778 0000000000007ee7 0000000000000e66",
        "fffffffffffdd2d8 fffffffffff4ac46 fffffffffff69dd1 fffffffffff57b10 000000000001541a ffffffffffec8c2c ffffffffffe7bbbc 0000000000007ee7 0000000000000f5c",
        "fffffffffffdc7ab fffffffffff493c6 fffffffffff68927 fffffffffff585de 00000000000167dc ffffffffffeca015 ffffffffffe7a000 0000000000007ee7 0000000000001051",
        "fffffffffffdbc89 fffffffffff47b3d fffffffffff67491 fffffffffff590b6 0000000000017b74 ffffffffffecb411 ffffffffffe78445 0000000000007ee7 0000000000001147",
        "fffffffffffdb174 fffffffffff462ac fffffffffff66012 fffffffffff59b94 0000000000018f3a ffffffffffecc845 ffffffffffe76889 0000000000007ee7 000000000000123d",
        "fffffffffffda66a fffffffffff44a11 fffffffffff64ba8 fffffffffff5a678 000000000001a27e ffffffffffecdc82 ffffffffffe74ccd 0000000000007ee7 0000000000001333",
        "fffffffffffd9b6b fffffffffff4316e fffffffffff63753 fffffffffff5b175 000000000001b59b ffffffffffecf071 ffffffffffe73112 0000000000007ee7 0000000000001428",
        "fffffffffffd9078 fffffffffff418c1 fffffffffff62313 fffffffffff5bc67 000000000001c892 ffffffffffed04a4 ffffffffffe71556 0000000000007ee7 000000000000151e",
        "fffffffffffd8591 fffffffffff4000b fffffffffff60eea fffffffffff5c739 000000000001dbb3 ffffffffffed1918 ffffffffffe6f99a 0000000000007ee7 0000000000001614",
        "fffffffffffd7ab6 fffffffffff3e74b fffffffffff5fad6 fffffffffff5d230 000000000001ee57 ffffffffffed2d54 ffffffffffe6ddde 0000000000007ee7 000000000000170a",
        "fffffffffffd6fe6 fffffffffff3ce81 fffffffffff5e6d8 fffffffffff5dd50 00000000000200a7 ffffffffffed41b7 ffffffffffe6c223 0000000000007ee7 00000000000017ff",
        "fffffffffffd6522 fffffffffff3b5ae fffffffffff5d2ef fffffffffff5e841 0000000000021324 ffffffffffed5614 ffffffffffe6a667 0000000000007ee7 00000000000018f5",
        "fffffffffffd5a6a fffffffffff39cd0 fffffffffff5bf1d fffffffffff5f388 0000000000022577 ffffffffffed6a9c ffffffffffe68aab 0000000000007ee7 00000000000019eb",
        "fffffffffffd4fbe fffffffffff383e8 fffffffffff5ab60 fffffffffff5fe7c 0000000000023777 ffffffffffed7f27 ffffffffffe66eef 0000000000007ee7 0000000000001ae1",
        "fffffffffffd451d fffffffffff36af5 fffffffffff597b9 fffffffffff609b5 0000000000024950 ffffffffffed9396 ffffffffffe65334 0000000000007ee7 0000000000001bd6",
        "fffffffffffd3a89 fffffffffff351f8 fffffffffff58428 fffffffffff614be 0000000000025b02 ffffffffffeda824 ffffffffffe63778 0000000000007ee7 0000000000001ccc",
        "fffffffffffd3000 fffffffffff338f0 fffffffffff570ae fffffffffff61fc4 0000000000026cb5 ffffffffffedbcd8 ffffffffffe61bbc 0000000000007ee7 0000000000001dc2",
        "fffffffffffd2583 fffffffffff31fdd fffffffffff55d49 fffffffffff62afb 0000000000027e14 ffffffffffedd197 ffffffffffe60000 0000000000007ee7 0000000000001eb8",
    ];

    // The drive decomposition, spelled as two shaping rows: a held-gated drift row authored first (its own lateral
    // rate and turnScale apply while the drift channel reads held), and the ordinary row behind it (unconditional,
    // last) — the FIRST open row governs, so the drift row's own facets replace the ordinary row's whenever it
    // wins. Both rows share the same longitudinal along facet; only across.lateral and turnScale differ.
    private static WorldShaping[] Shaping(float lateral = 22f) => [
        new WorldShaping(
            When: new ActionPredicate.Held(Channel: "drift"),
            Along: new WorldShapingAlong(Engage: 96f, ReversalRate: 120f, Release: 20f, BackwardSpeed: 5f),
            Across: new WorldShapingAcross(Lateral: 6f),
            TurnScale: 1.4f
        ),
        new WorldShaping(
            Along: new WorldShapingAlong(Engage: 96f, ReversalRate: 120f, Release: 20f, BackwardSpeed: 5f),
            Across: new WorldShapingAcross(Lateral: lateral)
        ),
    ];
    private static WorldDefinition BuildDriveDocument(float lateral = 22f, bool authorDrive = true) {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "drift", Shape: ChannelShape.Binary, Composition: true),
            new(Name: "boost", Shape: ChannelShape.Binary, Composition: true),
            new(Name: "pitch", Shape: ChannelShape.Bipolar, Role: ChannelRole.Pitch),
        };

        var drive = new BodyMotionProgram(
            Name: "drive",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveDriveFrame,
                BodyMotionOp.ResolveHold,
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
                    Envelope: new MotionScalarEnvelope(Max: 16f, Min: 16f),
                    Held: new WorldSpeedHeld(Channel: "boost", Multiplier: 1.5f)
                ),
                Turn: new WorldTurn(Rate: 2.4f, ReferenceSpeed: 4f, Falloff: 0.55f, PitchRate: 0.9f),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 30f),
                        Gravity: new WorldHoldGravity(Fall: 26f, Rise: 14f),
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

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [drive],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "kart-test",
        };
    }
    // The scripted intent, one span per tick range: throttle to the pinned speed, steer while moving (where lateral
    // lateral bites), back-throttle through a reversal into backward, a held drift stretch, a held sprint stretch, and a
    // pitched climb.
    private static PlayerIntent IntentAt(int tick) {
        var intent = default(PlayerIntent);

        if (tick < 40) {
            return intent.WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One);
        }
        if (tick < 88) {
            return intent
                .WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: TurnOrdinal, value: FixedQ4816.One);
        }
        if (tick < 136) {
            return intent
                .WithChannel(ordinal: ForwardOrdinal, value: -FixedQ4816.One)
                .WithChannel(ordinal: TurnOrdinal, value: -FixedQ4816.One);
        }
        if (tick < 176) {
            return intent
                .WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: TurnOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: DriftOrdinal, value: FixedQ4816.One);
        }
        if (tick < 208) {
            return intent
                .WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: TurnOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: BoostOrdinal, value: FixedQ4816.One);
        }

        return intent
            .WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
            .WithChannel(ordinal: PitchOrdinal, value: FixedQ4816.One);
    }
    private static string Hex(FixedQ4816 value) => value.Value.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
    private static string[] Trace(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

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

        Assert.True(condition: (moved > 0), userMessage: "a slidier drive lateral rate must move the trace, or the trace pins nothing about it");
    }
    [Fact]
    public void ADriveProgramOnAKitAuthoringNoShapingRow_RefusesValidationNamingTheFacet() {
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: BuildDriveDocument(),
            reason: out var admittedReason
        ), userMessage: admittedReason);
        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
            definition: BuildDriveDocument(authorDrive: false),
            reason: out var deniedReason
        ),
            userMessage: "a drive program against a kit authoring no shaping row was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "Shaping");
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
            (motion => motion with { ObstructionRaw = (motion.Obstruction with { GraceSeconds = 0f }) }, "obstruction.graceSeconds"),
            (motion => motion with { GroundStick = 0f }, "groundStick"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { When = new ActionPredicate.Held(Channel: "nope") }, motion.Shaping[1]] }, "shaping[0].when.channel"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { Across = motion.Shaping[0].Across! with { Lateral = 0f } }, motion.Shaping[1]] }, "shaping[0].across.lateral"),
            (motion => motion with { Shaping = [motion.Shaping![0] with { TurnScale = 0f }, motion.Shaping[1]] }, "shaping[0].turnScale"),
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
            Assert.Contains(actualString: reason, expectedSubstring: token);
        }
    }
    // The default kit (no maxPitch authored) climbs to exactly 1.2 radians — the engine's old hardcoded clamp, bit
    // for bit. A kit authoring a tighter ceiling climbs to its OWN bound instead, never the engine default.
    [Fact]
    public void MaxPitchClampsTheDriveFrameAtItsAuthoredCeilingNotAHardcodedOne() {
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 1.2), actual: SaturatedDrivePitch(definition: BuildDriveDocument()));

        var kits = BuildDriveDocument().Kits.ToList();
        kits[0] = (kits[0] with { Motion = (kits[0].Motion! with { Turn = kits[0].Motion!.Turn with { MaxPitch = 0.3f } }) });
        var narrowed = (BuildDriveDocument() with { KitRowsRaw = kits });

        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.3), actual: SaturatedDrivePitch(definition: narrowed));
    }
    // Full-pitch input held far longer than either ceiling takes to reach, so both scenarios have fully saturated
    // their own clamp by the last tick.
    private static FixedQ4816 SaturatedDrivePitch(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var intent = default(PlayerIntent)
            .WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
            .WithChannel(ordinal: PitchOrdinal, value: FixedQ4816.One);

        for (var tick = 0; (tick < 600); tick++) {
            body.SubmitIntent(intent: intent);
            fixture.Step();
        }

        return body.CaptureTransferState().DrivePitch;
    }
}
