import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { Button, Group, Modal, Stack, Text } from "@mantine/core";

type Confirm = (message: string) => Promise<boolean>;

const ConfirmationContext = createContext<Confirm>(async () => false);

/** The studio's confirm: resolves `true` when the author continues, `false` when they keep the current document. */
export const useStudioConfirmation = () => useContext(ConfirmationContext);

/**
 * Provides `useStudioConfirmation` to the studio and renders its one confirmation dialog. A new
 * request while one is open declines the earlier one; unmounting declines whatever is pending.
 */
export function StudioConfirmation({ children }: { children: ReactNode }) {
  const [message, setMessage] = useState<string | null>(null);
  const pending = useRef<((accepted: boolean) => void) | null>(null);
  // A stable identity: consumers list `confirm` in effect dependencies (the navigation guard).
  const confirm = useCallback<Confirm>(text => {
    pending.current?.(false);
    setMessage(text);
    return new Promise(resolve => { pending.current = resolve; });
  }, []);
  const finish = (accepted: boolean) => {
    pending.current?.(accepted);
    pending.current = null;
    setMessage(null);
  };
  useEffect(() => () => pending.current?.(false), []);

  return (
    <ConfirmationContext value={confirm}>
      {children}
      <Modal
        opened={message !== null}
        onClose={() => finish(false)}
        title="Confirm document change"
        zIndex={400}
        closeButtonProps={{ "aria-label": "Cancel document change" }}
      >
        <Stack gap="lg">
          <Text size="sm">{message}</Text>
          <Group justify="flex-end" gap="xs">
            <Button data-autofocus variant="default" onClick={() => finish(false)}>Keep current document</Button>
            <Button color="red" onClick={() => finish(true)}>Continue</Button>
          </Group>
        </Stack>
      </Modal>
    </ConfirmationContext>
  );
}
