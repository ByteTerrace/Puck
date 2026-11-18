namespace Puck.State;

/// <summary>A deterministic sampler for laws that exercise installed declarations and raw HMAC vectors.</summary>
internal sealed class PrivateDrawFixture() : PrivateDrawAuthority(new byte[32]) {
    internal Draw Bind(Draw draw, long ordinal) => InstallStream(draw, ordinal);
    // A stream under a raw key is what a private site bound to an imagined sampler under that key draws from.
    internal static PrivateDrawStream Raw(ClosedBitset256 key) =>
        new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), PrivateStream: true).Imagined(key: key).Secret!;
}
