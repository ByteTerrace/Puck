using System.Reflection;

namespace Puck.Maths.Tests;

/// <summary>A reference from a law case to a public member it exercises, by declaring type and member name. Resolved
/// against <see cref="MemberSurface"/> at run time, so the covered ids always match the reflection ids exactly — the
/// manifest is built mechanically, never by hand-transcribed id strings.</summary>
/// <param name="Type">The declaring type (open or closed generic; normalized on resolution).</param>
/// <param name="Name">The member name (for example <c>op_Multiply</c>, <c>Norm</c>, <c>MobiusStep</c>).</param>
internal readonly record struct CoverRef(Type Type, string Name);
/// <summary>
/// The reflected public surface of Puck.Maths: every public member of every public (or public-nested) type, each with a
/// stable id. Record/object boilerplate and property accessors are excluded so the manifest tracks real API. The id
/// formatter is the single source of member identity — both the manifest and the <see cref="CoverRef"/> resolver use
/// it, so covered ids can never drift from surface ids.
/// </summary>
internal static class MemberSurface {
    private static readonly HashSet<string> ExcludedNames = [
        "Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers", "<Clone>$",
        "Finalize", "GetType", "ReferenceEquals", "MemberwiseClone",
        "op_Equality", "op_Inequality", "get_EqualityContract",
    ];

    /// <summary>Gets every enumerated public member, ordered by id.</summary>
    public static IReadOnlyList<Record> All { get; } = Enumerate();

    /// <summary>Resolves a <see cref="CoverRef"/> to the ids of the matching members (all overloads of the name).</summary>
    /// <param name="reference">The reference to resolve.</param>
    /// <returns>The matching member ids.</returns>
    public static IEnumerable<string> Resolve(CoverRef reference) {
        var normalized = Normalize(type: reference.Type);

        return All
            .Where(predicate: record => ((record.DeclaringType == normalized) && (record.Name == reference.Name)))
            .Select(selector: record => record.Id);
    }

    /// <summary>Gets the set of all current member ids.</summary>
    public static IReadOnlySet<string> Ids { get; } = All.Select(selector: record => record.Id).ToHashSet();

