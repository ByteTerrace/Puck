using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateMachineCables(IReadOnlyList<WorldMachine> machines, IReadOnlyList<WorldScreen> screens, List<string> errors) {
        var cables = new Dictionary<string, List<(int Position, int Screen, string Path)>>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < machines.Count); index++) {
            var machine = machines[index];

            if (machine?.Cable is not { } cable) {
                continue;
            }
            var screen = screens.FirstOrDefault(predicate: candidate => ((candidate?.Source is WorldScreenSource.Machine source) && string.Equals(
                a: source.Instance,
                b: machine.Name,
                comparisonType: StringComparison.Ordinal
            )));
            var path = $"machines[{index}].cable";

            if (
                string.IsNullOrWhiteSpace(value: cable.Name) ||
                !IsKebabCase(value: cable.Name)
            ) {
                errors.Add(item: $"{path}.name '{cable.Name}' must be non-empty kebab-case.");
                continue;
            }
            if (cable.Position < 0) {
                errors.Add(item: $"{path}.position {cable.Position} must be non-negative.");
                continue;
            }
            if (screen is null) {
                errors.Add(item: $"{path}: machine '{machine.Name}' has a cable but no display source consumes it.");
                continue;
            }
            if (!cables.TryGetValue(
                key: cable.Name,
                value: out var members
            )) {
                members = [];
                cables.Add(
                    key: cable.Name,
                    value: members
                );
            }
            members.Add(item: (cable.Position, screen.Index, path));
        }
        foreach (var (name, members) in cables) {
            if (members.Count < 2) {
                errors.Add(item: $"cable '{name}' has one plugged port (screen {members[0].Screen}) — a cable links two or more machines.");
                continue;
            }
            var positions = new HashSet<int>();

            foreach (var member in members) {
                if (!positions.Add(item: member.Position)) {
                    errors.Add(item: $"{member.Path}.position {member.Position} is already taken on cable '{name}'.");
                } else if (member.Position >= members.Count) {
                    errors.Add(item: $"{member.Path}.position {member.Position} leaves a gap on cable '{name}'.");
                }
            }
        }
    }
    private static void ValidateMachines(WorldDefinition definition, IMachineValidationCatalog? catalog,
        List<string> errors, ICollection<string>? deferred) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < definition.Machines.Count); index++) {
            var machine = definition.Machines[index];
            var path = $"machines[{index}]";

            if (machine is null) {
                errors.Add(item: $"{path}: a machine declaration is required.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(value: machine.Name)) {
                errors.Add(item: $"{path}.name: a nonempty instance name is required.");
            } else if (!names.Add(item: machine.Name)) {
                errors.Add(item: $"{path}.name: duplicate instance '{machine.Name}'.");
            }
            path = $"{path} ({machine.Name})";
            ValidateMachineBindings(
                definition: definition,
                errors: errors,
                machine: machine,
                path: path
            );
            if (string.IsNullOrWhiteSpace(value: machine.Engine)) {
                errors.Add(item: $"{path}.engine: a provider identifier is required.");
            } else if (catalog is null) {
                deferred?.Add(item: $"{path}.configuration: validation is deferred because no machine catalog was supplied for '{machine.Engine}'.");
            } else if (!catalog.TryDescriptor(
                machine.Engine,
                out var descriptor
            )) {
                errors.Add(item: $"{path}.engine: provider '{machine.Engine}' is unavailable in the selected machine catalog.");
            } else {
                var configurationErrors = new List<string>();

                _ = MachineConfigurationValidation.TryValidate(
                    descriptor.Configuration,
                    machine.Configuration,
                    schemaTag: true,
                    configurationErrors
                );
                foreach (var error in configurationErrors) {
                    errors.Add(item: $"{path}.{error}");
                }
            }
        }
    }
    private static void ValidateMachineBindings(WorldDefinition definition, WorldMachine machine, string path, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var binding in (machine.Memory ?? [])) {
            if (binding is null) {
                errors.Add(item: $"{path}.memory: a binding is required.");
                continue;
            }
            var site = $"{path}.memory[{binding.Name}]";

            if (
                string.IsNullOrWhiteSpace(value: binding.Name) ||
                !names.Add(item: binding.Name)
            ) {
                errors.Add(item: $"{site}.name: binding names must be nonempty and unique within the machine.");
            }
            if (!Enum.IsDefined(value: binding.Direction)) {
                errors.Add(item: $"{site}.direction: unsupported direction.");
            }
            if (binding.Width == 0) {
                errors.Add(item: $"{site}.format: unsupported scalar format '{binding.Format}'.");
            }
            if (string.IsNullOrWhiteSpace(value: binding.Space)) {
                errors.Add(item: $"{site}.space: a provider address space is required.");
            }
            if (
                (binding.Address.HasValue == (binding.Symbol is not null)) ||
                (binding.Symbol is { Length: 0 })
            ) {
                errors.Add(item: $"{site}: specify exactly one raw address or nonempty symbol.");
            }
            if ((binding.Direction == WorldMachineMemoryDirection.Read)
                ? (binding.Access != "inspect")
                : (binding.Access is not ("patch" or "bus"))) {
                errors.Add(item: $"{site}.access: reads require inspect; writes require patch or bus.");
            }
            if (binding.Update is not ("onChange" or "everyTick")) {
                errors.Add(item: $"{site}.update: expected onChange or everyTick.");
            }
            if (binding.Conversion is not ("checked" or "truncate")) {
                errors.Add(item: $"{site}.conversion: expected checked or truncate.");
            }
            ValidateMemoryRow(
                binding.Row,
                binding.Key,
                site,
                definition,
                errors
            );
        }
    }
}
