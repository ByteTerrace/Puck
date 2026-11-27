namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] RationalPolynomialCases() => [
        ClaimCase(
            claim: PolynomialCalculusClaims.RationalText,
            id: "rational-polynomial.rational-text-floor-and-truncation"
        ),
        ClaimCase(
            claim: PolynomialCalculusClaims.ExactCalculus,
            id: "rational-polynomial.exact-calculus-and-composition"
        ),
        ClaimCase(
            claim: PolynomialCalculusClaims.PeriodicIntegral,
            id: "rational-polynomial.periodic-piecewise-integral"
        ),
    ];
}
