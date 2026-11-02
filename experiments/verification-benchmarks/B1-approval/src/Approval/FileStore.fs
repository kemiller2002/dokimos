namespace Approval

open System
open System.IO
open System.Text.Json

/// Serialised shape of a request on disk (the union `State` is stored by name).
/// Not part of the contract; public only so System.Text.Json can see its properties.
[<CLIMutable>]
type RequestDto =
    { Id: string
      Title: string
      State: string
      Version: int
      Note: string }

module internal Codec =

    let private stateName =
        function
        | Draft -> "Draft"
        | Submitted -> "Submitted"
        | Approved -> "Approved"
        | Rejected -> "Rejected"

    let private parseState =
        function
        | "Draft" -> Some Draft
        | "Submitted" -> Some Submitted
        | "Approved" -> Some Approved
        | "Rejected" -> Some Rejected
        | _ -> None

    let private options = JsonSerializerOptions(WriteIndented = true)

    let encode (r: Request) : byte[] =
        let dto: RequestDto =
            { Id = r.Id
              Title = r.Title
              State = stateName r.State
              Version = r.Version
              Note = r.Note }

        JsonSerializer.SerializeToUtf8Bytes(dto, options)

    let decode (bytes: byte[]) : Request option =
        try
            let dto = JsonSerializer.Deserialize<RequestDto>(ReadOnlySpan bytes, options)

            if obj.ReferenceEquals(dto, null) then
                None
            else
                parseState dto.State
                |> Option.map (fun state ->
                    { Id = dto.Id
                      Title = (if isNull dto.Title then "" else dto.Title)
                      State = state
                      Version = dto.Version
                      Note = (if isNull dto.Note then "" else dto.Note) })
        with :? JsonException ->
            None

/// A file store rooted at a directory; one file per request: <root>/<id>.json.
type FileStore(root: string) =
    let gate = obj ()
    let mutable readBackFilter: byte[] -> byte[] = id

    let pathOf (requestId: string) = Path.Combine(root, requestId + ".json")

    let tryRead (requestId: string) =
        let path = pathOf requestId
        if File.Exists path then Some(File.ReadAllBytes path) else None

    let storedVersion requestId =
        tryRead requestId
        |> Option.bind Codec.decode
        |> Option.map (fun r -> r.Version)
        |> Option.defaultValue -1

    let write requestId (bytes: byte[]) =
        Directory.CreateDirectory root |> ignore
        let target = pathOf requestId
        let temp = Path.Combine(root, sprintf ".%s.%s.tmp" requestId (Guid.NewGuid().ToString "N"))
        File.WriteAllBytes(temp, bytes)
        File.Move(temp, target, true)

    /// `expectedVersion` is the version currently stored (-1 for a request not yet stored).
    member _.Save(request: Request, expectedVersion: int) : Result<Request, StoreError> =
        lock gate (fun () ->
            let actual = storedVersion request.Id

            if actual <> expectedVersion then
                Error(Conflict(expectedVersion, actual))
            else
                let written = Codec.encode request
                write request.Id written
                let seen = File.ReadAllBytes(pathOf request.Id) |> readBackFilter

                if not (isNull seen) && seen = written then
                    Ok request
                else
                    Error ReadBackMismatch)

    member _.Load(requestId: string) : Result<Request, StoreError> =
        lock gate (fun () ->
            match tryRead requestId |> Option.bind Codec.decode with
            | Some r -> Ok r
            | None -> Error(NotFound requestId))

    /// Test seam for APR-002-O2: transforms the bytes the read-back sees (identity by default).
    member _.ReadBackFilter
        with get () = readBackFilter
        and set (f: byte[] -> byte[]) = readBackFilter <- f
