using System.Security.Cryptography;
using System.Text.Json;

using Puck.Maths;

namespace Puck.State;

/// <summary>How one pinned evidence target's reference code was produced. Every field is a recorded fact about the
/// offline build, never a property of the machine that happens to execute Puck.</summary>
/// <param name="Sdk">The exact .NET SDK version.</param>
/// <param name="GlobalJsonRollForward">The checkout's <c>global.json</c> roll-forward policy, which decides whether
/// recording the SDK version pins anything at all.</param>
/// <param name="RuntimePack">The runtime pack the reference build compiled against.</param>
/// <param name="IlCompiler">The ILCompiler package and its host architecture.</param>
/// <param name="Configuration">The build configuration.</param>
/// <param name="RuntimeIdentifier">The runtime identifier the reference code was generated for.</param>
/// <param name="Compilation">The compilation mode and the rooting the reference build used.</param>
/// <param name="Optimization">The optimization switch passed to the compiler.</param>
/// <param name="MethodBodyFolding">Whether identical method bodies were folded, which would merge kernels.</param>
/// <param name="InstructionSet">The explicit instruction-set target. Never <c>native</c>: a host-chosen set would
/// make the evidence a property of the machine that produced it.</param>
/// <param name="ProfileGuidedOptimization">The profile data the build consumed, or <c>none</c>.</param>
public sealed record ReferenceBuild(
    string Sdk,
    string GlobalJsonRollForward,
    string RuntimePack,
    string IlCompiler,
    string Configuration,
    string RuntimeIdentifier,
    string Compilation,
    string Optimization,
    string MethodBodyFolding,
    string InstructionSet,
    string ProfileGuidedOptimization
);
/// <summary>The offline tools that read one pinned evidence target's reference code, and the processor model whose
/// published measurements priced it.</summary>
/// <param name="Disassembler">The disassembler and its version.</param>
/// <param name="Scheduler">The scheduling-model tool and its version.</param>
/// <param name="Triple">The target triple the tools were driven with.</param>
/// <param name="CpuModel">The scheduling model naming the processor whose measurements are used.</param>
public sealed record ReferenceAnalysis(string Disassembler, string Scheduler, string Triple, string CpuModel);
/// <summary>One instruction form's published service on one evidence target.</summary>
/// <param name="Latency">The largest relevant operand latency, in cycles.</param>
/// <param name="ReciprocalThroughputNumerator">The reciprocal throughput's numerator, kept exact.</param>
/// <param name="ReciprocalThroughputDenominator">The reciprocal throughput's positive denominator.</param>
/// <param name="Service">The serialized service cost <c>q = max(1, ceil(L), ceil(T))</c>.</param>
public sealed record InstructionService(long Latency, long ReciprocalThroughputNumerator, long ReciprocalThroughputDenominator, long Service);
/// <summary>One pinned offline evidence target: a fixed reference build read by fixed tools against one published
/// scheduling model.</summary>
/// <param name="Id">The target's identifier.</param>
/// <param name="Family">The ISA family the family-balanced aggregation groups this target under.</param>
/// <param name="Build">The reference build.</param>
/// <param name="Analysis">The offline tools and the processor model.</param>
/// <param name="ScalarIntegerAddForm">The scalar integer-add instruction form this target normalizes to.</param>
/// <param name="ScalarIntegerAddService">That form's published service, the target's normalization unit.</param>
public sealed record ReferenceEvidenceTarget(
    string Id,
    string Family,
    ReferenceBuild Build,
    ReferenceAnalysis Analysis,
    string ScalarIntegerAddForm,
    InstructionService ScalarIntegerAddService
);
/// <summary>One instruction form and its published service on each pinned evidence target. A target absent from
/// <paramref name="Service"/> has no published measurement for this form, which is unresolved evidence.</summary>
/// <param name="Form">The instruction form as the reference lowering spells it, with absolute addresses folded away.</param>
/// <param name="Service">The per-target service, keyed by target identifier.</param>
public sealed record InstructionForm(string Form, IReadOnlyDictionary<string, InstructionService> Service);
/// <summary>A published measurement set the instruction service rests on.</summary>
/// <param name="Name">The source's name.</param>
/// <param name="Url">Where the source is read.</param>
/// <param name="Redistribution">The source's redistribution terms, when they restrict what may be stored here.</param>
public sealed record PublishedSource(string Name, string Url, string? Redistribution);
/// <summary>A reference kernel's source file and the digest its evidence was read against. A change to the file
/// changes the digest, which invalidates the kernel's recorded price.</summary>
/// <param name="Path">The repository-relative source path.</param>
/// <param name="Sha256">The file's SHA-256 digest, lowercase hexadecimal.</param>
public sealed record KernelSource(string Path, string Sha256);
/// <summary>A loop inside a reference kernel and the source contract that bounds its iteration count.</summary>
/// <param name="Symbol">The symbol carrying the loop.</param>
/// <param name="Iterations">The bound, in iterations.</param>
/// <param name="Contract">The source contract the bound is read from.</param>
public sealed record KernelLoopBound(string Symbol, long Iterations, string Contract);
/// <summary>What llvm-mca's whole-block simulation of a kernel's maximum path reported, kept beside the serialized
/// price it is evidence against.</summary>
/// <param name="TotalCycles">The simulated cycle count.</param>
/// <param name="InstructionsPerCycle">The simulated instructions per cycle.</param>
/// <param name="BlockReciprocalThroughput">The simulated block reciprocal throughput.</param>
/// <param name="ResourcePressure">The resource-pressure rows, as the tool printed them.</param>
public sealed record KernelSimulation(string TotalCycles, string InstructionsPerCycle, string BlockReciprocalThroughput, IReadOnlyList<string> ResourcePressure);
/// <summary>One reference kernel's price on one pinned evidence target, or why that target left it unresolved.</summary>
/// <param name="Cycles">The maximum sum of instruction service over the kernel's permitted paths.</param>
/// <param name="NormalizedNumerator">The price normalized to the target's scalar integer-add service, numerator.</param>
/// <param name="NormalizedDenominator">That normalization's positive denominator.</param>
/// <param name="MaximumPathInstructions">How many instructions the priced path carries.</param>
/// <param name="Simulation">The whole-block simulation kept as evidence.</param>
/// <param name="Unresolved">Why this target could not price the kernel; empty when it did.</param>
public sealed record KernelTargetPrice(
    long Cycles,
    long NormalizedNumerator,
    long NormalizedDenominator,
    long MaximumPathInstructions,
    KernelSimulation? Simulation,
    IReadOnlyList<string> Unresolved
);
/// <summary>One reference kernel: the entry point whose reference lowering was read, the source its evidence was
/// read against, the reviewed formula, and the price each pinned target reached.</summary>
/// <param name="Id">The kernel identifier coefficients name.</param>
/// <param name="EntryPoint">The managed entry point.</param>
/// <param name="Symbol">The symbol the reference lowering emits it under.</param>
/// <param name="Sources">The source files whose change invalidates this kernel's evidence.</param>
/// <param name="Formula">The reviewed formula the maximum path follows.</param>
/// <param name="LoopBounds">Each loop inside the kernel and the source contract bounding it.</param>
/// <param name="IncludedOverhead">What the price includes beyond the operation itself.</param>
/// <param name="InputDomainBound">The input domain the price is bounded over.</param>
/// <param name="Targets">The price on each pinned evidence target.</param>
/// <param name="FamilyMedians">The per-family median of the normalized prices, as exact rationals.</param>
/// <param name="RepresentativeService">The aggregated semantic price, or why it is unmodeled.</param>
public sealed record ReferenceKernel(
    string Id,
    string EntryPoint,
    string Symbol,
    IReadOnlyList<KernelSource> Sources,
    string Formula,
    IReadOnlyList<KernelLoopBound> LoopBounds,
    string IncludedOverhead,
    string InputDomainBound,
    IReadOnlyDictionary<string, KernelTargetPrice> Targets,
    IReadOnlyDictionary<string, (long Numerator, long Denominator)> FamilyMedians,
    CostBound RepresentativeService
);
/// <summary>A transfer rate as an exact rational number of bytes per reference cycle.</summary>
/// <param name="Numerator">The bytes transferred per <paramref name="Denominator"/> cycles.</param>
/// <param name="Denominator">The positive cycle denominator.</param>
public sealed record MemoryRational(long Numerator, long Denominator);
/// <summary>One plan-derived memory access class and the service coefficients recorded for it.</summary>
/// <param name="Class">The access class.</param>
/// <param name="TransferredBytes">What this class counts as transferred, including read-for-ownership, writeback
/// and unaligned line crossings.</param>
/// <param name="StartupCycles">The per-start setup service, or why it is unmodeled.</param>
/// <param name="AdditionalLatencyCycles">The dependent-access latency beyond the instruction baseline, or why it is
/// unmodeled.</param>
/// <param name="Bandwidth">The transfer rate, when one is established.</param>
/// <param name="BandwidthUnmodeled">Why no transfer rate is established, when none is.</param>
public sealed record MemoryClassEvidence(
    MemoryAccessClass Class,
    string TransferredBytes,
    CostBound StartupCycles,
    CostBound AdditionalLatencyCycles,
    MemoryRational? Bandwidth,
    string? BandwidthUnmodeled
);
/// <summary>One primitive cost coefficient: the vocabulary member it prices, the reference kernel that supplies its
/// evidence, and the bound that kernel's semantic price gives it.</summary>
/// <param name="Vocabulary">Which registered vocabulary the operation belongs to.</param>
/// <param name="Operation">The vocabulary member.</param>
/// <param name="NumericKinds">The numeric kinds the operation is defined over.</param>
/// <param name="ShapeParameters">The compiled shape parameters the price depends on.</param>
/// <param name="Kernel">The reference kernel supplying the reference implementation, instruction evidence,
/// input-domain bound and included overhead, or null when no kernel has been captured.</param>
/// <param name="Bound">The reference-cycle price, or why it is unmodeled.</param>
public sealed record CostCoefficient(
    string Vocabulary,
    string Operation,
    IReadOnlyList<string> NumericKinds,
    IReadOnlyList<string> ShapeParameters,
    string? Kernel,
    CostBound Bound
);
/// <summary>The one owning evidence manifest for the reference schedule: the pinned evidence targets, and the digest
/// of the bytes they were read from. A manifest that is missing, unreadable, or off-schema fails here rather than
/// passing as an empty one.</summary>
public static class ReferenceScheduleManifest {
    private const string ResourceName = "Puck.State.ReferenceSchedule.json";

