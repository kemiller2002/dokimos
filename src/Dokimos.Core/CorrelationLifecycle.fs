namespace Dokimos.Core

type CorrelationIdentity =
    { Path: string
      Kind: CorrelationKind }

type CorrelationPresence =
    | CorrelationPresent of CorrelatedEvidence
    | CorrelationAbsent
    | CorrelationUnknown

type CorrelationTransition =
    | CorrelationIntroduced
    | CorrelationPersistent
    | CorrelationResolved

module CorrelationLifecycle =
    let transition previous current =
        match previous,current with
        | CorrelationUnknown, _ | _, CorrelationUnknown -> None
        | CorrelationAbsent, CorrelationPresent _ -> Some CorrelationIntroduced
        | CorrelationPresent _, CorrelationPresent _ -> Some CorrelationPersistent
        | CorrelationPresent _, CorrelationAbsent -> Some CorrelationResolved
        | CorrelationAbsent, CorrelationAbsent -> None
