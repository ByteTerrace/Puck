using Microsoft.Extensions.DependencyInjection;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    // The destination: the away document, admitting every viewer with its whole replica, but one from 'kept/portal' with
    // a projection, each observing the whole world.
    private static WorldDefinition AdmittingDocument(WorldObserverDisclosure? observers = null) {
        static WorldAdmissionEntry Entry(string domain, WorldDisclosureTier tier) => new(
            Algorithm: string.Empty,
            Disclosure: tier,
            Domain: domain,
            Grants: [new WorldAdmissionGrant(
                Budget: 64,
                Capability: WorldCapability.Observe,
                Subject: GrantSubject.All
            )],
            Mode: WorldAdmissionTrustMode.FederatedAuthority,
            PublicKey: string.Empty,
            Subject: null
        );

        var document = AwayDocument();

        return document with {
            Admission = [
                Entry(
                    domain: WorldAdmissionEntry.AnyAuthority,
                    tier: WorldDisclosureTier.Replica
                ),
                Entry(
                    domain: "kept/portal",
                    tier: WorldDisclosureTier.Presentation
                ),
            ],
            PopulationRaw = (document.Population with { Disclosure = observers }),
        };
    }
    private static WorldSessionObservation Observe(WorldFixture fixture, string authority) {
        var observation = fixture.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed),
            sourceAuthority: authority
        );

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        return observation!;
    }

    // THE LAW: one endpoint, one residency. Every seat presented in a world and every portal window onto it whose session
    // is delivered everything the world holds render the one scene the presentation keeps for that world's endpoint, so
    // the one residency that renders it (WorldScreenBinder.TryResolveWindowView beside TryResolveRoutedView); a window
    // whose session is disclosed less (a projection, or an observer policy that redacts) never renders that scene's
    // mirror, and renders its own disclosed session instead. The session stays the gate: once it ends, its window leaves.
    [Fact]
    public void SeatsAndFullyDisclosedWindowsShareOneSceneAndARestrictedWindowNeverJoinsIt() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-sessions-");
        using var host = WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Layouts = [new WorldViewLayout(Name: "seat", Slots: [new WorldViewSlot(Height: 1f, Width: 1f, X: 0f, Y: 0f)])],
                }),
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);

        var document = AdmittingDocument();

        using var destination = Fixtures.FreshServer(definition: document);
        using var north = Endpoint(definition: document, identity: Away, position: AwayPose);
        using var full = Observe(authority: "full/portal", fixture: destination);
        using var twin = Observe(authority: "full/other", fixture: destination);
        using var kept = Observe(authority: "kept/portal", fixture: destination);
        using var fullRoute = new WorldSessionWindowRoute();
        using var twinRoute = new WorldSessionWindowRoute();
        using var keptRoute = new WorldSessionWindowRoute();

        Assert.Equal(actual: (full.Tier, kept.Tier), expected: (WorldDisclosureTier.Replica, WorldDisclosureTier.Presentation));

        // As the binder settles its window screens each frame, from each session's own delivery.
        void Settle() {
            fullRoute.Settle(disclosesEverything: full.DisclosesEverything, endpoint: north, presenter: presenter);
            twinRoute.Settle(disclosesEverything: twin.DisclosesEverything, endpoint: north, presenter: presenter);
            keptRoute.Settle(disclosesEverything: kept.DisclosesEverything, endpoint: north, presenter: presenter);
        }

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        Settle();

        var home = Capture(source: presenter);
        var routed = Enumerable.Range(start: 0, count: home.Views.Count).Single(predicate: view => presenter.TryRoutedView(index: out _, scene: out _, view: view));

        Assert.True(condition: presenter.TryRoutedView(index: out _, scene: out var scene, view: routed));

        // The seat and both fully disclosed windows are views of one scene, so of one residency.
        _ = Assert.Single(collection: new HashSet<WorldRoutedScene>(collection: [scene!, fullRoute.Window!.Scene, twinRoute.Window!.Scene], comparer: ReferenceEqualityComparer.Instance));
        Assert.Equal(actual: (fullRoute.Window.Index, twinRoute.Window.Index), expected: (1, 2));
        Assert.Equal(actual: Capture(source: scene!.FrameSource).Views.Count, expected: 3);
        // The projected window never renders the world's whole replica.
        Assert.Null(@object: keptRoute.Window);

        // Its session gone, a window leaves the scene; the other stays.
        full.Dispose();
        Settle();
        _ = Capture(source: presenter);
        Assert.Null(@object: fullRoute.Window);
        Assert.Equal(actual: twinRoute.Window!.Index, expected: 1);

        // A world whose observer policy redacts bodies admits no window to its whole replica, even at a replica tier.
        using var guarded = Fixtures.FreshServer(definition: AdmittingDocument(observers: new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius, Radius: 4f)));
        using var redacted = Observe(authority: "full/portal", fixture: guarded);
        using var redactedRoute = new WorldSessionWindowRoute();

        Assert.Equal(actual: redacted.Tier, expected: WorldDisclosureTier.Replica);
        redactedRoute.Settle(disclosesEverything: redacted.DisclosesEverything, endpoint: north, presenter: presenter);
        Assert.Null(@object: redactedRoute.Window);
    }
}