    /// <summary>Gets every registered coefficient, in the order the manifest records them.</summary>
    public static IReadOnlyList<CostCoefficient> Coefficients { get; }
    /// <summary>Gets the SHA-256 digest of the manifest bytes, lowercase hexadecimal.</summary>
    public static string Digest { get; }
    /// <summary>Gets the instruction forms of the priced reference lowerings, grouped by ISA family.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<InstructionForm>> InstructionForms { get; }
    /// <summary>Gets the rule the manifest prices one instruction form's service by.</summary>
    public static string InstructionServiceRule { get; }
    /// <summary>Gets the reference kernels, in the order the manifest records them.</summary>
    public static IReadOnlyList<ReferenceKernel> Kernels { get; }
    /// <summary>Gets how an even-sized median is taken when the family-balanced rule is applied.</summary>
    public static string EvenSizedMedianRule { get; }
    /// <summary>Gets how each target's kernel price is normalized before aggregation.</summary>
    public static string RepresentativeServiceNormalization { get; }
    /// <summary>Gets the family-balanced aggregation rule the semantic prices follow.</summary>
    public static string RepresentativeServiceRule { get; }
    /// <summary>Gets what the dependent-access latency excludes.</summary>
    public static string MemoryAdditionalLatencyDefinition { get; }
    /// <summary>Gets how the memory coefficients are aggregated across the cohort.</summary>
    public static string MemoryAggregation { get; }
    /// <summary>Gets the recorded memory access classes, one per declared class.</summary>
    public static IReadOnlyList<MemoryClassEvidence> MemoryClasses { get; }
    /// <summary>Gets when a cheaper residency class may be granted.</summary>
    public static string MemoryResidencyRule { get; }
    /// <summary>Gets the explicit conservative memory service form.</summary>
    public static string MemoryServiceForm { get; }
    /// <summary>Gets the cost model identifier the manifest names.</summary>
    public static string ModelId { get; }
    /// <summary>Gets the published measurement sets the instruction service rests on.</summary>
    public static IReadOnlyList<PublishedSource> PublishedSources { get; }
    /// <summary>Gets the manifest schema identifier.</summary>
    public static string Schema { get; }
    /// <summary>Gets the pinned evidence targets, in the order the manifest records them.</summary>
    public static IReadOnlyList<ReferenceEvidenceTarget> Targets { get; }

