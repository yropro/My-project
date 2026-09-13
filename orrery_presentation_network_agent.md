# Orrery Reusable Presentation Networking Agent — Historical Pointer

**Status:** SUPERSEDED / HISTORICAL  
**Original role:** architecture/design handoff for the Orrery reusable presentation-network migration  
**Implementation completed through:** `018404b2af23927bf37b827a932f1a4cfac3b980`

The architecture proposed by the original version of this document has now been implemented and refined in live source.

Do **not** use this file as the current implementation specification.

Use, in order:

1. newest explicit user requirement,
2. newest exact live source on `skill-trees`,
3. `ORRERY_PRESENTATION_NETWORK_STANDARD.md`,
4. `ORRERY_SPELL_LIFETIME_STANDARD.md`,
5. `ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md`,
6. shared Core networking / architecture standards.

The canonical current presentation architecture is:

```text
CoreNetwork
    ↓
slot 6: stable Orrery casting/satellite presentation
    ↓
OrreryPresentationNetwork
    six fixed 32-byte records
    records 0..5 -> Core slots 7..12
    ↓
explicit spell/effect codecs
    Shatterbolt -> records 0..2
    Plasma stroke -> record 3
    Plasma Burn refresh -> records 4..5
```

Important current rules:

- gameplay remains owner-authoritative;
- presentation failure never changes gameplay correctness;
- presentation is sampled at the actual Core ship-state send boundary;
- spell gameplay/fixed ticks do not own Core send hooks;
- all six presentation records are centrally registered;
- multipart groups validate completely before remote presentation mutation;
- generation identity protects replacement instances from stale/late presentation;
- Shatterbolt retains bounded cumulative impact-history semantics;
- Plasma Burn retains bounded rotating visual-refresh semantics;
- Plasma refresh batch size is presentation bookkeeping only and is **not** a gameplay spread/infection limit;
- new spells should reuse the fixed bank rather than claiming permanent Core slots.

For exact framing, byte budgets, integration checklist, and current verification status, read `ORRERY_PRESENTATION_NETWORK_STANDARD.md`.

If historical rationale from the original long-form proposal is ever needed, recover it from Git history rather than treating it as live guidance.
