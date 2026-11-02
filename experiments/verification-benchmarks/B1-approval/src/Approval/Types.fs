namespace Approval

type State =
    | Draft
    | Submitted
    | Approved
    | Rejected

type Command =
    | Submit
    | Approve of note: string
    | Reject of note: string
    | Revise

type Request =
    { Id: string
      Title: string
      State: State
      Version: int
      Note: string }

type DomainError = InvalidTransition of State * Command

type StoreError =
    | NotFound of id: string
    | Conflict of expected: int * actual: int
    | ReadBackMismatch
