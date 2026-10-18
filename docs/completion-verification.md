# Independent completion verification

`dokimos completion verify` checks a Praxis completion claim against authority
and evidence supplied independently by the host. The completion packet is
untrusted input: its claimed requirements, decisions, conflicts, tests, and
digests never establish their own truth.

```text
dokimos completion verify --packet packet.json --authority authority.json
```

The input contracts are
[`praxis-completion-packet.schema.json`](../schemas/praxis-completion-packet.schema.json)
and
[`dokimos-completion-authority.schema.json`](../schemas/dokimos-completion-authority.schema.json).
The command writes a
[`dokimos-completion-verification.schema.json`](../schemas/dokimos-completion-verification.schema.json)
receipt to stdout. A verified receipt exits `0`; a semantic refusal exits `4`;
missing or malformed evidence exits `3`. Intake rejects unknown and duplicate
JSON members.

## Verification boundary

The authority binds one work item, claim token, fencing generation, expected
commit, verification revision, Ordo constructibility contract and digest. It
also supplies the exact required requirement, decision, conflict-resolution,
and validation sets. Each claimed evidence item must match an independent host
observation by ID, SHA-256 digest, immutable revision, and passing status.
Missing and extra references both fail closed. A consumed canonical packet
digest is a replay and concurrent or obsolete claim tokens and fencing
generations are rejected.

The canonical packet digest frames and sorts set-like values, so JSON member
or array order cannot change identity. Repeated values remain in the digest
and are refused rather than silently normalized.

## Reusing evidence

Evidence can be reused without rerunning its producing test only when the host
marks the observation `Reusable: true`, independently observed it as `passed`,
and its ID, digest, and immutable revision exactly match the packet. The
receipt lists those IDs in `ReusedEvidenceIds`. A passing result from another
revision, a claimant-only assertion, a failed/unavailable observation, or a
digest mismatch is never reusable.

## Diagnostics

Receipts contain stable `DOK.COMPLETION.*` diagnostic codes. The principal
families are:

- `STALE_*`, `REPLAYED_PACKET`: obsolete or already-consumed completion.
- `MISSING_*`, `UNKNOWN_*`, `DUPLICATE_*`: incomplete, unauthorized, or
  ambiguous traceability.
- `UNSUPPORTED_*`, `ORDO_CONTRACT_DIGEST_MISMATCH`: incompatible contract
  semantics.
- `VALIDATION_*`: failed, unavailable, or unevidenced claimed checks.
- `EVIDENCE_*`, `INVALID_EVIDENCE_DIGEST`: independent evidence is absent,
  stale, forged, failed, unavailable, or malformed.

Diagnostics are sorted by code, subject, and detail so equivalent inputs yield
stable machine-readable receipts suitable for Praxis enforcement and audits.
