using System.Collections.Concurrent;
using System.Text.Json;

namespace Puck.Cli.Deps;

/// <summary>The versions nuget.org lists for a package, read from its flat container (the one feed the repository's
/// <c>nuget.config</c> names). A package the feed does not know has no versions.</summary>
public sealed class NuGetFeed(HttpClient http) {
    private const string Container = "https://api.nuget.org/v3-flatcontainer/";

    private readonly ConcurrentDictionary<string, Task<string[]>> m_versions = new(comparer: StringComparer.OrdinalIgnoreCase);

    private async Task<string[]> ReadAsync(string id, CancellationToken cancellationToken) {
        using var response = await http.GetAsync(cancellationToken: cancellationToken, requestUri: $"{Container}{id.ToLowerInvariant()}/index.json").ConfigureAwait(continueOnCapturedContext: false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { return []; }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        using var document = await JsonDocument.ParseAsync(cancellationToken: cancellationToken, utf8Json: stream).ConfigureAwait(continueOnCapturedContext: false);

        return [.. document.RootElement.GetProperty(propertyName: "versions").EnumerateArray().Select(selector: static version => version.GetString()!)];
    }

    /// <summary>Lists every version the feed holds for a package, prereleases included.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The versions as the feed spells them; empty when the feed has no such package.</returns>
    public Task<string[]> VersionsAsync(string id, CancellationToken cancellationToken) =>
        m_versions.GetOrAdd(key: id, valueFactory: key => ReadAsync(cancellationToken: cancellationToken, id: key));
    /// <summary>Finds the newest stable version of a package the feed holds.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="admits">Which stable versions count; every one when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The version, or <see langword="null"/> when none counts.</returns>
    public async Task<DependencyVersion?> LatestStableAsync(string id, Func<DependencyVersion, bool>? admits, CancellationToken cancellationToken) =>
        DependencyVersion.HighestStable(admits: admits, spellings: await VersionsAsync(cancellationToken: cancellationToken, id: id).ConfigureAwait(continueOnCapturedContext: false));
}
