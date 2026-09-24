using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions.Sources;

/// <summary>A producer of images that enter rendering from outside a pass, registered with its host under a stable id.
/// A host's own producer contract extends this one with how it opens a source; what every consumer reads about a
/// producer is here.</summary>
public interface IImageSourceProducer {
    /// <summary>Gets what the producer's pixels are a function of.</summary>
    ImageContentClass Content { get; }
    /// <summary>Gets the producer's registered id: lower camel case, unique within its registry.</summary>
    string Id { get; }
    /// <summary>Gets how the producer's images arrive.</summary>
    ImageSourceTransport Transport { get; }
}
/// <summary>
/// The producers a host has registered, by id. Adding an emulator, a capture API or a video decoder is one
/// <see cref="Register"/> call: nothing that names a source by id changes. A registry refuses a second producer under
/// an id already taken, so two producers never answer one name.
/// </summary>
/// <typeparam name="TProducer">The host's producer contract.</typeparam>
public sealed class ImageSourceProducerRegistry<TProducer> where TProducer : class, IImageSourceProducer {
    private readonly List<TProducer> m_ordered = [];
    private readonly Dictionary<string, TProducer> m_producers = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the registered producers in registration order.</summary>
    public IReadOnlyList<TProducer> Producers => m_ordered;

    /// <summary>Returns whether a producer id is spelled as an id: one or more ASCII letters and digits, starting with a
    /// lower-case letter.</summary>
    /// <param name="id">The candidate id.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> is a valid id.</returns>
    public static bool IsValidId([NotNullWhen(returnValue: true)] string? id) {
        if (
            string.IsNullOrEmpty(value: id) ||
            !char.IsAsciiLetterLower(c: id[0])
        ) {
            return false;
        }

        foreach (var character in id) {
            if (!char.IsAsciiLetterOrDigit(c: character)) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Registers a producer under its id.</summary>
    /// <param name="producer">The producer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="producer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The producer's id is not a valid id, its content class or transport is not
    /// defined, or another producer is registered under the same id.</exception>
    public void Register(TProducer producer) {
        ArgumentNullException.ThrowIfNull(argument: producer);

        if (!IsValidId(id: producer.Id)) {
            throw new ArgumentException(
                message: $"'{producer.Id}' is not a producer id: one or more ASCII letters and digits, starting with a lower-case letter.",
                paramName: nameof(producer)
            );
        }

        if (
            !Enum.IsDefined(value: producer.Content) ||
            !Enum.IsDefined(value: producer.Transport)
        ) {
            throw new ArgumentException(
                message: $"Producer '{producer.Id}' declares content class {producer.Content} and transport {producer.Transport}; both must be defined.",
                paramName: nameof(producer)
            );
        }

        if (!m_producers.TryAdd(
            key: producer.Id,
            value: producer
        )) {
            throw new ArgumentException(
                message: $"A producer is already registered under '{producer.Id}'.",
                paramName: nameof(producer)
            );
        }

        m_ordered.Add(item: producer);
    }
    /// <summary>Finds the producer registered under an id.</summary>
    /// <param name="id">The producer id.</param>
    /// <param name="producer">The producer, or <see langword="null"/> when none is registered under
    /// <paramref name="id"/>.</param>
    /// <returns><see langword="true"/> when a producer is registered under <paramref name="id"/>.</returns>
    public bool TryGet(string id, [NotNullWhen(returnValue: true)] out TProducer? producer) {
        ArgumentNullException.ThrowIfNull(argument: id);

        return m_producers.TryGetValue(
            key: id,
            value: out producer
        );
    }
}
