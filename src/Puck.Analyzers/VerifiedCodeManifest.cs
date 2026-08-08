namespace Puck.Analyzers;

/// <summary>One <c>VerifiedCode.json</c> entry: the recorded fingerprint a branded declaration must still match.</summary>
/// <param name="Id">The manifest key — the same string a <c>[VerifiedCode(id)]</c> attribute argument names.</param>
/// <param name="Assembly">The assembly that declares this entry's branded member, and the one compilation responsible for sweeping the entry when nothing claims it.</param>
/// <param name="Symbol">The documentation-comment id of the symbol this entry was recorded for; the claiming declaration must produce exactly this id.</param>
/// <param name="Algorithm">The fingerprint algorithm this entry's hash was computed with (only <c>csharp-tokens-v1</c> is understood).</param>
/// <param name="Sha256">The lowercase-hex SHA-256 fingerprint recorded at the last verification.</param>
/// <param name="Basis">Why this brand is trusted (see <see cref="Puck.VerifiedCodeAttribute.Basis"/>); compared against the attribute's own <c>Basis</c> when the attribute carries one.</param>
/// <param name="Dependencies">
/// The documentation-comment ids of the declarations outside the branded one that its proof rests on — the
/// constants it reads, the representation it is written against. Each is fingerprinted alongside the branded
/// declaration, so editing one moves the recorded hash. Exactly one level deep, listed by hand.
/// </param>
/// <param name="Laws">The law ids whose proof justifies this brand (see <c>VerifiedCodeManifestTests</c>, which checks these resolve).</param>
internal sealed record VerifiedCodeManifestEntry(string Id, string Assembly, string Symbol, string Algorithm, string Sha256, IReadOnlyList<string> Basis, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Laws);

/// <summary>Something wrong with the manifest that must be reported rather than absorbed.</summary>
/// <param name="EntryId">The entry the fault belongs to, or <see langword="null"/> when the whole document is at fault.</param>
/// <param name="Message">What is wrong, phrased for a build log.</param>
internal sealed record VerifiedCodeManifestFault(string? EntryId, string Message);

/// <summary>
/// The outcome of reading a <c>VerifiedCode.json</c> document.
/// </summary>
/// <param name="Usable">
/// <see langword="false"/> when the document itself could not be read — not JSON, a root that is not an object, a
/// schema version this reader does not implement, a repeated member, or no <c>entries</c> object. Nothing can then
/// be believed about any entry, so no brand may be judged against it.
/// </param>
/// <param name="Entries">Every entry that read cleanly, keyed by manifest id.</param>
/// <param name="FaultedIds">The ids of entries that did not read cleanly; a brand claiming one is already answered by the entry's own fault.</param>
/// <param name="Faults">Every fault found, document-level first.</param>
internal sealed record VerifiedCodeManifestReading(
    bool Usable,
    IReadOnlyDictionary<string, VerifiedCodeManifestEntry> Entries,
    ISet<string> FaultedIds,
    IReadOnlyList<VerifiedCodeManifestFault> Faults);

/// <summary>Reads <c>VerifiedCode.json</c>, refusing everything it cannot vouch for.</summary>
/// <remarks>
/// An entry-level failure is per-entry, not fatal to the document: the manifest exists so a brand can never
/// silently disappear, and discarding every other entry's sweep because one entry is off-schema throws away
/// exactly the guarantee it is meant to provide. A document-level failure is fatal, because nothing then
/// establishes that any entry was read at all.
/// </remarks>
internal static class VerifiedCodeManifest {
    /// <summary>The one fingerprint algorithm this analyzer knows how to recompute.</summary>
    public const string TokenAlgorithm = "csharp-tokens-v1";

    /// <summary>The one schema version this reader implements.</summary>
    public const int SupportedFormat = 1;

    /// <summary>The number of hex characters a recorded SHA-256 must have.</summary>
    private const int Sha256HexLength = 64;

