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

Input failures never produce a receipt: an absent, unreadable, or non-file
path, malformed JSON, unknown or duplicate members, unknown enum values, JSON
`null` for an optional member, fractional fencing generations, and timestamps
without an explicit offset all exit `3`. Semantic refusals exit `4` with a
schema-valid receipt, including when the packet's own commit, revision, or
digests are malformed (the receipt echoes the claimed commit and revision
verbatim; they are only guaranteed to be immutable IDs when `Verdict` is
`verified`, and `EvidenceDigests` lists only well-formed accepted digests).

Replay protection compares `ConsumedPacketDigests` against the receipt's
`PacketDigest` ignoring hex case; a malformed consumed digest is refused with
`INVALID_CONSUMED_PACKET_DIGEST` rather than silently never matching. Praxis
should record the receipt's `PacketDigest` as the consumed digest.

## Optional freshness and validation binding (additive to authority/1)

All members below are optional. A `dokimos.completion-authority/1` document
without them is accepted and verified exactly as before; no version bump is
needed because absence means "not asserted".

| Member | Where | Enforcement when present |
| --- | --- | --- |
| `ClaimAcquiredAt` | authority | Non-reusable cited evidence observed earlier is `EVIDENCE_STALE`; `Reusable: true` revision-matched evidence remains reusable. |
| `LeaseExpiresAt` | authority | Requires `EvaluatedAt` (else `FRESHNESS_UNDETERMINED`). Evaluation at or after expiry is `STALE_LEASE`; evidence observed at or after expiry is `EVIDENCE_STALE`. Expiry not after acquisition is `INVALID_FRESHNESS_WINDOW`. |
| `EvaluatedAt` | authority | Host-supplied instant; Dokimos never reads the clock. Evaluation before acquisition, or evidence observed after evaluation, is `INVALID_FRESHNESS_WINDOW`. |
| `ObservedAt` | evidence observation | Required for every cited observation when `ClaimAcquiredAt` or `LeaseExpiresAt` is present (else `FRESHNESS_UNDETERMINED`). |
| `Validation` | evidence observation | Only the named validation may cite it (`EVIDENCE_VALIDATION_MISMATCH`); a validation the host bound evidence to must cite at least one such item (`VALIDATION_UNBOUND`). |

Timestamps are RFC 3339 with an explicit `Z` or `±hh:mm` offset. Freshness
members are authority-only and do not change the packet or its digest.

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
- `VALIDATION_*`: failed, unavailable, unevidenced, or unbound claimed checks.
- `STALE_LEASE`, `EVIDENCE_STALE`, `FRESHNESS_UNDETERMINED`,
  `INVALID_FRESHNESS_WINDOW`: optional freshness window violations.
- `INVALID_*`: malformed fence, commit, revision, digest, or consumed digest.
- `EVIDENCE_*`, `INVALID_EVIDENCE_DIGEST`: independent evidence is absent,
  stale, forged, failed, unavailable, or malformed.

Diagnostics are sorted by code, subject, and detail so equivalent inputs yield
stable machine-readable receipts suitable for Praxis enforcement and audits.
