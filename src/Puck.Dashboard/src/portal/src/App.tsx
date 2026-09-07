/*
    - https://mantine.dev/core/package/
    - https://redux-toolkit.js.org/
    - https://remixicon.com/
    - https://r3f.docs.pmnd.rs/
    - https://threejs.org/
    - https://usehooks.com/
*/

import {
  createTheme,
  localStorageColorSchemeManager,
  Anchor,
  AppShell,
  Badge,
  Box,
  Burger,
  Button,
  Container,
  Flex,
  Group,
  Loader,
  MantineProvider,
  NavLink,
  Tooltip,
} from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { RiDatabase2Fill, RiHistoryLine, RiHome9Fill } from "@remixicon/react";
import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { useSelector } from "react-redux";
import { HostContextValue } from "../../shared/interfaces";
import { OnboardingState } from "../../shared/onboarding";
import { createGraphClient } from "./clients/graphClientApi";
import GraphClientProvider from "./clients/GraphClientProvider";
import logoUrl from "/assets/logo.png";

const AuditView = lazy(() => import("./components/AuditView"));
const DataExplorer = lazy(() => import("./components/DataExplorer"));
const WorldStudio = lazy(() => import("./components/world/WorldStudio"));

import "@mantine/core/styles.css";
import "./theme.css";

const puckTheme = createTheme({
  fontFamily: 'system-ui, -apple-system, "Segoe UI", sans-serif',
  fontFamilyMonospace: '"JetBrains Mono", ui-monospace, monospace',
  headings: {
    fontFamily: '"Lora", Georgia, "Iowan Old Style", "Palatino Linotype", serif',
    fontWeight: "600",
  },
  colors: {
    coral: [
      "#fdf0f3",
      "#fce1e6",
      "#f9c1cc",
      "#f2879a",
      "#ffa3b3",
      "#e56784",
      "#d15d78",
      "#ad3a53",
      "#8e2d42",
      "#2a0d14",
    ],
    jade: [
      "#e6fbf5",
      "#cbf7ec",
      "#9bf0dc",
      "#62e4c8",
      "#4fd0b4",
      "#1bc09f",
      "#0fa185",
      "#0e7d6b",
      "#0d6457",
      "#083b34",
    ],
    ground: [
      "#f6f5f9",
      "#e7e4ed",
      "#dbd7e4",
      "#b5aec3",
      "#877f99",
      "#5a5470",
      "#363145",
      "#262231",
      "#1b1924",
      "#15131c",
    ],
  },
  primaryColor: "coral",
  primaryShade: { light: 7, dark: 3 },
  defaultRadius: "sm",
});

const colorSchemeManager = localStorageColorSchemeManager({
  key: "byteterrace.mantine.theme.colorScheme",
});

type Section = "audit" | "data" | "studio" | "home";

// Sections are real routes so refreshes, deep links, and back/forward behave
// the way a site is expected to. The Puck section lives at /puck on every host, except a
// "puck.*" host, where the bare root already IS the Puck section — puckPath() picks the one
// in-app navigation should write, and sectionFromLocation's unmatched-path fallback already
// resolves both to "studio" without needing a path check of its own.
const isPuckHost = (): boolean => location.hostname.startsWith("puck.");
const puckPath = (): string => (isPuckHost() ? "/" : "/puck");

const sectionFromLocation = (): Section => {
  if (location.pathname.startsWith("/audit")) {
    return "audit";
  }

  if (location.pathname.startsWith("/data")) {
    return "data";
  }

  return "studio";
};

function OnboardingIndicator() {
  const onboarding = useSelector(
    (state: { onboarding?: OnboardingState }) => state.onboarding,
  );

  if (!onboarding) {
    return null;
  }

  switch (onboarding.status) {
    case "checking":
    case "onboarding":
      return (
        <Badge
          color="yellow"
          leftSection={<Loader color="yellow" size={12} />}
          variant="light"
        >
          Setting up your account…
        </Badge>
      );
    case "error":
      return (
        <Tooltip label={onboarding.error ?? "Unknown error."}>
          <Badge color="red" variant="light">
            Account setup failed
          </Badge>
        </Tooltip>
      );
    default:
      return null;
  }
}

