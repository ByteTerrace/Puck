using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Extension methods that evaluate the inverse cumulative distribution function (the quantile function) of the
/// normal distribution.
/// </summary>
public static class ProbabilityFunctions {
    /// <summary>
    /// Returns the inverse cumulative distribution function of the standard normal distribution.
    /// </summary>
    /// <param name="probability">The cumulative probability. Values must be in the inclusive range [0, 1].</param>
    /// <returns>The standard normal deviate <c>z</c> such that <c>Φ(z) = probability</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="probability"/> is NaN, or is less than 0 or greater than 1.</exception>
    /// <remarks>
    /// Computes the normal quantile via minimax rational (polynomial-ratio) approximations over three regions of
    /// <c>q = probability - 0.5</c>: a central region (<c>|q| &lt;= 0.425</c>), and two tail regions split at
    /// <c>r &lt;= 5</c> for progressively more extreme probabilities, each region using its own fitted numerator/
    /// denominator coefficients evaluated by Horner's method with fused multiply-adds.
    /// </remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static double InverseStandardNormalCdf(this double probability) {
        const double Five = 5.0d;
        const double Half = 0.5d;
        const double One = 1.0d;
        const double Zero = 0.0d;

        // !(>= && <=) rejects NaN (both ordered comparisons are false for NaN) alongside out-of-range values in
        // one branch, so the valid-range path keeps a single comparison.
        if (!((Zero <= probability) && (probability <= One))) {
            throw new ArgumentOutOfRangeException(
                message: "probability must be between the inclusive range [0, 1]",
                paramName: nameof(probability)
            );
        }

        if (Zero == probability) {
            return double.NegativeInfinity;
        }

        if (One == probability) {
            return double.PositiveInfinity;
        }

        var q = (probability - Half);

        double r;
        double v;

