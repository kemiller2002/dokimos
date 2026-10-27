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

## Attested mode (additive to authority/1, Dokimos 0.7.0)

REL-001 case C3b showed that observations supplied through the claimant's own
host process can be forged by the agent. In attested mode Dokimos itself
checks a signature from a host verifier whose private key the agent never
holds (run-02 contracts I4 and I6).

| Member | Where | Meaning |
| --- | --- | --- |
| `TrustedVerifierKeys` | authority | `[ { "KeyId": "sha256:…", "Algorithm": "ecdsa-p256-sha256", "PublicKey": "<base64 SPKI DER>" } ]`. Non-empty selects attested mode. |
| `ValidationPolicyDigest` | authority | `sha256:` digest of the host-owned `echelon.validation-policy/1` file. |
| `Attestation` | evidence observation | The DSSE v1 envelope (`payloadType`, base64 `payload`, `signatures: [ { keyid, sig } ]`) of an `echelon.verification-receipt/1`. |

Without `TrustedVerifierKeys` (absent or `[]`) verification is exactly as in
0.6.0: `ValidationPolicyDigest` and any `Attestation` are accepted but not
consulted, and receipts are unchanged.

Intake (exit `3`, no receipt) refuses a trusted key whose `Algorithm` is not
`ecdsa-p256-sha256`, whose `PublicKey` is not a base64 ECDSA P-256
SubjectPublicKeyInfo (no trailing data), whose `KeyId` is not
`"sha256:" + hex(SHA-256(SPKI DER))`, or whose `KeyId` repeats; a malformed
`ValidationPolicyDigest`; and an `Attestation` that is not a JSON object.

In attested mode every observation cited by a required validation must carry
an `Attestation`, judged in this order:

1. The envelope has exactly `payloadType`, `payload` and a non-empty
   `signatures` list of `{ keyid, sig }`, with standard base64, and
   `payloadType` is `application/vnd.echelon.verification-receipt.v1+json`
   (else `ATTESTATION_MALFORMED`).
2. At least one signature names a trusted `KeyId` (else
   `ATTESTATION_UNTRUSTED_KEY`).
3. One such signature verifies: ECDSA P-256 / SHA-256, IEEE P1363 `r||s`
   (64 bytes), over the DSSE v1 PAE
   `"DSSEv1" SP len(type) SP type SP len(payload) SP payload` of the exact
   decoded payload bytes (else `ATTESTATION_INVALID_SIGNATURE`). The payload is
   never parsed before its signature verifies, and never re-serialized.
4. The payload is a JSON object with every I4 member, correctly typed, no
   duplicates and `schemaVersion` `echelon.verification-receipt/1` (else
   `ATTESTATION_MALFORMED`). Additional signed members are tolerated and bind
   nothing.
5. Binding (each mismatch is its own `ATTESTATION_BINDING_MISMATCH`, detail
   prefixed with the member): the observation `Digest` equals the receipt
   identity `"sha256:" + hex(SHA-256(payload bytes))`; `workItemId` =
   `WorkItemId`; `fencingGeneration` = `FencingGeneration`; `commit` =
   `ExpectedVerificationRevision` and `ExpectedCommit`; `ordoContractDigest` =
   `ExpectedOrdoContractDigest`; `validationPolicyDigest` =
   `ValidationPolicyDigest` (an authority without one cannot bind);
   `validation` = the observation's `Validation` (an observation without one
   cannot bind); `evidenceId` = the observation's `Id`. Hex-valued members
   compare ignoring case; identifiers compare exactly.
6. `status` is `passed` (else `ATTESTATION_NOT_PASSED`).
7. `observedAt` lies in `[ClaimAcquiredAt, LeaseExpiresAt)` when those are
   present and is not after `EvaluatedAt` when present (else
   `ATTESTATION_STALE`). Reusability does not exempt a receipt.

An observation without an attestation is `EVIDENCE_UNATTESTED`. The 0.6.0
checks (including `ObservedAt` freshness) still apply to every observation;
hosts should set `ObservedAt` to the receipt's `observedAt`. One failing
observation refuses the whole completion even when others are valid.
Diagnostics use the evidence ID as subject and remain sorted and
deterministic: Dokimos never reads the clock.

## Reusing evidence

Evidence can be reused without rerunning its producing test only when the host
marks the observation `Reusable: true`, independently observed it as `passed`,
and its ID, digest, and immutable revision exactly match the packet. The
receipt lists those IDs in `ReusedEvidenceIds`. A passing result from another
revision, a claimant-only assertion, a failed/unavailable observation, or a
digest mismatch is never reusable.

Evidence produced at an earlier revision can be carried forward when its
content-addressed identity is unchanged ([evidence-reuse.md](evidence-reuse.md)).
The observation then carries an optional `Provenance` object
(`EvidenceIdentity`, `Freshness`, `ProducedAtRevision`). With `Provenance`
present: evidence produced at another revision without `Reusable: true` is
`REUSE_UNDECLARED`; carried `per-commit` evidence is `REUSE_STALE_COMMIT`;
`live` evidence that is reusable or carried is `REUSE_LIVE_EVIDENCE` and is
never listed in `ReusedEvidenceIds`. Absent `Provenance`, behaviour is as in
0.7.0. The receipt format is unchanged.

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
- `REUSE_UNDECLARED`, `REUSE_STALE_COMMIT`, `REUSE_LIVE_EVIDENCE`: evidence
  carried across revisions against its freshness policy.
- `EVIDENCE_UNATTESTED`, `ATTESTATION_UNTRUSTED_KEY`,
  `ATTESTATION_INVALID_SIGNATURE`, `ATTESTATION_MALFORMED`,
  `ATTESTATION_BINDING_MISMATCH`, `ATTESTATION_NOT_PASSED`,
  `ATTESTATION_STALE`: attested mode refusals (Praxis mirrors them as
  `PRX.COMPLETION.RECEIPT_*`).

Diagnostics are sorted by code, subject, and detail so equivalent inputs yield
stable machine-readable receipts suitable for Praxis enforcement and audits.
