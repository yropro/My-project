# Development documentation

Reviewed against the local source on 2026-09-14. Start here when adding skills or
resuming work from an old handoff.

## Current references, by subject

| Subject | Maintained reference |
|---|---|
| Networking and remote presentation | [Networking for skill authors](Assets/Leviathan/Content/Scripts/NETWORKING.md) |
| Skill mechanics, focus inheritance, geometry and tuning | [Spell design standard](ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md) |
| Authoritative persistent-spell ticking and teardown | [Gameplay lifetime standard](ORRERY_SPELL_LIFETIME_STANDARD.md) |
| Installed-game API compatibility | [Native API compatibility](NATIVE_API_COMPATIBILITY.md) |
| Automated network checks and their limits | [Networking validation](Assets/Leviathan/Content/Scripts/NETWORKING.md#validation) |

These documents have different scopes; none should duplicate another's protocol
tables. Networking belongs in Core and the per-mod networking adapters. Skill
mechanics supply semantic state and visual behavior. A designer is not expected
to design packet formats or engine networking hooks.

## Resolving disagreements

- Follow the user's explicit requirements and accepted design decisions.
- Use the installed game assembly as evidence for native signatures. The editor
  reference can lag. Use current source for implemented behavior and exact symbols.
- Use the maintained reference for the subject being changed. The spell design
  standard does not override the networking guide on transport or callbacks.
- Code can contain bugs, and documents can be stale. A disagreement is something
  to investigate; it is not permission to silently change balance or intended behavior.
- Historical audits, proposals and handoffs explain earlier decisions. Their
  checklists and claims do not establish the current feature or validation status.

## Historical material

| Document | Status |
|---|---|
| `ORRERY_IMPLEMENTATION_HANDOFF.md` | 2026-09-11 foundation snapshot |
| `ORRERY_PRECOMPILE_AUDIT.md` | 2026-09-12 static audit; contains old native signatures |
| `ORRERY_STATUS_D_TBD.md` | 2026-09-13 status snapshot, not the current backlog |
| `ORRERY_PRESENTATION_NETWORK_STANDARD.md` | Redirect to the maintained networking guide |
| `orrery_presentation_network_agent.md` | Historical pointer, not an implementation plan |
| [Earlier network standard](Documentation/History/ORRERY_PRESENTATION_NETWORK_STANDARD_2026-09-13.md) | Archived six-record/send-prefix design |

OpusMax's CoreWire review lives in the separate Notes_Readme_Documentation folder.
Its proposal and patch are review artifacts; do not apply them over an integrated
checkout without comparing the current source.

## Keeping guidance useful

Put tuning values in `OrrerySpellCompendium`, `OrrerySpellPower` or the owning
resolved-knob source. A dated design example is not a second tuning sheet. Record
behavioral decisions separately from temporary implementation workarounds.

Use explicit evidence labels: source inspected, standalone compiled, automated
checks passed, Unity editor tested, or two-peer tested. A selector lookup is not a
full Harmony patch test; test doubles are not a live transport or renderer.
A matching mod version string is not evidence of byte-identical development DLLs.

When changing an API, update its maintained guide and tests in the same change.
Keep old reports dated and labeled rather than adding another competing canonical
standard. Do not introduce approval requirements merely because an older agent
plan says to pause; apply the user's current authorization and task scope.
