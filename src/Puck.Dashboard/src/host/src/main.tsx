import {
  type AuthenticationResult,
  BrowserCacheLocation,
  type EventMessage,
  EventType,
  LogLevel,
  PublicClientApplication,
} from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import React from "react";
import ReactDOM from "react-dom/client";
import { createActor } from "xstate";
import type { HostServiceProviders } from "../../shared/interfaces";
import { onboardingMachine, selfOnboardRequest } from "../../shared/onboarding";
import App from "./App";
import "./host.css";
import HostProvider from "./HostProvider";
import { loadPortal, registerPortal } from "./remote";
import { DefaultServiceProvider } from "./serviceProvider";
import { PublicClientApplicationTokenCredential } from "./tokenCredential";

// A share link carries a recipient-bound SAS in the fragment (never sent to
// any server). Stash it before MSAL's redirect handling can clobber the hash,
// normalize the route to /data, and announce it so an already-running app
// reacts to share links clicked mid-session (those only change the hash).
const captureShare = () => {
  if (!location.hash.startsWith("#share=")) {
    return false;
  }

  try {
    sessionStorage.setItem(
      "byteterrace.pendingShare",
      decodeURIComponent(location.hash.slice("#share=".length)),
    );
  } catch {
    // Storage can be unavailable; the link simply won't carry over.
  }

  history.replaceState(null, "", `/data${location.search}`);

  return true;
};

captureShare();
window.addEventListener("hashchange", () => {
  if (captureShare()) {
    window.dispatchEvent(new CustomEvent("byteterrace-share"));
  }
});
// A session that outlives its release asks for hashed chunks the publisher has
// since deleted (the portal's chunks raise this event on the same window).
// Reload once per location within a minute; a repeat is a genuine build defect
// and surfaces as the original error.
window.addEventListener("vite:preloadError", (event) => {
  const key = "byteterrace.preloadRecovery";
  const now = Date.now();

  try {
    const previous = JSON.parse(sessionStorage.getItem(key) ?? "null");

    if (previous?.href === location.href && now - previous.at < 60_000) {
      return;
    }

    sessionStorage.setItem(key, JSON.stringify({ at: now, href: location.href }));
  } catch {
    return;
  }

  event.preventDefault();
  location.reload();
});

const urlSearchParams = new URLSearchParams(location.search);
const msalConfiguration = {
  clientId:
    urlSearchParams.get("msalClientId") ??
    "e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7",
  signInScopes: urlSearchParams.has("msalSignInScopes")
    ? urlSearchParams.getAll("msalSignInScopes")
    : ["https://api.byteterrace.com/user_impersonation"],
  tenantId:
    urlSearchParams.get("msalTenantId") ??
    "e09734be-ca09-41ec-b70d-98f5536fb774",
};
const publicClientApplication = new PublicClientApplication({
  auth: {
    authority: `https://login.microsoftonline.com/${msalConfiguration.tenantId}`,
    clientCapabilities: [],
    clientId: msalConfiguration.clientId,
    redirectUri: location.origin,
  },
  cache: {
    cacheLocation: BrowserCacheLocation.SessionStorage,
    cacheRetentionDays: 3,
  },
  system: {
    allowPlatformBroker: false,
    allowRedirectInIframe: false,
    loggerOptions: {
      correlationId: undefined,
      logLevel: LogLevel.Trace,
      piiLoggingEnabled: false,
    },
    preventCorsPreflight: false,
  },
  telemetry: {
    application: {
      appName: "Puck Dashboard",
      appVersion: __PUCK_DASHBOARD_VERSION__,
    },
  },
});
// In production the portal is served beneath the host; in development it runs on its own dev server.
const portalBaseUrl =
  urlSearchParams.get("portalBaseUrl") ??
  (import.meta.env.DEV ? "http://localhost:61101" : `${location.origin}/portal`);
const serviceProvider = new DefaultServiceProvider<HostServiceProviders>();
const tokenCredential = new PublicClientApplicationTokenCredential(publicClientApplication);
// Account setup lives as long as the page; the host starts it at sign-in and the portal shows (and retries) it.
const onboarding = createActor(onboardingMachine, {
  input: { request: selfOnboardRequest(tokenCredential, msalConfiguration.signInScopes) },
}).start();

publicClientApplication.addEventCallback((event: EventMessage) => {
  if (EventType.LOGIN_SUCCESS === event.eventType && event.payload) {
    const payload = event.payload as AuthenticationResult;

    publicClientApplication.setActiveAccount(payload.account);
  }
});

// Start the portal download now, so it overlaps MSAL's start-up rather than following it.
let initialPortalLoad: ReturnType<typeof loadPortal>;

try {
  registerPortal(portalBaseUrl);
  initialPortalLoad = loadPortal();
} catch (error) {
  initialPortalLoad = Promise.reject(error);
}

// React attaches to this promise only once it renders; until then a failure is expected, not unhandled.
initialPortalLoad.catch(() => undefined);

// MSAL requires initialize() before any other API (getAllAccounts included).
publicClientApplication
  .initialize()
  .then(() => {
    const accounts = publicClientApplication.getAllAccounts();

    if (accounts && 0 < accounts.length) {
      publicClientApplication.setActiveAccount(accounts[0]);
    }
    ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
      <React.StrictMode>
        <MsalProvider instance={publicClientApplication}>
          <HostProvider
            onboarding={onboarding}
            serviceProvider={serviceProvider}
            signInScopes={msalConfiguration.signInScopes}
            tokenCredential={tokenCredential}
          >
            <App initialLoad={initialPortalLoad} />
          </HostProvider>
        </MsalProvider>
      </React.StrictMode>,
    );
  })
  .catch((error) => {
    console.error("Sign-in failed to initialize:", error);
    document.getElementById("root")!.innerHTML = `
      <div class="host-screen" role="alert">
        <img alt="" height="48" src="/puck-dark-64.png" width="48" />
        <h1>Puck could not start</h1>
        <p>Sign-in could not start. Reload the page to try again; the browser console has the details.</p>
      </div>
    `;
  });
