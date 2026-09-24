using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.State;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Lowering;

public sealed partial class DocumentScope {
    private const string ModuleInstancesAnnotation = "ModuleInstances";

    // The aliases a module's own body uses its modules under, read once from its statements.
    private static readonly ConditionalWeakTable<TemplateNode, Dictionary<string, string>> ModuleBodyInstances = new();

    // Every `use module as alias(…)` among the statements, alias to the module name as written, through blocks and
    // compile-time loops the way the structural names are found.
    private static void CollectModuleInstances(IReadOnlyList<StatementNode> statements, Dictionary<string, string> instances) {
        foreach (var statement in statements) {
            switch (statement) {
                case ExpressionStatementNode { IsUse: true, UseAlias: { } alias, Expression: CallExpressionNode call }:
                    _ = instances.TryAdd(
                        key: alias,
                        value: call.Name
                    );

                    break;
                case BlockNode block:
                    CollectModuleInstances(
                        instances: instances,
                        statements: block.Statements
                    );

                    break;
                case ForStatementNode loop:
                    CollectModuleInstances(
                        instances: instances,
                        statements: loop.Body
                    );

                    break;
            }
        }
    }
    private void IndexModuleInstances(IReadOnlyList<StatementNode> statements) {
        if (
            !Annotations.TryGetValue(
            key: ModuleInstancesAnnotation,
            value: out var held
        ) ||
            (held is not Dictionary<string, string> instances)
        ) {
            instances = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            Annotations[ModuleInstancesAnnotation] = instances;
        }

        CollectModuleInstances(
            instances: instances,
            statements: statements
        );
    }
    private Dictionary<string, string>? InstancesOfModule(string module) {
        if (!Templates.TryGetValue(
            key: module,
            value: out var template
        )) {
            return null;
        }

        return ModuleBodyInstances.GetValue(
            createValueCallback: static template => {
                var instances = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

                CollectModuleInstances(
                    instances: instances,
                    statements: template.Body.Statements
                );

                return instances;
            },
            key: template
        );
    }

    /// <summary>Resolves a qualified reference to a name a module instance declares. An instance a
    /// <c>use module as alias(…)</c> of this scope stands declares each of its module's names as the generated name
    /// <c>alias$name</c> (<see cref="GeneratedName.Qualify"/>), and the scope reads it as <c>alias.name</c>; an instance
    /// that instance's module uses in turn reads through each alias (<c>box.child.score</c> is
    /// <c>box$child$score</c>). The resolution is by the aliases the scope and its modules' bodies write, never by
    /// what an expansion declared, so a reference reads the same before its <c>use</c> as after it.</summary>
    /// <param name="reference">The dotted reference as written.</param>
    /// <param name="qualified">The generated name, on success.</param>
    /// <returns><see langword="true"/> when the reference's head is a module instance of this scope and every segment
    /// after it but the last is an instance of the module before it; the last segment is the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public bool TryQualify(string reference, [NotNullWhen(returnValue: true)] out string? qualified) {
        ArgumentNullException.ThrowIfNull(argument: reference);
        qualified = null;

        var dotted = QualifiedName.Parse(text: reference);

        if (
            !dotted.IsQualified ||
            !Annotations.TryGetValue(
            key: ModuleInstancesAnnotation,
            value: out var held
        ) ||
            (held is not Dictionary<string, string> scopeInstances)
        ) {
            return false;
        }

        var segments = dotted.Segments;

        if (
            !dotted.IsWellFormed ||
            Locals.ContainsKey(key: segments[0]) ||
            !scopeInstances.TryGetValue(
            key: segments[0],
            value: out var module
        )
        ) {
            return false;
        }

        var heads = new List<string> { segments[0] };
        var index = 1;

        while (
            (index < (segments.Count - 1)) &&
            (InstancesOfModule(module: module) is { } instances) &&
            instances.TryGetValue(
            key: segments[index],
            value: out var inner
        )
        ) {
            heads.Add(item: segments[index]);
            module = inner;
            index++;
        }

        if (index != (segments.Count - 1)) {
            return false;
        }

        var name = segments[index];

        for (var head = (heads.Count - 1); (head >= 0); head--) {
            name = GeneratedName.Qualify(
                head: heads[head],
                name: name
            );
        }

        qualified = name;

        return true;
    }
}
