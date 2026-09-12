using System.Collections.Frozen;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

public sealed partial class WorldMachineHost {
    private static FrozenDictionary<string, MachineMemoryAddress> ResolveBindings(WorldMachine declaration, MachineLease lease) {
        var resolved = new Dictionary<string, MachineMemoryAddress>(StringComparer.Ordinal);
        var writes = new List<(string Name, MachineMemoryAddress Address)>();
        foreach (var binding in declaration.Memory ?? []) {
            var site = $"{declaration.Name}.memory[{binding.Name}]";
            if (lease.Runtime is not IMachineHardwareAccess hardware) {
                throw new ArgumentException($"{site}: the runtime has no hardware access capability.");
            }
            var address = binding.Address;
            if (binding.Symbol is { } symbol) {
                foreach (var asset in lease.Assets.Values) {
                    if (asset.PreparedContent?.Symbols.TryGetValue(symbol, out var exported) != true) {
                        continue;
                    }
                    if (address is not null) {
                        throw new ArgumentException($"{site}: symbol '{symbol}' is ambiguous across prepared content assets.");
                    }
                    if (exported.Space != binding.Space) {
                        throw new ArgumentException($"{site}: symbol '{symbol}' belongs to space '{exported.Space}', not '{binding.Space}'.");
                    }
                    address = exported.Address;
                }
            }
            if (address is not { } start) {
                throw new ArgumentException($"{site}: symbol '{binding.Symbol}' is unavailable in the prepared content.");
            }
            var space = hardware.MemorySpaces.FirstOrDefault(space => space.Name == binding.Space);
            var mode = binding.Access switch {
                "patch" => MachineAccessMode.Patch, "bus" => MachineAccessMode.Bus, _ => MachineAccessMode.Inspect,
            };
            if (space is null || !space.Widths.Contains(binding.Width) || !space.AccessModes.Contains(mode)) {
                throw new ArgumentException($"{site}: space '{binding.Space}' does not support {binding.Format} {binding.Access} access.");
            }
            if (start < space.FirstAddress || start > space.LastAddress ||
                (ulong)(binding.Width - 1) > space.LastAddress - start) {
                throw new ArgumentException($"{site}: address 0x{start:X} is outside space '{binding.Space}' for this configuration.");
            }
            var scalar = new MachineMemoryAddress(binding.Space, start, binding.Width);
            if (binding.Direction == WorldMachineMemoryDirection.Write) {
                foreach (var previous in writes) {
                    if (previous.Address.Space == scalar.Space &&
                        start <= previous.Address.Address + (ulong)previous.Address.Width - 1 &&
                        previous.Address.Address <= start + (ulong)binding.Width - 1) {
                        throw new ArgumentException($"{site}: write overlaps binding '{previous.Name}'.");
                    }
                }
                writes.Add((binding.Name, scalar));
            }
            resolved.Add(binding.Name, scalar);
        }
        return resolved.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
