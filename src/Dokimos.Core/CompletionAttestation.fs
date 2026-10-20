namespace Dokimos.Core

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// Attested completion evidence (REL-001 F1; run-02 contracts I4 and I6).
///
/// A host verifier signs one `echelon.verification-receipt/1` payload per
/// validation and wraps it in a DSSE v1 envelope. The signature is ECDSA
/// P-256 over SHA-256 in IEEE P1363 (r||s) form, computed over the DSSE
/// pre-authentication encoding (PAE) of the exact payload bytes. This module
/// is pure: it never reads the clock or the file system, and it never
/// re-serializes the payload.
module CompletionAttestation =
    [<Literal>]
    let PayloadType = "application/vnd.echelon.verification-receipt.v1+json"

    [<Literal>]
    let ReceiptSchemaVersion = "echelon.verification-receipt/1"

    [<Literal>]
    let KeyAlgorithm = "ecdsa-p256-sha256"

    /// A verifier key the host trusts, as declared by the authority.
    /// `PublicKey` is the base64 SubjectPublicKeyInfo DER; `KeyId` is
    /// `"sha256:" + hex(SHA-256(SPKI DER))`.
    type TrustedVerifierKey =
        { KeyId: string
          Algorithm: string
          PublicKey: string }

    type ReceiptStatus =
        | ReceiptPassed
        | ReceiptFailed
        | ReceiptUnavailable

    type VerifierIdentity =
        { Id: string
          Version: string
          KeyId: string }

    /// The I4 payload. Every member is required; `ExitCode` may be JSON null.
    type ReceiptPayload =
        { SchemaVersion: string
          ReceiptId: Guid
          WorkItemId: string
          Claimant: string
          FencingGeneration: int64
          Commit: string
          TreeId: string
          OrdoContractDigest: string
          ValidationPolicyId: string
          ValidationPolicyDigest: string
          Validation: string
          EvidenceId: string
          Status: ReceiptStatus
          ExitCode: int option
          Command: string list
          OutputSha256: string
          StartedAt: DateTimeOffset
          ObservedAt: DateTimeOffset
          DurationMs: int64
          Verifier: VerifierIdentity }

    /// Why an envelope could not be authenticated. Binding, status and
    /// freshness are judged by the caller against its authority.
    type AttestationFailure =
        | Malformed of string
        | UntrustedKey of string
        | InvalidSignature of string

    /// An authenticated receipt: `Identity` is `"sha256:" + hex(SHA-256(payload bytes))`.
    type AttestedReceipt =
        { Identity: string
          KeyId: string
          Payload: ReceiptPayload }

    type private Signature = { KeyId: string; Sig: string }

    type private Envelope =
        { PayloadType: string
          Payload: byte[]
          Signatures: Signature list }

    let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

    /// `"sha256:" + lowercase hex` of the given bytes.
    let sha256Identity (bytes: byte[]) = "sha256:" + hex (SHA256.HashData bytes)

    /// DSSE v1 pre-authentication encoding:
    /// `"DSSEv1" SP len(type) SP type SP len(payload) SP payload`, lengths in
    /// ASCII decimal bytes.
    let pae (payloadType: string) (payload: byte[]) =
        let typeBytes = Encoding.UTF8.GetBytes payloadType
        let header (text: string) = Encoding.ASCII.GetBytes text
        Array.concat
            [ header ("DSSEv1 " + string typeBytes.Length + " ")
              typeBytes
              header (" " + string payload.Length + " ")
              payload ]

    let private isHex length (value: string) =
        not (Object.ReferenceEquals(value, null)) && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    let isSha256Digest (value: string) =
        not (Object.ReferenceEquals(value, null))
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && isHex 64 (value.Substring 7)

    let private isObjectId value = isHex 40 value || isHex 64 value

    let private base64 (text: string) =
        try
            Ok(Convert.FromBase64String text)
        with :? FormatException ->
            Error "is not valid base64"

    let private p256Oid = "1.2.840.10045.3.1.7"

    /// Imports a P-256 SubjectPublicKeyInfo, refusing trailing data and other curves.
    let private importP256 (spki: byte[]) =
        let key = ECDsa.Create()
        try
            let mutable read = 0
            key.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>(spki), &read)
            let curve = key.ExportParameters(false).Curve
            let named =
                curve.IsNamed
                && (curve.Oid.Value = p256Oid
                    || List.contains curve.Oid.FriendlyName [ "nistP256"; "ECDSA_P256"; "prime256v1"; "secp256r1" ])
            if read <> spki.Length then
                key.Dispose()
                Error "SubjectPublicKeyInfo has trailing data"
            elif not named || key.KeySize <> 256 then
                key.Dispose()
                Error "public key is not an ECDSA P-256 key"
            else
                Ok key
        with :? CryptographicException as problem ->
            key.Dispose()
            Error $"public key is not a SubjectPublicKeyInfo: {problem.Message}"

    /// Host-input check for one trusted key: algorithm, SPKI and the KeyId
    /// derived from it.
    let validateTrustedKey (key: TrustedVerifierKey) : Result<unit, string> =
        if key.Algorithm <> KeyAlgorithm then
            Error $"Algorithm must be '{KeyAlgorithm}', found '{key.Algorithm}'"
        elif not (isSha256Digest key.KeyId) then
            Error "KeyId must be sha256:<64 hex>"
        else
            match base64 key.PublicKey with
            | Error problem -> Error $"PublicKey {problem}"
            | Ok spki ->
                match importP256 spki with
                | Error problem -> Error $"PublicKey: {problem}"
                | Ok imported ->
                    imported.Dispose()
                    let derived = sha256Identity spki
                    if String.Equals(derived, key.KeyId, StringComparison.OrdinalIgnoreCase) then Ok()
                    else Error $"KeyId '{key.KeyId}' does not equal the SubjectPublicKeyInfo digest '{derived}'"

    let private verifySignature (key: TrustedVerifierKey) (message: byte[]) (signature: byte[]) =
        signature.Length = 64
        && (match base64 key.PublicKey |> Result.bind importP256 with
            | Ok imported ->
                use verifier = imported
                verifier.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            | Error _ -> false)

    /// Object member check: required members present, no duplicates; unknown
    /// members refused when `strict`.
    let private members strict (required: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error $"expected object, found {element.ValueKind}"
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            match names |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(name, _) -> Error $"duplicate JSON member '{name}'"
            | None ->
                match names |> List.tryFind (fun name -> strict && not (List.contains name required)) with
                | Some name -> Error $"unknown JSON member '{name}'"
                | None ->
                    match required |> List.tryFind (fun name -> not (List.contains name names)) with
                    | Some name -> Error $"missing required field '{name}'"
                    | None -> Ok()

    /// RFC 3339 instant with an explicit offset (offset-less text would depend
    /// on the verifying host's time zone).
    let private instantPattern =
        Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)

    let instant: Json.Decoder<DateTimeOffset> =
        Json.string
        >> Result.bind (fun text ->
            match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, value when instantPattern.IsMatch text -> Ok value
            | _ -> Error $"expected an RFC 3339 timestamp with offset, found '{text}'")

    let private check predicate problem : Json.Decoder<string> =
        Json.string >> Result.bind (fun value -> if predicate value then Ok value else Error $"{problem}, found '{value}'")

    let private signature: Json.Decoder<Signature> =
        fun element ->
            Json.result {
                do! members true [ "keyid"; "sig" ] element
                let! keyId = Json.field "keyid" Json.nonEmptyString element
                let! sigText = Json.field "sig" Json.nonEmptyString element
                return { KeyId = keyId; Sig = sigText }
            }

    let private envelope: Json.Decoder<Envelope> =
        fun element ->
            Json.result {
                do! members true [ "payloadType"; "payload"; "signatures" ] element
                let! payloadType = Json.field "payloadType" Json.string element
                let! payloadText = Json.field "payload" Json.string element
                let! payload = base64 payloadText |> Result.mapError (fun problem -> $"payload {problem}")
                let! signatures = Json.field "signatures" (Json.list signature) element
                if signatures.IsEmpty then return! Error "signatures: at least one signature is required"
                else return { PayloadType = payloadType; Payload = payload; Signatures = signatures }
            }

    let private receiptStatus =
        Json.oneOf [ "passed", ReceiptPassed; "failed", ReceiptFailed; "unavailable", ReceiptUnavailable ]

    let private nullableInt: Json.Decoder<int option> =
        fun element ->
            if element.ValueKind = JsonValueKind.Null then Ok None else Json.int element |> Result.map Some

    let private verifier: Json.Decoder<VerifierIdentity> =
        fun element ->
            Json.result {
                do! members false [ "id"; "version"; "keyId" ] element
                let! id = Json.field "id" Json.nonEmptyString element
                let! version = Json.field "version" Json.nonEmptyString element
                let! keyId = Json.field "keyId" Json.nonEmptyString element
                return { Id = id; Version = version; KeyId = keyId }
            }

    let private uuid: Json.Decoder<Guid> =
        Json.string
        >> Result.bind (fun text ->
            match Guid.TryParseExact(text, "D") with
            | true, value -> Ok value
            | _ -> Error $"expected a UUID, found '{text}'")

    /// Payload members are all required and typed. Additional signed members
    /// are tolerated (they are authenticated and bind nothing), duplicates are
    /// refused.
    let private payload: Json.Decoder<ReceiptPayload> =
        fun element ->
            Json.result {
                do!
                    members false
                        [ "schemaVersion"; "receiptId"; "workItemId"; "claimant"; "fencingGeneration"; "commit"; "treeId"
                          "ordoContractDigest"; "validationPolicyId"; "validationPolicyDigest"; "validation"; "evidenceId"
                          "status"; "exitCode"; "command"; "outputSha256"; "startedAt"; "observedAt"; "durationMs"; "verifier" ]
                        element
                let! schema = Json.field "schemaVersion" (check ((=) ReceiptSchemaVersion) $"expected '{ReceiptSchemaVersion}'") element
                let! receiptId = Json.field "receiptId" uuid element
                let! workItem = Json.field "workItemId" Json.nonEmptyString element
                let! claimant = Json.field "claimant" Json.nonEmptyString element
                let! generation = Json.field "fencingGeneration" Json.int64 element
                let! commit = Json.field "commit" (check isObjectId "expected a full hex Git object ID") element
                let! tree = Json.field "treeId" (check isObjectId "expected a full hex Git object ID") element
                let! contract = Json.field "ordoContractDigest" (check isSha256Digest "expected sha256:<64 hex>") element
                let! policyId = Json.field "validationPolicyId" Json.nonEmptyString element
                let! policy = Json.field "validationPolicyDigest" (check isSha256Digest "expected sha256:<64 hex>") element
                let! validation = Json.field "validation" Json.nonEmptyString element
                let! evidenceId = Json.field "evidenceId" Json.nonEmptyString element
                let! status = Json.field "status" receiptStatus element
                let! exitCode = Json.field "exitCode" nullableInt element
                let! command = Json.field "command" (Json.list Json.string) element
                let! output = Json.field "outputSha256" Json.string element
                let! started = Json.field "startedAt" instant element
                let! observed = Json.field "observedAt" instant element
                let! duration = Json.field "durationMs" Json.int64 element
                let! identity = Json.field "verifier" verifier element
                return
                    { SchemaVersion = schema; ReceiptId = receiptId; WorkItemId = workItem; Claimant = claimant
                      FencingGeneration = generation; Commit = commit; TreeId = tree; OrdoContractDigest = contract
                      ValidationPolicyId = policyId; ValidationPolicyDigest = policy; Validation = validation
                      EvidenceId = evidenceId; Status = status; ExitCode = exitCode; Command = command
                      OutputSha256 = output; StartedAt = started; ObservedAt = observed; DurationMs = duration
                      Verifier = identity }
            }

    let private parseWith (decoder: Json.Decoder<'a>) (read: unit -> JsonDocument) =
        try
            use document = read ()
            decoder document.RootElement
        with
        | :? JsonException as problem -> Error problem.Message
        | :? ArgumentException as problem -> Error problem.Message

    /// Authenticates one envelope (the exact JSON object text of an
    /// observation's `Attestation`) against the trusted keys. Order: envelope
    /// shape, key trust, signature over PAE, then payload parsing — the
    /// payload is never interpreted before its signature verifies.
    let verify (trusted: TrustedVerifierKey list) (envelopeJson: string) : Result<AttestedReceipt, AttestationFailure> =
        match parseWith envelope (fun () -> JsonDocument.Parse envelopeJson) with
        | Error problem -> Error(Malformed $"envelope: {problem}")
        | Ok parsed when parsed.PayloadType <> PayloadType ->
            Error(Malformed $"envelope: payloadType must be '{PayloadType}', found '{parsed.PayloadType}'")
        | Ok parsed ->
            let candidates =
                parsed.Signatures
                |> List.choose (fun signature ->
                    trusted
                    |> List.tryFind (fun key -> String.Equals(key.KeyId, signature.KeyId, StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun key -> key, signature))
            if candidates.IsEmpty then
                let named = parsed.Signatures |> List.map _.KeyId |> List.distinct |> List.sort |> String.concat ", "
                Error(UntrustedKey $"no signature is by a trusted verifier key (keyid: {named})")
            else
                match candidates |> List.tryFind (fun (_, signature) -> match base64 signature.Sig with Ok _ -> false | Error _ -> true) with
                | Some(_, signature) -> Error(Malformed $"envelope: signature by '{signature.KeyId}' is not valid base64")
                | None ->
                    let message = pae parsed.PayloadType parsed.Payload
                    let verified =
                        candidates
                        |> List.tryFind (fun (key, signature) ->
                            match base64 signature.Sig with
                            | Ok bytes -> verifySignature key message bytes
                            | Error _ -> false)
                    match verified with
                    | None ->
                        let named = candidates |> List.map (fun (key, _) -> key.KeyId) |> List.distinct |> List.sort |> String.concat ", "
                        Error(InvalidSignature $"signature does not verify over the DSSE PAE under trusted key {named}")
                    | Some(key, _) ->
                        match parseWith payload (fun () -> JsonDocument.Parse(ReadOnlyMemory<byte>(parsed.Payload))) with
                        | Error problem -> Error(Malformed $"payload: {problem}")
                        | Ok receipt ->
                            Ok
                                { Identity = sha256Identity parsed.Payload
                                  KeyId = key.KeyId
                                  Payload = receipt }
