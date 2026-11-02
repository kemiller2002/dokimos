namespace Approval

open System
open System.IO

module Program =

    /// Finds `--name value` or `--name=value`; ignores every other argument.
    let rec private option (name: string) (args: string list) =
        match args with
        | [] -> None
        | a :: v :: _ when a = name -> Some v
        | a :: _ when a.StartsWith(name + "=") -> Some(a.Substring(name.Length + 1))
        | _ :: rest -> option name rest

    [<EntryPoint>]
    let main argv =
        let args = List.ofArray argv
        let urls = option "--urls" args |> Option.defaultValue "http://127.0.0.1:5000"
        let data = option "--data" args |> Option.defaultValue (Path.Combine(Environment.CurrentDirectory, "data"))
        let token = Environment.GetEnvironmentVariable "APPROVAL_TOKEN" |> Option.ofObj |> Option.defaultValue ""

        let root = Path.GetFullPath data
        Directory.CreateDirectory root |> ignore

        let app = Web.build (FileStore root) token urls
        app.Run()
        0
