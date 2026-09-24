import { Center, Loader, Stack, Text } from "@mantine/core";

/** The quiet placeholder a section shows while its code loads. */
export function SectionFallback({ label }: { label: string }) {
  return (
    <Center mih={240}>
      <Stack align="center" gap="xs" role="status">
        <Loader size="sm" />
        <Text c="dimmed" size="sm">
          {label}
        </Text>
      </Stack>
    </Center>
  );
}