    /// <summary>Reads a <c>VerifiedCode.json</c> document, collecting everything wrong with it rather than throwing.</summary>
    /// <param name="json">The complete file text.</param>
    /// <returns>The entries that read cleanly, and every fault that must be reported.</returns>
    public static VerifiedCodeManifestReading Read(string json) {
        Dictionary<string, object?> root;

        try {
            root = ((MiniJson.Parse(json: json) as Dictionary<string, object?>)
                ?? throw new FormatException(message: "its root is not a JSON object"));
        } catch (FormatException exception) {
            return Unusable(message: $"VerifiedCode.json could not be read: {exception.Message}");
        }

        if (!root.TryGetValue(key: "format", value: out var rawFormat) || (rawFormat is not double format)) {
            return Unusable(message: "VerifiedCode.json has no numeric 'format' member, so the schema its entries are written in is unknown.");
        }

        if (format != SupportedFormat) {
            return Unusable(message: $"VerifiedCode.json declares format '{Describe(value: rawFormat)}', which this analyzer does not implement; it reads format {SupportedFormat} only.");
        }

        if (!root.TryGetValue(key: "entries", value: out var rawEntries) || (rawEntries is not Dictionary<string, object?> entriesObject)) {
            return Unusable(message: "VerifiedCode.json has no object-valued 'entries' member.");
        }

        var entries = new Dictionary<string, VerifiedCodeManifestEntry>(comparer: StringComparer.Ordinal);
        var faultedIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var faults = new List<VerifiedCodeManifestFault>();

        foreach (var pair in entriesObject) {
            try {
                entries[pair.Key] = ReadEntry(id: pair.Key, value: pair.Value);
            } catch (FormatException exception) {
                faultedIds.Add(item: pair.Key);
                faults.Add(item: new VerifiedCodeManifestFault(EntryId: pair.Key, Message: exception.Message));
            }
        }

        return new VerifiedCodeManifestReading(Usable: true, Entries: entries, FaultedIds: faultedIds, Faults: faults);
    }

    /// <summary>A reading of a manifest that is not there at all, or whose text could not be obtained.</summary>
    public static VerifiedCodeManifestReading Unusable(string message) =>
        new(
            Usable: false,
            Entries: new Dictionary<string, VerifiedCodeManifestEntry>(comparer: StringComparer.Ordinal),
            FaultedIds: new HashSet<string>(comparer: StringComparer.Ordinal),
            Faults: [new VerifiedCodeManifestFault(EntryId: null, Message: message)]);

    private static VerifiedCodeManifestEntry ReadEntry(string id, object? value) {
        var entryObject = ((value as Dictionary<string, object?>)
            ?? throw new FormatException(message: $"VerifiedCode.json entry '{id}' is not a JSON object."));

        var assembly = RequireString(entryObject: entryObject, id: id, field: "assembly");
        var symbol = RequireString(entryObject: entryObject, id: id, field: "symbol");
        var algorithm = RequireString(entryObject: entryObject, id: id, field: "algorithm");
        var sha256 = RequireString(entryObject: entryObject, id: id, field: "sha256");
        var basis = ReadStringArray(entryObject: entryObject, id: id, field: "basis");
        var dependencies = ReadStringArray(entryObject: entryObject, id: id, field: "dependencies");
        var laws = ReadStringArray(entryObject: entryObject, id: id, field: "laws");

        if (!string.Equals(a: algorithm, b: TokenAlgorithm, comparisonType: StringComparison.Ordinal)) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' records algorithm '{algorithm}', which nothing here implements; the only fingerprint this analyzer can recompute is '{TokenAlgorithm}'.");
        }

