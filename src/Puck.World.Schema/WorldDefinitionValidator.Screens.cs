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

    // One authored camera subject: a placement id must resolve, a world point must be finite; a reference needs no
    // check of its own (it names the program's externally supplied reference pose).
    private static void ValidateSubject(WorldCameraSubject? subject, string path, ISet<string> placementIds, List<string> errors) {
        switch (subject) {
            case null:
            case WorldCameraSubject.Reference:
                break;
            case WorldCameraSubject.Placement placement:
                if (
                    string.IsNullOrWhiteSpace(value: placement.PlacementId) ||
                    !placementIds.Contains(item: placement.PlacementId)
                ) {
                    errors.Add(item: $"{path} references undeclared placement '{placement.PlacementId}'.");
                }

                break;
            case WorldCameraSubject.WorldPoint worldPoint:
                if (!IsFinite(value: worldPoint.Point)) {
                    errors.Add(item: $"{path}.point must contain finite coordinates.");
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown camera subject kind.");

                break;
        }
    }
    // An authored camera program: name/version/operation-count, per-op finiteness and subject references, and the
    // op-ordering rules an evaluator relies on (an anchor op — if any — leads, and a clampPitch op — if any —
    // precedes the orbit op it governs). Blend's cross-program name/cycle check runs once over the whole document's
    // program table (ValidateCameraPrograms), not here — no single program's own validation can see its siblings.
    private static void ValidateProgram(WorldCameraProgram program, WorldDefinition definition, string path, ISet<string> placementIds, List<string> errors) {
        if (program is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (string.IsNullOrWhiteSpace(value: program.Name)) {
            errors.Add(item: $"{path}.name is required.");
        }

        if (!string.Equals(
            a: program.Version,
            b: WorldCameraProgram.CurrentVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"{path}.version '{program.Version}' must be '{WorldCameraProgram.CurrentVersion}'.");
        }

        var operations = program.Operations;

        if (
            (operations is null) ||
            (operations.Count == 0) ||
            (operations.Count > WorldCameraProgram.MaxOperations)
        ) {
            errors.Add(item: $"{path}.operations count must be within 1..{WorldCameraProgram.MaxOperations}.");

            return;
        }

        var seenAnchor = false;
        var seenClampPitch = false;
        var seenFov = false;
        var seenOrbit = false;
        var seenSmooth = false;
        var seenBlend = false;

        for (var index = 0; (index < operations.Count); index++) {
            var opPath = $"{path}.operations[{index}]";

            switch (operations[index]) {
                case null:
                    errors.Add(item: $"{opPath} is required.");

                    break;
                case WorldCameraProgramOp.Anchor anchorOp:
                    if (seenAnchor) {
                        errors.Add(item: $"{opPath} is a second 'anchor' op — at most one is admitted.");
                    } else if (index != 0) {
                        errors.Add(item: $"{opPath} 'anchor' must be the first operation.");
                    }

                    seenAnchor = true;

                    ValidateSubject(
                        errors: errors,
                        path: $"{opPath}.subject",
                        placementIds: placementIds,
                        subject: anchorOp.Subject
                    );

                    break;
                case WorldCameraProgramOp.Offset offset:
                    if (
                        !IsFinite(value: offset.Value) ||
                        !float.IsFinite(f: offset.SpreadPullback)
                    ) {
                        errors.Add(item: $"{opPath} needs a finite value and spreadPullback.");
                    }

                    break;
                case WorldCameraProgramOp.LookAt lookAt:
                    if (lookAt.Subject is null) {
                        if (
                            !float.IsFinite(f: lookAt.FocusDistance) ||
                            (lookAt.FocusDistance < 0f)
                        ) {
                            errors.Add(item: $"{opPath}.focusDistance must be finite and non-negative.");
                        }
                    } else {
                        if (
                            (lookAt.TargetOffset is { } lookAtOffset) &&
                            !IsFinite(value: lookAtOffset)
                        ) {
                            errors.Add(item: $"{opPath}.targetOffset must contain finite coordinates.");
                        }

                        ValidateSubject(
                            errors: errors,
                            path: $"{opPath}.subject",
                            placementIds: placementIds,
                            subject: lookAt.Subject
                        );
                    }

                    break;
                case WorldCameraProgramOp.Orbit orbit:
                    if (seenOrbit) {
                        errors.Add(item: $"{opPath} is a second 'orbit' op — at most one is admitted.");
                    }

                    seenOrbit = true;

                    if (
                        !float.IsFinite(f: orbit.Distance) ||
                        (orbit.Distance <= 0f)
                    ) {
                        errors.Add(item: $"{opPath}.distance must be positive and finite.");
                    }

                    if (
                        !float.IsFinite(f: orbit.Yaw) ||
                        !float.IsFinite(f: orbit.Pitch) ||
                        ((orbit.PivotOffset is { } pivotOffset) && !IsFinite(value: pivotOffset))
                    ) {
                        errors.Add(item: $"{opPath} needs a finite yaw, pitch, and pivotOffset.");
                    }

                    break;
                case WorldCameraProgramOp.Smooth smooth:
                    if (seenSmooth) {
                        errors.Add(item: $"{opPath} is a second 'smooth' op — at most one is admitted.");
                    }

                    seenSmooth = true;

                    if (
                        !float.IsFinite(f: smooth.Rate) ||
                        (smooth.Rate < 0f)
                    ) {
                        errors.Add(item: $"{opPath}.rate must be finite and non-negative.");
                    }

                    break;
                case WorldCameraProgramOp.ClampPitch clampPitch:
                    if (seenClampPitch) {
                        errors.Add(item: $"{opPath} is a second 'clampPitch' op — at most one is admitted.");
                    } else if (seenOrbit) {
                        errors.Add(item: $"{opPath} 'clampPitch' must precede the 'orbit' op it governs.");
                    }

                    seenClampPitch = true;

                    if (
                        !float.IsFinite(f: clampPitch.MinPitch) ||
                        !float.IsFinite(f: clampPitch.MaxPitch) ||
                        (clampPitch.MinPitch >= clampPitch.MaxPitch)
                    ) {
                        errors.Add(item: $"{opPath} needs a finite minPitch strictly less than maxPitch.");
                    }

                    break;
                case WorldCameraProgramOp.Fov fov:
                    if (seenFov) {
                        errors.Add(item: $"{opPath} is a second 'fov' op — at most one is admitted.");
                    }

                    seenFov = true;

                    RequireBindableScalar(
                        definition: definition,
                        errors: errors,
                        path: $"{opPath}.fieldOfViewRadians",
                        scalar: fov.FieldOfViewRadians
                    );

                    break;
                case WorldCameraProgramOp.Blend blend:
                    if (seenBlend) {
                        errors.Add(item: $"{opPath} is a second 'blend' op — at most one is admitted.");
                    }

                    seenBlend = true;

                    if (
                        string.IsNullOrWhiteSpace(value: blend.A) ||
                        string.IsNullOrWhiteSpace(value: blend.B)
                    ) {
                        errors.Add(item: $"{opPath} needs non-empty program names 'a' and 'b'.");
                    }

                    RequireBindableScalar(
                        definition: definition,
                        errors: errors,
                        path: $"{opPath}.weight",
                        scalar: blend.Weight
                    );

                    break;
                default:
                    errors.Add(item: $"{opPath} is an unknown camera program op kind.");

                    break;
            }
        }

        if (
            !seenFov &&
            !seenBlend
        ) {
            errors.Add(item: $"{path}.operations must include a 'fov' op (or a 'blend' op resolving to programs that do) — every rig needs a rendered field of view.");
        }
    }
    // Cross-program blend references: every cameras[].rig, views.seatRig, and views.cameraRig shares ONE name
    // namespace (a blend op resolves any of them), so dangling names and cycles can only be checked once the whole
    // table is assembled — never inside one program's own validation.
    private static void ValidateCameraPrograms(IReadOnlyDictionary<string, WorldCameraProgram> programs, List<string> errors) {
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);
        var settled = new HashSet<string>(comparer: StringComparer.Ordinal);

        void Walk(string name, string path) {
            if (
                settled.Contains(item: name) ||
                !programs.TryGetValue(
                    key: name,
                    value: out var program
                )
            ) {
                return;
            }

            if (!visiting.Add(item: name)) {
                errors.Add(item: $"{path} names '{name}', which cycles back to a program already being blended.");

                return;
            }

            if (program.BlendOp is { } blend) {
                if (
                    !string.IsNullOrWhiteSpace(value: blend.A) &&
                    !programs.ContainsKey(key: blend.A)
                ) {
                    errors.Add(item: $"{path} blend.a names undeclared camera program '{blend.A}'.");
                } else if (!string.IsNullOrWhiteSpace(value: blend.A)) {
                    Walk(
                        name: blend.A,
                        path: $"{path} -> '{blend.A}'"
                    );
                }

                if (
                    !string.IsNullOrWhiteSpace(value: blend.B) &&
                    !programs.ContainsKey(key: blend.B)
                ) {
                    errors.Add(item: $"{path} blend.b names undeclared camera program '{blend.B}'.");
                } else if (!string.IsNullOrWhiteSpace(value: blend.B)) {
                    Walk(
                        name: blend.B,
                        path: $"{path} -> '{blend.B}'"
                    );
                }
            }

            _ = visiting.Remove(item: name);
            _ = settled.Add(item: name);
        }

        foreach (var name in programs.Keys) {
            Walk(
                name: name,
                path: $"cameras program '{name}'"
            );
        }
    }
    // The engage-route policy: a finite non-negative radius, plus authored channel names (kebab-case, non-empty),
    // plus the channel MASK (channelNames must resolve) and the pad KIT reference (padKits must carry it).
    // engageChannel is CONSUMED (WorldServer.ResolveEngageProbes resolves it against the same declared-channel
    // ordinal table), so it is held to the same "must resolve" bar — a misspelled name is otherwise a silent,
    // permanent no-op. cycleChannel stays unconsumed (no reader exists yet) and keeps its lighter kebab-case-only
    // bar.
    private static void ValidateRoute(WorldScreenRoute route, string path, ISet<string> channelNames, ISet<string> padKits, List<string> errors) {
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

        if (
            (route.Kit is { Length: > 0 } kit) &&
            !padKits.Contains(item: kit)
        ) {
            errors.Add(item: $"{path}.kit '{kit}' names no kit carrying a pad map.");
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
    // The window composition (PRESENTATION-ONLY): the seat rig valid, layout names unique, slot rects inside
    // [0,1] and non-degenerate, and every named-camera slot resolving against the authored camera set. ABSENT is a
    // seatless document's right — the engine ships no rig, so a census implying a body must author one.
    private static void ValidateViews(WorldViewDefaults? views, WorldDefinition definition, int capacity, HashSet<string> cameras, ISet<string> placementIds, List<string> errors) {
        if (views is null) {
            if (capacity > 0) {
                errors.Add(item: $"views is required when population.capacity ({capacity}) is nonzero; the engine declares no seat rig (author one, or name a basis document that does).");
            }

            return;
        }

        ValidateProgram(
            definition: definition,
            errors: errors,
            path: "views.seatRig",
            placementIds: placementIds,
            program: views.SeatRig
        );
        ValidateSeatControl(
            control: views.SeatControl,
            path: "views.seatControl",
            errors: errors
        );
        if (views.SeatRig?.OrbitOp is null) {
            errors.Add(item: "views.seatRig must contain an 'orbit' op because seatControl declares live yaw/pitch input; use cameras for non-interactive authored views.");
        }

        if (views.CameraRig is { } cameraRig) {
            ValidateProgram(
                definition: definition,
                errors: errors,
                path: "views.cameraRig",
                placementIds: placementIds,
                program: cameraRig
            );

            if (
                (cameraRig.OrbitOp is not null) ||
                (cameraRig.OffsetOp is not null)
            ) {
                errors.Add(item: "views.cameraRig must author no 'orbit' or 'offset' op — it is the first-person rig a camera-targeting mode state resolves through, sitting exactly at the possessed body's own pose.");
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