function App({ context }: { context?: HostContextValue }) {
  const tokenCredential = context?.serviceProvider?.tryGet?.("tokenCredential") ?? ({
    getToken: async () => ({ token: "", expiresOnTimestamp: 0 }),
  } as any);
  const graphClient = useMemo(
    () => createGraphClient(tokenCredential),
    [tokenCredential]
  );
  const [opened, { toggle, close }] = useDisclosure();
  const [activeSection, setActiveSection] = useState<Section>(sectionFromLocation);
  const userObjectId = context?.activeAccount?.localAccountId;

  useEffect(() => {
    const syncToLocation = () => {
      const next = sectionFromLocation();
      if (next === activeSection) return;
      const destination = location.pathname + location.search + location.hash;
      const continueNavigation = () => { history.replaceState(null,"",destination); setActiveSection(next); close(); };
      if (!window.dispatchEvent(new CustomEvent("puck-before-navigate",{cancelable:true,detail:{continueNavigation}}))) {
        history.pushState(null,"",activeSection === "audit" ? "/audit" : activeSection === "data" ? "/data" : puckPath());
        return;
      }
      continueNavigation();
    };

    window.addEventListener("byteterrace-share", syncToLocation);
    window.addEventListener("popstate", syncToLocation);

    return () => {
      window.removeEventListener("byteterrace-share", syncToLocation);
      window.removeEventListener("popstate", syncToLocation);
    };
  }, [activeSection,close]);

  const navigateTo = (section: Section) => {
    const continueNavigation = () => {
      close();
      history.pushState(null,"",section === "audit" ? "/audit" : section === "data" ? "/data" : puckPath());
      setActiveSection(section);
    };
    if (section !== activeSection && !window.dispatchEvent(new CustomEvent("puck-before-navigate", {cancelable:true,detail:{continueNavigation}}))) return;
    continueNavigation();
  };

  return (
    <GraphClientProvider graphClient={graphClient}>
      <MantineProvider
        theme={puckTheme}
        colorSchemeManager={colorSchemeManager}
        defaultColorScheme={"auto"}
      >
        <a href="#main-content" className="skip-link">Skip to main content</a>
        <AppShell //
          header={{ height: 60 }}
          navbar={{
            breakpoint: "sm",
            collapsed: { mobile: !opened, desktop: activeSection === "studio" && !opened },
            width: 240,
          }}
          padding="md"
        >
          <AppShell.Header>
            <Container fluid h="100%">
              <Group h="100%" justify="space-between" wrap="nowrap">
                <Flex align="center" justify="flex-start">
                  <Burger
                    aria-label="Toggle navigation"
                    onClick={toggle}
                    opened={opened}
                    size="sm"
                  />
                  <Anchor
                    href="#"
                    style={{ color: "var(--mantine-color-text)" }}
                    underline="never"
                  >
                    <Flex align="center" gap="sm" justify="center">
                      <img alt="Home" src={logoUrl} width={48} />
                      <Box visibleFrom="sm" fw={700} style={{ letterSpacing: 1 }}>PUCK STUDIO</Box>
                    </Flex>
                  </Anchor>
                </Flex>
                <Flex align="center" justify="flex-end">
                  <Group wrap="nowrap">
                    {context?.isSignedIn ? (
                      <>
                        <OnboardingIndicator />
                        <Button onClick={context.signOut} variant="default">
                          Sign Out
                        </Button>
                      </>
                    ) : (
                      <>
                        <Button onClick={() => context?.signIn?.()} variant="default" disabled={!context?.signIn}>
                          {context?.signIn ? "Sign In" : "Local Studio Mode"}
                        </Button>
                      </>
                    )}
                  </Group>
                </Flex>
              </Group>
            </Container>
          </AppShell.Header>
          <AppShell.Navbar>
            <Box ml={12} mt={12}>
              <NavLink
                active={"studio" === activeSection || "home" === activeSection}
                href={puckPath()}
                label="World Studio"
                leftSection={<RiHome9Fill />}
                onClick={(event) => {
                  event.preventDefault();
                  navigateTo("studio");
                }}
              />
              <NavLink
                active={"data" === activeSection}
                href="/data"
                label="Cloud Storage"
                leftSection={<RiDatabase2Fill />}
                onClick={(event) => {
                  event.preventDefault();
                  navigateTo("data");
                }}
              />
              <NavLink
                active={"audit" === activeSection}
                href="/audit"
                label="Audit Trail"
                leftSection={<RiHistoryLine />}
                onClick={(event) => {
                  event.preventDefault();
                  navigateTo("audit");
                }}
              />
            </Box>
          </AppShell.Navbar>
          <AppShell.Main id="main-content" tabIndex={-1}>
            {"data" === activeSection ? (
              context?.isSignedIn && userObjectId ? (
                <Suspense fallback="loading…">
                  <DataExplorer
                    tokenCredential={tokenCredential}
                    userObjectId={userObjectId}
                  />
                </Suspense>
              ) : (
                "Sign in to explore your data."
              )
            ) : "audit" === activeSection ? (
              context?.isSignedIn && userObjectId ? (
                <Suspense fallback="loading…">
                  <AuditView
                    tokenCredential={tokenCredential}
                    userObjectId={userObjectId}
                  />
                </Suspense>
              ) : (
                "Sign in to see your audit history."
              )
            ) : (
              <Suspense fallback="loading World Studio…">
                <WorldStudio />
              </Suspense>
            )}
          </AppShell.Main>
        </AppShell>
      </MantineProvider>
    </GraphClientProvider>
  );
}

export default App;
