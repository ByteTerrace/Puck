using System.Text;
using Puck.Hosting;
using Puck.Assets.Qr;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A non-empty kebab-case token: lowercase ASCII letters/digits, single hyphens between them, no leading/trailing
    // hyphen. The channel/link-name grammar.
    private static bool IsKebabCase(string value) {
        if (
            string.IsNullOrEmpty(value: value) ||
            (value[index: 0] == '-') ||
            (value[index: (value.Length - 1)] == '-')
        ) {
            return false;
        }

        var previousHyphen = false;

        foreach (var character in value) {
            var isLower = ((character >= 'a') && (character <= 'z'));
            var isDigit = ((character >= '0') && (character <= '9'));

            if (character == '-') {
                if (previousHyphen) {
                    return false;
                }

                previousHyphen = true;
            } else if (
                isLower ||
                isDigit
            ) {
                previousHyphen = false;
            } else {
                return false;
            }
        }

        return true;
    }
    // A world-event channel name, when present: non-empty kebab-case (lowercase, digits, single hyphens).
    private static void ValidateChannel(string? channel, string name, List<string> errors) {
        if (
            (channel is not null) &&
            !IsKebabCase(value: channel)
        ) {
            errors.Add(item: $"{name} '{channel}' must be non-empty kebab-case.");
        }
    }
    // The cable links: name required/kebab/unique; two or more screens; every index declared; no duplicate within a link;
    // no screen in two links. NOT validated: engine identity of the members — that is a RUNTIME fact (a screen.insert
    // changes it), so the binder reports a dormant link with a reason rather than the validator rejecting the row.
    private static void ValidateLinks(IReadOnlyList<WorldScreenLink> links, HashSet<int> screenIndices, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var claimed = new HashSet<int>();

        for (var index = 0; (index < links.Count); index++) {
            var link = links[index];
            var path = $"links[{index}]";

            if (link is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (
                string.IsNullOrWhiteSpace(value: link.Name) ||
                !IsKebabCase(value: link.Name)
            ) {
                errors.Add(item: $"{path}.name '{link.Name}' must be non-empty kebab-case.");
            } else if (!names.Add(item: link.Name)) {
                errors.Add(item: $"{path}.name '{link.Name}' is duplicated.");
            }

            if (
                (link.Screens is null) ||
                (link.Screens.Count < 2)
            ) {
                errors.Add(item: $"{path}.screens requires two or more screen indices.");

                continue;
            }

            var withinLink = new HashSet<int>();

            foreach (var screen in link.Screens) {
                if (!screenIndices.Contains(item: screen)) {
                    errors.Add(item: $"{path}.screens names undeclared screen {screen}.");
                } else if (!withinLink.Add(item: screen)) {
                    errors.Add(item: $"{path}.screens names screen {screen} twice.");
                } else if (!claimed.Add(item: screen)) {
                    errors.Add(item: $"{path}.screens: screen {screen} is already in another link.");
                }
            }
        }
    }
    // The per-screen magazine: at least one entry, a selected index in range, and each entry crossing the SAME source
    // gate as a declared source.
    private static void ValidateMagazine(WorldDefinition definition, WorldScreenMagazine? magazine, string path, HashSet<string> cameras, HashSet<string> destinationNames, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
        if (magazine is not { } value) {
            return;
        }

        if (
            (value.Entries is null) ||
            (value.Entries.Count == 0)
        ) {
            errors.Add(item: $"{path}.entries requires at least one entry.");

            return;
        }

        if (
            (value.Selected < 0) ||
            (value.Selected >= value.Entries.Count)
        ) {
            errors.Add(item: $"{path}.selected {value.Selected} is outside 0..{(value.Entries.Count - 1)}.");
        }

        for (var index = 0; (index < value.Entries.Count); index++) {
            _ = ValidateScreenSource(
                definition: definition,
                source: value.Entries[index],
                path: $"{path}.entries[{index}]",
                cameras: cameras,
                destinationNames: destinationNames,
                fontNames: fontNames,
                hasTextCatalog: hasTextCatalog,
                errors: errors
            );
        }
    }
    private static void ValidateProfile(WorldFeedProfile profile, string path, List<string> errors) {
        if (
            (profile.Width <= 0) ||
            (profile.Height <= 0) ||
            (profile.Width > MaxSurfaceDimension) ||
            (profile.Height > MaxSurfaceDimension)
        ) {
            errors.Add(item: $"{path} dimensions must be within 1..{MaxSurfaceDimension}.");
        }

        try {
            _ = EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz);
        } catch (ArgumentException exception) {
            errors.Add(item: $"{path}.refreshRateHz is invalid: {exception.Message}");
        }
    }
    // The QR source gate: a non-empty payload that FITS the encoder's supported version range at the requested level, a
    // recognized EC-level letter, and a non-negative quiet zone. The capacity question is asked of the ENCODER
    // (QrEncoder.TryFindVersion) rather than re-derived here, so an authoring-time refusal names the identical byte
    // count and capacity a live screen.source <index> qr refusal names — one arithmetic, one message, no drift.
    private static void ValidateQr(WorldScreenSource.Qr qr, string path, List<string> errors) {
        if (string.IsNullOrEmpty(value: qr.Payload)) {
            errors.Add(item: $"{path}.qr.payload is required.");

            return;
        }

        if (!QrErrorCorrection.TryParse(
            text: qr.EcLevel,
            level: out var level
        )) {
            errors.Add(item: $"{path}.qr.ecLevel '{qr.EcLevel}' must be one of {QrErrorCorrection.Vocabulary}.");

            return;
        }

        if (qr.QuietZoneModules < 0) {
            errors.Add(item: $"{path}.qr.quietZoneModules {qr.QuietZoneModules} must be non-negative.");
        }

        if (!QrEncoder.TryFindVersion(
            payloadByteCount: Encoding.UTF8.GetByteCount(s: qr.Payload),
            level: level,
            version: out _,
            error: out var capacityError
        )) {
            errors.Add(item: $"{path}.qr.payload: {capacityError}.");
        }
    }

    // The references section: null names nothing. Each row's Name already crossed WorldSafeName at JSON parse, so
    // this pass owns only uniqueness within the section and a non-empty Document — no boot-time file-existence
    // check (resolving a reference's Document is a future consumer's job). Returns the validated name set so a
    // later pass (a placement face's portal facet) can refuse an undeclared destination by name.
    /// <summary>The reserved prefix a <see cref="WorldReference.Document"/> must never begin with — the owner-named
    /// arm's own <see cref="WorldReference.NeighbourKey"/> spelling, so a document string authored to collide with it
    /// can never be handed to the local file resolver in place of a signature-checked one.</summary>
    private const string OwnerNeighbourKeyPrefix = "owner/";

    // Camera motion, aim, lens, and tracks are presentation-only authoring state.
    private static void ValidateRig(WorldCameraRig rig, string path, List<string> errors) {
        if (rig is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (
            (rig.Lens is null) ||
            !float.IsFinite(f: rig.Lens.FieldOfViewRadians) ||
            (rig.Lens.FieldOfViewRadians <= 0f) ||
            (rig.Lens.FieldOfViewRadians >= MathF.PI)
        ) {
            errors.Add(item: $"{path}.lens.fieldOfViewRadians must be finite and between 0 and pi.");
        }

        if (
            !float.IsFinite(f: rig.SmoothRate) ||
            (rig.SmoothRate < 0f)
        ) {
            errors.Add(item: $"{path}.smoothRate must be finite and non-negative.");
        }

        switch (rig.Motion) {
            case WorldCameraMotion.Fly fly:
                if (
                    !float.IsFinite(f: fly.MinSpeed) ||
                    !float.IsFinite(f: fly.MaxSpeed) ||
                    (fly.MinSpeed <= 0f) ||
                    (fly.MaxSpeed < fly.MinSpeed)
                ) {
                    errors.Add(item: $"{path}.motion needs a positive, finite minSpeed and a maxSpeed no smaller than it.");
                }

                if (
                    !float.IsFinite(f: fly.DefaultSpeed) ||
                    (fly.DefaultSpeed < fly.MinSpeed) ||
                    (fly.DefaultSpeed > fly.MaxSpeed)
                ) {
                    errors.Add(item: $"{path}.motion.defaultSpeed must be finite and within [minSpeed, maxSpeed].");
                }

                if (
                    !float.IsFinite(f: fly.LookRateRadiansPerSecond) ||
                    (fly.LookRateRadiansPerSecond <= 0f)
                ) {
                    errors.Add(item: $"{path}.motion.lookRateRadiansPerSecond must be positive and finite.");
                }

                if (
                    !float.IsFinite(f: fly.MaxPitchRadians) ||
                    (fly.MaxPitchRadians <= 0f) ||
                    (fly.MaxPitchRadians >= (MathF.PI / 2f))
                ) {
                    errors.Add(item: $"{path}.motion.maxPitchRadians must be finite and within (0, pi/2).");
                }

                break;
            case WorldCameraMotion.Follow follow:
                if (!IsFinite(value: follow.Offset)) {
                    errors.Add(item: $"{path}.motion.offset must contain finite coordinates.");
                }

                if (!float.IsFinite(f: follow.SpreadPullback)) {
                    errors.Add(item: $"{path}.motion.spreadPullback must be finite.");
                }

                break;
            case WorldCameraMotion.Orbit orbit:
                if (
                    !float.IsFinite(f: orbit.Distance) ||
                    (orbit.Distance <= 0f)
                ) {
                    errors.Add(item: $"{path}.motion.distance must be positive and finite.");
                }

                if (
                    !float.IsFinite(f: orbit.Yaw) ||
                    !float.IsFinite(f: orbit.Pitch) ||
                    !IsFinite(value: orbit.PivotOffset)
                ) {
                    errors.Add(item: $"{path}.motion needs a finite yaw, pitch, and pivot offset.");
                }

                break;
            case WorldCameraMotion.Static value:
                if (!IsFinite(value: value.Position)) {
                    errors.Add(item: $"{path}.motion.position must contain finite coordinates.");
                }

                break;
            case WorldCameraMotion.Track track:
                ValidateTrack(
                    errors: errors,
                    path: $"{path}.motion",
                    track: track
                );
                break;
            default:
                errors.Add(item: $"{path}.motion is an unknown camera motion kind.");

                break;
        }

        switch (rig.Aim) {
            case WorldCameraAim.Anchor anchor:
                if (!IsFinite(value: anchor.Offset)) {
                    errors.Add(item: $"{path}.aim.offset must contain finite coordinates.");
                }

                break;
            case WorldCameraAim.Forward forward:
                if (
                    !float.IsFinite(f: forward.FocusDistance) ||
                    (forward.FocusDistance < 0f)
                ) {
                    errors.Add(item: $"{path}.aim.focusDistance must be finite and non-negative.");
                }

                break;
            case WorldCameraAim.WorldPoint worldPoint:
                if (!IsFinite(value: worldPoint.Target)) {
                    errors.Add(item: $"{path}.aim.target must contain finite coordinates.");
                }

                break;
            default:
                errors.Add(item: $"{path}.aim is an unknown camera aim kind.");

                break;
        }
    }
    // The engage-route policy: a finite non-negative radius, plus authored channel names (kebab-case, non-empty),
    // plus the context-routes widening's two route-row fields: the channel MASK (channelNames must resolve) and the
    // authored TRANSLATION table (each row's channel must resolve to a defined WorldPadElement). engageChannel is
    // CONSUMED (WorldServer.ResolveEngageProbes resolves it against the same declared-channel ordinal table), so it
    // is held to the same "must resolve" bar — a misspelled name is otherwise a silent, permanent no-op. cycleChannel
    // stays unconsumed (no reader exists yet) and keeps its lighter kebab-case-only bar.
    private static void ValidateRoute(WorldScreenRoute route, string path, ISet<string> channelNames, List<string> errors) {
        if (
            !float.IsFinite(f: route.EngageRadius) ||
            (route.EngageRadius < 0f)
        ) {
            errors.Add(item: $"{path}.engageRadius {route.EngageRadius} must be finite and non-negative.");
        }

        ValidateChannel(
            channel: route.EngageChannel,
            name: $"{path}.engageChannel",
            errors: errors
        );
        ValidateChannel(
            channel: route.CycleChannel,
            name: $"{path}.cycleChannel",
            errors: errors
        );

        if (
            (route.EngageChannel is { Length: > 0 } engageChannel) &&
            !channelNames.Contains(item: engageChannel)
        ) {
            errors.Add(item: $"{path}.engageChannel '{engageChannel}' names no declared channel.");
        }

        if (
            !route.Engageable &&
            ((route.EngageChannel is not null) || (route.CycleChannel is not null))
        ) {
            errors.Add(item: $"{path} names an engageChannel/cycleChannel but engageable is false — a screen cannot answer a gesture it can never be engaged from.");
        }

        if (route.Channels is { } mask) {
            if (mask.Count == 0) {
                errors.Add(item: $"{path}.channels omit the field for 'reach everything' instead of an empty list — an authored empty mask reaches nothing by accident.");
            }

            for (var index = 0; (index < mask.Count); index++) {
                if (!channelNames.Contains(item: mask[index])) {
                    errors.Add(item: $"{path}.channels[{index}] '{mask[index]}' names no declared channel.");
                }
            }
        }

        if (route.Translation is { } translation) {
            for (var index = 0; (index < translation.Count); index++) {
                var row = translation[index];

                if (!channelNames.Contains(item: row.Channel)) {
                    errors.Add(item: $"{path}.translation[{index}].channel '{row.Channel}' names no declared channel.");
                }

                if (!Enum.IsDefined(value: row.Element)) {
                    errors.Add(item: $"{path}.translation[{index}].element '{row.Element}' is not a defined WorldPadElement.");
                }
            }
        }
    }
    // The one screen-source gate, shared by a declared source and every magazine entry — a pure extraction that closes a
    // real duplication risk (a magazine entry could otherwise name an undeclared camera). Returns whether the source is a
    // live CONSOLE (the caller counts these against the one-live ceiling).
    private static bool ValidateScreenSource(WorldDefinition definition, WorldScreenSource source, string path, HashSet<string> cameras, HashSet<string> destinationNames, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
        switch (source) {
            case null:
                errors.Add(item: $"{path} is required.");

                return false;
            case WorldScreenSource.Machine machine:
                if (string.IsNullOrWhiteSpace(value: machine.Engine)) {
                    errors.Add(item: $"{path}.machine.engine is required.");
                } else if (!WorldExtensionVocabularyHook.IsRegisteredScreenMachineEngine(engineId: machine.Engine)) {
                    // Deny-by-default: an engine key the host never registered refuses HERE, at load, by name — not a
                    // per-slot boot fault discovered only once WorldMachineHost tries to resolve it (screen.state
                    // reported the fault, but boot itself succeeded regardless). The hook is REQUIRED, never skipped
                    // when absent: an unchecked key is the one outcome this refusal exists to prevent.
                    errors.Add(item: $"{path}.machine.engine '{machine.Engine}' names no registered screen-machine engine.");
                }

                // An empty contentPath is a valid "unconfigured" screen; the binder faults the slot gracefully at boot.
                // A present-but-missing file is a runtime fact, not a structural authoring error.
                return false;
            case WorldScreenSource.TestPattern pattern:
                if (
                    (pattern.Width <= 0) ||
                    (pattern.Height <= 0) ||
                    (pattern.Width > MaxSurfaceDimension) ||
                    (pattern.Height > MaxSurfaceDimension)
                ) {
                    errors.Add(item: $"{path} test-pattern dimensions must be within 1..{MaxSurfaceDimension}.");
                }

                return false;
            case WorldScreenSource.Camera camera:
                ValidateProfile(
                    profile: camera.Profile,
                    path: $"{path}.camera",
                    errors: errors
                );

                return false;
            case WorldScreenSource.Capture capture:
                // Selector: monitor mode validates the index; window mode requires a title (its unused counterpart).
                if (capture.MonitorIndex is { } monitorIndex) {
                    if (monitorIndex < 0) {
                        errors.Add(item: $"{path}.capture.monitorIndex must be non-negative.");
                    }
                } else if (string.IsNullOrWhiteSpace(value: capture.WindowTitle)) {
                    errors.Add(item: $"{path}.capture.windowTitle is required.");
                }

                ValidateProfile(
                    profile: capture.Profile,
                    path: $"{path}.capture",
                    errors: errors
                );

                return false;
            case WorldScreenSource.View view:
                if (!cameras.Contains(item: view.CameraName)) {
                    errors.Add(item: $"{path}.view references undeclared camera '{view.CameraName}'.");
                }

                return false;
            case WorldScreenSource.Console console:
                if (
                    (console.Rows < 1) ||
                    (console.Rows > 120)
                ) {
                    errors.Add(item: $"{path}.console.rows {console.Rows} is outside 1..120.");
                }

                if (
                    (console.Columns < 1) ||
                    (console.Columns > 400)
                ) {
                    errors.Add(item: $"{path}.console.columns {console.Columns} is outside 1..400.");
                }

                return true;
            case WorldScreenSource.Qr qr:
                ValidateQr(
                    errors: errors,
                    path: path,
                    qr: qr
                );

                return false;
            case WorldScreenSource.Session session:
                // No placement face reaches here (a top-level screens row or magazine entry) — portal:null makes
                // ValidateSessionSource refuse Window unconditionally, which is correct: there is no face for a
                // portal facet to sit on, so no counterpart can ever pair with a window authored at this position.
                ValidateSessionSource(
                    destinationNames: destinationNames,
                    errors: errors,
                    path: path,
                    portal: null,
                    session: session
                );

                return false;
            case WorldScreenSource.Text text:
                ValidateTextSource(
                    definition: definition,
                    errors: errors,
                    fontNames: fontNames,
                    hasTextCatalog: hasTextCatalog,
                    path: path,
                    text: text
                );

                return false;
            default:
                return false;
        }
    }
    private static void ValidateSeatControl(WorldSeatViewControl control, string path, List<string> errors) {
        if (control is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }
        if (!Enum.IsDefined(value: control.YawReference)) {
            errors.Add(item: $"{path}.yawReference is unknown.");
        }
        if (
            !float.IsFinite(f: control.MinPitch) ||
            !float.IsFinite(f: control.MaxPitch) ||
            (control.MinPitch < (-MathF.PI / 2f)) ||
            (control.MaxPitch > (MathF.PI / 2f))
        ) {
            errors.Add(item: $"{path}.minPitch and {path}.maxPitch must be finite and within [-pi/2, pi/2].");
        } else if (control.MinPitch >= control.MaxPitch) {
            errors.Add(item: $"{path}.minPitch must be less than {path}.maxPitch.");
        }
        if (
            (control.SwapRate is { } swapRate) &&
            (!float.IsFinite(f: swapRate) || (swapRate < 0f))
        ) {
            errors.Add(item: $"{path}.swapRate must be finite and non-negative — 0 is an instant swap.");
        }
        if (control.Follow is { } follow) {
            if (
                !float.IsFinite(f: follow.Rate) ||
                (follow.Rate <= 0f)
            ) {
                errors.Add(item: $"{path}.follow.rate must be finite and positive.");
            }
            if (control.YawReference != WorldSeatYawReference.World) {
                errors.Add(item: $"{path}.follow needs {path}.yawReference 'World' — a body-relative yaw already rides the body.");
            }
        }
    }
    // A seat's control feel (PRESENTATION-ONLY, REQUIRED): pointer sensitivities, stick look rate, and gyro response
    // finite and non-negative. The member itself is required — an absent row is refused by the caller before this
    // runs, never silently defaulted.
    private static void ValidateSeatLook(WorldSeatLook seatLook, string path, List<string> errors) {
        if (
            !float.IsFinite(f: seatLook.YawSensitivity) ||
            (seatLook.YawSensitivity < 0f)
        ) {
            errors.Add(item: $"{path}.yawSensitivity must be finite and non-negative.");
        }

        if (
            !float.IsFinite(f: seatLook.PitchSensitivity) ||
            (seatLook.PitchSensitivity < 0f)
        ) {
            errors.Add(item: $"{path}.pitchSensitivity must be finite and non-negative.");
        }

        if (
            !float.IsFinite(f: seatLook.StickLookRate) ||
            (seatLook.StickLookRate < 0f)
        ) {
            errors.Add(item: $"{path}.stickLookRate must be finite and non-negative.");
        }

        var gyro = seatLook.Gyro;

        if (
            !float.IsFinite(f: gyro.Scale) ||
            (gyro.Scale < 0f)
        ) {
            errors.Add(item: $"{path}.gyro.scale must be finite and non-negative.");
        }
        if (
            !IsFinite(value: gyro.DeadZone) ||
            (gyro.DeadZone.X < 0f) ||
            (gyro.DeadZone.Y < 0f) ||
            (gyro.DeadZone.Z < 0f)
        ) {
            errors.Add(item: $"{path}.gyro.deadZone components must be finite and non-negative.");
        }
        if (!IsFinite(value: gyro.Yaw)) {
            errors.Add(item: $"{path}.gyro.yaw components must be finite.");
        }
        if (!IsFinite(value: gyro.Pitch)) {
            errors.Add(item: $"{path}.gyro.pitch components must be finite.");
        }
    }
    // The session-source gate, shared by a declared/magazine-entry source (which carries the current document's
    // destinationNames) and a placement face override (ValidateFaceSources, which already threads destinationNames
    // for the PORTAL facet on the same row). Destination must name a declared destinations row — the row's own
    // resolution (reference/instance/generation) is a bind-time fact this pass cannot see (see docs/vision.md).
    // Camera, when present, is validated only as non-empty here — the destination's own definition is not joined at
    // boot, so an unknown camera name is a loud bind-time refusal (WorldScreenBinder), never a boot refusal.
    private static void ValidateSessionSource(WorldScreenSource.Session session, HashSet<string> destinationNames, WorldPlacementPortal? portal, string path, List<string> errors) {
        if (
            string.IsNullOrWhiteSpace(value: session.Destination) ||
            !destinationNames.Contains(item: session.Destination)
        ) {
            errors.Add(item: ((destinationNames.Count > 0)
                ? $"{path}.session.destination '{session.Destination}' names no destinations row; the world declares: {string.Join(
                    separator: ", ",
                    values: destinationNames
                )}."
                : $"{path}.session.destination '{session.Destination}' names no destinations row; the world declares none."));
        }

        if (
            (session.CameraName is { } camera) &&
            string.IsNullOrWhiteSpace(value: camera)
        ) {
            errors.Add(item: $"{path}.session.camera must be non-empty when present.");
        }

        // WINDOW needs the SAME face's own portal facet: the aperture (WorldFaceCatalog) and the isometry that maps
        // the viewer's eye through it both come from the SAME mapped border pair, so a face with no counterpart has
        // no destination-space frame to fit an off-axis frustum against. A top-level screens row or magazine entry
        // passes portal:null unconditionally (see the two call sites) and is refused here for the identical reason.
        if (session.Projection == WorldScreenProjection.Window) {
            if (portal is not { Arrival: WorldPortalArrival.Mapped, Counterpart: not null }) {
                errors.Add(item: $"{path}.session.projection 'window' requires THIS SAME face's portal facet to author arrival 'mapped' with a counterpart — a window has no destination-space aperture to fit a frustum against without a mapped border pair.");
            }
        }

        if (session.Resolution is { } resolution) {
            if (
                (resolution.Width <= 0) ||
                (resolution.Height <= 0) ||
                (resolution.Width > MaxSurfaceDimension) ||
                (resolution.Height > MaxSurfaceDimension)
            ) {
                errors.Add(item: $"{path}.session.resolution [{resolution.Width}, {resolution.Height}] must be within 1..{MaxSurfaceDimension} on each axis.");
            }
        }
    }
    // The decal-text source gate: the grid must fit the engine's per-screen decal cell budget, every authored line
    // must fit the grid (a clipped sign is silent data loss — refused by name instead), the font must resolve
    // through the declared catalog, and the colors must be #RRGGBB.
    private static void ValidateTextSource(WorldDefinition definition, WorldScreenSource.Text text, string path, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
        if (!hasTextCatalog) {
            errors.Add(item: $"{path}.text requires the world to declare a text font catalog.");
        } else if (
            (text.Font is { } font) &&
            !fontNames.Contains(item: font)
        ) {
            errors.Add(item: $"{path}.text font '{font}' names no text.fonts row.");
        }

        if (text.Lines is not { Count: > 0 } lines) {
            errors.Add(item: $"{path}.text.lines requires at least one row.");

            return;
        }

        var widestLine = 0;

        for (var index = 0; (index < lines.Count); index++) {
            if (lines[index] is not { } line) {
                errors.Add(item: $"{path}.text.lines[{index}] is required.");

                return;
            }

            var runeCount = 0;

            foreach (var _ in line.EnumerateRunes()) {
                runeCount++;
            }

            widestLine = Math.Max(
                val1: widestLine,
                val2: runeCount
            );
        }

        var columns = (text.Columns ?? Math.Max(
            val1: widestLine,
            val2: 1
        ));
        var rows = (text.Rows ?? lines.Count);

        if (
            (text.Columns is { } authoredColumns) &&
            (authoredColumns < 1)
        ) {
            errors.Add(item: $"{path}.text.columns {authoredColumns} must be at least 1.");
        } else if (widestLine > columns) {
            errors.Add(item: $"{path}.text a line spans {widestLine} scalars, wider than the {columns}-column grid.");
        }

        if (
            (text.Rows is { } authoredRows) &&
            (authoredRows < 1)
        ) {
            errors.Add(item: $"{path}.text.rows {authoredRows} must be at least 1.");
        } else if (lines.Count > rows) {
            errors.Add(item: $"{path}.text authors {lines.Count} lines, taller than the {rows}-row grid.");
        }

        if (
            (columns > 0) &&
            (rows > 0) &&
            ((((long)columns) * rows) > Puck.SignedDistance.SdfScreenDecalLayout.MaxScreenDecalCells)
        ) {
            errors.Add(item: $"{path}.text grid {columns}x{rows} exceeds the {Puck.SignedDistance.SdfScreenDecalLayout.MaxScreenDecalCells}-cell per-screen decal budget.");
        }

        if (
            (text.Foreground is { } foreground) &&
            !IsColor(
            definition: definition,
            value: foreground
        )
        ) {
            errors.Add(item: $"{path}.text.foreground {WorldColor.Grammar}.");
        }

        if (
            (text.Background is { } background) &&
            !IsColor(
            definition: definition,
            value: background
        )
        ) {
            errors.Add(item: $"{path}.text.background {WorldColor.Grammar}.");
        }
    }
    private static void ValidateTrack(WorldCameraMotion.Track track, string path, List<string> errors) {
        if (
            (track.Definition is null) ||
            (track.Playback is null)
        ) {
            errors.Add(item: $"{path} requires definition and playback state.");

            return;
        }
        if (
            !Enum.IsDefined(value: track.Definition.ClockDomain) ||
            !Enum.IsDefined(value: track.Definition.Interpolation) ||
            !Enum.IsDefined(value: track.Playback.LoopMode)
        ) {
            errors.Add(item: $"{path} contains an unknown clock, interpolation, or loop mode.");
        }
        var keyframes = track.Definition.Keyframes;

        if (
            (keyframes is null) ||
            (keyframes.Count < 2)
        ) {
            errors.Add(item: $"{path}.definition.keyframes requires at least two rows.");

            return;
        }
        for (var index = 0; (index < keyframes.Count); index++) {
            var keyframe = keyframes[index];

            if (keyframe is null) {
                errors.Add(item: $"{path}.definition.keyframes[{index}] requires a finite position.");

                continue;
            }
            if (!IsFinite(value: keyframe.Position)) {
                errors.Add(item: $"{path}.definition.keyframes[{index}] requires a finite position.");
            }
            if (
                (index > 0) &&
                (keyframes[(index - 1)] is { } previous) &&
                (keyframe.Tick <= previous.Tick)
            ) {
                errors.Add(item: $"{path}.definition.keyframes[{index}].tick must be greater than the preceding tick.");
            }
        }
    }
    // The window composition (PRESENTATION-ONLY): the seat rig valid, layout names unique, slot rects inside
    // [0,1] and non-degenerate, and every named-camera slot resolving against the authored camera set. ABSENT is a
    // seatless document's right — the engine ships no rig, so a census implying a body must author one.
    private static void ValidateViews(WorldViewDefaults? views, int capacity, HashSet<string> cameras, List<string> errors) {
        if (views is null) {
            if (capacity > 0) {
                errors.Add(item: $"views is required when population.capacity ({capacity}) is nonzero; the engine declares no seat rig (author one, or name a basis document that does).");
            }

            return;
        }

        ValidateRig(
            rig: views.SeatRig,
            path: "views.seatRig",
            errors: errors
        );
        ValidateSeatControl(
            control: views.SeatControl,
            path: "views.seatControl",
            errors: errors
        );
        if (views.SeatRig?.Motion is not WorldCameraMotion.Orbit) {
            errors.Add(item: "views.seatRig.motion must be orbit because seatControl declares live yaw/pitch input; use cameras for non-interactive authored views.");
        }

        if (views.FlyRig is { } flyRig) {
            ValidateRig(
                rig: flyRig,
                path: "views.flyRig",
                errors: errors
            );

            if (flyRig.Motion is not WorldCameraMotion.Fly) {
                errors.Add(item: "views.flyRig.motion must be fly — it is the rig a camera-targeting mode state swaps to.");
            }
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layouts = views.Layouts;

        for (var index = 0; (index < layouts.Count); index++) {
            var layout = layouts[index];
            var path = $"views.layouts[{index}]";

            if (layout is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: layout.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: layout.Name)) {
                errors.Add(item: $"{path}.name '{layout.Name}' is duplicated.");
            }

            if (layout.SeatCount < 0) {
                errors.Add(item: $"{path}.seatCount {layout.SeatCount} must be non-negative.");
            }

            if (
                !float.IsFinite(f: layout.TransitionSeconds) ||
                (layout.TransitionSeconds < 0f)
            ) {
                errors.Add(item: $"{path}.transitionSeconds must be finite and non-negative.");
            }

            if (
                !float.IsFinite(f: layout.TransitionRenderScale) ||
                (layout.TransitionRenderScale <= 0f) ||
                (layout.TransitionRenderScale > 1f)
            ) {
                errors.Add(item: $"{path}.transitionRenderScale must be finite and within (0, 1].");
            }

            var slots = layout.Slots;

            if (slots.Count == 0) {
                errors.Add(item: $"{path}.slots must declare at least one slot.");
            }

            for (var slotIndex = 0; (slotIndex < slots.Count); slotIndex++) {
                var slot = slots[slotIndex];
                var slotPath = $"{path}.slots[{slotIndex}]";

                if (
                    !float.IsFinite(f: slot.X) ||
                    !float.IsFinite(f: slot.Y) ||
                    !float.IsFinite(f: slot.Width) ||
                    !float.IsFinite(f: slot.Height) ||
                    (slot.X < 0f) ||
                    (slot.Y < 0f) ||
                    (slot.Width <= 0f) ||
                    (slot.Height <= 0f) ||
                    ((slot.X + slot.Width) > 1.0001f) ||
                    ((slot.Y + slot.Height) > 1.0001f)
                ) {
                    errors.Add(item: $"{slotPath} rect must lie within [0, 1] with positive extents.");
                }

                if (
                    (slot.Camera is { } camera) &&
                    !cameras.Contains(item: camera)
                ) {
                    errors.Add(item: $"{slotPath}.camera '{camera}' names no camera row.");
                }
            }
        }
    }
}