    private static Type Normalize(Type type) =>
        (type.IsGenericType ? type.GetGenericTypeDefinition() : type);
    private static IReadOnlyList<Record> Enumerate() {
        var assembly = typeof(FixedQ4816).Assembly;
        var records = new List<Record>();

        foreach (var type in assembly.GetTypes()) {
            if (!type.IsVisible || type.Name.Contains(value: '<')) {
                continue;
            }

            foreach (var member in type.GetMembers(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if ((member is Type) || (member is EventInfo) || ExcludedNames.Contains(item: member.Name) || member.Name.Contains(value: '<')) {
                    continue;
                }

                if ((member is MethodInfo method) && IsAccessor(name: method.Name)) {
                    continue;
                }

                records.Add(item: new Record(Id: Format(declaringType: type, member: member), DeclaringType: type, Name: member.Name));
            }
        }

        return [.. records
            .GroupBy(keySelector: record => record.Id)
            .Select(selector: group => group.First())
            .OrderBy(keySelector: record => record.Id, comparer: StringComparer.Ordinal)];
    }
    private static bool IsAccessor(string name) =>
        (name.StartsWith(comparisonType: StringComparison.Ordinal, value: "get_") ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "set_") ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "add_") ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "remove_"));
    private static string Format(Type declaringType, MemberInfo member) {
        var typeName = TypeName(type: declaringType);

        if (member is MethodBase methodBase) {
            return $"{typeName}.{methodBase.Name}({ParameterList(parameters: methodBase.GetParameters())})";
        }

        // An indexer is a parameterized property; carry its index parameters so overloaded indexers stay distinct, the
        // same discipline the method ids follow. A plain property renders as its bare name.
        if ((member is PropertyInfo property) && (property.GetIndexParameters().Length > 0)) {
            return $"{typeName}.{property.Name}({ParameterList(parameters: property.GetIndexParameters())})";
        }

        return $"{typeName}.{member.Name}";
    }
    private static string ParameterList(ParameterInfo[] parameters) =>
        string.Join(separator: ",", values: parameters.Select(selector: parameter => TypeName(type: parameter.ParameterType)));
    private static string TypeName(Type type) {
        if (type.IsByRef) {
            return (TypeName(type: type.GetElementType()!) + "&");
        }

        if (type.IsArray) {
            return (TypeName(type: type.GetElementType()!) + "[]");
        }

        if (type.IsPointer) {
            return (TypeName(type: type.GetElementType()!) + "*");
        }

        if (type.IsGenericParameter) {
            return type.Name;
        }

        if (type.IsGenericType) {
            return GenericTypeName(type: type, arguments: type.GetGenericArguments());
        }

        return (type.FullName ?? type.Name).Replace(newChar: '.', oldChar: '+');
    }
    // Renders a generic type, distributing the flattened argument list across the nesting chain so a type nested in a
    // generic keeps BOTH its own segment and its own arguments (QuadraticAlgebra<TScalar>.Element, never the collapsed
    // outer name). The reflection argument list is ordered outer-to-inner, so each level takes the arguments its
    // declaring type does not, and the remainder are its own.
    private static string GenericTypeName(Type type, Type[] arguments) {
        var declaring = type.DeclaringType;
        var inherited = (((declaring is not null) && declaring.IsGenericType) ? declaring.GetGenericArguments().Length : 0);
        var ownArguments = arguments[inherited..];
        var simpleName = StripArity(name: type.Name);
        var rendered = ((ownArguments.Length == 0)
            ? simpleName
            : $"{simpleName}<{string.Join(separator: ",", values: ownArguments.Select(selector: TypeName))}>");

        if (declaring is null) {
            return ((type.Namespace is { Length: > 0 } space) ? $"{space}.{rendered}" : rendered);
        }

        var declaringName = (declaring.IsGenericType
            ? GenericTypeName(type: declaring, arguments: arguments[..inherited])
            : TypeName(type: declaring));

        return $"{declaringName}.{rendered}";
    }
    private static string StripArity(string name) {
        var tick = name.IndexOf(value: '`');

        return ((tick < 0) ? name : name[..tick]).Replace(newChar: '.', oldChar: '+');
    }

    /// <summary>One enumerated public member.</summary>
    /// <param name="Id">The stable member id.</param>
    /// <param name="DeclaringType">The declaring type (normalized to the generic definition when generic).</param>
    /// <param name="Name">The member name.</param>
    public sealed record Record(string Id, Type DeclaringType, string Name);
}
/// <summary>One member's coverage state in the committed manifest.</summary>
internal sealed class ManifestEntry {
    /// <summary>Gets or sets the member id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the state: <c>covered</c>, <c>waived</c>, or <c>uncovered</c>.</summary>
    public string State { get; set; } = "uncovered";
    /// <summary>Gets or sets the law/fact ids covering this member (present only when <see cref="State"/> is covered).</summary>
    public List<string>? CoveredBy { get; set; }
    /// <summary>Gets or sets the mandatory waiver reason (present only when <see cref="State"/> is waived).</summary>
    public string? Reason { get; set; }
}
/// <summary>The committed coverage manifest.</summary>
internal sealed class Manifest {
    /// <summary>Gets the per-member states, ordered by id.</summary>
    public List<ManifestEntry> Members { get; init; } = [];
}
/// <summary>
/// Module 4 — the coverage ratchet. Enumerates the public Puck.Maths surface, derives the covered set mechanically from
/// the <see cref="LawRegistry"/> member declarations, and reconciles against the committed manifest. The gate fails iff
/// a public member is classified nowhere — neither by the committed manifest nor by a declaration (new API appeared
/// silently) — or a member moved covered→uncovered. It never fails on the large initial uncovered backlog: coverage
/// only grows. Classification is explicit — a law case covering the member, or a waiver naming it — and
/// <see cref="Generate"/> never invents one, so an unclassified member keeps failing the gate on every run.
/// </summary>
internal static class Coverage {
    /// <summary>The waived members: intentionally outside the algebra law suite, each with a mandatory reason. Declared
    /// by reference (type + name) so the ids resolve mechanically.</summary>
    private static readonly (CoverRef Reference, string Reason)[] WaiverDeclarations = [
        // Three FixedPoint presentation seams are deliberately NOT waived here — FixedComplex.ToComplex,
        // FixedQuaternion.ToQuaternion and FixedQ4816's explicit conversion to double. "Presentation-only; the algebra
        // laws pin the exact raw contract instead" is false about the thing each of them decides alone: the lane ORDER
        // for the two conversions, and the rounding of the narrowing for the third. Swapping two lanes in either
        // conversion leaves the rest of the suite green. They answer at complex.presentation-seam,
        // quaternion.presentation-ladder and scalar.double-projection-vs-oracle.

        // UnsignedNumberFunctions.JacobiSymbol is likewise not waived. "No operand domain, subject shape, or oracle in
        // this suite" is false about it three times over: Oracles.JacobiSymbolReciprocity is the oracle, the ulong
        // extension is already reached by PrimeField64.IsStrongLucasProbablePrime's Selfridge search
        // (PrimeField64.cs:215), and the candidate stream of prime-field.lucas-vs-companion-matrix is the operand
        // domain. Inverting the symbol's returned sign reddens that law. It answers at
        // prime-field.lucas-vs-companion-matrix, whose ENVELOPE names the class the law does NOT reach: a defect that
        // suppresses the −1 outcome makes the unbounded search spin rather than redden. The cross-carrier and
        // composite-modulus statements are core.jacobi-symbol-cross-carrier and
        // scalar.jacobi-symbol-fixed-width-vs-exact-descent.

        // The three PrimeField64 probable-prime predicates — IsStrongProbablePrime, IsStrongLucasProbablePrime and
        // IsBaillieProbablePrime — are laws, not waivers. "Not an algebra operation" is false about every one of them:
        // each is a deterministic total function of ulongs with an operand domain, a subject shape and an oracle. What
        // once blocked a law was the absence of an integer wing in this project, and that wing now exists — so the
        // waiver reasons that leaned on it are gone. Nor does membership in the Baillie-PSW conjunction stand in for evidence
        // about the base-two round: the composition cannot see a round that wrongly accepts a composite the Lucas half
        // rejects, and the exhaustive 32-bit sweep gates the COMPOSITION rather than the Lucas half on its own.
        // prime-field.strong-round-vs-oracle, prime-field.lucas-vs-companion-matrix, prime-field.baillie-composition
        // and prime-field.pseudoprime-populations are where they answer, and prime-field.baillie-psw-exhaustive is the
        // gate of record for SCALE; the last of the four pins probable-ness as a FACT, enumerating the published
        // pseudoprimes each test is REQUIRED to accept.

        // ---- the sampler's bijection primitive ----
        // A word permutation with no operand domain, no subject shape and no oracle in this suite: its statement is
        // that the map is invertible, which Post's digital-net stage round-trips directly.

        // ---- the two integer lattices ----
        // Node arithmetic on a fixed combinatorial lattice, not fixed-point algebra. What is waived here is the part of
        // each type this suite never drives; the seven members the two reflection cases DO drive as a subject or as a
        // named oracle — SymmetryLattice.AreOrthogonal, Cycle, RayCycleFactors, RayCycleOrder and Reflect, and
        // CyclicRotation.Period and Step — are credited to those cases in LawRegistry.cs instead.
        // CyclicRotation.PlaneCount is likewise not waived: "a declared plane COUNT, not an operation" is no reason
        // when the count is checkable, and scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy checks it
        // against the E8 Coxeter-plane decomposition — the reduced residue system of Period splits into exactly
        // PlaneCount conjugate pairs, independently re-derived by a from-scratch Euclidean gcd, so a PlaneCount of
        // three or five actually reddens the case. The members listed below are gated by NOTHING; the reason they
        // carry says so and names the two laws owed. Three of them — Ring, RingCount's sibling RingSize, and RayCount
        // — are additionally named by integer.symmetry-lattice-exact-structure, so their entries are inert rather than
        // load-bearing; they are left in place because the waiver register is a declaration of intent and thinning it
        // is a separate review.
        (new CoverRef(Name: "Antipode", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "CanonicalRay", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "Dimension", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "NodeCount", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "Project", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "RayCount", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "Ring", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "RingCount", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),
        (new CoverRef(Name: "RingSize", Type: typeof(SymmetryLattice)), SymmetryLatticeReason),

        // ---- the presented charged algebra ----
        // One CATEGORY reason per kind, repeated verbatim; an individual reason appears only where the category does
        // not honestly fit. Nothing here is arithmetic: every member that computes a value answers to a law.
        (new CoverRef(Name: "value__", Type: typeof(ChargeLane)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(ClosureCertificate)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(ClosureOutcome)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(ResidualTwist)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(RuleKind)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(DiscreteMeasureCompilationFailure)), EnumStorageReason),
        (new CoverRef(Name: "value__", Type: typeof(SecondOrderDynamicsBranch)), EnumStorageReason),
        (new CoverRef(Name: "LocallyFinite", Type: typeof(ClosureCertificate)), UnreachedCertificateReason),

        // ---- the continued-fraction lenses ----
        // Nothing is waived here: every member of this group answers to a law.
        // CertifiedLowDiscrepancy.Point and .DiscrepancyBound are the approximate readout beneath each lens — a
        // measurement rather than an identity — and the measurement is a law at
        // sampling.certified-low-discrepancy-measured-across-scales, which names both members and puts teeth on the
        // bound: it computes the EXACT star discrepancy in BigInteger at six certificates across five point counts and
        // requires it to fall under DiscrepancyBound at every one.
        //
        // QuadraticQuasicrystal.Chain.Inflation and .InflationFactor answer at
        // quasicrystal.chain-single-term-matches-metallic-and-new-periods, which pins both directly — the chain's cached
        // Inflation and InflationFactor are compared against a FRESH QuadraticInflation.FromQuadraticIrrational call built
        // from the same (p,q,d,r), so a wiring defect in either accessor (the wrong field assigned, a stale copy, an
        // omitted read) reddens the case even though the underlying eigenvalue arithmetic is pinned elsewhere.

        // ---- the scalar-field seam ----
        // A contract declaration with no implementation in this assembly: nothing here computes a distance or a
        // gradient, so there is no operand domain, subject shape or oracle to state a law against — a law would have to
        // supply an implementation, and it would then be testing that implementation rather than this declaration. The
        // behavior belongs to each provider and is gated where the provider lives: SdfFieldEvaluator answers in
        // tests/Puck.SignedDistance.Tests. FieldEvaluatorCapabilities is the one-flag carrier the contract hands back.
        // NOTHING is owed here; a law would become owed only if Puck.Maths ever ships a field implementation of its own.
        (new CoverRef(Name: "Capabilities", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: "LineOfSight", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: "Overlap", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: "Raycast", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: "SphereCast", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: "TryGroundHeight", Type: typeof(IWorldQuery)), FieldSeamReason),
        (new CoverRef(Name: ".ctor", Type: typeof(QueryCapabilities)), FieldSeamCarrierReason),
        (new CoverRef(Name: "HasBlocked", Type: typeof(QueryCapabilities)), FieldSeamCarrierReason),
        (new CoverRef(Name: "HasHeightfield", Type: typeof(QueryCapabilities)), FieldSeamCarrierReason),
        (new CoverRef(Name: "HasOccupancy", Type: typeof(QueryCapabilities)), FieldSeamCarrierReason),
        (new CoverRef(Name: ".ctor", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Confidence", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Distance", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Material", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Normal", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Point", Type: typeof(RayHit)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Bounded", Type: typeof(WorldQueryConfidence)), FieldSeamCarrierReason),
        (new CoverRef(Name: "Exact", Type: typeof(WorldQueryConfidence)), FieldSeamCarrierReason),
        (new CoverRef(Name: "value__", Type: typeof(WorldQueryConfidence)), EnumStorageReason),
        (new CoverRef(Name: "Capabilities", Type: typeof(IFieldEvaluator)), FieldSeamReason),
        (new CoverRef(Name: "TryDistance", Type: typeof(IFieldEvaluator)), FieldSeamReason),
        (new CoverRef(Name: "TryFieldGradient", Type: typeof(IFieldEvaluator)), FieldSeamReason),
        (new CoverRef(Name: ".ctor", Type: typeof(FieldEvaluatorCapabilities)), FieldSeamReason),
        (new CoverRef(Name: "WarpFree", Type: typeof(FieldEvaluatorCapabilities)), FieldSeamReason),

    ];

    // The shared category reasons. Each is written once and cited by every member of its category, which is what makes
    // the category reviewable as a category rather than as a pile of one-off prose.
    private const string FieldSeamCarrierReason = "A field- or query-seam carrier: it holds a value some provider computed and this assembly computes nothing to put in it, so THIS member has no operand domain, subject shape or oracle here. The producing arithmetic is gated where the provider lives. Owed only if Puck.Maths ever ships a field or query implementation of its own.";
    private const string FieldSeamReason = "A contract declaration with no implementation in this assembly: no member here computes a distance or a gradient, so THIS member carries no operand domain, subject shape or oracle — stating a law would mean supplying an implementation and then testing that implementation instead. Each provider's behavior is gated where the provider lives (SdfFieldEvaluator at tests/Puck.SignedDistance.Tests). Owed only if Puck.Maths ever ships a field implementation of its own.";
    private const string EnumStorageReason = "The compiler-generated enum storage field, not authored API.";
    private const string SymmetryLatticeReason = "Node arithmetic on a fixed reflection lattice, not fixed-point algebra: THIS member carries no operand domain, subject shape or oracle in this suite (AreOrthogonal, Cycle, RayCycleFactors, RayCycleOrder and Reflect do, and are covered by the two reflection cases rather than waived). NARROW, and deliberately not smoothed over: the no-oracle argument is the whole of it, and no gate anywhere stands over these members — nothing in or out of this suite checks the E8/Ising mass spectrum or the reflection-world group-order closure, so they are gated by nothing. OWED: an in-suite E8/Ising mass-spectrum law and a group-order closure law, either of which would promote most of this list out of the register.";
    private const string UnreachedCertificateReason = "Enumeration case with no producer: the guarded sum names the certificate it ATTEMPTED, and every attempt in the library is Nilpotent, Idempotent or FieldResolvent. A divisibility window is locally finite but reports the nilpotence it observed, so LocallyFinite is still issued nowhere, and None is the absence of any issued certificate rather than a certificate itself.";

    /// <summary>Builds the map from covered member id to the sorted law ids covering it, from the registry. Every case
    /// in <see cref="LawRegistry.All"/> is a law the runner executes, so coverage is only ever credited from a case
    /// that can run and assert.</summary>
    /// <returns>The covered map.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CoveredMembers() {
        var covered = new Dictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);

