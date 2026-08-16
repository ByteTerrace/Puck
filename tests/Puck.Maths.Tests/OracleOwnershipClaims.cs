using System.Numerics;
using System.Reflection;

namespace Puck.Maths.Tests;

internal static class OracleOwnershipClaims {
    internal static string? FunctorRequiresOneMaterial() {
        var three = PrimeFieldMaterial.Create(modulus: 3);
        var five = PrimeFieldMaterial.Create(modulus: 5);
        var unwindowedSource = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: three)
        );
        var unwindowedForeignTarget = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: five)
        );

        if (RefusedParameter(
            action: () => _ = PresentedFunctor<ulong, PrimeFieldMaterial>.TryCreate(
                source: unwindowedSource,
                target: unwindowedForeignTarget,
                images: [unwindowedForeignTarget.Generator(symbol: 0)],
                functor: out _,
                obstruction: out _
            )
        ) is not "target") {
            return "an unwindowed GF(3) source mapped into a GF(5) target without a scalar morphism";
        }

        var source = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: three, windowDegree: 2)
        );
        var target = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: PrimeFieldMaterial.Create(modulus: 3), windowDegree: 2)
        );

        if (!PresentedFunctor<ulong, PrimeFieldMaterial>.TryCreate(
            source: source,
            target: target,
            images: [target.Generator(symbol: 0)],
            functor: out var functor,
            obstruction: out var obstruction
        )) {
            return $"equal GF(3) materials refused at rule {obstruction.RuleIndex} and pair ({obstruction.LeftKey},{obstruction.RightKey})";
        }

        var zero = source.Zero;
        var one = source.Identity;
        var generator = source.Generator(symbol: 0);
        var sum = source.Add(left: one, right: generator);
        var product = source.Multiply(left: sum, right: generator);

        if (!target.AreEqual(left: functor!.Map(value: zero), right: target.Zero)) {
            return "the admitted map did not preserve zero";
        }

        if (!target.AreEqual(left: functor.Map(value: one), right: target.Identity)) {
            return "the admitted map did not preserve one";
        }

        if (!target.AreEqual(
            left: functor.Map(value: source.Add(left: sum, right: generator)),
            right: target.Add(left: functor.Map(value: sum), right: functor.Map(value: generator))
        )) {
            return "the admitted map did not preserve addition";
        }

        foreach (var scalar in new ulong[] { 0UL, 1UL, 2UL }) {
            if (!target.AreEqual(
                left: functor.Map(value: Scale(algebra: source, scalar: scalar, value: sum)),
                right: Scale(algebra: target, value: functor.Map(value: sum), scalar: scalar)
            )) {
                return $"the admitted map did not preserve scalar multiplication at {scalar}";
            }
        }

        if (!target.AreEqual(
            left: functor.Map(value: product),
            right: target.Multiply(left: functor.Map(value: sum), right: functor.Map(value: generator))
        )) {
            return "the admitted map did not preserve product";
        }

        return null;
    }
    internal static string? PresentationOwnsAdmittedMemory() {
        int[] inputs = [0];
        int[] outputs = [0];
        BigInteger[] reassociationCharges = [BigInteger.One];
        int[] pattern = [0, 0];
        int[] replacement = [1, 0];
        BigInteger[] charges = [BigInteger.One];
        BigInteger[] generatorCharges = [BigInteger.One];
        Generator[] generators = [new(degree: 1, inputs: inputs, outputs: outputs, symbol: 0)];
        RewriteRule<BigInteger>[] rules = [
            new(
                kind: RuleKind.Reassociate,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: ReadOnlyMemory<int>.Empty,
                charges: reassociationCharges
            ),
            new(charges: charges, kind: RuleKind.Reduce, pattern: pattern, replacement: replacement),
        ];
        var presentation = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: generators,
            rules: rules,
            material: default,
            generatorCharges: generatorCharges
        );
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
        var compiled = algebra.Compile();
        var normalWord = presentation.NormalFormWord(key: 1L).ToArray();

        inputs[0] = 41;
        outputs[0] = 42;
        reassociationCharges[0] = 3;
        pattern[0] = 43;
        pattern[1] = 44;
        replacement[0] = 0;
        replacement[1] = 45;
        charges[0] = 5;
        generatorCharges[0] = 7;
        generators[0] = default;
        rules[0] = default;
        rules[1] = default;

        var generator = algebra.Generator(symbol: 0);
        var compiledProduct = algebra.Multiply(left: generator, right: generator);

        if ((BigInteger.One != generator.Coefficients[0])
            || !algebra.AreEqual(left: compiledProduct, right: generator)
            || (1 != compiled.TargetCount(leftKey: 1L, rightKey: 1L))
            || (1L != compiled.Target(leftKey: 1L, rightKey: 1L))
            || (BigInteger.One != compiled.Charge(leftKey: 1L, rightKey: 1L))) {
            return "the compiled product changed after caller-owned presentation arrays were mutated";
        }

        if (!algebra.TryNormalize(
            term: Term.Node(symbol: 0, children: [Term.Leaf(symbol: 0)]),
            stepLimit: 16L,
            normalForm: out var interpreted,
            obstruction: out _
        ) || !algebra.AreEqual(left: interpreted, right: generator)) {
            return "the interpreted normalizer changed after caller-owned rule arrays were mutated";
        }

        if (!presentation.NormalFormWord(key: 1L).SequenceEqual(other: normalWord)) {
            return "a normal-form word changed after caller-owned presentation arrays were mutated";
        }

        var target = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);

        if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            source: algebra,
            target: target,
            images: [target.Generator(symbol: 0)],
            functor: out var functor,
            obstruction: out var obstruction
        ) || !target.AreEqual(left: functor!.Map(value: compiledProduct), right: target.Generator(symbol: 0))) {
            return $"functor admission changed after mutation at rule {obstruction.RuleIndex}";
        }

        var failures = 0;

        Parallel.For(fromInclusive: 0, toExclusive: 32, body: _ => {
            var reader = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
            var value = reader.Generator(symbol: 0);

            if (!reader.AreEqual(left: reader.Multiply(left: value, right: value), right: value)) {
                Interlocked.Increment(location: ref failures);
            }
        });

        if (0 != failures) {
            return $"{failures} concurrent presentation reader(s) observed mutated admission arrays";
        }

        // The boundary lists are not otherwise part of this monoid's public semantics, so inspect the admitted value
        // directly to keep their independent deep-copy obligation executable.
        var field = typeof(ChargedPresentation<BigInteger, IntegerMaterial>).GetField(
            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic,
            name: "m_generators"
        );

        if ((field?.GetValue(obj: presentation) is not Generator[] admitted)
            || !admitted[0].Inputs.SequenceEqual(other: new int[] { 0 })
            || !admitted[0].Outputs.SequenceEqual(other: new int[] { 0 })) {
            return "the admitted generator boundaries still alias caller-owned memory";
        }

        return null;
    }
    internal static string? ForeignElementsAreRejectedUniformly() {
        var first = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(letterCount: 1, material: default, windowDegree: 2)
        );
        var second = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(letterCount: 1, material: default, windowDegree: 2)
        );
        var foreign = second.Generator(symbol: 0);
        var keys = new long[first.MaximumSupportCount];
        var coefficients = new BigInteger[first.MaximumSupportCount];
        var checks = new List<(string Name, string Parameter, Action Invoke)> {
            ("PresentedAlgebra.Add(left)", "left", () => _ = first.Add(left: foreign, right: first.Zero)),
            ("PresentedAlgebra.Add(right)", "right", () => _ = first.Add(left: first.Zero, right: foreign)),
            ("PresentedAlgebra.AreEqual", "left", () => _ = first.AreEqual(left: foreign, right: first.Zero)),
            ("PresentedAlgebra.AreEqual(right)", "right", () => _ = first.AreEqual(left: first.Zero, right: foreign)),
            ("PresentedAlgebra.Multiply", "left", () => _ = first.Multiply(left: foreign, right: first.Identity)),
            ("PresentedAlgebra.Multiply(right)", "right", () => _ = first.Multiply(left: first.Identity, right: foreign)),
            ("PresentedAlgebra.MultiplyInto", "left", () => _ = first.MultiplyInto(left: foreign, right: first.Identity, keys: keys, coefficients: coefficients)),
            ("PresentedAlgebra.MultiplyInto(right)", "right", () => _ = first.MultiplyInto(left: first.Identity, right: foreign, keys: keys, coefficients: coefficients)),
            ("PresentedAlgebra.Negate", "value", () => _ = first.Negate(value: foreign)),
            ("PresentedAlgebra.Power at exponent zero", "value", () => _ = first.Power(exponent: 0UL, value: foreign)),
            ("PresentedAlgebra.PowerSequential at exponent zero", "value", () => _ = first.PowerSequential(exponent: 0UL, value: foreign)),
            ("PresentedAlgebra.Subtract(left)", "left", () => _ = first.Subtract(left: foreign, right: first.Zero)),
            ("PresentedAlgebra.Subtract", "right", () => _ = first.Subtract(left: first.Zero, right: foreign)),
            ("PresentedAlgebra.TruncatedSum at bound zero", "value", () => _ = first.TruncatedSum(bound: 0, value: foreign)),
            ("PresentedAlgebra.TrySumOverAllLengths", "value", () => _ = first.TrySumOverAllLengths(obstruction: out _, total: out _, value: foreign)),
            ("PresentedAlgebra.Residual", "value", () => _ = first.Residual(symbol: 0, value: foreign, twist: ResidualTwist.Counit)),
            ("PresentedAlgebra.TryCompileClosure", "seed", () => _ = first.TryCompileClosure(closure: out _, obstruction: out _, seed: foreign, shiftSymbol: -1, stateLimit: 2, twist: ResidualTwist.Counit)),
            ("PresentedAlgebra.Pair(covector)", "covector", () => _ = first.Pair(covector: foreign, value: first.Identity)),
            ("PresentedAlgebra.Pair", "value", () => _ = first.Pair(covector: first.Identity, value: foreign)),
            ("PresentedAlgebra.Behavior", "initial", () => _ = first.Behavior(initial: foreign, value: first.Identity, readout: first.Identity)),
            ("PresentedAlgebra.Behavior(value)", "value", () => _ = first.Behavior(initial: first.Identity, value: foreign, readout: first.Identity)),
            ("PresentedAlgebra.Behavior(readout)", "readout", () => _ = first.Behavior(initial: first.Identity, value: first.Identity, readout: foreign)),
            ("PresentedAlgebra.Trace", "value", () => _ = first.Trace(value: foreign)),
        };

        var fieldMaterial = PrimeFieldMaterial.Create(modulus: 5);
        var fieldFirst = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: fieldMaterial, windowDegree: 1)
        );
        var fieldSecond = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: fieldMaterial, windowDegree: 1)
        );
        var fieldForeign = fieldSecond.Generator(symbol: 0);

        checks.Add(item: ("PresentedAlgebra.TrySolve(divisor)", "divisor", () => _ = fieldFirst.TrySolve(divisor: fieldForeign, target: fieldFirst.Identity, quotient: out _, obstruction: out _)));
        checks.Add(item: ("PresentedAlgebra.TrySolve(target)", "target", () => _ = fieldFirst.TrySolve(divisor: fieldFirst.Identity, target: fieldForeign, quotient: out _, obstruction: out _)));
        checks.Add(item: ("PresentedAlgebra.TryResolvent", "value", () => _ = fieldFirst.TryResolvent(obstruction: out _, resolvent: out _, value: fieldForeign)));
        checks.Add(item: ("GraphZeta.TryCreate", "value", () => _ = GraphZeta<ulong, PrimeFieldMaterial>.TryCreate(algebra: fieldFirst, degreeBound: 1, obstruction: out _, order: 1, value: fieldForeign, zeta: out _)));

        var transferFirst = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
        var transferSecond = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
        var transferForeign = transferSecond.Digit(partialQuotient: 7);

        checks.Add(item: ("ConvergentTransfer.Entry", "value", () => _ = transferFirst.Entry(column: 0, row: 0, value: transferForeign)));

        var patternFirst = TokenPattern<bool, BooleanMaterial>.Create(letterCount: 1, material: default, window: 2);
        var patternSecond = TokenPattern<bool, BooleanMaterial>.Create(letterCount: 1, material: default, window: 2);
        var patternForeign = patternSecond.Predicate(letters: 1UL);

        checks.Add(item: ("TokenPattern.Concatenate", "left", () => _ = patternFirst.Concatenate(left: patternForeign, right: patternFirst.EmptyWord)));
        checks.Add(item: ("TokenPattern.Concatenate(right)", "right", () => _ = patternFirst.Concatenate(left: patternFirst.EmptyWord, right: patternForeign)));
        checks.Add(item: ("TokenPattern.Derivative", "value", () => _ = patternFirst.Derivative(letter: 0, value: patternForeign)));
        checks.Add(item: ("TokenPattern.Intersect(left)", "left", () => _ = patternFirst.Intersect(left: patternForeign, right: patternFirst.EmptyWord)));
        checks.Add(item: ("TokenPattern.Intersect(right)", "right", () => _ = patternFirst.Intersect(left: patternFirst.EmptyWord, right: patternForeign)));
        checks.Add(item: ("TokenPattern.Scale", "value", () => _ = patternFirst.Scale(value: patternForeign, weight: true)));
        checks.Add(item: ("TokenPattern.TryIterate", "value", () => _ = patternFirst.TryIterate(iterated: out _, obstruction: out _, value: patternForeign)));
        checks.Add(item: ("TokenPattern.TryWeigh", "value", () => _ = patternFirst.TryWeigh(letters: [0], value: patternForeign, weight: out _)));
        checks.Add(item: ("TokenPattern.Union", "left", () => _ = patternFirst.Union(left: patternForeign, right: patternFirst.EmptyWord)));
        checks.Add(item: ("TokenPattern.Union(right)", "right", () => _ = patternFirst.Union(left: patternFirst.EmptyWord, right: patternForeign)));
        checks.Add(item: ("PatternComplement.Complement", "value", () => _ = patternFirst.Complement(value: patternForeign)));
        checks.Add(item: ("PatternMatcher.TryCompile", "value", () => _ = PatternMatcher<bool, BooleanMaterial>.TryCompile(matcher: out _, obstruction: out _, pattern: patternFirst, stateLimit: 2, value: patternForeign)));

        var cliffordFirst = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(degenerateCount: 0, material: default, negativeCount: 0, positiveCount: 1)
        );
        var cliffordSecond = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(degenerateCount: 0, material: default, negativeCount: 0, positiveCount: 1)
        );
        var bladeForeign = cliffordSecond.Generator(symbol: 0);
        var complement = GradedComplement<BigInteger, IntegerMaterial>.Create(algebra: cliffordFirst);

        checks.Add(item: ("GradedComplement.LeftComplement", "value", () => _ = complement.LeftComplement(value: bladeForeign)));
        checks.Add(item: ("GradedComplement.OuterProduct(left)", "left", () => _ = complement.OuterProduct(left: bladeForeign, right: cliffordFirst.Identity)));
        checks.Add(item: ("GradedComplement.OuterProduct(right)", "right", () => _ = complement.OuterProduct(left: cliffordFirst.Identity, right: bladeForeign)));
        checks.Add(item: ("GradedComplement.RegressiveProduct", "left", () => _ = complement.RegressiveProduct(left: bladeForeign, right: cliffordFirst.Identity)));
        checks.Add(item: ("GradedComplement.RegressiveProduct(right)", "right", () => _ = complement.RegressiveProduct(left: cliffordFirst.Identity, right: bladeForeign)));
        checks.Add(item: ("GradedComplement.RightComplement", "value", () => _ = complement.RightComplement(value: bladeForeign)));

        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(algebra: cliffordFirst, group: out var group, obstruction: out var groupObstruction)) {
            return $"the ownership matrix's group fixture did not certify: {groupObstruction.Outcome}";
        }

        checks.Add(item: ("PresentedGroup.TryInvert", "value", () => _ = group!.TryInvert(inverse: out _, obstruction: out _, value: bladeForeign)));

        var functorTarget = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: first.Presentation);

        if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            source: first,
            target: functorTarget,
            images: [functorTarget.Generator(symbol: 0)],
            functor: out var functor,
            obstruction: out _
        )) {
            return "the ownership matrix's functor fixture did not admit";
        }

        checks.Add(item: ("PresentedFunctor.Map", "value", () => _ = functor!.Map(value: foreign)));
        checks.Add(item: ("PresentedFunctor.TryCreate(images)", "images", () => _ = PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(functor: out _, images: [foreign], obstruction: out _, source: first, target: functorTarget)));
        checks.Add(item: ("PresentedMachine.Create(initial)", "initial", () => _ = PresentedMachine<BigInteger, IntegerMaterial>.Create(algebra: first, initial: foreign, steps: [first.Identity], readout: first.Identity)));
        checks.Add(item: ("PresentedMachine.Create(steps)", "steps", () => _ = PresentedMachine<BigInteger, IntegerMaterial>.Create(algebra: first, initial: first.Identity, steps: [foreign], readout: first.Identity)));
        checks.Add(item: ("PresentedMachine.Create(readout)", "readout", () => _ = PresentedMachine<BigInteger, IntegerMaterial>.Create(algebra: first, initial: first.Identity, steps: [first.Identity], readout: foreign)));

        int[] dimensions = [0];
        var calculusFirst = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(dimensions: dimensions, incidences: [], material: default);
        var calculusSecond = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(dimensions: dimensions, incidences: [], material: default);
        var chainForeign = calculusSecond.Chain(values: [BigInteger.One]);
        var cochainForeign = calculusSecond.Cochain(values: [BigInteger.One]);

        checks.Add(item: ("ExteriorCalculus.Boundary", "chain", () => _ = calculusFirst.Boundary(chain: chainForeign)));
        checks.Add(item: ("ExteriorCalculus.Coboundary", "cochain", () => _ = calculusFirst.Coboundary(cochain: cochainForeign)));
        checks.Add(item: ("ExteriorCalculus.Pair", "cochain", () => _ = calculusFirst.Pair(cochain: cochainForeign, chain: calculusFirst.Chain(values: [BigInteger.One]))));
        checks.Add(item: ("ExteriorCalculus.Pair(chain)", "chain", () => _ = calculusFirst.Pair(cochain: calculusFirst.Cochain(values: [BigInteger.One]), chain: chainForeign)));

        foreach (var check in checks) {
            var actual = RefusedParameter(action: check.Invoke);

            if (actual != check.Parameter) {
                return $"{check.Name} did not reject its foreign element by naming '{check.Parameter}' (observed '{(actual ?? "no refusal")}')";
            }
        }

        var tropicalTransfer = ConvergentTransfer<FixedQ4816, TropicalMaterial>.Create(material: default);

        if (!first.AreEqual(left: first.Add(left: default, right: default), right: first.Zero)
            || (BigInteger.Zero != transferFirst.Entry(column: 0, row: 0, value: default))
            || (tropicalTransfer.Algebra.Presentation.Material.Zero != tropicalTransfer.Entry(column: 0, row: 0, value: default))
            || (0 != patternFirst.Intersect(left: default, right: default).SupportCount)
            || !patternFirst.Algebra.AreEqual(left: patternFirst.Complement(value: default), right: patternFirst.Complement(value: patternFirst.Algebra.Zero))
            || group!.TryInvert(inverse: out _, obstruction: out _, value: default)
            || !functorTarget.AreEqual(left: functor!.Map(value: default), right: functorTarget.Zero)) {
            return "the default element was not preserved as the universal zero";
        }

        if (PublicElementConsumerInventory() is { } inventory) { return inventory; }
        if (TensorPairingOwnership() is { } tensor) { return tensor; }

        return null;
    }

    private static string? PublicElementConsumerInventory() {
        string[] expected = [
            "ConvergentTransfer`2.Entry:value",
            "ExteriorCalculus`2.Boundary:chain",
            "ExteriorCalculus`2.Coboundary:cochain",
            "ExteriorCalculus`2.Pair:cochain,chain",
            "GradedComplement`2.LeftComplement:value",
            "GradedComplement`2.OuterProduct:left,right",
            "GradedComplement`2.RegressiveProduct:left,right",
            "GradedComplement`2.RightComplement:value",
            "GraphZeta`2.TryCreate:value",
            "PatternComplement.Complement:value",
            "PatternMatcher`2.TryCompile:value",
            "PresentedAlgebra`2.Add:left,right",
            "PresentedAlgebra`2.AreEqual:left,right",
            "PresentedAlgebra`2.Behavior:initial,value,readout",
            "PresentedAlgebra`2.Multiply:left,right",
            "PresentedAlgebra`2.MultiplyInto:left,right",
            "PresentedAlgebra`2.Negate:value",
            "PresentedAlgebra`2.Pair:covector,value",
            "PresentedAlgebra`2.PairUp:left,right",
            "PresentedAlgebra`2.Power:value",
            "PresentedAlgebra`2.PowerSequential:value",
            "PresentedAlgebra`2.Residual:value",
            "PresentedAlgebra`2.Subtract:left,right",
            "PresentedAlgebra`2.Trace:value",
            "PresentedAlgebra`2.TruncatedSum:value",
            "PresentedAlgebra`2.TryCompileClosure:seed",
            "PresentedAlgebra`2.TryResolvent:value",
            "PresentedAlgebra`2.TrySolve:divisor,target",
            "PresentedAlgebra`2.TrySumOverAllLengths:value",
            "PresentedFunctor`2.Map:value",
            "PresentedFunctor`2.TryCreate:images",
            "PresentedGroup`2.TryInvert:value",
            "PresentedMachine`2.Create:initial,steps,readout",
            "TokenPattern`2.Concatenate:left,right",
            "TokenPattern`2.Derivative:value",
            "TokenPattern`2.Intersect:left,right",
            "TokenPattern`2.Scale:value",
            "TokenPattern`2.TryIterate:value",
            "TokenPattern`2.TryWeigh:value",
            "TokenPattern`2.Union:left,right",
        ];
        var actual = typeof(PresentedAlgebra<,>).Assembly
            .GetTypes()
            .Where(predicate: static type => (type.IsPublic && (type.Namespace == typeof(PresentedAlgebra<,>).Namespace)))
            .SelectMany(selector: static type => type.GetMethods(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(second: type.GetConstructors(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(selector: method => (
                    Method: method,
                    Parameters: method.GetParameters().Where(predicate: static parameter => (!parameter.IsOut && ContainsElement(type: parameter.ParameterType))).ToArray()
                ))
                .Where(predicate: static entry => (0 != entry.Parameters.Length))
                .Select(selector: entry => $"{type.Name}.{entry.Method.Name}:{string.Join(separator: ',', values: entry.Parameters.Select(selector: static parameter => parameter.Name))}"))
            .Distinct()
            .Order()
            .ToArray();

        return (actual.SequenceEqual(second: expected.Order().ToArray())
            ? null
            : $"the public Element-consumer inventory changed: expected [{string.Join(separator: "; ", values: expected.Order())}], actual [{string.Join(separator: "; ", values: actual)}]");
    }
    private static string? TensorPairingOwnership() {
        var material = PrimeFieldMaterial.Create(modulus: 5);
        var left = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: material, windowDegree: 1)
        );
        var right = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 1, material: material, windowDegree: 1)
        );
        var tensor = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.Tensor(left: left.Presentation, right: right.Presentation)
        );
        var paired = tensor.PairUp(left: left.Generator(symbol: 0), right: right.Generator(symbol: 0), rightKeyCount: right.Presentation.NormalFormCount);

        if ((1 != paired.SupportCount) || (3L != paired.Keys[0]) || (1UL != paired.Coefficients[0])) {
            return "the documented cross-algebra tensor pairing did not embed the two owned factor coordinates";
        }

        if (0 != tensor.PairUp(left: default, right: default, rightKeyCount: right.Presentation.NormalFormCount).SupportCount) {
            return "tensor pairing did not admit default elements as universal zeros";
        }

        var wrongShape = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(letterCount: 2, material: material, windowDegree: 1)
        );

        if (RefusedParameter(
            action: () => _ = tensor.PairUp(left: wrongShape.Identity, right: right.Identity, rightKeyCount: right.Presentation.NormalFormCount)
        ) is not "left") {
            return "tensor pairing accepted a left factor with the wrong owned coordinate width";
        }

        if (RefusedParameter(
            action: () => _ = tensor.PairUp(left: left.Identity, right: wrongShape.Identity, rightKeyCount: right.Presentation.NormalFormCount)
        ) is not "right") {
            return "tensor pairing accepted a right factor with the wrong owned coordinate width";
        }

        var wrongMaterial = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: Presentations.FreeMonoid<ulong, PrimeFieldMaterial>(
                letterCount: 1,
                material: PrimeFieldMaterial.Create(modulus: 7),
                windowDegree: 1
            )
        );

        if (RefusedParameter(
            action: () => _ = tensor.PairUp(left: left.Identity, right: wrongMaterial.Identity, rightKeyCount: right.Presentation.NormalFormCount)
        ) is not "right") {
            return "tensor pairing accepted a right factor carrying a different material value";
        }

        if (RefusedParameter(
            action: () => _ = tensor.PairUp(left: wrongMaterial.Identity, right: right.Identity, rightKeyCount: right.Presentation.NormalFormCount)
        ) is not "left") {
            return "tensor pairing accepted a left factor carrying a different material value";
        }

        return null;
    }
    private static bool ContainsElement(Type type) {
        if (type.IsByRef || type.IsArray || type.IsPointer) {
            return ContainsElement(type: type.GetElementType()!);
        }

        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(PresentedAlgebra<,>.Element))) {
            return true;
        }

        return (type.IsGenericType && type.GetGenericArguments().Any(predicate: ContainsElement));
    }
    private static string? RefusedParameter(Action action) {
        try {
            action();

            return null;
        } catch (ArgumentException exception) {
            return exception.ParamName;
        }
    }
    private static PresentedAlgebra<ulong, PrimeFieldMaterial>.Element Scale(
        PresentedAlgebra<ulong, PrimeFieldMaterial> algebra,
        in PresentedAlgebra<ulong, PrimeFieldMaterial>.Element value,
        ulong scalar
    ) {
        var scaled = new ulong[value.SupportCount];

        for (var index = 0; (index < scaled.Length); ++index) {
            scaled[index] = algebra.Presentation.Material.Multiply(left: scalar, right: value.Coefficients[index]);
        }

        return algebra.FromSupport(keys: value.Keys, coefficients: scaled);
    }
}