        if (Math.Abs(value: q) <= 0.425d) {
            const double A0 = 3.3871328727963666080e+00d;
            const double A1 = 1.3314166789178437745e+02d;
            const double A2 = 1.9715909503065514427e+03d;
            const double A3 = 1.3731693765509461125e+04d;
            const double A4 = 4.5921953931549871457e+04d;
            const double A5 = 6.7265770927008700853e+04d;
            const double A6 = 3.3430575583588128105e+04d;
            const double A7 = 2.5090809287301226727e+03d;
            const double B1 = 4.2313330701600911252e+01d;
            const double B2 = 6.8718700749205790830e+02d;
            const double B3 = 5.3941960214247511077e+03d;
            const double B4 = 2.1213794301586595867e+04d;
            const double B5 = 3.9307895800092710610e+04d;
            const double B6 = 2.8729085735721942674e+04d;
            const double B7 = 5.2264952788528545610e+03d;

            r = Math.FusedMultiplyAdd(
                x: -q,
                y: q,
                z: 0.180625d
            );
            v = (
                (q
                * Math.FusedMultiplyAdd(
                x: Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: A7,
                                        y: r,
                                        z: A6
                                    ),
                                    y: r,
                                    z: A5
                                ),
                                y: r,
                                z: A4
                            ),
                            y: r,
                            z: A3
                        ),
                        y: r,
                        z: A2
                    ),
                    y: r,
                    z: A1
                ),
                y: r,
                z: A0
            ))
                / Math.FusedMultiplyAdd(
                x: Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: B7,
                                        y: r,
                                        z: B6
                                    ),
                                    y: r,
                                    z: B5
                                ),
                                y: r,
                                z: B4
                            ),
                            y: r,
                            z: B3
                        ),
                        y: r,
                        z: B2
                    ),
                    y: r,
                    z: B1
                ),
                y: r,
                z: One
            )
            );
        } else {
            const double C0 = 1.42343711074968357734e+00d;
            const double C1 = 4.63033784615654529590e+00d;
            const double C2 = 5.76949722146069140550e+00d;
            const double C3 = 3.64784832476320460504e+00d;
            const double C4 = 1.27045825245236838258e+00d;
            const double C5 = 2.41780725177450611770e-01d;
            const double C6 = 2.27238449892691845833e-02d;
            const double C7 = 7.74545014278341407640e-04d;
            const double D1 = 2.05319162663775882187e+00d;
            const double D2 = 1.67638483018380384940e+00d;
            const double D3 = 6.89767334985100004550e-01d;
            const double D4 = 1.48103976427480074590e-01d;
            const double D5 = 1.51986665636164571966e-02d;
            const double D6 = 5.47593808499534494600e-04d;
            const double D7 = 1.05075007164441684324e-09d;
            const double E0 = 6.65790464350110377720e+00d;
            const double E1 = 5.46378491116411436990e+00d;
            const double E2 = 1.78482653991729133580e+00d;
            const double E3 = 2.96560571828504891230e-01d;
            const double E4 = 2.65321895265761230930e-02d;
            const double E5 = 1.24266094738807843860e-03d;
            const double E6 = 2.71155556874348757815e-05d;
            const double E7 = 2.01033439929228813265e-07d;
            const double F1 = 5.99832206555887937690e-01d;
            const double F2 = 1.36929880922735805310e-01d;
            const double F3 = 1.48753612908506148525e-02d;
            const double F4 = 7.86869131145613259100e-04d;
            const double F5 = 1.84631831751005468180e-05d;
            const double F6 = 1.42151175831644588870e-07d;
            const double F7 = 2.04426310338993978564e-15d;

            r = ((q < Zero)
                ? probability
                : (One - probability));
            r = Math.Sqrt(d: -Math.Log(d: r));

            if (r <= Five) {
                r -= 1.6d;
                v = (
                    Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: Math.FusedMultiplyAdd(
                                            x: C7,
                                            y: r,
                                            z: C6
                                        ),
                                        y: r,
                                        z: C5
                                    ),
                                    y: r,
                                    z: C4
                                ),
                                y: r,
                                z: C3
                            ),
                            y: r,
                            z: C2
                        ),
                        y: r,
                        z: C1
                    ),
                    y: r,
                    z: C0
                )
                    / Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: Math.FusedMultiplyAdd(
                                            x: D7,
                                            y: r,
                                            z: D6
                                        ),
                                        y: r,
                                        z: D5
                                    ),
                                    y: r,
                                    z: D4
                                ),
                                y: r,
                                z: D3
                            ),
                            y: r,
                            z: D2
                        ),
                        y: r,
                        z: D1
                    ),
                    y: r,
                    z: One
                )
                );
            } else {
                r -= Five;
                v = (
                    Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: Math.FusedMultiplyAdd(
                                            x: E7,
                                            y: r,
                                            z: E6
                                        ),
                                        y: r,
                                        z: E5
                                    ),
                                    y: r,
                                    z: E4
                                ),
                                y: r,
                                z: E3
                            ),
                            y: r,
                            z: E2
                        ),
                        y: r,
                        z: E1
                    ),
                    y: r,
                    z: E0
                )
                    / Math.FusedMultiplyAdd(
                    x: Math.FusedMultiplyAdd(
                        x: Math.FusedMultiplyAdd(
                            x: Math.FusedMultiplyAdd(
                                x: Math.FusedMultiplyAdd(
                                    x: Math.FusedMultiplyAdd(
                                        x: Math.FusedMultiplyAdd(
                                            x: F7,
                                            y: r,
                                            z: F6
                                        ),
                                        y: r,
                                        z: F5
                                    ),
                                    y: r,
                                    z: F4
                                ),
                                y: r,
                                z: F3
                            ),
                            y: r,
                            z: F2
                        ),
                        y: r,
                        z: F1
                    ),
                    y: r,
                    z: One
                )
                );
            }

            v = Math.CopySign(
                x: v,
                y: q
            );
        }

        return v;
    }
    /// <summary>
    /// Returns the inverse cumulative distribution function of a normal distribution.
    /// </summary>
    /// <param name="mean">The mean of the normal distribution. Must be finite.</param>
    /// <param name="probability">The cumulative probability. Values must be in the inclusive range [0, 1].</param>
    /// <param name="standardDeviation">The standard deviation of the normal distribution. Must be finite and strictly positive.</param>
    /// <returns>The value <c>x</c> such that <c>Φ((x - mean) / standardDeviation) = probability</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="probability"/> is NaN, or is less than 0 or greater than 1.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mean"/> is not finite, or <paramref name="standardDeviation"/> is not finite or is not strictly positive.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static double InverseNormalCdf(
        this double probability,
        double mean,
        double standardDeviation
    ) {
        if (!double.IsFinite(d: mean)) {
            throw new ArgumentOutOfRangeException(
                message: "mean must be finite",
                paramName: nameof(mean)
            );
        }

        if (!(double.IsFinite(d: standardDeviation) && (0.0d < standardDeviation))) {
            throw new ArgumentOutOfRangeException(
                message: "standardDeviation must be finite and strictly positive",
                paramName: nameof(standardDeviation)
            );
        }

        return (mean + (standardDeviation * InverseStandardNormalCdf(probability: probability)));
    }
}
