/**
 * The island check: once a workspace revision's source-tier diagnostics settle clean, the world engine compiles the
 * open source in full (the semantic tier, which costs seconds on a large world) and composes the workspace's root
 * with every edited file, so an edit that breaks another document of the island is caught before a preview.
 *
 * The engine cannot cancel a composition, so at most one runs at a time. While it runs, the newest ready revision
 * waits and each newer one replaces it; a revision that is no longer the workspace's newest when its composition
 * finishes is dropped rather than reported, and a waiting revision that has gone stale never starts.
 */
import { Observable } from "rxjs";

/** Where the workspace stands at one revision: whether it is ready for a check, and how to run one. */
export interface IslandRequest<T> {
  readonly revision: number;
  /** Runs the composition; `null` while the revision is not ready (diagnostics pending, errors, no world engine). */
  readonly run: (() => Promise<T>) | null;
}

/** One finished check, reported only while its revision is still the newest. */
export interface IslandResult<T> {
  readonly revision: number;
  readonly outcome: T;
}

/** Runs the ready revisions of `requests$` under the rules above; errors when a composition rejects. */
export function islandChecks<T>(requests$: Observable<IslandRequest<T>>): Observable<IslandResult<T>> {
  return new Observable<IslandResult<T>>((subscriber) => {
    let newest = -1;
    let running: number | null = null;
    let waiting: IslandRequest<T> | null = null;
    let checked = -1;

    const start = (request: IslandRequest<T>) => {
      running = request.revision;
      checked = request.revision;
      request.run!().then((outcome) => {
        running = null;
        if (subscriber.closed) return;
        if (request.revision === newest) subscriber.next({ revision: request.revision, outcome });
        const next = waiting;
        waiting = null;
        if (next && next.revision === newest) start(next);
      }, (error) => subscriber.error(error));
    };

    const subscription = requests$.subscribe({
      next: (request) => {
        newest = Math.max(newest, request.revision);
        if (request.revision !== newest || !request.run || request.revision === checked) return;
        if (running !== null) {
          waiting = request;
        } else {
          start(request);
        }
      },
      error: (error) => subscriber.error(error),
    });

    return () => subscription.unsubscribe();
  });
}
