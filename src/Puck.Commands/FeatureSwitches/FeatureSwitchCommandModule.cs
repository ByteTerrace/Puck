using System.Text;

namespace Puck.Commands;

/// <summary>
/// The generic <c>feature.*</c> console verbs over a <see cref="FeatureSwitchRegistry"/>: <c>feature.list</c> prints a
/// fixed-width, invariant table of every switch (name, kind, value, default, allowed); <c>feature.set</c> and
/// <c>feature.get</c> drive and read one switch; <c>feature.reset</c> restores one switch (or all) to its default. The
/// module speaks only the registry — no engine, backend, or demo reference — so any host that populates a registry gets
/// the same control plane. It is content-blind by construction: the registry's descriptors carry the delegates that
/// reach the real levers.
/// </summary>
public sealed class FeatureSwitchCommandModule(FeatureSwitchRegistry registry) : ICommandModule {
    private readonly FeatureSwitchRegistry m_registry = registry;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Plain(
            description: "Lists every feature switch as a fixed-width table: name, kind, value, default, allowed.",
            handler: _ => new CommandResult(List()),
            name: "feature.list"
        );
        yield return CommandDefinition.WithWireArgs(
            description: "Sets a feature switch: feature.set <name> <value>. The value must be one of the switch's allowed values; a switch may still reject it (read-only / boot-only).",
            handler: (_, args) => new CommandResult(Set(args: in args)),
            name: "feature.set"
        );
        yield return CommandDefinition.WithWireArgs(
            description: "Reads one feature switch: feature.get <name> — echoes its current value, default, kind, and allowed set.",
            handler: (_, args) => new CommandResult(Get(args: in args)),
            name: "feature.get"
        );
        yield return CommandDefinition.WithWireArgs(
            description: "Resets a feature switch to its default: feature.reset [name]. With no name, resets every switch.",
            handler: (_, args) => new CommandResult(Reset(args: in args)),
            name: "feature.reset"
        );
    }

    private string List() {
        var switches = m_registry.All;

        if (switches.Count == 0) {
            return "[feature.list: no switches registered]";
        }

        // Materialize the rows once (Get() is a live delegate) so the value column is read a single time and column
        // widths line up with what actually prints.
        var rows = new (string Name, string Kind, string Value, string Default, string Allowed)[switches.Count];

        for (var index = 0; (index < switches.Count); index++) {
            var descriptor = switches[index];

            rows[index] = (
                descriptor.Name,
                descriptor.Kind.ToString(),
                descriptor.Get(),
                descriptor.DefaultValue,
                string.Join(separator: '/', values: descriptor.AllowedValues)
            );
        }

        var nameWidth = ColumnWidth(header: "name", selector: static row => row.Name, rows: rows);
        var kindWidth = ColumnWidth(header: "kind", selector: static row => row.Kind, rows: rows);
        var valueWidth = ColumnWidth(header: "value", selector: static row => row.Value, rows: rows);
        var defaultWidth = ColumnWidth(header: "default", selector: static row => row.Default, rows: rows);

        var builder = new StringBuilder();

        _ = builder.AppendLine(value: $"[feature] {"name".PadRight(totalWidth: nameWidth)}  {"kind".PadRight(totalWidth: kindWidth)}  {"value".PadRight(totalWidth: valueWidth)}  {"default".PadRight(totalWidth: defaultWidth)}  allowed");

        foreach (var row in rows) {
            _ = builder.AppendLine(value: $"[feature] {row.Name.PadRight(totalWidth: nameWidth)}  {row.Kind.PadRight(totalWidth: kindWidth)}  {row.Value.PadRight(totalWidth: valueWidth)}  {row.Default.PadRight(totalWidth: defaultWidth)}  {row.Allowed}");
        }

        // Trim the trailing newline so the result is one transcript block, not a block plus a blank line.
        return builder.ToString().TrimEnd();
    }
    private string Set(in WireArgs args) {
        if (args.Count < 2) {
            return "[feature.set: usage — feature.set <name> <value>]";
        }

        var name = args[0].ToString();
        var value = args[1].ToString();

        if (!m_registry.TryGet(name: name, descriptor: out var descriptor)) {
            return $"[feature.set: unknown switch '{name}' — feature.list shows the valid set]";
        }

        if (!descriptor.AllowedValues.Contains(value: value, comparer: StringComparer.Ordinal)) {
            return $"[feature.set: {name} rejects '{value}' — allowed: {string.Join(separator: '/', values: descriptor.AllowedValues)}]";
        }

        if (!descriptor.Set(arg: value)) {
            return $"[feature.set: {name} rejected '{value}' (read-only / boot-only) — value unchanged at '{descriptor.Get()}']";
        }

        return $"[feature.set: {name} = {descriptor.Get()}]";
    }
    private string Get(in WireArgs args) {
        if (args.Count < 1) {
            return "[feature.get: usage — feature.get <name>]";
        }

        var name = args[0].ToString();

        if (!m_registry.TryGet(name: name, descriptor: out var descriptor)) {
            return $"[feature.get: unknown switch '{name}' — feature.list shows the valid set]";
        }

        return $"[feature.get: {name} = {descriptor.Get()} (default {descriptor.DefaultValue}, kind {descriptor.Kind}, allowed {string.Join(separator: '/', values: descriptor.AllowedValues)})]";
    }
    private string Reset(in WireArgs args) {
        if (args.Count == 0) {
            foreach (var entry in m_registry.All) {
                _ = entry.Set(arg: entry.DefaultValue);
            }

            return $"[feature.reset: {m_registry.All.Count} switch(es) restored to defaults]";
        }

        var name = args[0].ToString();

        if (!m_registry.TryGet(name: name, descriptor: out var descriptor)) {
            return $"[feature.reset: unknown switch '{name}' — feature.list shows the valid set]";
        }

        if (!descriptor.Set(arg: descriptor.DefaultValue)) {
            return $"[feature.reset: {name} rejected its default '{descriptor.DefaultValue}' — value unchanged at '{descriptor.Get()}']";
        }

        return $"[feature.reset: {name} = {descriptor.Get()}]";
    }
    private static int ColumnWidth(string header, Func<(string Name, string Kind, string Value, string Default, string Allowed), string> selector, (string Name, string Kind, string Value, string Default, string Allowed)[] rows) {
        var width = header.Length;

        foreach (var row in rows) {
            width = Math.Max(val1: width, val2: selector(arg: row).Length);
        }

        return width;
    }

    // A no-argument console verb (mirrors GravityCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);
}
