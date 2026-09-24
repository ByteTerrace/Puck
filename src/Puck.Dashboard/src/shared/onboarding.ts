import type { TokenCredential } from "@azure/identity";
import {
  catchError,
  concat,
  concatMap,
  defer,
  EmptyError,
  first,
  map,
  type Observable,
  of,
  switchMap,
  take,
  throwError,
  timer,
} from "rxjs";
import { type ActorRefFrom, assign, fromObservable, setup } from "xstate";

interface SelfOnboardResponse {
  State?: string;
  state?: string;
}

/** One self-onboarding call; the protocol below decides when to make each. */
export type OnboardingRequest = (method: "GET" | "POST") => Promise<{ state: string }>;

const POLL_ATTEMPT_LIMIT = 60;
const POLL_INTERVAL_MILLISECONDS = 5000;
const SELF_ONBOARD_PATH = "/api/self-onboard";

async function sendSelfOnboardRequest(
  accessToken: string,
  method: "GET" | "POST",
): Promise<{ state: string; statusCode: number }> {
  const response = await fetch(SELF_ONBOARD_PATH, {
    headers: {
      Authorization: `Bearer ${accessToken}`,
    },
    method: method,
  });

  if (401 === response.status || 403 === response.status) {
    throw new Error(
      `Onboarding request was rejected (HTTP ${response.status}).`,
    );
  }

  if (500 <= response.status) {
    throw new Error(`Onboarding request failed (HTTP ${response.status}).`);
  }

  const body = (await response.json().catch(() => ({}))) as SelfOnboardResponse;

  return {
    state: body.State ?? body.state ?? "Unknown",
    statusCode: response.status,
  };
}

const isReady = (state: string) => "Ready" === state || "Migrating" === state;

export interface OnboardingPolling {
  attempts: number;
  intervalMilliseconds: number;
}

/**
 * The self-onboarding protocol as a stream of statuses, ending with "ready" or an error. It POSTs unconditionally
 * first, never GET-and-skip: for an already-provisioned user the POST is what deposits a fresh escrow and triggers a
 * pending partition migration, which a GET alone would report as Ready and never fire. The POST is idempotent and
 * fast for users with nothing to do. Until provisioning completes it polls, re-POSTing whenever the account reports
 * it is not onboarded. Migrating counts as ready: the data stays reachable at its recorded home for the whole
 * migration (reads throughout; writes are frozen until the flip and surface as errors).
 */
export function onboardingProgress(
  request: (method: "GET" | "POST") => Promise<{ state: string }>,
  polling: OnboardingPolling = { attempts: POLL_ATTEMPT_LIMIT, intervalMilliseconds: POLL_INTERVAL_MILLISECONDS },
): Observable<"onboarding" | "ready"> {
  const poll$ = timer(polling.intervalMilliseconds, polling.intervalMilliseconds).pipe(
    take(polling.attempts),
    concatMap(() => defer(() => request("GET"))),
    concatMap((result) => ("NotOnboarded" === result.state ? defer(() => request("POST")) : of(result))),
    first((result) => isReady(result.state)),
    catchError((error: unknown) =>
      throwError(() =>
        error instanceof EmptyError ? new Error("Onboarding did not complete within the expected time frame.") : error,
      ),
    ),
    map(() => "ready" as const),
  );

  return defer(() => request("POST")).pipe(
    switchMap((start) => (isReady(start.state) ? of("ready" as const) : concat(of("onboarding" as const), poll$))),
  );
}


/** Self-onboarding calls authorized as the signed-in user. */
export function selfOnboardRequest(tokenCredential: TokenCredential, scopes: string[]): OnboardingRequest {
  return async (method) => {
    const token = await tokenCredential.getToken(scopes);

    if (!token) {
      throw new Error("No access token was issued for account setup.");
    }

    return sendSelfOnboardRequest(token.token, method);
  };
}

export interface OnboardingInput {
  polling?: OnboardingPolling;
  request: OnboardingRequest;
}

/**
 * The account's setup lifecycle, one per page. It waits for sign-in, then runs the protocol above (`onboardingProgress`)
 * as an invoked observable: `working.checking` covers the first POST, `working.onboarding` the polling, and the
 * protocol's end is `ready` or `failed`. A failure is retried by the author (`RETRY`) or by the next sign-in
 * notice; leaving `working` stops the poll. Readers ask questions rather than naming states: the `busy` tag while
 * setup runs, and `snapshot.can({ type: "RETRY" })` for a failure worth offering a retry.
 */
export const onboardingMachine = setup({
  types: {
    context: {} as { error: string | null; input: OnboardingInput },
    events: {} as { type: "RETRY" } | { type: "SIGNED_IN" },
    input: {} as OnboardingInput,
    tags: {} as "busy",
  },
  actors: {
    progress: fromObservable(({ input }: { input: OnboardingInput }) => onboardingProgress(input.request, input.polling)),
  },
}).createMachine({
  id: "onboarding",
  context: ({ input }) => ({ error: null, input }),
  initial: "signedOut",
  states: {
    signedOut: {
      on: { SIGNED_IN: "working" },
    },
    working: {
      tags: "busy",
      entry: assign({ error: null }),
      initial: "checking",
      invoke: {
        src: "progress",
        input: ({ context }) => context.input,
        onSnapshot: { guard: ({ event }) => "onboarding" === event.snapshot.context, target: ".onboarding" },
        onDone: "ready",
        onError: {
          actions: assign({ error: ({ event }) => (event.error instanceof Error ? event.error.message : String(event.error)) }),
          target: "failed",
        },
      },
      states: {
        checking: {},
        onboarding: {},
      },
    },
    ready: {},
    failed: {
      on: { RETRY: "working", SIGNED_IN: "working" },
    },
  },
});

export type OnboardingActor = ActorRefFrom<typeof onboardingMachine>;
