using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// Refuses every <c>InternalsVisibleTo</c> grant (<see cref="Ivt001InternalsVisibleTo"/>), whichever assembly it names, a
/// test assembly included. A member another assembly needs is public: a grant hands a whole assembly's internals to a
/// friend, invisibly at every call site, where publicity is visible and reviewable.
/// <para>
/// <b>Both spellings.</b> The rule reads the compiled assembly's attributes, so it sees an
/// <c>[assembly: InternalsVisibleTo(...)]</c> written in source and a csproj <c>&lt;InternalsVisibleTo&gt;</c> item alike:
/// the SDK generates the item into the same attribute in the project's generated assembly-info file, which this rule
/// analyzes.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InternalsVisibleToAnalyzer : DiagnosticAnalyzer {
    private const string AttributeName = "System.Runtime.CompilerServices.InternalsVisibleToAttribute";
    private const string Category = "Puck.Accessibility";

    /// <summary>IVT001: an assembly grants its internals to another assembly.</summary>
    public static readonly DiagnosticDescriptor Ivt001InternalsVisibleTo = new(
        id: "IVT001",
        title: "InternalsVisibleTo grant",
        messageFormat: "'{0}' grants its internals to '{1}'; InternalsVisibleTo is forbidden in Puck, tests included: make each member the other assembly needs public and delete the grant (the [assembly: InternalsVisibleTo] attribute or the csproj <InternalsVisibleTo> item)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "No Puck assembly grants another its internals, whether as an [assembly: InternalsVisibleTo] attribute or a csproj <InternalsVisibleTo> item, and a test assembly is no exception. A member another assembly needs is public; a member that looks wrong to make public is evidence about the design.",
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(item: Ivt001InternalsVisibleTo);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        // The csproj item reaches the compilation only as generated source.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationAction(action: AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context) {
        var assembly = context.Compilation.Assembly;

        foreach (var attribute in assembly.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != AttributeName) {
                continue;
            }

            var friend = (((attribute.ConstructorArguments.Length != 0) && (attribute.ConstructorArguments[0].Value is string name))
                ? name
                : "?");
            var location = (attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken: context.CancellationToken).GetLocation() ?? Location.None);

            context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Ivt001InternalsVisibleTo,
                location: location,
                messageArgs: [assembly.Name, friend]
            ));
        }
    }
}
