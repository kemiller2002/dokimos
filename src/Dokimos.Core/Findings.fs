namespace Dokimos.Core

open Dokimos.Domain

type FindingPresence =
    | Present of magnitude: decimal option
    | Absent
    | Unknown

module Findings =
    let transition previous current =
        match previous, current with
        | Unknown, _
        | _, Unknown -> None
        | Absent, Present _ -> Some Introduced
        | Present _, Absent -> Some Resolved
        | Absent, Absent -> None
        | Present oldMagnitude, Present newMagnitude ->
            match oldMagnitude, newMagnitude with
            | Some oldValue, Some newValue when newValue < oldValue -> Some Improved
            | Some oldValue, Some newValue when newValue > oldValue -> Some Regressed
            | _ -> Some Persistent

    let resurfaced previouslyResolved current =
        match previouslyResolved, current with
        | true, Present _ -> Some Resurfaced
        | _ -> None
