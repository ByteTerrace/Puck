using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// Shared tolerance assertions over measured values — the failure message reports the measured value the way the
/// measurement sink formats it, so a red law's output reads like the report it would have written.
/// </summary>
internal static class MeasurementAssert {
    /// <summary>Asserts <paramref name="actual"/> lies within <paramref name="tolerance"/> of
    /// <paramref name="expected"/>, comparing in double precision.</summary>
    /// <param name="actual">The measured value.</param>
    /// <param name="expected">The expected value.</param>
    /// <param name="tolerance">The absolute tolerance.</param>
    /// <param name="subject">What the measurement is of, for the failure message.</param>
    internal static void Near(FixedQ4816 actual, double expected, double tolerance, string subject) {
        var difference = Math.Abs(value: (((double)actual) - expected));

        Assert.True(
            condition: (difference <= tolerance),
            userMessage: $"{subject}: expected {expected}, measured {MeasurementReport.Format(value: actual)}"
        );
    }
    /// <summary>Asserts <paramref name="actual"/> lies within <paramref name="tolerance"/> of
    /// <paramref name="expected"/>.</summary>
    /// <param name="actual">The measured value.</param>
    /// <param name="expected">The expected value.</param>
    /// <param name="tolerance">The absolute tolerance.</param>
    /// <param name="subject">What the measurement is of, for the failure message.</param>
    internal static void Near(double actual, double expected, double tolerance, string subject) {
        Assert.True(
            condition: (Math.Abs(value: (actual - expected)) <= tolerance),
            userMessage: $"{subject}: expected {expected}, measured {actual}"
        );
    }
}
