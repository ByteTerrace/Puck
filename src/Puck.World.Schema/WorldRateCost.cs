namespace Puck.World;

/// <summary>The retained storage of one compiled presentation rate, counted when a world is loaded or reloaded.</summary>
/// <param name="Pieces">The number of normalized polynomial pieces.</param>
/// <param name="Coefficients">The retained double coefficients, including antiderivatives.</param>
/// <param name="Degree">The largest rate polynomial degree, before integration.</param>
public readonly record struct WorldRateCost(int Pieces, int Coefficients, int Degree) {
    /// <summary>The exact coefficient-array payload in bytes, excluding array headers and piece metadata.</summary>
    public long CoefficientBytes => (((long)Coefficients) * sizeof(double));
}
/// <summary>The counted work of evaluating one compiled rate; no inactive piece is evaluated.</summary>
/// <param name="PieceSearches">Binary-search comparisons used to select the active piece.</param>
/// <param name="CoefficientBlends">De Casteljau blends in the active antiderivative.</param>
/// <param name="PeriodDoublings">Modular doublings used to include complete clock periods.</param>
public readonly record struct WorldRateEvaluation(int PieceSearches, int CoefficientBlends, int PeriodDoublings);
