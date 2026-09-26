namespace Dokimos.Core

type ApiSymbol =
    { Project: string
      QualifiedName: string
      Signature: string }

type ApiChange =
    | Added of ApiSymbol
    | Removed of ApiSymbol
    | SignatureChanged of before: ApiSymbol * after: ApiSymbol

type ApiHistory =
    { Added: int
      Removed: int
      Changed: int
      Changes: ApiChange list }

module Api =
    let compare before after =
        let byName symbols = symbols |> List.map (fun x -> (x.Project,x.QualifiedName),x) |> Map.ofList
        let b = byName before
        let a = byName after
        let keys = Set.union (b |> Map.keys |> Set.ofSeq) (a |> Map.keys |> Set.ofSeq)
        let changes =
            [ for key in keys do
                match Map.tryFind key b, Map.tryFind key a with
                | None, Some current -> Added current
                | Some previous, None -> Removed previous
                | Some previous, Some current when previous.Signature <> current.Signature -> SignatureChanged(previous,current)
                | _ -> () ]
        { Added = changes |> List.sumBy (function Added _ -> 1 | _ -> 0)
          Removed = changes |> List.sumBy (function Removed _ -> 1 | _ -> 0)
          Changed = changes |> List.sumBy (function SignatureChanged _ -> 1 | _ -> 0)
          Changes = changes }
