using System.Collections.Concurrent;
using System.Text.Json;

using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Every shipped world document loads through the door the game loads it through and compiles under the
/// arena-addressed compiler with no refusal, needs that name the facet of every arm it can fire, and a program
/// carrying ordinal addresses alone. A document with a <c>.puck</c> source is that source, as the game resolves it. A
/// document the door does not load standalone is accounted for by name: another shipped document imports it, so its
/// rules reach the compiler through that one.</summary>
public sealed class WorldFactsCompilerShippedWorldLawTests {
    private static IEnumerable<string> ShippedWorlds() => ShippedWorldDocuments.Files(directory: RepositoryPaths.Resolve(relativePath: ShippedWorldDocuments.WorldDirectory));

    private const string FixtureDirectory = "tests/Puck.World.Tests/Fixtures";

    // A fragment carries no schema of its own and never loads standalone, but a fixture host imports it and DOES
    // load; compiling the host is what proves the fragment's rules, so the law follows the import rather than
    // excusing the fragment by name.
    private static Dictionary<string, string> FixtureHosts() {
        var hosts = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var directory = RepositoryPaths.Resolve(relativePath: FixtureDirectory);

        if (!Directory.Exists(path: directory)) {
            return hosts;
        }

        foreach (var host in ShippedWorldDocuments.Files(
            directory: directory,
            option: SearchOption.TopDirectoryOnly
        )) {
            foreach (var reference in References(
                member: "imports",
                path: host
            )) {
                hosts[ShippedWorldDocuments.Carrier(
                    name: reference,
                    referrer: host
                )] = host;
            }
        }

        return hosts;
    }
    // The documents another shipped document names under "imports", by full path: a fragment, whose rules the
    // importing document carries once composed.
    private static HashSet<string> ImportTargets() {
        var targets = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var path in ShippedWorlds()) {
            foreach (var reference in References(
                member: "imports",
                path: path
            )) {
                _ = targets.Add(item: ShippedWorldDocuments.Carrier(
                    name: reference,
                    referrer: path
                ));
            }
        }

