import { Alert, Button, Code, Group, Modal, Select, Stack, Text, TextInput } from "@mantine/core";
import { RiAlertLine } from "@remixicon/react";
import { type ReactNode, useState } from "react";
import { SHARE_DURATIONS } from "./storage";

interface ConfirmDialogProps {
  children: ReactNode;
  confirmLabel: string;
  isWorking: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  opened: boolean;
  title: string;
}

/** Asks before an action that loses something: the consequence in a sentence, and a red confirm. */
export function ConfirmDialog({ children, confirmLabel, isWorking, onCancel, onConfirm, opened, title }: ConfirmDialogProps) {
  return (
    <Modal onClose={onCancel} opened={opened} title={title}>
      <Stack>
        <Text size="sm">{children}</Text>
        <Group justify="flex-end">
          <Button onClick={onCancel} variant="default">
            Cancel
          </Button>
          <Button color="red" loading={isWorking} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

interface PublishDialogProps {
  blobName: string | undefined;
  isPublishing: boolean;
  onCancel: () => void;
  onPublish: () => void;
  userObjectId: string;
}

/** Confirms moving a private file to a public address, and shows that address before it exists. */
export function PublishDialog({ blobName, isPublishing, onCancel, onPublish, userObjectId }: PublishDialogProps) {
  return (
    <Modal onClose={onCancel} opened={undefined !== blobName} title={`Publish ${blobName ?? ""}`}>
      <Stack>
        <Alert color="yellow" icon={<RiAlertLine size={18} />} variant="light">
          This <b>moves</b> the file out of your private storage. Anyone on the internet will be able to download it,
          and it will leave your files above until you unpublish it.
        </Alert>
        <div>
          <Text fw={600} size="sm">
            Its public address
          </Text>
          <Code block mt={4}>
            {`byteterrace.com/public/${userObjectId}/${(blobName ?? "").replace(/^private\//, "")}`}
          </Code>
        </div>
        <Group justify="flex-end">
          <Button onClick={onCancel} variant="default">
            Cancel
          </Button>
          <Button loading={isPublishing} onClick={onPublish}>
            Publish
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

interface ShareWithUserDialogProps {
  blobName: string | undefined;
  isSharing: boolean;
  onCancel: () => void;
  onShare: (recipient: string, hours: number) => void;
}

function ShareWithUserForm({ isSharing, onCancel, onShare }: Omit<ShareWithUserDialogProps, "blobName">) {
  const [recipient, setRecipient] = useState("");
  const [hours, setHours] = useState(168);

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();

        if (0 < recipient.trim().length) {
          onShare(recipient.trim(), hours);
        }
      }}
    >
      <Stack>
        <TextInput
          data-autofocus
          label="Email or sign-in name"
          onChange={(event) => setRecipient(event.currentTarget.value)}
          placeholder="user@example.com"
          value={recipient}
        />
        <Select
          allowDeselect={false}
          data={SHARE_DURATIONS.map((duration) => ({ label: duration.label, value: String(duration.hours) }))}
          label="Expires after"
          onChange={(value) => setHours(Number(value ?? 168))}
          value={String(hours)}
        />
        <Text c="dimmed" size="xs">
          The link only works for that person while they are signed in — forwarding it to anyone else does nothing. It
          cannot be revoked early, so pick the shortest expiry that works.
        </Text>
        <Group justify="flex-end">
          <Button onClick={onCancel} variant="default">
            Cancel
          </Button>
          <Button disabled={0 === recipient.trim().length} loading={isSharing} type="submit">
            Share
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/** Shares one file with one named user through a link that opens it in this portal. */
export function ShareWithUserDialog({ blobName, ...form }: ShareWithUserDialogProps) {
  return (
    <Modal onClose={form.onCancel} opened={undefined !== blobName} title={`Share ${blobName ?? ""} with a user`}>
      <ShareWithUserForm {...form} />
    </Modal>
  );
}
