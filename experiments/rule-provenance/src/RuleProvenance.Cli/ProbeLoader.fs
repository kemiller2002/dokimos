namespace RuleProvenance.Cli

open System
open System.Collections
open System.IO
open System.Reflection
open Microsoft.FSharp.Reflection
open RuleProvenance

/// Reflection adapter: fills the oracle's Probe record from an assembly exposing
/// `Invoice.Slice.Probe` (start/step/stateName/outstanding/paymentsApplied and the
/// ProbeCommand union). The oracle never sees the implementation's types.
module ProbeLoader =
    let private flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

    let private findType (asm: Assembly) (suffix: string) =
        let types =
            try asm.GetTypes()
            with :? ReflectionTypeLoadException as ex -> ex.Types |> Array.choose Option.ofObj
        types
        |> Array.filter (fun t ->
            let name = (Option.ofObj t.FullName |> Option.defaultValue t.Name).Replace('+', '.')
            name = suffix || name.EndsWith("." + suffix, StringComparison.Ordinal))
        |> Array.sortBy (fun t -> t.FullName)
        |> Array.tryHead

    /// Apply a curried F# function value to arguments one at a time.
    let private applyCurried (f: obj) (args: obj list) =
        args
        |> List.fold
            (fun (fn: obj) arg ->
                let invoke = fn.GetType().GetMethod("Invoke", [| arg.GetType() |])
                match Option.ofObj invoke with
                | Some m -> m.Invoke(fn, [| arg |]) |> nonNull
                | None ->
                    let any = fn.GetType().GetMethods() |> Array.find (fun m -> m.Name = "Invoke" && m.GetParameters().Length = 1)
                    any.Invoke(fn, [| arg |]) |> nonNull)
            f

    /// Call a module function compiled either as a static method or as a function-valued property.
    let private caller (moduleType: Type) (name: string) (arity: int) : Result<obj list -> obj, string> =
        let methods = moduleType.GetMethods(flags) |> Array.filter (fun m -> m.Name = name && m.GetParameters().Length = arity)
        match methods |> Array.tryHead with
        | Some m -> Ok(fun args -> m.Invoke(null, Array.ofList args) |> nonNull)
        | None ->
            match Option.ofObj (moduleType.GetProperty(name, flags)) with
            | Some p -> Ok(fun args -> applyCurried (p.GetValue null |> nonNull) args)
            | None -> Error $"Invoice.Slice.Probe.{name} (arity {arity}) not found"

    let private unwrapResult (value: obj) : Result<obj, string> =
        let case, fields = FSharpValue.GetUnionFields(value, value.GetType())
        match case.Name, fields with
        | "Ok", [| s |] -> Ok(nonNull s)
        | "Error", [| e |] -> Error(string e)
        | other, _ -> Error $"PROBE_UNEXPECTED_RESULT:{other}"

    /// Build a Probe<obj> from a loaded assembly.
    let fromAssembly (asm: Assembly) : Result<Probe<obj>, string list> =
        match findType asm "Invoice.Slice.Probe", findType asm "Invoice.Slice.Probe.ProbeCommand" with
        | None, _ -> Error [ "type Invoice.Slice.Probe not found in the assembly" ]
        | _, None -> Error [ "union Invoice.Slice.Probe.ProbeCommand not found in the assembly" ]
        | Some probeType, Some commandType when not (FSharpType.IsUnion(commandType, flags)) ->
            Error [ $"{commandType.FullName} is not an F# union" ]
        | Some probeType, Some commandType ->
            let cases = FSharpType.GetUnionCases(commandType, flags)
            let case name = cases |> Array.tryFind (fun c -> c.Name = name)
            let fns =
                [ caller probeType "start" 1
                  caller probeType "step" 2
                  caller probeType "stateName" 1
                  caller probeType "outstanding" 1
                  caller probeType "paymentsApplied" 1 ]
            let errors = (fns |> List.choose (function Error e -> Some e | Ok _ -> None)) @ ([ "Issue"; "Pay"; "Void" ] |> List.filter (fun n -> (case n).IsNone) |> List.map (fun n -> $"ProbeCommand.{n} not found"))
            if not errors.IsEmpty then Error errors
            else
                let get i = match fns[i] with Ok f -> f | Error _ -> failwith "unreachable"
                let start, step, stateName, outstanding, payments = get 0, get 1, get 2, get 3, get 4
                let issue: obj = FSharpValue.MakeUnion((case "Issue").Value, [||], flags) |> nonNull
                let voidCmd: obj = FSharpValue.MakeUnion((case "Void").Value, [||], flags) |> nonNull
                let payCase = (case "Pay").Value
                let toCommand =
                    function
                    | Issue -> issue
                    | Void -> voidCmd
                    | Pay(customer, amount) ->
                        let args =
                            payCase.GetFields()
                            |> Array.map (fun f -> if f.PropertyType = typeof<string> then box customer else box amount)
                        FSharpValue.MakeUnion(payCase, args, flags) |> nonNull
                Ok
                    { Start = fun total -> start [ (total :> obj) ]
                      Step =
                        fun s c ->
                            try unwrapResult (step [ s; toCommand c ])
                            with :? TargetInvocationException as ex ->
                                let inner = Option.ofObj ex.InnerException |> Option.map (fun e -> e.GetType().Name) |> Option.defaultValue "unknown"
                                Error("PROBE_EXCEPTION:" + inner)
                      StateName = fun s -> string (stateName [ s ])
                      Outstanding = fun s -> outstanding [ s ] |> unbox<decimal>
                      PaymentsApplied = fun s -> (payments [ s ] :?> IEnumerable) |> Seq.cast<decimal> |> List.ofSeq }

    /// Load the assembly at `path`. Failure is "evidence unavailable" (exit 3).
    let load (path: string) : Result<Probe<obj>, string list> =
        try
            let full = Path.GetFullPath path
            if not (File.Exists full) then Error [ $"probe assembly not found: {path}" ]
            else
                let directory = Path.GetDirectoryName full |> nonNull
                // Resolve the probe's own dependencies from its directory.
                let resolve (_: obj | null) (args: ResolveEventArgs) : Assembly | null =
                    match AssemblyName(args.Name).Name with
                    | null -> null
                    | name ->
                        let candidate = Path.Combine(directory, name + ".dll")
                        if File.Exists candidate then Assembly.LoadFrom candidate else null
                AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler resolve)
                fromAssembly (Assembly.LoadFrom full)
        with ex ->
            Error [ $"could not load probe assembly {path}: {ex.GetType().Name}: {ex.Message}" ]