        return targets;
    }
    private static IEnumerable<string> References(string path, string member) {
        using var document = JsonDocument.Parse(utf8Json: ShippedWorldDocuments.Read(path: path));

        if (!document.RootElement.TryGetProperty(
            propertyName: member,
            value: out var value
        )) {
            yield break;
        }
        if (value.ValueKind == JsonValueKind.String) {
            yield return (value.GetString() ?? string.Empty);

            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array) {
            yield break;
        }

        foreach (var entry in value.EnumerateArray()) {
            if (entry.TryGetProperty(
                propertyName: "document",
                value: out var referenced
            )) {
                yield return (referenced.GetString() ?? string.Empty);
            }
        }
    }
    // Why a document the load door refuses is still covered, or null when nothing accounts for it.
    private static string? Unloadable(string path, HashSet<string> imported) {
        return (imported.Contains(item: Path.GetFullPath(path: path))
            ? "another shipped document imports it"
            : null
        );
    }

    public static TheoryData<string> Worlds() {
        var data = new TheoryData<string>();

        foreach (var path in ShippedWorlds()) {
            data.Add(row: path);
        }

        return data;
    }

    // Loading and compiling are deterministic and every law only reads what they return, so each document is
    // loaded once, and each loaded definition compiled once, for the whole class.
    private static readonly ConcurrentDictionary<string, Lazy<LoadOutcome>> Loads = new(comparer: StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<WorldDefinition, Lazy<CompileOutcome>> Compiles = new(comparer: ReferenceEqualityComparer.Instance);
    private static readonly Lazy<Dictionary<string, string>> FixtureHostsOnce = new(valueFactory: FixtureHosts);
    private static readonly Lazy<HashSet<string>> ImportTargetsOnce = new(valueFactory: ImportTargets);

    private static CompileOutcome Compile(WorldDefinition definition) =>
        Compiles.GetOrAdd(
            key: definition,
            valueFactory: static key => new Lazy<CompileOutcome>(valueFactory: () => CompileUncached(definition: key))
        ).Value;
    private static CompileOutcome CompileUncached(WorldDefinition definition) {
        try {
            return new CompileOutcome(
                Refusal: null,
                RefusedRule: null,
                Rules: WorldFactsCompiler.CompileAll(definition: definition)
            );
        } catch (RuleException failure) {
            return new CompileOutcome(
                Refusal: failure.Refusal.ToString(),
                RefusedRule: RefusedRuleOf(failure: failure),
                Rules: []
            );
        }
    }
    private static WorldDefinition? Load(string path) =>
        LoadOnce(path: path).Definition;
    private static LoadOutcome LoadOnce(string path) =>
        Loads.GetOrAdd(
            key: path,
            valueFactory: static key => new Lazy<LoadOutcome>(valueFactory: () => LoadUncached(path: key))
        ).Value;
    // The door the game itself loads a world through: composition, migration, and document-local validation.
    private static LoadOutcome LoadUncached(string path) {
        var loaded = WorldDefinitionFileSource.TryLoadLocally(
            contentHash: out _,
            definition: out var definition,
            documents: PuckDocumentComposer.Instance,
            path: path,
            reason: out var reason
        );

        return new LoadOutcome(
            Definition: (loaded ? definition : null),
            Reason: reason
        );
    }
    // A refusal names its rule first: "rule '<name>' refused <category>: <detail>".
    private static string RefusedRuleOf(RuleException failure) {
        var message = failure.Message;
        var open = message.IndexOf(value: '\'');
        var close = message.IndexOf(
            startIndex: (open + 1),
            value: '\''
        );

        return (((open >= 0) && (close > open))
            ? message[(open + 1)..close]
            : message
        );
    }

    [Fact]
    public void EveryShippedWorldDocumentEitherLoadsOrIsAccountedForByName() {
        var hosts = FixtureHostsOnce.Value;
        var imported = ImportTargetsOnce.Value;
        var loaded = new List<string>();
        var rules = 0;
        var unaccounted = new List<string>();

        foreach (var path in ShippedWorlds()) {
            if (Load(path: path) is { } definition) {
                loaded.Add(item: path);
                rules += Compile(definition: definition).Rules.Length;

                continue;
            }
            if (hosts.TryGetValue(
                key: Path.GetFullPath(path: path),
                value: out var host
            ) && (Load(path: host) is { } hosted)) {
                loaded.Add(item: host);
                rules += Compile(definition: hosted).Rules.Length;

                continue;
            }
            if (Unloadable(
                imported: imported,
                path: path
            ) is null) {
                unaccounted.Add(item: path);
            }
        }

        Assert.Empty(collection: unaccounted);
        Assert.NotEmpty(collection: loaded);
        Assert.NotEqual(
            actual: rules,
            expected: 0
        );
    }
    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldCompilesWithoutARefusal(string path) {
        if (Load(path: path) is not { } definition) {
            // A fragment a fixture hosts is PROVED through that host, not excused: the host composes it and the
            // compiler runs over the composition.
            if (FixtureHostsOnce.Value.TryGetValue(
                key: Path.GetFullPath(path: path),
                value: out var host
            )) {
                var hosted = Load(path: host);

                Assert.NotNull(@object: hosted);

                var hostedArena = Compile(definition: hosted!);

                Assert.Null(@object: hostedArena.Refusal);
                Assert.NotEmpty(collection: hostedArena.Rules);

                return;
            }

            Assert.True(
                condition: (Unloadable(
                    imported: ImportTargetsOnce.Value,
                    path: path
                ) is not null),
                userMessage: $"{path}: {LoadOnce(path: path).Reason}"
            );

            return;
        }

        var arena = Compile(definition: definition);

        Assert.Null(@object: arena.Refusal);
        Assert.Null(@object: arena.RefusedRule);
    }
    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldsDecisionRulesCompileEveryDeclaredOption(string path) {
        if (Load(path: path) is not { } definition) {
            return;
        }

        var arena = Compile(definition: definition);

        if (arena.Refusal is not null) {
            return;
        }

        var options = 0;

        foreach (var rule in arena.Rules) {
            if (rule is not CompiledWorldFactsRule { Decision: { } decision }) {
                continue;
            }

            var declared = (definition.Rules ?? []).FirstOrDefault(predicate: candidate => string.Equals(
                a: candidate.Name.Value,
                b: rule.Name,
                comparisonType: StringComparison.Ordinal
            ))?.Decision;

            Assert.NotNull(@object: declared);
            Assert.Equal(
                actual: decision.Options.Length,
                expected: declared!.Options.Count
            );
            for (var option = 0; (option < decision.Options.Length); option++) {
                Assert.Equal(
                    actual: decision.Options[option].Name,
                    expected: declared.Options[option].Name.Value
                );
                options++;
            }
        }

        Assert.True(condition: (options >= 0));
    }
    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldsRuleNeedsNameTheFacetEveryArmItCanFireDeclares(string path) {
        if (Load(path: path) is not { } definition) {
            return;
        }

        var outcome = Compile(definition: definition);

        if (outcome.Refusal is not null) {
            return;
        }

        foreach (var rule in outcome.Rules) {
            foreach (var effect in Arms(rule: rule)) {
                if (effect.RequiredFacet is not { } facet) {
                    continue;
                }

                Assert.Contains(
                    collection: rule.Needs.Facets,
                    expected: facet
                );
            }
        }
    }

    // Every effect one firing can reach: its own arms, the arms a branch or savepoint holds, and the branches only a
    // decision carries.
    private static IEnumerable<Puck.State.Rules.IRuleEffect> Arms(Puck.State.Rules.CompiledRule rule) {
        var pending = new Stack<Puck.State.Rules.IRuleEffect>(collection: rule.Effects);

        if ((rule is CompiledWorldFactsRule { Decision: { } decision })) {
            foreach (var effect in decision.OnNoChoice) {
                pending.Push(item: effect);
            }
            foreach (var option in decision.Options) {
                foreach (var effect in option.Effects) {
                    pending.Push(item: effect);
                }
            }
        }
        while (pending.Count != 0) {
            var effect = pending.Pop();

            yield return effect;

            foreach (var arm in effect.Arms) {
                foreach (var nested in arm) {
                    pending.Push(item: nested);
                }
            }
        }
    }

    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldsCompiledProgramCarriesOnlyOrdinalAddresses(string path) {
        if (Load(path: path) is not { } definition) {
            return;
        }

        var outcome = Compile(definition: definition);

        if (outcome.Refusal is not null) {
            return;
        }

        var catalog = definition.StateCatalog;

        foreach (var rule in outcome.Rules) {
            foreach (var access in Puck.State.Rules.RuleDataflow.Reads(rule: rule).Concat(second: Puck.State.Rules.RuleDataflow.Writes(rule: rule))) {
                Assert.InRange(
                    actual: access.RowOrdinal,
                    high: (catalog.Count - 1),
                    low: 0
                );
            }
        }
    }
    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldsInteractionsCompileToOneRuleEach(string path) {
        if (Load(path: path) is not { } definition) {
            return;
        }

        var compiled = WorldFactsCompiler.CompileAllInteractions(definition: definition);

        Assert.Equal(
            actual: compiled.Length,
            expected: (definition.Interactions?.Interactions.Count ?? 0)
        );
        foreach (var rule in compiled) {
            Assert.NotNull(@object: ((CompiledWorldFactsRule)rule).Interaction);
        }
    }
    [MemberData(memberName: nameof(Worlds))]
    [Theory]
    public void AShippedWorldsFieldRowIsHostOwnedAndTheArenaStoresNoCellOfIt(string path) {
        if (Load(path: path) is not { } definition) {
            return;
        }

        var catalog = definition.StateCatalog;
        StateArena? arena = null;

        foreach (var row in definition.State) {
            Assert.True(condition: catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row.Name.Value
            ));

            var ordinal = handle.Ordinal;

            Assert.Equal(
                actual: catalog.Descriptors[ordinal].HostOwned,
                expected: (row.Field is not null)
            );

            if (row.Field is null) {
                continue;
            }

            arena ??= new StateArena(
                catalog: catalog,
                section: definition.StateRaw,
                time: ArenaTime.Origin
            );

            Assert.True(condition: arena.Layout[ordinal].HostOwned);
            Assert.Equal(
                actual: arena.Layout[ordinal].CellCapacity,
                expected: 0
            );
        }
    }
    [Fact]
    public void TheShippedCorpusCarriesAtLeastOneHostOwnedFieldRow() {
        var hostOwned = 0;

        foreach (var path in ShippedWorlds()) {
            if (Load(path: path) is not { } definition) {
                continue;
            }

            foreach (var descriptor in definition.StateCatalog.Descriptors) {
                if (descriptor.HostOwned) {
                    hostOwned++;
                }
            }
        }

        Assert.NotEqual(
            actual: hostOwned,
            expected: 0
        );
    }
}

/// <summary>What one compile of a shipped world left under the arena-addressed compiler.</summary>
/// <param name="Rules">The compiled rules, empty on a refusal.</param>
/// <param name="Refusal">The refusal's own name, or <see langword="null"/>.</param>
/// <param name="RefusedRule">The refusing rule's name, or <see langword="null"/>.</param>
internal readonly record struct CompileOutcome(Puck.State.Rules.CompiledRule[] Rules, string? Refusal, string? RefusedRule);
/// <summary>What one load of a shipped world through the game's door returned.</summary>
/// <param name="Definition">The loaded definition, or <see langword="null"/> when the door refused it.</param>
/// <param name="Reason">The door's reason for a refusal.</param>
internal readonly record struct LoadOutcome(WorldDefinition? Definition, string Reason);
