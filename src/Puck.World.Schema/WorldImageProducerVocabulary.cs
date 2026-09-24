using System.Diagnostics.CodeAnalysis;
using System.Text;
using Puck.Abstractions.Sources;
using Puck.Assets.Qr;

namespace Puck.World;

/// <summary>
/// What a document may know of an image producer: its id, content class and transport, and the check its settings
/// object must pass. A host registers one of these beside the producer that opens its sources
/// (<see cref="WorldImageProducerVocabulary.Register"/>), so a document naming the producer validates wherever that
/// host validates documents and is refused by name wherever it does not.
/// </summary>
public abstract class WorldImageProducerShape : IImageSourceProducer {
    /// <inheritdoc/>
    public abstract ImageContentClass Content { get; }
    /// <inheritdoc/>
    public abstract string Id { get; }
    /// <inheritdoc/>
    public abstract ImageSourceTransport Transport { get; }

    /// <summary>Checks a producer source's settings against this producer's shape, adding one line to
    /// <paramref name="errors"/> for each refusal.</summary>
    /// <param name="source">The source naming this producer.</param>
    /// <param name="definition">The document the source appears in.</param>
    /// <param name="path">The source's document path, which every refusal starts with.</param>
    /// <param name="errors">The refusals.</param>
    public abstract void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors);
}
/// <summary>A producer shape whose settings bind to a record (<see cref="WorldImageProducerSettings.Bind{T}"/>): a
/// settings object that does not bind is refused with the serializer's own message, which names the member, and one
/// that binds is checked by <see cref="Check"/>.</summary>
/// <typeparam name="T">The settings record.</typeparam>
public abstract class WorldImageProducerShape<T> : WorldImageProducerShape where T : class {
    /// <summary>Checks bound settings, adding one line to <paramref name="errors"/> for each refusal.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="definition">The document the source appears in.</param>
    /// <param name="path">The settings object's document path.</param>
    /// <param name="errors">The refusals.</param>
    protected abstract void Check(T settings, WorldDefinition definition, string path, List<string> errors);

    /// <inheritdoc/>
    public sealed override void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: errors);

        var (settings, refusal) = WorldImageProducerSettings.Bind<T>(producer: source);

        if (settings is null) {
            errors.Add(item: $"{path}.settings: {refusal}");

            return;
        }

        Check(
            definition: definition,
            errors: errors,
            path: $"{path}.settings",
            settings: settings
        );
    }
}
/// <summary>
/// The image producers a document may name, by id: the four the engine ships (<c>testPattern</c>, <c>qr</c>,
/// <c>camera</c>, <c>capture</c>) and every one a host adds through <see cref="Register"/>. The validator refuses a
/// <see cref="WorldScreenSource.Producer"/> naming an id no shape is registered under, and otherwise refuses what the
/// producer's shape refuses; adding a producer changes neither the document model nor its generated schemas.
/// Registration is process-wide, as the other document vocabularies are, and refuses a second shape under a taken id.
/// </summary>
public static class WorldImageProducerVocabulary {
    private static readonly Lock Gate = new();
    private static readonly ImageSourceProducerRegistry<WorldImageProducerShape> Registry = Shipped();

    /// <summary>Gets the registered shapes in registration order: the shipped four first.</summary>
    public static IReadOnlyList<WorldImageProducerShape> Shapes {
        get {
            lock (Gate) {
                return [.. Registry.Producers];
            }
        }
    }

    private static ImageSourceProducerRegistry<WorldImageProducerShape> Shipped() {
        var registry = new ImageSourceProducerRegistry<WorldImageProducerShape>();

        registry.Register(producer: new TestPatternShape());
        registry.Register(producer: new QrShape());
        registry.Register(producer: new CameraShape());
        registry.Register(producer: new CaptureShape());

        return registry;
    }

