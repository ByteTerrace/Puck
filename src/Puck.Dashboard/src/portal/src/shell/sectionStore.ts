import { BehaviorSubject, fromEvent, merge } from "rxjs";
import { useObservableValue } from "../streams/useObservableValue";
import { sectionFromLocation, sectionPath, type Section } from "./sections";

// The committed section is a BehaviorSubject: the URL is its durable form, and every change goes through the same
// cancelable `puck-before-navigate` event, so the studio can hold navigation while it has unsaved edits (including
// browser Back, whose URL change is reverted until the studio lets it through).

let section$: BehaviorSubject<Section> | undefined;

/**
 * Asks every guard whether navigation may proceed. Returns whether it proceeded now; a guard that held it calls
 * `continueNavigation` later, once its own confirmation resolves.
 */
function requestNavigation(continueNavigation: () => void): boolean {
  const detail = { continueNavigation };
  const allowed = window.dispatchEvent(new CustomEvent("puck-before-navigate", { cancelable: true, detail }));

  if (allowed) continueNavigation();

  return allowed;
}

/** The section store, created on first use and kept for the page's lifetime, like the history it follows. */
function sections(): BehaviorSubject<Section> {
  if (section$) return section$;

  const store = new BehaviorSubject(sectionFromLocation(location));

  // Follows URL changes something else made: Back/Forward, and the host's share handling.
  merge(fromEvent(window, "popstate"), fromEvent(window, "byteterrace-share")).subscribe(() => {
    const next = sectionFromLocation(location);
    const previous = store.getValue();

    if (next === previous) return;

    const destination = location.pathname + location.search + location.hash;
    const proceeded = requestNavigation(() => {
      history.replaceState(null, "", destination);
      store.next(next);
    });

    // A held move shows the committed section's URL again until the guard lets it through.
    if (!proceeded) history.pushState(null, "", sectionPath(previous, location.hostname));
  });

  section$ = store;

  return store;
}

/** Moves to `section` once every navigation guard agrees. */
export function navigateToSection(section: Section): void {
  const store = sections();

  if (section === store.getValue()) return;

  requestNavigation(() => {
    history.pushState(null, "", sectionPath(section, location.hostname));
    store.next(section);
  });
}

/** The section the portal is showing. */
export function useSection(): Section {
  return useObservableValue(sections());
}
