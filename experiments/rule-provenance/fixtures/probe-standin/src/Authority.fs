namespace Invoice.Slice

open System

/// Provenance marker: the accepted records a rule-bearing symbol was built from.
[<AttributeUsage(AttributeTargets.All, AllowMultiple = false)>]
type AuthorityAttribute([<ParamArray>] ids: string array) =
    inherit Attribute()
    member _.Ids = ids
