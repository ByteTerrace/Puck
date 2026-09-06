import { createSlice, PayloadAction } from "@reduxjs/toolkit";
import type { HostDispatch, HostState, HostThunkExtra } from "./store";

export type OnboardingStatus =
  | "checking"
  | "error"
  | "idle"
  | "onboarding"
  | "ready";

export interface OnboardingState {
  error?: string;
  status: OnboardingStatus;
}

interface SelfOnboardResponse {
  State?: string;
  state?: string;
}

const POLL_ATTEMPT_LIMIT = 60;
const POLL_INTERVAL_MILLISECONDS = 5000;
const SELF_ONBOARD_PATH = "/api/self-onboard";

const initialState: OnboardingState = { status: "idle" };

export const onboardingSlice = createSlice({
  initialState,
  name: "onboarding",
  reducers: {
    failed(state, action: PayloadAction<string>) {
      state.error = action.payload;
      state.status = "error";
    },
    statusChanged(state, action: PayloadAction<OnboardingStatus>) {
      state.error = undefined;
      state.status = action.payload;
    },
  },
});

const { failed, statusChanged } = onboardingSlice.actions;

const delay = (milliseconds: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, milliseconds));

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

export const ensureOnboarded =
  (scopes: string[]) =>
  async (
    dispatch: HostDispatch,
    getState: () => HostState,
    extra: HostThunkExtra,
  ): Promise<void> => {
    const { status } = (getState() as { onboarding: OnboardingState })
      .onboarding;

    if ("idle" !== status && "error" !== status) {
      return;
    }

    dispatch(statusChanged("checking"));

    try {
      const getAccessToken = async () =>
        (await extra.tokenCredential.getToken(scopes))!.token;
      // POST unconditionally, never GET-and-skip: for an already-provisioned user the POST is
      // what deposits a fresh escrow and triggers a pending partition migration — a GET alone
      // would report Ready and the migration would never fire. The POST is idempotent and fast
      // for users with nothing to do.
      const startResult = await sendSelfOnboardRequest(
        await getAccessToken(),
        "POST",
      );

      // Migrating counts as ready: the data stays reachable at its recorded home for the whole
      // migration (reads throughout; writes are frozen until the flip and surface as errors).
      if ("Ready" === startResult.state || "Migrating" === startResult.state) {
        dispatch(statusChanged("ready"));

        return;
      }

      dispatch(statusChanged("onboarding"));

      for (let attempt = 0; attempt < POLL_ATTEMPT_LIMIT; attempt++) {
        await delay(POLL_INTERVAL_MILLISECONDS);

        const pollResult = await sendSelfOnboardRequest(
          await getAccessToken(),
          "GET",
        );

        if ("Ready" === pollResult.state || "Migrating" === pollResult.state) {
          dispatch(statusChanged("ready"));

          return;
        }

        if ("NotOnboarded" === pollResult.state) {
          await sendSelfOnboardRequest(await getAccessToken(), "POST");
        }
      }

      dispatch(
        failed("Onboarding did not complete within the expected time frame."),
      );
    } catch (e) {
      dispatch(failed(e instanceof Error ? e.message : String(e)));
    }
  };