        if (!IsRecordedHash(text: sha256)) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' records sha256 '{sha256}', which is not a {Sha256HexLength}-character lowercase-hex SHA-256 and so can never match a computed fingerprint.");
        }

        if (assembly.Length == 0) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' records an empty 'assembly', so no compilation can ever be held responsible for it.");
        }

        var qualifiedName = QualifiedName(symbol: symbol);

        if (qualifiedName is null) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' records symbol '{symbol}', which is not a documentation-comment id, so the entry names no declaration.");
        }

        if (!qualifiedName.StartsWith(value: $"{assembly}.", comparisonType: StringComparison.Ordinal)) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' records symbol '{symbol}' against assembly '{assembly}', but that symbol does not name a member of '{assembly}'; an entry whose recorded owner and recorded symbol disagree is swept by no compilation.");
        }

        CheckDependencies(id: id, assembly: assembly, dependencies: dependencies);

        return new VerifiedCodeManifestEntry(Id: id, Assembly: assembly, Symbol: symbol, Algorithm: algorithm, Sha256: sha256, Basis: basis, Dependencies: dependencies, Laws: laws);
    }

    /// <summary>
    /// Refuses a <c>dependencies</c> array the fingerprint could not fold deterministically: an element that is not
    /// a documentation-comment id names nothing, an element outside the entry's own assembly names nothing the
    /// owning compilation can walk, and a repeated element would be sealed twice under a list that reads as a set.
    /// </summary>
    private static void CheckDependencies(string id, string assembly, IReadOnlyList<string> dependencies) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var dependency in dependencies) {
            var qualifiedName = QualifiedName(symbol: dependency);

            if (qualifiedName is null) {
                throw new FormatException(message: $"VerifiedCode.json entry '{id}' records dependency '{dependency}', which is not a documentation-comment id, so the entry names no declaration to seal alongside its own.");
            }

            if (!qualifiedName.StartsWith(value: $"{assembly}.", comparisonType: StringComparison.Ordinal)) {
                throw new FormatException(message: $"VerifiedCode.json entry '{id}' records dependency '{dependency}' against assembly '{assembly}', but that symbol does not name a member of '{assembly}'; only the owning compilation walks this entry, and it can only walk its own source.");
            }

            if (!seen.Add(item: dependency)) {
                throw new FormatException(message: $"VerifiedCode.json entry '{id}' records dependency '{dependency}' more than once; the dependency list is sealed as a set, so a repeat has no meaning the fingerprint could honour.");
            }
        }
    }

    /// <summary>Strips a documentation-comment id's <c>M:</c>/<c>T:</c>/<c>P:</c> kind prefix, or refuses text that carries none.</summary>
    private static string? QualifiedName(string symbol) {
        if ((symbol.Length < 3) || (symbol[1] != ':') || ("NTFMPE!".IndexOf(value: symbol[0]) < 0)) {
            return null;
        }

        return symbol.Substring(startIndex: 2);
    }

    /// <summary>Whether <paramref name="text"/> is the exact shape this analyzer writes: lowercase hex, full width.</summary>
    private static bool IsRecordedHash(string text) {
        if (text.Length != Sha256HexLength) {
            return false;
        }

        foreach (var character in text) {
            var isHex = (((character >= '0') && (character <= '9')) || ((character >= 'a') && (character <= 'f')));

            if (!isHex) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Renders a parsed JSON number back into something a build log can quote.</summary>
    private static string Describe(object? value) =>
        (value switch {
            double number => number.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            null => "null",
            _ => value.ToString(),
        } ?? "null");
    private static string RequireString(Dictionary<string, object?> entryObject, string id, string field) =>
        ((entryObject.TryGetValue(key: field, value: out var raw) && (raw is string text))
            ? text
            : throw new FormatException(message: $"VerifiedCode.json entry '{id}' is missing a string '{field}' member."));
    private static IReadOnlyList<string> ReadStringArray(Dictionary<string, object?> entryObject, string id, string field) {
        if (!entryObject.TryGetValue(key: field, value: out var raw) || (raw is not List<object?> items)) {
            throw new FormatException(message: $"VerifiedCode.json entry '{id}' is missing an array '{field}' member.");
        }

        return items
            .Select(selector: (item, index) => ((item as string) ?? throw new FormatException(message: $"VerifiedCode.json entry '{id}' has a non-string element at '{field}[{index}]'.")))
            .ToArray();
    }
}
