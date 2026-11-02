namespace Puck.Physics;

/// <summary>The collider span an uncertain pair's collider index counts in.</summary>
public enum ContactUncertaintySpan : byte {
    /// <summary>The colliders compiled once (a static solver's first span).</summary>
    Static = 0,
    /// <summary>The colliders a caller recomputes every tick (a static solver's second span).</summary>
    Dynamic = 1,
    /// <summary>A world-wide contact field, which is one collider.</summary>
    Field = 2,
}
/// <summary>One body volume and collider a contact solve proved neither clear of nor in contact with.</summary>
/// <param name="Span">The collider span <paramref name="Collider"/> counts in.</param>
/// <param name="Collider">The collider's index in its span; zero for a world-wide field.</param>
/// <param name="Volume">The volume's index in the body's volumes.</param>
public readonly record struct ContactUncertainPair(ContactUncertaintySpan Span, int Collider, int Volume);
/// <summary>Which volume and collider pairs a contact resolution left uncertain: unproved, or embedded with no measured
/// way out (<see cref="FixedFieldContact.Uncertain"/>). Holds up to four pairs by name; past them, or where the solve
/// cannot name what it left unproved (an exhausted sweep budget, a pair resolved through another authority), it is
/// <see cref="Unnamed"/>, which no other uncertainty is ever proved within.</summary>
public readonly record struct ContactUncertainty {
    private ContactUncertainPair First { get; init; }
    private ContactUncertainPair Second { get; init; }
    private ContactUncertainPair Third { get; init; }
    private ContactUncertainPair Fourth { get; init; }

    /// <summary>Gets the number of pairs held by name.</summary>
    public int Count { get; private init; }
    /// <summary>Gets whether the resolution left uncertainty it does not name.</summary>
    public bool Unnamed { get; private init; }
    /// <summary>Gets whether nothing was left uncertain.</summary>
    public bool IsEmpty => ((Count == 0) && !Unnamed);
    /// <summary>Gets uncertainty the resolution cannot name.</summary>
    public static ContactUncertainty Unknown { get; } = new() { Unnamed = true };

    /// <summary>Returns this uncertainty with one more pair; a pair already held changes nothing.</summary>
    /// <param name="pair">The pair left uncertain.</param>
    /// <returns>The uncertainty holding <paramref name="pair"/>, or <see cref="Unnamed"/> past four pairs.</returns>
    public ContactUncertainty With(ContactUncertainPair pair) {
        if (Holds(pair: pair)) {
            return this;
        }

        return Count switch {
            0 => this with { First = pair, Count = 1 },
            1 => this with { Second = pair, Count = 2 },
            2 => this with { Third = pair, Count = 3 },
            3 => this with { Fourth = pair, Count = 4 },
            _ => this with { Unnamed = true },
        };
    }
    /// <summary>Returns the union of two uncertainties.</summary>
    /// <param name="other">The other uncertainty.</param>
    /// <returns>Every pair of both, unnamed when either is.</returns>
    public ContactUncertainty Union(ContactUncertainty other) {
        var union = (other.Unnamed ? (this with { Unnamed = true }) : this);

        for (var index = 0; (index < other.Count); index++) {
            union = union.With(pair: other.At(index: index));
        }

        return union;
    }
    /// <summary>Returns whether every pair of this uncertainty is also one of <paramref name="earlier"/>'s: a pose
    /// reached from the earlier one is no more uncertain than it was.</summary>
    /// <param name="earlier">The uncertainty of the pose moved from.</param>
    /// <returns><see langword="true"/> when this is empty, or names only pairs <paramref name="earlier"/> names;
    /// never when this is <see cref="Unnamed"/>.</returns>
    public bool IsWithin(ContactUncertainty earlier) {
        if (Unnamed) {
            return false;
        }

        for (var index = 0; (index < Count); index++) {
            if (!earlier.Holds(pair: At(index: index))) {
                return false;
            }
        }

        return true;
    }

    private bool Holds(ContactUncertainPair pair) {
        for (var index = 0; (index < Count); index++) {
            if (At(index: index) == pair) {
                return true;
            }
        }

        return false;
    }
    private ContactUncertainPair At(int index) => index switch {
        0 => First,
        1 => Second,
        2 => Third,
        _ => Fourth,
    };
}
