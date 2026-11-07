using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Cli;

/// <summary>
/// Names the source state a World build is made from, so an unchanged checkout is built once and every later run
/// reuses that build.
/// <para>
/// The key hashes what <see cref="CheckoutContent"/> reads over the <see cref="WorldArtifactClosure"/> roots only:
/// git's object id for each root in <c>HEAD</c>, and the path, status and on-disk SHA-256 of every changed or untracked
/// path under a root, excluding changed outputs owned by <c>ShaderDeclarations</c>: the build regenerates those
/// from model inputs already in the closure. Files git ignores are not inputs; every item the World's build names outside <c>bin</c> and
/// <c>obj</c> is tracked or untracked, never ignored. The key also carries the runtime identifier of the machine, the
/// <c>CI</c> environment variable the shared build properties read, and the build's own command line, since each
/// changes what the same sources build into.
/// </para>
/// <para>
/// A change anywhere outside the closure, such as documentation, tests or a project the World does not reference,
/// leaves the key unchanged. Two worktrees whose closures hold identical content produce the same key.
/// </para>
/// </summary>
public static class WorldArtifactKey {
    // The schema name distinguishes the keying rule, so a build keyed under another rule cannot name these inputs.
    private const string Schema = "puck.world-artifact-key.v2";

    /// <summary>Computes the key of the checkout's current World sources.</summary>
    /// <param name="repositoryRoot">The checkout, which must be a git working tree with a <c>HEAD</c> commit.</param>
    /// <param name="roots">The closure roots, from <see cref="WorldArtifactClosure.Walk"/>.</param>
    /// <param name="buildArguments">The build's command line, without its output directory.</param>
    /// <param name="key">The key: 32 lowercase hexadecimal digits, or empty on failure.</param>
    /// <param name="reason">Why no key could be computed, or empty on success.</param>
    /// <returns><see langword="true"/> when both git queries answered.</returns>
    public static bool TryCompute(string repositoryRoot, IReadOnlyList<string> roots, IReadOnlyList<string> buildArguments, out string key, out string reason) {
        key = string.Empty;

        if (!CheckoutContent.TryRead(
            reason: out reason,
            repositoryRoot: repositoryRoot,
            roots: roots,
            snapshot: out var snapshot
        )) {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        void Append(string text) =>
            hash.AppendData(data: Encoding.UTF8.GetBytes(s: $"{text}\0"));

        Append(text: Schema);
        Append(text: RuntimeInformation.RuntimeIdentifier);
        Append(text: $"CI={Environment.GetEnvironmentVariable(variable: "CI")}");
        Append(text: string.Join(
            separator: '\u001f',
            values: buildArguments
        ));
        Append(text: snapshot.Committed);
        // These files are regenerated before compilation from the model in this closure. Their on-disk drift is
        // output, not a source edit. Use the generator's one ownership list rather than another filename policy.
        var generated = Puck.SdfVm.ShaderDeclarations.Of(
            Puck.SdfVm.ShaderDeclarations.InterfaceFiles(repositoryRoot), Puck.Shaders.RenderGraphPackageCatalog.Engine, [])
            .Select(declaration => declaration.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var change in snapshot.Changes) {
            if (generated.Contains(change.Path)) { continue; }
            Append(text: $"{change.Entry}\0{change.Content}");
        }

        key = Convert.ToHexStringLower(inArray: hash.GetHashAndReset()[..16]);

        return true;
    }
}
