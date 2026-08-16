using System.Text.Json;

namespace Puck.Maths.Tests;

/// <summary>One law's declaration, as authored in <c>laws/*.json</c>.</summary>
/// <param name="Id">The law id, which is also the test's display name and the key its run binding is registered under.</param>
/// <param name="Tier">The tier token, parsed against <see cref="Tests.Tier"/>.</param>
/// <param name="Members">The public members this case claims to cover.</param>
/// <param name="Legs">What the statement stands on; at least one is required.</param>
internal sealed record LawDeclaration(string Id, string Tier, IReadOnlyList<MemberRef> Members, IReadOnlyList<LegText> Legs);
/// <summary>A covered member, named by declaring type and member name.</summary>
/// <param name="Type">The declaring type's full name, generic definitions included (for example <c>PresentedAlgebra`2</c>).</param>
/// <param name="Name">The member name; all overloads of that name are credited together, exactly as the C# form did.</param>
internal sealed record MemberRef(string Type, string Name);
/// <summary>A leg's authored text, one field per <see cref="Leg"/> slot.</summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="Leg"/> exactly — kind, flavor and the five prose slots — so the round trip through
/// JSON is lossless and both leg gates read precisely what they read before.
/// </para>
/// <para>
/// <b>A row is materialized directly rather than through the factories in <c>Legs.cs</c>.</b> Those factories exist to
/// make illegal combinations unspellable in C#, which a JSON row can obviously ignore, so the guarantee moves rather
/// than disappearing: <c>LegLedgerTests.LawLegsAreDeclared</c> already checks every invariant the factories encode —
/// an agreement names what it stands against, a shared-substrate leg names what is shared, a delegation or
/// shared-exact leg carries a citation, a transcription names its witness, a canary names an absolute sibling, a
/// doc-gap citation sits on a structural leg. What was a compile error becomes a Default-tier failure, on a tier every
/// change already runs.
/// </para>
/// </remarks>
internal sealed record LegText(string Kind, string Flavor, string Subject, string Against, string Shared, string Citation, string Absolute) {
    /// <summary>Materializes the authored row as the leg the gates read.</summary>
    /// <returns>The leg carrying this row's kind, flavor and prose.</returns>
    /// <exception cref="InvalidOperationException">The kind or flavor token names no member of its enum.</exception>
    public Leg ToLeg() =>
        new(
            Kind: Parse<LegKind>(token: Kind, slot: nameof(Kind)),
            Flavor: Parse<ShareFlavor>(token: Flavor, slot: nameof(Flavor)),
            Subject: Subject,
            Against: Against,
            Shared: Shared,
            Citation: Citation,
            Absolute: Absolute
        );

    private static TEnum Parse<TEnum>(string token, string slot) where TEnum : struct, Enum =>
        (Enum.TryParse<TEnum>(ignoreCase: false, result: out var parsed, value: token)
            ? parsed
            : throw new InvalidOperationException(message: $"'{token}' is not a {typeof(TEnum).Name} and cannot fill a leg's {slot} slot."));
}
/// <summary>
/// Reads the authored law declarations out of <c>laws/*.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The declarations are DATA — ids, tiers, covered members and leg prose — and live in one file per family so a family
/// can be reviewed, diffed and edited on its own. What stays in C# is the part that genuinely binds to code: the run
/// delegate, registered by id in <see cref="LawRegistry"/>. The split is not cosmetic. The leg text is the ONE safety
/// property no gate can check — nothing reads the bodies a leg describes — so it has to be readable by a person, and
/// four hundred character string literals buried among generic combinators are not.
/// </para>
/// <para>
/// <b>These files are AUTHORED, unlike <c>coverage-manifest.json</c>, <c>frontier.json</c> and <c>leg-ledger.md</c>,
/// which are machine-written and must never be hand-edited.</b> The directory is the tell: authored declarations live
/// under <c>laws/</c>, generated registers at the project root.
/// </para>
/// </remarks>
internal static class LawDeclarations {
    private static readonly JsonSerializerOptions ReadOptions = new() {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly IReadOnlyDictionary<string, LawDeclaration> Declarations = Load();

    /// <summary>Gets every authored declaration, keyed by law id.</summary>
    public static IReadOnlyDictionary<string, LawDeclaration> All => Declarations;
    /// <summary>Gets the directory the authored declaration files live in.</summary>
    public static string Directory => Path.Combine(path1: TestPaths.ProjectDirectory, path2: "laws");

    private static IReadOnlyDictionary<string, LawDeclaration> Load() {
        var declarations = new Dictionary<string, LawDeclaration>(comparer: StringComparer.Ordinal);

        if (!System.IO.Directory.Exists(path: Directory)) { return declarations; }

        foreach (var path in System.IO.Directory.EnumerateFiles(path: Directory, searchPattern: "*.json").OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal)) {
            var rows = (JsonSerializer.Deserialize<List<LawDeclaration>>(json: File.ReadAllText(path: path), options: ReadOptions)
                ?? throw new InvalidOperationException(message: $"{Path.GetFileName(path: path)} did not parse as a law declaration list."));

            foreach (var row in rows) {
                if (!declarations.TryAdd(key: row.Id, value: row)) {
                    throw new InvalidOperationException(message: $"the law id '{row.Id}' is declared twice; ids are the test display name and must be unique.");
                }
            }
        }

        return declarations;
    }
}
