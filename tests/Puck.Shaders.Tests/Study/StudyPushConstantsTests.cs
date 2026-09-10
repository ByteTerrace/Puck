using System.Numerics;
using System.Runtime.InteropServices;

using Puck.Shaders.Study;

namespace Puck.Shaders.Tests.Study;

public sealed class StudyPushConstantsTests {
    // Every byte of a sentinel-filled field reads as this pattern (a nonzero float bit pattern doubling as a nonzero
    // int32), so a single byte-by-byte scan of CopyTo's output proves both that the field sits at its declared
    // offset and that nothing outside it was touched.
    private const byte SentinelByte = 0x01;
    private static readonly float SentinelFloat = BitConverter.Int32BitsToSingle(0x01010101);
    private static readonly Vector2 SentinelVector2 = new(SentinelFloat, SentinelFloat);
    private static readonly Vector3 SentinelVector3 = new(SentinelFloat, SentinelFloat, SentinelFloat);
    private static readonly Vector4 SentinelVector4 = new(SentinelFloat, SentinelFloat, SentinelFloat, SentinelFloat);

    [Fact]
    public void SizeMatchesTheFixedWireLayout() {
        Assert.Equal(expected: StudyPushConstants.SizeBytes, actual: Marshal.SizeOf<StudyPushConstants>());
        Assert.Equal(expected: 112, actual: StudyPushConstants.SizeBytes);
    }

    [Theory]
    [InlineData(nameof(StudyPushConstants.IResolution), 0, 12)]
    [InlineData(nameof(StudyPushConstants.ITime), 12, 4)]
    [InlineData(nameof(StudyPushConstants.ITimeDelta), 16, 4)]
    [InlineData(nameof(StudyPushConstants.IFrame), 20, 4)]
    [InlineData(nameof(StudyPushConstants.Pad0), 24, 8)]
    [InlineData(nameof(StudyPushConstants.IMouse), 32, 16)]
    [InlineData(nameof(StudyPushConstants.IDate), 48, 16)]
    [InlineData(nameof(StudyPushConstants.ICameraPos), 64, 12)]
    [InlineData(nameof(StudyPushConstants.ICameraFov), 76, 4)]
    [InlineData(nameof(StudyPushConstants.ICameraTarget), 80, 12)]
    [InlineData(nameof(StudyPushConstants.Pad1), 92, 4)]
    [InlineData(nameof(StudyPushConstants.ICameraUp), 96, 12)]
    [InlineData(nameof(StudyPushConstants.Pad2), 108, 4)]
    public void CopyToPlacesEveryFieldAtItsContractedRowOffsetAndTouchesNothingElse(string field, int offset, int size) {
        var bytes = new byte[StudyPushConstants.SizeBytes];

        WithOnlyOneFieldSet(field: field).CopyTo(destination: bytes);

        for (var index = 0; (index < bytes.Length); index++) {
            var expected = (((index >= offset) && (index < (offset + size))) ? SentinelByte : ((byte)0));

            Assert.True(condition: (expected == bytes[index]), userMessage: $"byte {index}: expected 0x{expected:x2}, got 0x{bytes[index]:x2} (probing '{field}' at [{offset}, {(offset + size)}))");
        }
    }

    private static StudyPushConstants WithOnlyOneFieldSet(string field) => field switch {
        nameof(StudyPushConstants.IResolution) => new StudyPushConstants(IResolution: SentinelVector3, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ITime) => new StudyPushConstants(IResolution: default, ITime: SentinelFloat, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ITimeDelta) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: SentinelFloat, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.IFrame) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: 0x01010101, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.Pad0) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: SentinelVector2, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.IMouse) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: SentinelVector4, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.IDate) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: SentinelVector4, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ICameraPos) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: SentinelVector3, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ICameraFov) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: SentinelFloat, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ICameraTarget) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: SentinelVector3, Pad1: default, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.Pad1) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: SentinelFloat, ICameraUp: default, Pad2: default),
        nameof(StudyPushConstants.ICameraUp) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: SentinelVector3, Pad2: default),
        nameof(StudyPushConstants.Pad2) => new StudyPushConstants(IResolution: default, ITime: default, ITimeDelta: default, IFrame: default, Pad0: default, IMouse: default, IDate: default, ICameraPos: default, ICameraFov: default, ICameraTarget: default, Pad1: default, ICameraUp: default, Pad2: SentinelFloat),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(field), actualValue: field, message: "Unknown field."),
    };
}
