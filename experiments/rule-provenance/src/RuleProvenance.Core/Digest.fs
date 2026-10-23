namespace RuleProvenance

open System
open System.Security.Cryptography
open System.Text

/// SHA-256 digests in the contract's `sha256:<hex>` form.
module Digest =
    let ofBytes (bytes: byte[]) =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let ofString (text: string) = ofBytes (Encoding.UTF8.GetBytes text)

    /// The digest of no input. Used for traceDigest when no trace is supplied,
    /// so "no trace" is an explicit, comparable assumption rather than a gap.
    let empty = ofString ""

    /// Framed digest over the audited files, ordered by relative path (ordinal):
    /// for each file `<role>\n<path>\n<utf8 byte length>\n<content>\n`. Framing makes
    /// the digest unambiguous (no two file sets share a byte stream).
    let ofSources (files: SourceFile list) =
        files
        |> List.sortBy (fun f -> f.RelativePath, (match f.Role with Constructed -> 0 | Legacy -> 1))
        |> List.map (fun f ->
            let role = match f.Role with Constructed -> "constructed" | Legacy -> "legacy"
            let length = Encoding.UTF8.GetByteCount f.Text
            $"{role}\n{f.RelativePath}\n{length}\n{f.Text}\n")
        |> String.concat ""
        |> ofString
