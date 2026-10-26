namespace Puck.Launcher.Release;

/// <summary>A local development channel for completed build output. Publication uses the ordinary directory
/// source; installation still goes through signature verification, the stager and the applier.</summary>
public sealed class LocalBuildReleaseSource(string buildDirectory, string sourceDirectory) : IReleaseSource {
    private readonly DirectoryReleaseSource m_source = new(root: sourceDirectory);

    /// <summary>Publishes a complete build with a development key kept outside its versioned payload.</summary>
    public ReleaseTrustAnchor Publish(string app, string channel, string version, string keyPath, ulong sequence) {
        using var key = DevelopmentReleaseSigner.LoadOrCreate(path: keyPath);
        ReleaseTrustAnchor? anchor = null;

        m_source.Publish(buildDirectory, UpdateService.CurrentRid(), new ReleaseManifest(
            Schema: ReleaseManifest.CurrentSchema, App: app, Channel: channel, Version: version,
            StateGeneration: 0, MinimumSupported: null, Payloads: [], Rollout: new ReleaseRollout(Percent: 100),
            Revoked: null, Notes: null, Signature: null), manifest => {
                var signed = DevelopmentReleaseSigner.Sign(manifest: manifest, root: key, sequence: sequence);

                anchor = signed.TrustAnchor;
                return signed.Manifest;
            });
        return anchor!;
    }
    /// <inheritdoc/>
    public Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) =>
        m_source.TryGetLatestManifestAsync(cancellationToken: cancellationToken, channel: channel);
    /// <inheritdoc/>
    public Task<bool> TryGetFileAsync(Puck.Assets.ContentPin pin, Stream destination, long length, CancellationToken cancellationToken) =>
        m_source.TryGetFileAsync(cancellationToken: cancellationToken, destination: destination, length: length, pin: pin);
}
