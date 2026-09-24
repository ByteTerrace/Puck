import { Avatar, Badge, Button, Group, Loader, Menu, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { RiArrowDownSLine, RiLogoutBoxRLine } from "@remixicon/react";
import { useSelector } from "@xstate/react";
import type { HostContextValue } from "../../../shared/interfaces";
import type { OnboardingActor } from "../../../shared/onboarding";

function OnboardingIndicator({ onboarding }: { onboarding: OnboardingActor }) {
  const working = useSelector(onboarding, (snapshot) => snapshot.hasTag("busy"));
  const failure = useSelector(onboarding, (snapshot) =>
    (snapshot.can({ type: "RETRY" }) ? snapshot.context.error ?? "Unknown error." : null));

  if (working) {
    return (
      <Badge color="yellow" leftSection={<Loader color="yellow" size={10} type="oval" />}>
        Setting up your account
      </Badge>
    );
  }

  if (failure) {
    return (
      <Tooltip label={failure}>
        <Button color="red" onClick={() => onboarding.send({ type: "RETRY" })} size="compact-xs" variant="light">
          Account setup failed · Retry
        </Button>
      </Tooltip>
    );
  }

  return null;
}

function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join("");
}

/**
 * Sign-in state in the header. Without a host (the studio served on its own) there is no account to sign in
 * to, and the control says so rather than offering a button that cannot work.
 */
export function AccountControls({ context }: { context: HostContextValue | undefined }) {
  if (!context) {
    return (
      <Tooltip label="Served without the dashboard host: the studio works, account pages need the host.">
        <Badge color="gray" variant="outline">
          Local studio
        </Badge>
      </Tooltip>
    );
  }

  if (!context.isSignedIn) {
    return (
      <Button onClick={() => void context.signIn()} size="xs">
        Sign in
      </Button>
    );
  }

  const name = context.activeAccount?.name ?? context.activeAccount?.username ?? "Account";

  return (
    <Group gap="sm" wrap="nowrap">
      <OnboardingIndicator onboarding={context.onboarding} />
      <Menu position="bottom-end">
        <Menu.Target>
          <UnstyledButton aria-label="Account menu">
            <Group gap={6} wrap="nowrap">
              <Avatar color="coral" radius="xl" size="sm">
                {initials(name)}
              </Avatar>
              <Text fw={500} size="sm" visibleFrom="sm">
                {name}
              </Text>
              <RiArrowDownSLine size={16} />
            </Group>
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{context.activeAccount?.username ?? name}</Menu.Label>
          <Menu.Item leftSection={<RiLogoutBoxRLine size={16} />} onClick={() => void context.signOut()}>
            Sign out
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
    </Group>
  );
}
