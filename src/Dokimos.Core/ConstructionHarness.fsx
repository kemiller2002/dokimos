// Dokimos construction oracle harness (dokimos.construction-harness/1).
// Dokimos prepends `#load` lines for the candidate's generated files, read
// from Git objects at the candidate commit, and runs this script itself.
// It only observes: it drives Tier 2 `decide` and reads the ports interface
// by reflection and prints one JSON line per case. It never sees the
// oracle's expectations, so it cannot grade itself.

open System
open System.Text.Json
open Microsoft.FSharp.Reflection

let private input = JsonDocument.Parse(IO.File.ReadAllText(fsi.CommandLineArgs[1])).RootElement
let private str (name: string) (e: JsonElement) = e.GetProperty(name).GetString() |> string
let private ns = str "namespace" input

let private candidateTypes =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.filter (fun a -> (string (a.GetName().Name)).StartsWith("FSI-ASSEMBLY", StringComparison.Ordinal))
    |> Array.collect (fun a -> a.GetTypes())

let private findType (name: string) =
    let full = ns + "." + name
    candidateTypes |> Array.tryFind (fun t -> t.FullName = full || t.FullName.EndsWith("." + full, StringComparison.Ordinal))

let private caseValue (t: Type) (name: string) =
    FSharpType.GetUnionCases(t, true)
    |> Array.tryFind (fun c -> c.Name = name && c.GetFields().Length = 0)
    |> Option.map (fun c -> FSharpValue.MakeUnion(c, [||], true))

let private caseName (value: obj) =
    let case, _ = FSharpValue.GetUnionFields(value, value.GetType(), true)
    case.Name

let private emit (record: obj) = Console.Out.WriteLine(JsonSerializer.Serialize record)

let private report caseId status (steps: obj list) (members: string list) (reason: string) =
    emit {| caseId = caseId; status = status; steps = steps; members = members; reason = reason |}

let private decideCase (c: JsonElement) caseId =
    let stateType = findType (str "stateType" input)
    let commandType = findType (str "commandType" input)
    let transition = findType (str "transitionModule" input)
    match stateType, commandType, transition with
    | None, _, _ -> report caseId "missing" [] [] ("state type " + str "stateType" input)
    | _, None, _ -> report caseId "missing" [] [] ("command type " + str "commandType" input)
    | _, _, None -> report caseId "missing" [] [] ("transition module " + str "transitionModule" input)
    | Some st, Some ct, Some tm ->
        match tm.GetMethod("decide") with
        | null -> report caseId "missing" [] [] "decide"
        | decide when decide.GetParameters().Length <> 2 ->
            report caseId "unsupported" [] [] (sprintf "decide takes %d parameters; guard/capability rules are not driven by this harness" (decide.GetParameters().Length))
        | decide ->
            match caseValue st (str "state" c) with
            | None -> report caseId "missing" [] [] ("state " + str "state" c)
            | Some initial ->
                let commands = [ for x in c.GetProperty("commands").EnumerateArray() -> x.GetString() |> string ]
                let rec run (state: obj) (remaining: string list) (acc: obj list) =
                    match remaining with
                    | [] -> report caseId "observed" (List.rev acc) [] ""
                    | command :: rest ->
                        match caseValue ct command with
                        | None -> report caseId "missing" (List.rev acc) [] ("command " + command)
                        | Some cmd ->
                            let result = decide.Invoke(null, [| state; cmd |])
                            let case, fields = FSharpValue.GetUnionFields(result, result.GetType(), true)
                            if case.Name = "Ok" then
                                let decision = fields[0]
                                let next = decision.GetType().GetProperty("Next").GetValue(decision)
                                let effects =
                                    decision.GetType().GetProperty("Effects").GetValue(decision) :?> Collections.IEnumerable
                                    |> Seq.cast<obj> |> Seq.map caseName |> Seq.toList
                                run next rest (box {| outcome = "accepted"; next = caseName next; effects = effects |} :: acc)
                            else
                                run state rest (box {| outcome = "refused"; next = ""; effects = ([]: string list) |} :: acc)
                run initial commands []

let private portCase caseId =
    match findType (str "portsInterface" input) with
    | None -> report caseId "missing" [] [] ("ports interface " + str "portsInterface" input)
    | Some ports ->
        let members = ports.GetMethods() |> Array.map _.Name |> Array.map (fun n -> n.Replace("get_", "")) |> Array.distinct |> Array.sort |> Array.toList
        report caseId "observed" [] members ""

for c in input.GetProperty("cases").EnumerateArray() do
    let caseId = str "id" c
    try
        match str "kind" c with
        | "decide" | "sequence" -> decideCase c caseId
        | "port-surface" -> portCase caseId
        | other -> report caseId "unsupported" [] [] ("case kind " + other)
    with failure ->
        let inner = match failure with :? Reflection.TargetInvocationException as t when not (isNull t.InnerException) -> t.InnerException | e -> e
        report caseId "threw" [] [] (inner.GetType().Name + ": " + inner.Message)