    internal static void ValidateProfile(WorldFeedProfile profile, string path, List<string> errors) {
        if (
            (profile.Width <= 0) ||
            (profile.Height <= 0) ||
            (profile.Width > WorldDefinitionValidator.MaxSurfaceDimension) ||
            (profile.Height > WorldDefinitionValidator.MaxSurfaceDimension)
        ) {
            errors.Add(item: $"{path} dimensions must be within 1..{WorldDefinitionValidator.MaxSurfaceDimension}.");
        }

        try {
            _ = Puck.Hosting.EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz);
        } catch (ArgumentException exception) {
            errors.Add(item: $"{path}.refreshRateHz is invalid: {exception.Message}");
        }
    }

    /// <summary>Registers a producer's shape under its id.</summary>
    /// <param name="shape">The shape.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The shape's id is not a producer id or is already registered.</exception>
    public static void Register(WorldImageProducerShape shape) {
        ArgumentNullException.ThrowIfNull(argument: shape);

        lock (Gate) {
            Registry.Register(producer: shape);
        }
    }
    /// <summary>Finds the shape registered under an id.</summary>
    /// <param name="id">The producer id.</param>
    /// <param name="shape">The shape, or <see langword="null"/> when none is registered under <paramref name="id"/>.</param>
    /// <returns><see langword="true"/> when a shape is registered under <paramref name="id"/>.</returns>
    public static bool TryGet(string id, [NotNullWhen(returnValue: true)] out WorldImageProducerShape? shape) {
        ArgumentNullException.ThrowIfNull(argument: id);

        lock (Gate) {
            return Registry.TryGet(
                id: id,
                producer: out shape
            );
        }
    }
    /// <summary>Validates a producer source: a registered id, and settings its producer's shape accepts.</summary>
    /// <param name="source">The source.</param>
    /// <param name="definition">The document the source appears in.</param>
    /// <param name="path">The source's document path.</param>
    /// <param name="errors">The refusals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="definition"/> or
    /// <paramref name="errors"/> is <see langword="null"/>.</exception>
    public static void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: errors);

        if (string.IsNullOrEmpty(value: source.Id)) {
            errors.Add(item: $"{path}.producer.id is required.");

            return;
        }

        if (!TryGet(
            id: source.Id,
            shape: out var shape
        )) {
            errors.Add(item: $"{path}.producer.id '{source.Id}' names no registered image producer.");

            return;
        }

        shape.Validate(
            definition: definition,
            errors: errors,
            path: $"{path}.producer",
            source: source
        );
    }

    private sealed class CameraShape : WorldImageProducerShape<WorldCameraSettings> {
        public override ImageContentClass Content => ImageContentClass.External;
        public override string Id => WorldImageProducerSettings.CameraId;
        public override ImageSourceTransport Transport => ImageSourceTransport.Imported;

        protected override void Check(WorldCameraSettings settings, WorldDefinition definition, string path, List<string> errors) {
            if (settings.Profile is { } profile) {
                ValidateProfile(
                    errors: errors,
                    path: $"{path}.profile",
                    profile: profile
                );
            }

            if (!Enum.IsDefined(value: settings.Sensor)) {
                errors.Add(item: $"{path}.sensor '{settings.Sensor}' is not recognized.");
            }

            if (
                (settings.Seat is { } seat) &&
                ((seat < 1) || (seat > definition.Population.LocalSeats))
            ) {
                errors.Add(item: $"{path}.seat {seat} is outside 1..{definition.Population.LocalSeats} (population.localSeats).");
            }

            if (settings.Controls?.Vendor is not { } vendorControls) {
                return;
            }

            for (var index = 0; (index < vendorControls.Count); index++) {
                var control = vendorControls[index];

                if (control is null) {
                    errors.Add(item: $"{path}.controls.vendor[{index}] is required.");

                    continue;
                }

                if (
                    (control.Id < byte.MinValue) ||
                    (control.Id > byte.MaxValue)
                ) {
                    errors.Add(item: $"{path}.controls.vendor[{index}].id {control.Id} is outside 0..255.");
                }

                if (
                    (control.Value < byte.MinValue) ||
                    (control.Value > byte.MaxValue)
                ) {
                    errors.Add(item: $"{path}.controls.vendor[{index}].value {control.Value} is outside 0..255.");
                }
            }
        }
    }
    private sealed class CaptureShape : WorldImageProducerShape<WorldCaptureSettings> {
        public override ImageContentClass Content => ImageContentClass.External;
        public override string Id => WorldImageProducerSettings.CaptureId;
        public override ImageSourceTransport Transport => ImageSourceTransport.Imported;

        protected override void Check(WorldCaptureSettings settings, WorldDefinition definition, string path, List<string> errors) {
            if (settings.MonitorIndex is { } monitorIndex) {
                if (monitorIndex < 0) {
                    errors.Add(item: $"{path}.monitorIndex must be non-negative.");
                }
            } else if (string.IsNullOrWhiteSpace(value: settings.WindowTitle)) {
                errors.Add(item: $"{path}.windowTitle is required when no monitorIndex is given.");
            }

            ValidateProfile(
                errors: errors,
                path: $"{path}.profile",
                profile: settings.Profile
            );
        }
    }
    private sealed class QrShape : WorldImageProducerShape<WorldQrSettings> {
        public override ImageContentClass Content => ImageContentClass.Deterministic;
        public override string Id => WorldImageProducerSettings.QrId;
        public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        // The capacity question is asked of the encoder (QrEncoder.TryFindVersion) rather than re-derived here, so a
        // document refusal names the same byte count and capacity a live screen.source <index> qr refusal names.
        protected override void Check(WorldQrSettings settings, WorldDefinition definition, string path, List<string> errors) {
            if (string.IsNullOrEmpty(value: settings.Payload)) {
                errors.Add(item: $"{path}.payload is required.");

                return;
            }

            if (!QrErrorCorrection.TryParse(
                level: out var level,
                text: settings.EcLevel
            )) {
                errors.Add(item: $"{path}.ecLevel '{settings.EcLevel}' must be one of {QrErrorCorrection.Vocabulary}.");

                return;
            }

            if (settings.QuietZoneModules < 0) {
                errors.Add(item: $"{path}.quietZoneModules {settings.QuietZoneModules} must be non-negative.");
            }

            if (!QrEncoder.TryFindVersion(
                error: out var capacityError,
                level: level,
                payloadByteCount: Encoding.UTF8.GetByteCount(s: settings.Payload),
                version: out _
            )) {
                errors.Add(item: $"{path}.payload: {capacityError}.");
            }
        }
    }
    private sealed class TestPatternShape : WorldImageProducerShape<WorldTestPatternSettings> {
        public override ImageContentClass Content => ImageContentClass.Deterministic;
        public override string Id => WorldImageProducerSettings.TestPatternId;
        public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        protected override void Check(WorldTestPatternSettings settings, WorldDefinition definition, string path, List<string> errors) {
            if (
                (settings.Width <= 0) ||
                (settings.Height <= 0) ||
                (settings.Width > WorldDefinitionValidator.MaxSurfaceDimension) ||
                (settings.Height > WorldDefinitionValidator.MaxSurfaceDimension)
            ) {
                errors.Add(item: $"{path} dimensions must be within 1..{WorldDefinitionValidator.MaxSurfaceDimension}.");
            }
        }
    }
}