    static ReferenceScheduleManifest() {
        using var stream = (typeof(ReferenceScheduleManifest).Assembly.GetManifestResourceStream(name: ResourceName)
            ?? throw new InvalidOperationException(message: $"The reference-schedule manifest '{ResourceName}' is not embedded in Puck.State."));
        using var buffer = new MemoryStream();

        stream.CopyTo(destination: buffer);

        var bytes = buffer.ToArray();

        Digest = Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
        try {
            using var document = JsonDocument.Parse(utf8Json: bytes);

            ModelId = Text(
                element: document.RootElement,
                name: "modelId"
            );
            Schema = Text(
                element: document.RootElement,
                name: "schema"
            );
            Targets = ReadTargets(root: document.RootElement);

            var service = Section(
                element: document.RootElement,
                name: "instructionService"
            );

            InstructionForms = ReadForms(service: service);
            InstructionServiceRule = Text(
                element: service,
                name: "rule"
            );
            PublishedSources = ReadSources(service: service);

            var representative = Section(
                element: document.RootElement,
                name: "representativeService"
            );

            EvenSizedMedianRule = Text(
                element: representative,
                name: "evenSizedMedian"
            );
            Coefficients = ReadCoefficients(root: document.RootElement);
            Kernels = ReadKernels(root: document.RootElement);

            var memory = Section(
                element: document.RootElement,
                name: "memory"
            );

            MemoryAdditionalLatencyDefinition = Text(
                element: memory,
                name: "additionalLatencyCycles"
            );
            MemoryAggregation = Text(
                element: memory,
                name: "aggregation"
            );
            MemoryClasses = ReadMemoryClasses(memory: memory);
            MemoryResidencyRule = Text(
                element: memory,
                name: "residency"
            );
            MemoryServiceForm = Text(
                element: memory,
                name: "serviceForm"
            );
            RepresentativeServiceNormalization = Text(
                element: representative,
                name: "normalization"
            );
            RepresentativeServiceRule = Text(
                element: representative,
                name: "rule"
            );
        } catch (JsonException exception) {
            throw new InvalidOperationException(
                innerException: exception,
                message: "The reference-schedule manifest is not readable JSON."
            );
        }
    }

