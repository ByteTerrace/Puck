namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? SelfridgeSymbolRefusals() {
        var failure = (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 3UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol below the first magnitude")
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 64UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol at the table limit")
        ?? (Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 6UL, residue: 1UL), type: typeof(ArgumentOutOfRangeException), parameterName: "magnitude",
            what: "a Selfridge symbol over an even magnitude")
        ?? Refuses(action: () => _ = PrimeKernels.SelfridgeSymbol(magnitude: 7UL, residue: 7UL), type: typeof(ArgumentOutOfRangeException), parameterName: "residue",
            what: "a Selfridge symbol of a residue that is not reduced"))));

        return (failure ?? ((PrimeKernels.SelfridgeSymbol(magnitude: 7UL, residue: 2UL) == 1) ? null : "wrong Selfridge symbol (2/7)"));
    }
}
