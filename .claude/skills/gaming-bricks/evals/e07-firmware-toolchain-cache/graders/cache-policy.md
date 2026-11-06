---
type: llm
weight: 1
---

The answer rejects retaining generated BIOS bytes in Git. It identifies LLVM clang
and ld.lld as machine requirements, identifies AgbFirmwareCompiler and the shared
DerivedBytes counted cache as the runtime derivation path, and requires a named
missing-tool refusal with the machine-setup link. It explains that player publishing
or world prepare prewarms the same cache, so a shipped player needs no compiler.
Fail if it proposes a committed binary exception, silent fallback, or a second cache.
