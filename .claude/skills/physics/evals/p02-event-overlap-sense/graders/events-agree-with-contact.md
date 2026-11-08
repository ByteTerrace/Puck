---
type: llm
weight: 1
---

The response has the event ask the same overlap sense the contact solver uses, so an event and a contact never disagree.

PASS if it rejects a separate bounds-based predicate in the event feed and says events should use the dynamic-contact kernel's overlap query, which reports overlap exactly when the narrowphase would push the pair apart, giving the event and the physical contact one answer. Example: "Don't write a second test: call `FixedDynamicBodyContacts.Overlaps` with the bodies' scaled volumes; it uses the same closest segment-box features as contact, so an overlap event never fires where no contact exists."

FAIL if it endorses the world-bounds test, or writes a new capsule/box predicate of its own, even an exact one. Example: "AABB overlap is fine for events; refine it later with a capsule-box distance function in the feed."
