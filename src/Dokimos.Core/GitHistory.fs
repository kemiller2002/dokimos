namespace Dokimos.Core

open System

type GitNumstatEntry =
    { Commit: string
      ChangedAt: DateTimeOffset
      Path: string
      Additions: int
      Deletions: int }

module GitHistory =
    let parse (text: string) =
        let lines = text.Replace("\r\n","\n").Split('\n')
        let mutable commit = ""
        let mutable changedAt = DateTimeOffset.MinValue
        [ for raw in lines do
            let line = raw.TrimEnd()
            if line.StartsWith("commit ") then
                let parts = line.Split(' ',3,StringSplitOptions.RemoveEmptyEntries)
                if parts.Length >= 3 then
                    commit <- parts[1]
                    match DateTimeOffset.TryParse(parts[2]) with
                    | true,value -> changedAt <- value
                    | _ -> changedAt <- DateTimeOffset.MinValue
            elif line.Contains("\t") then
                let parts = line.Split('\t')
                if parts.Length >= 3 then
                    match Int32.TryParse(parts[0]), Int32.TryParse(parts[1]) with
                    | (true,added),(true,deleted) ->
                        yield { Commit=commit; ChangedAt=changedAt; Path=parts[2]; Additions=added; Deletions=deleted }
                    | _ -> () ]

    let summarize entries =
        entries
        |> List.groupBy _.Path
        |> List.choose (fun (path,items) ->
            let changes =
                items |> List.map (fun x ->
                    { Path=x.Path; Commit=x.Commit; ChangedAt=x.ChangedAt
                      Additions=x.Additions; Deletions=x.Deletions; PreviousPath=None })
            Temporal.summarize path changes |> Option.map (fun x -> path,x))
        |> Map.ofList
