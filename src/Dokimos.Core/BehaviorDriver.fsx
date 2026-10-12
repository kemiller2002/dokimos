// Dokimos adapter behavior driver (dokimos.behavior-driver/1).
// Dokimos builds the candidate from Git objects at the candidate commit and
// runs this script itself: `dotnet fsi --quiet driver.fsx input.json`.
// It only observes. It loads the built assembly, looks up the frozen adapter
// (`<namespace>.<module>.decide` and `.recordedMinutes`), calls it for every
// case and writes one JSON line per case to the output file named in the
// input. It never receives the oracle's expectations, so it cannot grade.

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading.Tasks
open Microsoft.FSharp.Reflection

type Decide = string -> string -> Result<string * string list, string>
type Minutes = (int64 * int64) list -> int option

let private input = JsonDocument.Parse(File.ReadAllText(fsi.CommandLineArgs.[1])).RootElement
let private str (name: string) (e: JsonElement) = e.GetProperty(name).GetString() |> string
let private output = new StreamWriter(str "output" input, false)
let private emit (record: obj) =
    output.WriteLine(JsonSerializer.Serialize record)
    output.Flush()

let private perCallTimeout = TimeSpan.FromSeconds 10.0

let private unwrap (e: exn) =
    match e with
    | :? TargetInvocationException as t when not (isNull t.InnerException) -> t.InnerException
    | :? AggregateException as a when not (isNull a.InnerException) ->
        match a.InnerException with
        | :? TargetInvocationException as t when not (isNull t.InnerException) -> t.InnerException
        | inner -> inner
    | e -> e

let private describe (m: MemberInfo) =
    match m with
    | :? MethodInfo as mi ->
        let ps = mi.GetParameters() |> Array.map (fun p -> p.ParameterType.ToString()) |> String.concat ", "
        sprintf "method (%s) -> %s" ps (mi.ReturnType.ToString())
    | :? PropertyInfo as pi -> sprintf "value of type %s" (pi.PropertyType.ToString())
    | other -> other.MemberType.ToString()

/// Finds `name` as a compiled module function with exactly the frozen
/// parameter and return types, or as a module value of the frozen function
/// type. Anything else named `name` is a signature mismatch.
let private bind<'f> (moduleType: Type) (name: string) (parameters: Type[]) (returns: Type) (fromMethod: MethodInfo -> 'f) =
    let flags = BindingFlags.Public ||| BindingFlags.Static
    let candidates = moduleType.GetMember(name, flags)
    let exact =
        candidates
        |> Array.tryPick (fun m ->
            match m with
            | :? MethodInfo as mi when (mi.GetParameters() |> Array.map _.ParameterType) = parameters && mi.ReturnType = returns ->
                Some(fromMethod mi)
            | :? PropertyInfo as pi when pi.PropertyType = typeof<'f> -> Some(pi.GetValue(null) :?> 'f)
            | _ -> None)
    match exact, candidates with
    | Some f, _ -> Ok f
    | None, [||] -> Error("missing", name + " is not defined in the adapter module")
    | None, found -> Error("signature-mismatch", name + ": " + (found |> Array.map describe |> String.concat "; "))

let private loaded =
    try Ok(Assembly.LoadFrom(str "assembly" input))
    with e -> Error(e.GetType().Name + ": " + e.Message)

let private moduleName = str "namespace" input + "." + str "module" input

let private moduleType =
    match loaded with
    | Error _ -> None
    | Ok assembly ->
        let types = try assembly.GetTypes() with :? ReflectionTypeLoadException as e -> e.Types |> Array.filter (isNull >> not)
        types
        |> Array.filter (fun t -> t.FullName = moduleName || t.FullName = moduleName + "Module")
        |> Array.filter FSharpType.IsModule
        |> Array.tryHead

let private decide : Result<Decide, string * string> =
    match moduleType with
    | None -> Error("adapter-missing", "module " + moduleName + " not found")
    | Some t ->
        bind<Decide> t "decide" [| typeof<string>; typeof<string> |] typeof<Result<string * string list, string>> (fun mi ->
            fun state command -> mi.Invoke(null, [| box state; box command |]) :?> Result<string * string list, string>)

let private minutes : Result<Minutes, string * string> =
    match moduleType with
    | None -> Error("adapter-missing", "module " + moduleName + " not found")
    | Some t ->
        bind<Minutes> t "recordedMinutes" [| typeof<(int64 * int64) list> |] typeof<int option> (fun mi ->
            fun segments -> mi.Invoke(null, [| box segments |]) :?> int option)

let private functionStatus (r: Result<'a, string * string>) =
    match r with
    | Ok _ -> "ok"
    | Error(status, _) -> status

emit
    {| probe =
        {| assembly = (match loaded with Ok _ -> "loaded" | Error _ -> "unloadable")
           ``module`` = (match moduleType with Some t -> t.FullName | None -> "missing")
           functions = dict [ "decide", functionStatus decide; "recordedMinutes", functionStatus minutes ]
           detail =
            [ (match loaded with Error e -> e | Ok _ -> "")
              (match decide with Error(_, d) -> d | Ok _ -> "")
              (match minutes with Error(_, d) -> d | Ok _ -> "") ]
            |> List.filter (String.IsNullOrEmpty >> not)
            |> String.concat "; " |} |}

let private report caseId status (steps: obj list) (minutes: int option) (reason: string) =
    emit {| caseId = caseId; status = status; steps = steps; minutes = Option.toNullable minutes; reason = reason |}

/// Runs one call with a time limit. A call that does not return ends the
/// driver: its thread cannot be stopped, so later cases stay unobserved.
let private timed (f: unit -> 'a) =
    let task = Task.Run(fun () -> f ())
    if task.Wait perCallTimeout then task.Result
    else raise (TimeoutException(sprintf "no result within %.0f seconds" perCallTimeout.TotalSeconds))

let private decideCase (c: JsonElement) caseId =
    match decide with
    | Error(status, reason) -> report caseId status [] None reason
    | Ok f ->
        let commands = [ for x in c.GetProperty("commands").EnumerateArray() -> x.GetString() |> string ]
        let rec run state remaining (acc: obj list) =
            match remaining with
            | [] -> report caseId "observed" (List.rev acc) None ""
            | command :: rest ->
                match timed (fun () -> f state command) with
                | Ok(next, effects) ->
                    let next = string next
                    let effects = if isNull (box effects) then [] else effects |> List.map string
                    run next rest (box {| outcome = "accepted"; next = next; effects = effects |} :: acc)
                | Error _ -> run state rest (box {| outcome = "refused"; next = ""; effects = ([]: string list) |} :: acc)
        run (str "state" c) commands []

let private minutesCase (c: JsonElement) caseId =
    match minutes with
    | Error(status, reason) -> report caseId status [] None reason
    | Ok f ->
        let segments = [ for p in c.GetProperty("segments").EnumerateArray() -> p.[0].GetInt64(), p.[1].GetInt64() ]
        report caseId "observed" [] (timed (fun () -> f segments)) ""

for c in input.GetProperty("cases").EnumerateArray() do
    let caseId = str "id" c
    try
        match str "kind" c with
        | "decide" -> decideCase c caseId
        | "recorded-minutes" -> minutesCase c caseId
        | other -> report caseId "unsupported" [] None ("case kind " + other)
    with
    | :? TimeoutException as t ->
        report caseId "timeout" [] None t.Message
        output.Dispose()
        exit 0
    | failure ->
        let inner = unwrap failure
        report caseId "threw" [] None (inner.GetType().Name + ": " + inner.Message)

output.Dispose()
