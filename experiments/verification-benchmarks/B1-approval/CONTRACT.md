# Benchmark 1 interface contract (frozen before implementation)

Both agents build against this contract and nothing else. Test agents do not read `src/`.

## F# (project `src/Approval/Approval.fsproj`, net10.0)

```fsharp
namespace Approval

type State = Draft | Submitted | Approved | Rejected

type Command =
    | Submit
    | Approve of note: string
    | Reject of note: string
    | Revise

type Request = { Id: string; Title: string; State: State; Version: int; Note: string }

type DomainError = InvalidTransition of State * Command

type StoreError =
    | NotFound of id: string
    | Conflict of expected: int * actual: int
    | ReadBackMismatch

module Domain =
    /// A new request: Draft, version 0, empty note.
    val create: id: string -> title: string -> Request
    /// Applies one command (APR-001, APR-004). A legal command increments Version by one.
    val decide: Command -> Request -> Result<Request, DomainError>

/// A file store rooted at a directory; one file per request: <root>/<id>.json.
type FileStore =
    new: root: string -> FileStore
    /// `expectedVersion` is the version currently stored (-1 for a request not yet stored).
    member Save: request: Request * expectedVersion: int -> Result<Request, StoreError>
    member Load: id: string -> Result<Request, StoreError>
    /// Test seam for APR-002-O2: transforms the bytes the read-back sees (identity by default).
    member ReadBackFilter: (byte[] -> byte[]) with get, set

module Web =
    /// Builds the web application (ASP.NET Core minimal hosting) serving the UI below,
    /// storing in `store`. `token` is the APPROVAL_TOKEN value.
    val build: store: FileStore -> token: string -> urls: string -> Microsoft.AspNetCore.Builder.WebApplication
```

The executable (`dotnet run --project src/Approval -- --urls http://127.0.0.1:PORT --data DIR`) reads `APPROVAL_TOKEN` from the environment.

## HTTP and DOM (server-rendered HTML; no client framework)

| Route | Behaviour |
|---|---|
| `GET /` | A form `form[data-testid=create]` with input `name=title` and `button[data-testid=create-submit]`. |
| `POST /requests` | Creates a request (id = a new GUID without dashes), saves it, and redirects 303 to `/requests/{id}`. |
| `GET /requests/{id}` | The request page. |
| `POST /requests/{id}/command` | Form fields `command` (`Submit`, `Approve`, `Reject` or `Revise`), `note`, and `version` (the version the page was rendered at). Saves with `expectedVersion = version` and redirects 303 to the request page. On a conflict, it answers 409 with the request page showing `[data-testid=conflict]`. On `InvalidTransition`, it answers 422 with `[data-testid=error]`. |

The request page shows:
- `[data-testid=title]`
- `[data-testid=state]`, whose text is exactly the state name
- `[data-testid=version]`, whose text is the version number
- `[data-testid=note]`, whose text is the note

It holds one `form[data-testid=command-form]`, with a hidden input `version`, a text input `note` (only when Approve or Reject is legal), and one `button[name=command][value=<Command>]` per legal command, `data-testid=cmd-<Command>`.
