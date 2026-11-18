namespace Puck.State;

/// <summary>A deterministic sampler for laws that exercise installed declarations and raw HMAC vectors.</summary>
internal sealed class PrivateDrawFixture() : PrivateDrawAuthority(new byte[32]) {
    internal Draw Bind(Draw draw, long ordinal) => InstallStream(draw, ordinal);
    internal static PrivateDrawSample Raw(ClosedBitset256 key) => FromKey(key);
}
