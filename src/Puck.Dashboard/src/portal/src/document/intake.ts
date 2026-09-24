/**
 * The one size limit the studio applies itself: a local draft stays within 2 MB of UTF-8, so the browser library
 * never grows past what one storage write can hold. Everything about a source's meaning is the engine's job.
 */

export const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;

export class IntakeRefusal extends Error {
  constructor(message: string) {
    super(message);
    this.name = "IntakeRefusal";
  }
}

/** Refuses by name when `text`'s UTF-8 encoding exceeds the 2 MB cap. */
export function checkDocumentSize(text: string): void {
  const byteLength = new TextEncoder().encode(text).length;
  if (byteLength > MAX_DOCUMENT_BYTES) {
    throw new IntakeRefusal(`the draft is ${byteLength} bytes, over the ${MAX_DOCUMENT_BYTES} byte cap.`);
  }
}
