import type { TokenCredential } from "@azure/identity";
import { AppShell, Burger, Divider, Group, NavLink, ScrollArea, Stack } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import type { HostContextValue } from "../../../shared/interfaces";
import { Kicker } from "../ui/Kicker";
import puckMark from "../ui/puck-mark.png";
import { AccountControls } from "./AccountControls";
import { ColorSchemeToggle } from "./ColorSchemeToggle";
import { navigation, navigationItem } from "./navigation";
import classes from "./PortalShell.module.css";
import { SectionOutlet } from "./SectionOutlet";
import { navigateToSection, useSection } from "./sectionStore";
import { sectionPath, type Section } from "./sections";

interface PortalShellProps {
  context: HostContextValue | undefined;
  tokenCredential: TokenCredential;
}

/**
 * The portal frame: the brand bar, the section navigation, and the active section. World Studio takes the whole
 * width on large screens, so the navigation starts folded there and the burger opens it on every screen size.
 */
export function PortalShell({ context, tokenCredential }: PortalShellProps) {
  const section = useSection();
  const [opened, { close, toggle }] = useDisclosure(false);
  const current = navigationItem(section);
  const go = (next: Section) => {
    close();
    navigateToSection(next);
  };

  return (
    <>
      <a className="skip-link" href="#main-content">
        Skip to main content
      </a>
      <AppShell
        header={{ height: 56 }}
        navbar={{
          breakpoint: "sm",
          collapsed: { desktop: section === "studio" && !opened, mobile: !opened },
          width: 256,
        }}
        padding="lg"
      >
        <AppShell.Header>
          <Group h="100%" justify="space-between" px="md" wrap="nowrap">
            <Group gap="sm" wrap="nowrap">
              <Burger aria-label="Toggle navigation" onClick={toggle} opened={opened} size="sm" />
              <a
                aria-label="Puck home"
                className={classes.brand}
                href={sectionPath("studio", location.hostname)}
                onClick={(event) => {
                  event.preventDefault();
                  go("studio");
                }}
              >
                <img alt="" className={classes.mark} height={32} src={puckMark} width={32} />
                <span className={classes.wordmark}>Puck</span>
              </a>
              <Divider className={classes.divider} orientation="vertical" visibleFrom="xs" />
              <Kicker c="dimmed" visibleFrom="xs">
                {current.label}
              </Kicker>
            </Group>
            <Group gap="xs" wrap="nowrap">
              <AccountControls context={context} />
              <ColorSchemeToggle />
            </Group>
          </Group>
        </AppShell.Header>

        <AppShell.Navbar aria-label="Sections" component="nav" p="sm">
          <AppShell.Section component={ScrollArea} grow scrollbarSize={6}>
            <Stack gap="lg">
              {navigation.map((group) => (
                <Stack gap={4} key={group.label}>
                  <Kicker c="dimmed" px="sm">
                    {group.label}
                  </Kicker>
                  {group.items.map((item) => (
                    <NavLink
                      active={item.section === section}
                      aria-current={item.section === section ? "page" : undefined}
                      description={item.description}
                      href={sectionPath(item.section, location.hostname)}
                      key={item.section}
                      label={item.label}
                      leftSection={item.icon}
                      onClick={(event) => {
                        event.preventDefault();
                        go(item.section);
                      }}
                    />
                  ))}
                </Stack>
              ))}
            </Stack>
          </AppShell.Section>
        </AppShell.Navbar>

        <AppShell.Main id="main-content" tabIndex={-1}>
          <SectionOutlet context={context} section={section} tokenCredential={tokenCredential} />
        </AppShell.Main>
      </AppShell>
    </>
  );
}
