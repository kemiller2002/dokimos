namespace Approval

/// Pure decision logic for the approval workflow (APR-001, APR-004).
module Domain =

    /// A new request: Draft, version 0, empty note.
    let create (id: string) (title: string) : Request =
        { Id = id
          Title = title
          State = Draft
          Version = 0
          Note = "" }

    let private advance state note (request: Request) =
        { request with
            State = state
            Note = note
            Version = request.Version + 1 }

    /// Applies one command. A legal command increments Version by one;
    /// every other (state, command) pair fails closed and leaves the request unchanged.
    let decide (command: Command) (request: Request) : Result<Request, DomainError> =
        match request.State, command with
        | Draft, Submit -> Ok(advance Submitted request.Note request)
        | Submitted, Approve note -> Ok(advance Approved note request)
        | Submitted, Reject note -> Ok(advance Rejected note request)
        | Rejected, Revise -> Ok(advance Draft request.Note request)
        | state, cmd -> Error(InvalidTransition(state, cmd))
