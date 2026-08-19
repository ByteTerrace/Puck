namespace Puck.ShaderVm;

/// <summary>Compiles a <see cref="ShaderExpression"/> graph into a packed program.</summary>
public static class ShaderExpressionCompiler {
    /// <summary>Compiles one value graph, evaluating each shared node once into a local register.</summary>
    /// <param name="root">The value the program returns.</param>
    /// <returns>The packed program.</returns>
    /// <exception cref="InvalidOperationException">More values are live at once than the register file holds.</exception>
    public static ShaderProgram Compile(ShaderExpression root) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var uses = new Dictionary<ShaderExpression, int>(comparer: ReferenceEqualityComparer.Instance);

        CountUses(node: root, uses: uses);

        // The emission order is fixed by the walk below, so one dry pass over it dates every register's definition
        // and last read. The second pass hands a register back the moment its value is read for the last time.
        var lastRead = new Dictionary<ShaderExpression, int>(comparer: ReferenceEqualityComparer.Instance);
        var spilled = new HashSet<ShaderExpression>(comparer: ReferenceEqualityComparer.Instance);
        var step = 0;

        Plan(
            lastRead: lastRead,
            node: root,
            spilled: spilled,
            step: ref step,
            uses: uses
        );

        var builder = new ShaderProgramBuilder();
        var free = new Stack<int>(collection: Enumerable.Range(count: ShaderIsa.MaxLocals, start: 0).Reverse());
        var registers = new Dictionary<ShaderExpression, int>(comparer: ReferenceEqualityComparer.Instance);

        step = 0;

        Emit(
            builder: builder,
            free: free,
            lastRead: lastRead,
            node: root,
            registers: registers,
            spilled: spilled,
            step: ref step
        );

        return builder.Build();
    }
    private static void CountUses(ShaderExpression node, Dictionary<ShaderExpression, int> uses) {
        if (uses.TryGetValue(key: node, value: out var count)) {
            uses[node] = (count + 1);

            return;
        }

        uses.Add(key: node, value: 1);

        foreach (var child in node.Children) {
            CountUses(node: child, uses: uses);
        }
    }
    private static void Plan(ShaderExpression node, Dictionary<ShaderExpression, int> uses, HashSet<ShaderExpression> spilled, Dictionary<ShaderExpression, int> lastRead, ref int step) {
        if (spilled.Contains(item: node)) {
            lastRead[node] = step++;

            return;
        }

        foreach (var child in node.Children) {
            Plan(
                lastRead: lastRead,
                node: child,
                spilled: spilled,
                step: ref step,
                uses: uses
            );
        }

        // A leaf reloads more cheaply than it spills, so only a shared operation earns a register.
        if ((uses[node] > 1) && (node.Kind == ShaderExpressionKind.Operation)) {
            _ = spilled.Add(item: node);
            lastRead[node] = step;
        }

        step++;
    }
    private static void Emit(ShaderProgramBuilder builder, ShaderExpression node, HashSet<ShaderExpression> spilled, Dictionary<ShaderExpression, int> registers, Dictionary<ShaderExpression, int> lastRead, Stack<int> free, ref int step) {
        if (registers.TryGetValue(key: node, value: out var local)) {
            _ = builder.LoadLocal(index: local);

            if (lastRead[node] == step) {
                _ = registers.Remove(key: node);

                free.Push(item: local);
            }

            step++;

            return;
        }

        foreach (var child in node.Children) {
            Emit(
                builder: builder,
                free: free,
                lastRead: lastRead,
                node: child,
                registers: registers,
                spilled: spilled,
                step: ref step
            );
        }

        _ = node.Kind switch {
            ShaderExpressionKind.Constant => builder.LoadConstant(value: node.ConstantValue),
            ShaderExpressionKind.Input => builder.LoadInput(input: ((ShaderInput)node.Operand)),
            ShaderExpressionKind.Parameter => builder.LoadParameter(index: checked((int)node.Operand)),
            _ => builder.Append(op: node.Op, operand: node.Operand),
        };

        if (spilled.Contains(item: node)) {
            if (free.Count == 0) {
                throw new InvalidOperationException(message: $"The value graph keeps more than {ShaderIsa.MaxLocals} results live at once.");
            }

            local = free.Pop();

            registers.Add(key: node, value: local);
            _ = builder.StoreLocal(index: local);
            _ = builder.LoadLocal(index: local);
        }

        step++;
    }
}
