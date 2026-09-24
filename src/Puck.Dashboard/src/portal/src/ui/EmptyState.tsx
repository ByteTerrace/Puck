import { Stack, Text, ThemeIcon, Title } from "@mantine/core";
import type { ReactNode } from "react";

interface EmptyStateProps {
  action?: ReactNode;
  children: ReactNode;
  icon: ReactNode;
  title: string;
}

/** A centered explanation for a view with nothing to show yet, and the one action that changes that. */
export function EmptyState({ action, children, icon, title }: EmptyStateProps) {
  return (
    <Stack align="center" gap="sm" maw={420} mx="auto" py="xl" ta="center">
      <ThemeIcon radius="xl" size={48} variant="light">
        {icon}
      </ThemeIcon>
      <Title order={2} size="h3">
        {title}
      </Title>
      <Text c="dimmed" size="sm">
        {children}
      </Text>
      {action}
    </Stack>
  );
}
