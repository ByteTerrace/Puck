global using static Puck.Maths.Tests.Refusals;

using Xunit;

namespace Puck.Maths.Tests;

/// <summary>The refusal probes claim bodies share. Each runs one call and reports whether, and how, it refused, so a
/// refusal ladder states the diagnosis it promises — the exception type and the parameter it names — rather than
/// merely that something went wrong.</summary>
internal static class Refusals {
    /// <summary>Describes how a call failed to refuse as documented.</summary>
    /// <param name="action">The call.</param>
    /// <param name="type">The exception type the call must throw, or a base of it.</param>
    /// <param name="parameterName">The parameter the argument exception must name, or <see langword="null"/> when
    /// any throw of <paramref name="type"/> satisfies.</param>
    /// <param name="what">The call's description, quoted in the counterexample.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the call refused as documented.</returns>
    public static string? Refuses(Action action, Type type, string? parameterName, string what) {
        try {
            action();
        } catch (Exception thrown) when (type.IsInstanceOfType(o: thrown)) {
            if (parameterName is null) { return null; }

            return (((thrown is ArgumentException argument) && (argument.ParamName == parameterName))
                ? null
                : $"{what} threw {thrown.GetType().Name} naming '{(thrown as ArgumentException)?.ParamName}' rather than '{parameterName}'"
            );
        } catch (Exception thrown) {
            return $"{what} threw {thrown.GetType().Name} rather than {type.Name}";
        }

        return $"{what} did not throw at all";
    }
    /// <summary>Asserts a checked call either overflows or answers the expected value, as the exact arithmetic says it
    /// must.</summary>
    /// <typeparam name="T">The call's result type.</typeparam>
    /// <param name="action">The call.</param>
    /// <param name="overflows">Whether the exact result leaves the carrier.</param>
    /// <param name="expected">The expected result, evaluated only when the call must answer.</param>
    public static void ExpectValueOrOverflow<T>(Func<T> action, bool overflows, Func<T> expected) {
        if (overflows) {
            _ = Assert.Throws<OverflowException>(testCode: () => action());
        } else {
            Assert.Equal(
                actual: action(),
                expected: expected()
            );
        }
    }
    /// <summary>Reads the message an argument refusal carried.</summary>
    /// <param name="action">The call.</param>
    /// <returns>The message, or <see langword="null"/> when the call did not refuse.</returns>
    public static string? RefusedMessage(Action action) {
        try {
            action();
        } catch (ArgumentException exception) {
            return exception.Message;
        }

        return null;
    }
    /// <summary>Reads the parameter an argument refusal named.</summary>
    /// <param name="action">The call.</param>
    /// <returns>The parameter name, the empty string when the refusal named none, or <see langword="null"/> when the
    /// call did not refuse.</returns>
    public static string? RefusedParameter(Action action) {
        try {
            action();
        } catch (ArgumentException exception) {
            return (exception.ParamName ?? string.Empty);
        }

        return null;
    }
    /// <summary>Reports whether a call refused with <typeparamref name="TException"/> or a type derived from it.</summary>
    /// <typeparam name="TException">The exception type.</typeparam>
    /// <param name="action">The call.</param>
    /// <returns><see langword="true"/> when the call threw it.</returns>
    public static bool Throws<TException>(Action action)
        where TException : Exception {
        try {
            action();
        } catch (TException) {
            return true;
        }

        return false;
    }
    /// <summary>Reports whether a call refused with <typeparamref name="TException"/>, or a type derived from it, AND
    /// named the expected parameter.</summary>
    /// <typeparam name="TException">The argument exception type.</typeparam>
    /// <param name="action">The call.</param>
    /// <param name="paramName">The parameter the refusal must name.</param>
    /// <returns><see langword="true"/> when the call refused as described.</returns>
    public static bool Throws<TException>(Action action, string paramName)
        where TException : ArgumentException {
        try {
            action();
        } catch (TException exception) {
            return (exception.ParamName == paramName);
        }

        return false;
    }
    /// <summary>Reports whether a call refused with EXACTLY <typeparamref name="TException"/> and named the expected
    /// parameter. A ladder that has to tell <see cref="ArgumentOutOfRangeException"/> apart from its
    /// <see cref="ArgumentException"/> base cannot do it with a catch clause.</summary>
    /// <typeparam name="TException">The argument exception type.</typeparam>
    /// <param name="action">The call.</param>
    /// <param name="paramName">The parameter the refusal must name.</param>
    /// <returns><see langword="true"/> when the call refused as described.</returns>
    public static bool ThrowsExactly<TException>(Action action, string paramName)
        where TException : ArgumentException {
        try {
            action();
        } catch (TException exception) {
            return (
                (exception.GetType() == typeof(TException)) &&
                (exception.ParamName == paramName)
            );
        }

        return false;
    }
}