    private static long Number(JsonElement element, string name) => ((element.TryGetProperty(
        propertyName: name,
        value: out var member
    ) && (member.ValueKind == JsonValueKind.Number))
        ? member.GetInt64()
        : throw new InvalidOperationException(message: $"The reference-schedule manifest is missing the number '{name}'."));
    private static IReadOnlyDictionary<string, IReadOnlyList<InstructionForm>> ReadForms(JsonElement service) {
        var families = new Dictionary<string, IReadOnlyList<InstructionForm>>(comparer: StringComparer.Ordinal);

        foreach (var family in Section(
            element: service,
            name: "forms"
        ).EnumerateObject()) {
            var rows = new List<InstructionForm>();

            foreach (var row in family.Value.EnumerateArray()) {
                var perTarget = new Dictionary<string, InstructionService>(comparer: StringComparer.Ordinal);

                foreach (var target in Section(
                    element: row,
                    name: "service"
                ).EnumerateObject()) {
                    perTarget.Add(
                        key: target.Name,
                        value: ReadService(element: target.Value)
                    );
                }
                rows.Add(item: new(
                    Form: Text(
                        element: row,
                        name: "form"
                    ),
                    Service: perTarget
                ));
            }
            families.Add(
                key: family.Name,
                value: rows
            );
        }
        return families;
    }
    private static InstructionService ReadService(JsonElement element) {
        var throughput = ((element.TryGetProperty(
            propertyName: "reciprocalThroughput",
            value: out var rational
        ) && (rational.ValueKind == JsonValueKind.Array) && (rational.GetArrayLength() == 2))
            ? rational
            : throw new InvalidOperationException(message: "A reference-schedule service row carries no reciprocal-throughput rational."));

        return new(
            Latency: Number(
                element: element,
                name: "latency"
            ),
            ReciprocalThroughputNumerator: throughput[0].GetInt64(),
            ReciprocalThroughputDenominator: throughput[1].GetInt64(),
            Service: Number(
                element: element,
                name: "q"
            )
        );
    }
    private static IReadOnlyList<ReferenceKernel> ReadKernels(JsonElement root) {
        if (!root.TryGetProperty(
            propertyName: "kernels",
            value: out var kernels
        ) || (kernels.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: "The reference-schedule manifest records no reference kernels.");
        }
        var rows = new List<ReferenceKernel>(capacity: kernels.GetArrayLength());

        foreach (var kernel in kernels.EnumerateArray()) {
            var representative = Section(
                element: kernel,
                name: "representativeService"
            );
            var medians = new Dictionary<string, (long, long)>(comparer: StringComparer.Ordinal);
            var bound = (representative.TryGetProperty(
                propertyName: "cycles",
                value: out var cycles
            )
                ? CostBound.Known(cycles: cycles.GetInt64())
                : CostBound.Unmodeled(reason: Text(
                    element: representative,
                    name: "unmodeled"
                )));

            if (representative.TryGetProperty(
                propertyName: "familyMedians",
                value: out var familyMedians
            )) {
                foreach (var family in familyMedians.EnumerateObject()) {
                    medians.Add(
                        key: family.Name,
                        value: (family.Value[0].GetInt64(), family.Value[1].GetInt64())
                    );
                }
            }
            rows.Add(item: new(
                EntryPoint: Text(
                    element: kernel,
                    name: "entryPoint"
                ),
                FamilyMedians: medians,
                Formula: Text(
                    element: kernel,
                    name: "formula"
                ),
                Id: Text(
                    element: kernel,
                    name: "id"
                ),
                IncludedOverhead: Text(
                    element: kernel,
                    name: "includedOverhead"
                ),
                InputDomainBound: Text(
                    element: kernel,
                    name: "inputDomainBound"
                ),
                LoopBounds: ReadLoopBounds(kernel: kernel),
                RepresentativeService: bound,
                Sources: ReadKernelSources(kernel: kernel),
                Symbol: Text(
                    element: kernel,
                    name: "symbol"
                ),
                Targets: ReadKernelPrices(kernel: kernel)
            ));
        }
        return rows;
    }
    private static IReadOnlyDictionary<string, KernelTargetPrice> ReadKernelPrices(JsonElement kernel) {
        var prices = new Dictionary<string, KernelTargetPrice>(comparer: StringComparer.Ordinal);

        foreach (var target in Section(
            element: kernel,
            name: "targets"
        ).EnumerateObject()) {
            if (target.Value.TryGetProperty(
                propertyName: "unresolved",
                value: out var unresolved
            )) {
                prices.Add(
                    key: target.Name,
                    value: new(
                        Cycles: 0L,
                        MaximumPathInstructions: 0L,
                        NormalizedDenominator: 1L,
                        NormalizedNumerator: 0L,
                        Simulation: null,
                        Unresolved: [.. unresolved.EnumerateArray().Select(selector: reason => (reason.GetString() ?? string.Empty))]
                    )
                );
                continue;
            }
            var normalized = target.Value.GetProperty(propertyName: "normalizedToScalarIntegerAdd");
            var simulation = Section(
                element: target.Value,
                name: "mca"
            );

            prices.Add(
                key: target.Name,
                value: new(
                    Cycles: Number(
                        element: target.Value,
                        name: "cycles"
                    ),
                    MaximumPathInstructions: Number(
                        element: target.Value,
                        name: "maximumPathInstructions"
                    ),
                    NormalizedDenominator: normalized[1].GetInt64(),
                    NormalizedNumerator: normalized[0].GetInt64(),
                    Simulation: new(
                        BlockReciprocalThroughput: Text(
                            element: simulation,
                            name: "blockReciprocalThroughput"
                        ),
                        InstructionsPerCycle: Text(
                            element: simulation,
                            name: "instructionsPerCycle"
                        ),
                        ResourcePressure: [.. simulation.GetProperty(propertyName: "resourcePressure").EnumerateArray().Select(selector: row => (row.GetString() ?? string.Empty))],
                        TotalCycles: Text(
                            element: simulation,
                            name: "totalCycles"
                        )
                    ),
                    Unresolved: []
                )
            );
        }
        return prices;
    }
    private static IReadOnlyList<KernelSource> ReadKernelSources(JsonElement kernel) {
        var rows = new List<KernelSource>();

        foreach (var source in kernel.GetProperty(propertyName: "sources").EnumerateArray()) {
            rows.Add(item: new(
                Path: Text(
                    element: source,
                    name: "path"
                ),
                Sha256: Text(
                    element: source,
                    name: "sha256"
                )
            ));
        }
        return rows;
    }
    private static IReadOnlyList<KernelLoopBound> ReadLoopBounds(JsonElement kernel) {
        var rows = new List<KernelLoopBound>();

        foreach (var loop in kernel.GetProperty(propertyName: "loopBounds").EnumerateArray()) {
            rows.Add(item: new(
                Contract: Text(
                    element: loop,
                    name: "contract"
                ),
                Iterations: Number(
                    element: loop,
                    name: "iterations"
                ),
                Symbol: Text(
                    element: loop,
                    name: "symbol"
                )
            ));
        }
        return rows;
    }
    private static IReadOnlyList<CostCoefficient> ReadCoefficients(JsonElement root) {
        if (!root.TryGetProperty(
            propertyName: "coefficients",
            value: out var coefficients
        ) || (coefficients.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: "The reference-schedule manifest registers no coefficients.");
        }
        var rows = new List<CostCoefficient>(capacity: coefficients.GetArrayLength());

        foreach (var coefficient in coefficients.EnumerateArray()) {
            var bound = Section(
                element: coefficient,
                name: "bound"
            );

            rows.Add(item: new(
                Bound: (bound.TryGetProperty(
                    propertyName: "known",
                    value: out var known
                )
                    ? CostBound.Known(cycles: known.GetInt64())
                    : CostBound.Unmodeled(reason: Text(
                        element: bound,
                        name: "unmodeled"
                    ))),
                Kernel: ((coefficient.TryGetProperty(
                    propertyName: "kernel",
                    value: out var kernel
                ) && (kernel.ValueKind == JsonValueKind.String))
                    ? kernel.GetString()
                    : null),
                NumericKinds: [.. Strings(
                    element: coefficient,
                    name: "numericKinds"
                )],
                Operation: Text(
                    element: coefficient,
                    name: "operation"
                ),
                ShapeParameters: [.. Strings(
                    element: coefficient,
                    name: "shapeParameters"
                )],
                Vocabulary: Text(
                    element: coefficient,
                    name: "vocabulary"
                )
            ));
        }
        return rows;
    }
    private static CostBound ReadCoefficient(JsonElement element, string name) {
        var coefficient = Section(
            element: element,
            name: name
        );

        return (coefficient.TryGetProperty(
            propertyName: "cycles",
            value: out var cycles
        )
            ? CostBound.Known(cycles: cycles.GetInt64())
            : CostBound.Unmodeled(reason: Text(
                element: coefficient,
                name: "unmodeled"
            )));
    }
    private static IReadOnlyList<MemoryClassEvidence> ReadMemoryClasses(JsonElement memory) {
        if (!memory.TryGetProperty(
            propertyName: "classes",
            value: out var classes
        ) || (classes.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: "The reference-schedule manifest records no memory access classes.");
        }
        var rows = new List<MemoryClassEvidence>(capacity: classes.GetArrayLength());

        foreach (var entry in classes.EnumerateArray()) {
            var bandwidth = Section(
                element: entry,
                name: "bandwidthBytesPerCycle"
            );
            var rate = (bandwidth.TryGetProperty(
                propertyName: "rational",
                value: out var rational
            )
                ? new MemoryRational(
                    Denominator: rational[1].GetInt64(),
                    Numerator: rational[0].GetInt64()
                )
                : null);

            rows.Add(item: new(
                AdditionalLatencyCycles: ReadCoefficient(
                    element: entry,
                    name: "additionalLatencyCycles"
                ),
                Bandwidth: rate,
                BandwidthUnmodeled: ((rate is null)
                    ? Text(
                        element: bandwidth,
                        name: "unmodeled"
                    )
                    : null),
                Class: Enum.Parse<MemoryAccessClass>(
                    ignoreCase: false,
                    value: Text(
                        element: entry,
                        name: "class"
                    )
                ),
                StartupCycles: ReadCoefficient(
                    element: entry,
                    name: "startupCycles"
                ),
                TransferredBytes: Text(
                    element: entry,
                    name: "transferredBytes"
                )
            ));
        }
        return rows;
    }
    private static IReadOnlyList<PublishedSource> ReadSources(JsonElement service) {
        if (!service.TryGetProperty(
            propertyName: "publishedSources",
            value: out var sources
        ) || (sources.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: "The reference-schedule manifest names no published measurement sources.");
        }
        var rows = new List<PublishedSource>(capacity: sources.GetArrayLength());

        foreach (var source in sources.EnumerateArray()) {
            rows.Add(item: new(
                Name: Text(
                    element: source,
                    name: "name"
                ),
                Redistribution: (source.TryGetProperty(
                    propertyName: "redistribution",
                    value: out var terms
                )
                    ? terms.GetString()
                    : null),
                Url: Text(
                    element: source,
                    name: "url"
                )
            ));
        }
        return rows;
    }
    private static IEnumerable<string> Strings(JsonElement element, string name) {
        if (!element.TryGetProperty(
            propertyName: name,
            value: out var member
        ) || (member.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: $"The reference-schedule manifest is missing the list '{name}'.");
        }
        foreach (var entry in member.EnumerateArray()) {
            yield return (entry.GetString() ?? string.Empty);
        }
    }
    private static IReadOnlyList<ReferenceEvidenceTarget> ReadTargets(JsonElement root) {
        if (!root.TryGetProperty(
            propertyName: "targets",
            value: out var targets
        ) || (targets.ValueKind != JsonValueKind.Array)) {
            throw new InvalidOperationException(message: "The reference-schedule manifest records no evidence targets.");
        }
        var rows = new List<ReferenceEvidenceTarget>(capacity: targets.GetArrayLength());

        foreach (var target in targets.EnumerateArray()) {
            var build = Section(
                element: target,
                name: "referenceBuild"
            );
            var analysis = Section(
                element: target,
                name: "analysis"
            );
            var add = Section(
                element: target,
                name: "scalarIntegerAddService"
            );

            rows.Add(item: new(
                Analysis: new(
                    CpuModel: Text(
                        element: analysis,
                        name: "cpuModel"
                    ),
                    Disassembler: Text(
                        element: analysis,
                        name: "disassembler"
                    ),
                    Scheduler: Text(
                        element: analysis,
                        name: "scheduler"
                    ),
                    Triple: Text(
                        element: analysis,
                        name: "triple"
                    )
                ),
                Build: new(
                    Compilation: Text(
                        element: build,
                        name: "compilation"
                    ),
                    Configuration: Text(
                        element: build,
                        name: "configuration"
                    ),
                    GlobalJsonRollForward: Text(
                        element: build,
                        name: "globalJsonRollForward"
                    ),
                    IlCompiler: Text(
                        element: build,
                        name: "ilCompiler"
                    ),
                    InstructionSet: Text(
                        element: build,
                        name: "instructionSet"
                    ),
                    MethodBodyFolding: Text(
                        element: build,
                        name: "methodBodyFolding"
                    ),
                    Optimization: Text(
                        element: build,
                        name: "optimization"
                    ),
                    ProfileGuidedOptimization: Text(
                        element: build,
                        name: "profileGuidedOptimization"
                    ),
                    RuntimeIdentifier: Text(
                        element: build,
                        name: "runtimeIdentifier"
                    ),
                    RuntimePack: Text(
                        element: build,
                        name: "runtimePack"
                    ),
                    Sdk: Text(
                        element: build,
                        name: "sdk"
                    )
                ),
                Family: Text(
                    element: target,
                    name: "family"
                ),
                Id: Text(
                    element: target,
                    name: "id"
                ),
                ScalarIntegerAddForm: Text(
                    element: add,
                    name: "form"
                ),
                ScalarIntegerAddService: ReadService(element: add)
            ));
        }
        return rows;
    }
    private static JsonElement Section(JsonElement element, string name) => ((element.TryGetProperty(
        propertyName: name,
        value: out var member
    ) && (member.ValueKind == JsonValueKind.Object))
        ? member
        : throw new InvalidOperationException(message: $"The reference-schedule manifest is missing the section '{name}'."));
    private static string Text(JsonElement element, string name) => ((element.TryGetProperty(
        propertyName: name,
        value: out var member
    ) && (member.ValueKind == JsonValueKind.String))
        ? (member.GetString() ?? string.Empty)
        : throw new InvalidOperationException(message: $"The reference-schedule manifest is missing the text '{name}'."));
}
