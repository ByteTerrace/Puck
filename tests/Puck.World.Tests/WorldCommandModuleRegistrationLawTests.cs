using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Puck.Commands;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Registers every console module the engine ships, without a device, so a table mismatch in a verb only a windowed
/// World or a silo composes (the render levers, the pointer and stick routers, the SDF verbs, the silo's own) fails
/// here and not at that boot: a definition factory refuses a description that does not contain its declared forms, a
/// form table that spells no form, and a command without a declared bindability, each when the module's
/// <see cref="ICommandModule.GetCommands"/> runs.
/// </summary>
public sealed class WorldCommandModuleRegistrationLawTests {
    // Dependencies a module's definitions are built from. Every other parameter is null: a definition only closes over
    // its module, and no handler runs here.
    private static object? Supply(Type parameter) => ((parameter == typeof(Puck.World.Client.PlayerRoster))
        ? ClientFixtures.Client(definition: Fixtures.BuildDocument()).Roster
        : (parameter.IsValueType ? Activator.CreateInstance(type: parameter) : null));

    // Every concrete ICommandModule of the World's closure: the composition root, the launcher, the console, the client
    // and the silo, which is composed by a different root.
    private static IReadOnlyList<Type> ModuleTypes() {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var queue = new Queue<Assembly>(collection: [typeof(WorldBootComposition).Assembly, typeof(Puck.World.Silo.SiloCommandModule).Assembly]);
        var types = new List<Type>();

        while (queue.Count > 0) {
            var assembly = queue.Dequeue();

            if (!seen.Add(item: assembly.GetName().Name!)) {
                continue;
            }

            types.AddRange(collection: assembly.GetTypes().Where(predicate: static type => (type.IsClass && !type.IsAbstract && typeof(ICommandModule).IsAssignableFrom(c: type))));

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (reference.Name!.StartsWith(value: "Puck.", comparisonType: StringComparison.Ordinal)) {
                    queue.Enqueue(item: Assembly.Load(assemblyRef: reference));
                }
            }
        }

        return [.. types.OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal)];
    }
    private static ICommandModule Create(Type type) {
        foreach (var constructor in type.GetConstructors(bindingAttr: (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).OrderBy(keySelector: static constructor => constructor.GetParameters().Length)) {
            try {
                return (ICommandModule)constructor.Invoke(parameters: [.. constructor.GetParameters().Select(selector: static parameter => Supply(parameter: parameter.ParameterType))]);
            } catch (TargetInvocationException) {
                // A constructor that refuses a null dependency: the next shape, or the bare instance below.
            }
        }

        return (ICommandModule)RuntimeHelpers.GetUninitializedObject(type: type);
    }
    private static IReadOnlyList<(Type Module, CommandDefinition Definition)> Definitions() {
        var definitions = new List<(Type, CommandDefinition)>();

        foreach (var type in ModuleTypes()) {
            try {
                definitions.AddRange(collection: Create(type: type).GetCommands().Select(selector: definition => (type, definition)));
            } catch (Exception exception) {
                Assert.Fail(message: $"{type.FullName} did not register without a device: {exception.GetType().Name}: {exception.Message}. A module whose definitions need a dependency that a null cannot stand for is supplied it in {nameof(Supply)}.");
            }
        }

        return definitions;
    }

    [Fact]
    public void EveryModuleRegistersItsVerbsWithoutADevice() {
        var definitions = Definitions();
        var modules = definitions.Select(selector: static entry => entry.Module.Name).ToHashSet(comparer: StringComparer.Ordinal);

        // The shapes a headless boot never composes are in the set.
        Assert.Contains(expected: "WorldRenderLeverCommandModule", collection: modules);
        Assert.Contains(expected: "WorldSdfCommandModule", collection: modules);
        Assert.Contains(expected: "SiloCommandModule", collection: modules);
        Assert.Contains(expected: "PlayerCommandModule", collection: modules);
        Assert.Contains(expected: "world.render-scale", collection: definitions.Select(selector: static entry => entry.Definition.Name));
    }

    // A verb without a form table takes no argument, so its description spells none after its name: the grammar of
    // a verb lives in its table alone.
    [Fact]
    public void AVerbWithoutAFormTableSpellsNoGrammarInItsDescription() {
        var prose = new List<string>();

        foreach (var (module, definition) in Definitions()) {
            if (definition.Forms is not null) {
                continue;
            }

            var spelled = new Regex(
                pattern: $@"(?<![\w.\-]){Regex.Escape(str: definition.Name)} (\[|<[A-Za-z]|--)",
                options: RegexOptions.CultureInvariant
            );

            if (spelled.IsMatch(input: definition.Description)) {
                prose.Add(item: $"{definition.Name} ({module.Name})");
            }
        }

        Assert.True(condition: (prose.Count == 0), userMessage: $"These verbs spell a grammar in their description beside no CommandForms table: {string.Join(separator: ", ", values: prose)}.");
    }

    // The whole closure but the silo composes into one registry (the silo answers a different console), so a verb or
    // alias two modules both claim, or a command with no declared bindability, fails here whatever shape boots.
    [Fact]
    public void TheModulesOfOneConsoleComposeIntoOneRegistry() {
        var modules = ModuleTypes()
            .Where(predicate: static type => (type.Namespace != "Puck.World.Silo"))
            .Select(selector: Create)
            .ToArray();
        var registry = new CommandRegistry(modules: modules);

        Assert.True(condition: (registry.CommandCount > 0));
    }
}
