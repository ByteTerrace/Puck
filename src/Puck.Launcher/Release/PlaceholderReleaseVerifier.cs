namespace Puck.Launcher.Release;

/// <summary>
/// The <see cref="IReleaseVerifier"/> <see cref="LauncherServiceRegistration.AddSelfUpdate"/> resolves when
/// <see cref="UpdateOptions.TrustAnchor"/> is still <see cref="ReleaseTrustAnchor.Placeholder"/>: it refuses every
/// manifest outright, by name, without importing the placeholder's (empty) key bytes or constructing a
/// <see cref="Puck.Attestation.TrustList"/> from them — a build that still carries the placeholder can never accept
/// a release by accident, and never crashes trying to import an anchor that was never meant to verify anything.
/// </summary>
public sealed class PlaceholderReleaseVerifier : IReleaseVerifier {
    /// <inheritdoc/>
    public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) =>
        ReleaseVerifyOutcome.Refuse(reason: "the release trust anchor is still the build-time placeholder — no release can be accepted until the composition root pins a real one");
}