        foreach (var lawCase in LawRegistry.All) {
            foreach (var reference in lawCase.Members) {
                foreach (var id in MemberSurface.Resolve(reference: reference)) {
                    if (!covered.TryGetValue(key: id, value: out var laws)) {
                        laws = new SortedSet<string>(comparer: StringComparer.Ordinal);
                        covered[id] = laws;
                    }

                    _ = laws.Add(item: lawCase.Id);
                }
            }
        }

        return covered.ToDictionary(keySelector: pair => pair.Key, elementSelector: IReadOnlyList<string> (pair) => [.. pair.Value], comparer: StringComparer.Ordinal);
    }
    /// <summary>The waived member ids resolved from the declarations.</summary>
    /// <returns>The map from waived member id to reason.</returns>
    public static IReadOnlyDictionary<string, string> WaivedMembers() {
        var waived = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var (reference, reason) in WaiverDeclarations) {
            foreach (var id in MemberSurface.Resolve(reference: reference)) {
                waived[id] = reason;
            }
        }

        return waived;
    }
    /// <summary>Regenerates the manifest mechanically: upgrades to covered, applies waivers, keeps a previously-covered
    /// member covered (so a regression stays visible until fixed), and re-emits every other member with the state the
    /// committed manifest already gave it. A member the committed manifest does not mention is NOT written: an
    /// unclassified member is exactly what <see cref="Ratchet"/> fails on, so persisting it as uncovered would let the
    /// failing run heal itself. Bootstrapping — no committed manifest at all — is the one place the uncovered backlog
    /// is written wholesale, because there is no ratchet to satisfy yet. Only current members are emitted; removed
    /// members drop out.</summary>
    /// <param name="existing">The committed manifest, if any.</param>
    /// <returns>The regenerated manifest.</returns>
    public static Manifest Generate(Manifest? existing) {
        var covered = CoveredMembers();
        var waived = WaivedMembers();
        var bootstrapping = (existing is null);
        var previous = (existing?.Members ?? []).ToDictionary(keySelector: entry => entry.Id, elementSelector: entry => entry, comparer: StringComparer.Ordinal);
        var members = new List<ManifestEntry>();

        foreach (var id in MemberSurface.Ids) {
            if (covered.TryGetValue(key: id, value: out var laws)) {
                members.Add(item: new ManifestEntry { CoveredBy = [.. laws], Id = id, State = "covered" });
            } else if (previous.TryGetValue(key: id, value: out var prior) && (prior.State == "covered")) {
                // Sticky: a member that was covered but no longer is stays covered in the manifest so the ratchet keeps
                // failing until coverage is restored (grow-only never silently downgrades).
                members.Add(item: new ManifestEntry { CoveredBy = prior.CoveredBy, Id = id, State = "covered" });
            } else if (waived.TryGetValue(key: id, value: out var reason)) {
                members.Add(item: new ManifestEntry { Id = id, Reason = reason, State = "waived" });
            } else if (bootstrapping || previous.ContainsKey(key: id)) {
                members.Add(item: new ManifestEntry { Id = id, State = "uncovered" });
            }
        }

        members.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.Id, strB: right.Id));

        return new Manifest { Members = members };
    }
    /// <summary>Counts the manifest states.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The covered/waived/uncovered counts.</returns>
    public static (int Covered, int Waived, int Uncovered) Counts(Manifest manifest) {
        var covered = manifest.Members.Count(predicate: entry => (entry.State == "covered"));
        var waived = manifest.Members.Count(predicate: entry => (entry.State == "waived"));
        var uncovered = manifest.Members.Count(predicate: entry => (entry.State == "uncovered"));

        return (covered, waived, uncovered);
    }
    /// <summary>Applies the ratchet against the committed manifest. A member counts as classified when the committed
    /// manifest gives it a state OR a declaration does — a law case covering it, or a waiver naming it — so landing a
    /// new member together with its law or its waiver passes on the first run, while a member with neither fails on
    /// every run (<see cref="Generate"/> never writes it, so the failure cannot heal itself).</summary>
    /// <param name="committed">The committed manifest.</param>
    /// <returns>The violations: unclassified members and covered→uncovered regressions. Empty means the gate holds.</returns>
    public static (IReadOnlyList<string> NewMembers, IReadOnlyList<string> Regressions) Ratchet(Manifest committed) {
        var covered = CoveredMembers();
        var waived = WaivedMembers();
        var known = committed.Members.ToDictionary(keySelector: entry => entry.Id, elementSelector: entry => entry, comparer: StringComparer.Ordinal);
        var newMembers = new List<string>();
        var regressions = new List<string>();

        foreach (var id in MemberSurface.Ids) {
            if (!known.ContainsKey(key: id)) {
                if (!covered.ContainsKey(key: id) && !waived.ContainsKey(key: id)) {
                    newMembers.Add(item: id);
                }
            } else if ((known[id].State == "covered") && !covered.ContainsKey(key: id)) {
                regressions.Add(item: id);
            }
        }

        newMembers.Sort(comparison: StringComparer.Ordinal.Compare);
        regressions.Sort(comparison: StringComparer.Ordinal.Compare);

        return (newMembers, regressions);
    }
}
