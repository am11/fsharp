// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.FSharp.Compiler.SourceCodeServices

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Range

module UnusedOpens =
    open Microsoft.FSharp.Compiler.PrettyNaming

    /// Represents single open statement.
    type OpenStatement =
        { /// Full namespace or module identifier as it's presented in source code.
          Idents: Set<string>
          /// Modules.
          Modules: FSharpEntity list
          /// Range of open statement itself.
          Range: range
          /// Scope on which this open declaration is applied.
          AppliedScope: range
          /// If it's prefixed with the special "global" namespace.
          IsGlobal: bool }

        member this.AllChildSymbols =
            let rec getAllChildSymbolsInModule (modul: FSharpEntity) =
                seq {
                    for ent in modul.NestedEntities do
                        yield ent :> FSharpSymbol
                        
                        if ent.IsFSharpRecord then
                            for rf in ent.FSharpFields do
                                yield upcast rf
                        
                        if ent.IsFSharpUnion && not (hasAttribute<RequireQualifiedAccessAttribute> ent.Attributes) then
                            for unionCase in ent.UnionCases do
                                yield upcast unionCase

                        if ent.IsFSharpModule && hasAttribute<AutoOpenAttribute> ent.Attributes then
                            yield! getAllChildSymbolsInModule ent
                    
                    for fv in modul.MembersFunctionsAndValues do 
                        yield upcast fv
                }

            seq { for modul in this.Modules do
                    yield! getAllChildSymbolsInModule modul
            } |> Seq.cache

    let getOpenStatements (openDeclarations: FSharpOpenDeclaration list) : OpenStatement list = 
        openDeclarations
        |> List.choose (fun openDeclaration ->
             match openDeclaration with
             | FSharpOpenDeclaration.Open ((firstId :: _) as longId, modules, appliedScope) ->
                 Some { Idents = 
                            modules 
                            |> List.choose (fun x -> x.TryFullName |> Option.map (fun fullName -> x, fullName)) 
                            |> List.collect (fun (modul, fullName) -> 
                                 [ yield fullName
                                   if modul.HasFSharpModuleSuffix then
                                     yield fullName.[..fullName.Length - 7] // "Module" length plus zero index correction
                                 ])
                            |> Set.ofList
                        Modules = modules
                        Range =
                            let lastId = List.last longId
                            mkRange appliedScope.FileName firstId.idRange.Start lastId.idRange.End
                        AppliedScope = appliedScope
                        IsGlobal = firstId.idText = MangledGlobalName  }
             | _ -> None // for now
           )

    let getAutoOpenAccessPath (ent:FSharpEntity) =
        // Some.Namespace+AutoOpenedModule+Entity

        // HACK: I can't see a way to get the EnclosingEntity of an Entity
        // Some.Namespace + Some.Namespace.AutoOpenedModule are both valid
        ent.TryFullName |> Option.bind(fun _ ->
            if (not ent.IsNamespace) && ent.QualifiedName.Contains "+" then 
                Some ent.QualifiedName.[0..ent.QualifiedName.IndexOf "+" - 1]
            else
                None)

    let entityNamespace (entOpt: FSharpEntity option) =
        match entOpt with
        | Some ent ->
            if ent.IsFSharpModule then
                [ yield Some ent.QualifiedName
                  yield Some ent.LogicalName
                  yield Some ent.AccessPath
                  yield Some ent.FullName
                  yield Some ent.DisplayName
                  yield ent.TryGetFullDisplayName()
                  if ent.HasFSharpModuleSuffix then
                    yield Some (ent.AccessPath + "." + ent.DisplayName)]
            else
                [ yield ent.Namespace
                  yield Some ent.AccessPath
                  yield getAutoOpenAccessPath ent
                  for path in ent.AllCompilationPaths do
                    yield Some path 
                ]
        | None -> []

    type NamespaceUse =
        { Ident: string
          ExtraNamespaces: string[] }
    
    let getNamespaceInUse (fullIsland: string) (fullName: string) : NamespaceUse option =
        // given a full island such as `Text.ISegment` and a full name of `MonoDevelop.Core.Text.ISegment`, return `MonoDevelop.Core`
        let lengthDiff = fullName.Length - fullIsland.Length - 2
        if lengthDiff <= 0 || lengthDiff > fullName.Length - 1 then None
        else 
            let requiredOpenNamespace = fullName.[0..lengthDiff]
            let rest = fullName.[lengthDiff + 1..]
            let extraNamespaces =
                match rest.Split '.' with
                | [||] | [|_|] -> [||]
                | rest -> rest.[..rest.Length - 2]
            Some { Ident = requiredOpenNamespace; ExtraNamespaces = extraNamespaces }

    let getPossibleNamespaces (getSourceLineStr: int -> string) (symbolUse: FSharpSymbolUse) : NamespaceUse[] =
        let lineStr = getSourceLineStr symbolUse.RangeAlternate.StartLine
        let partialName = QuickParse.GetPartialLongNameEx (lineStr, symbolUse.RangeAlternate.EndColumn - 1)
        if partialName.PartialIdent = "" then [||]
        else
            let qualifyingIsland = partialName.QualifyingIdents |> String.concat "."
            let fullIsland = qualifyingIsland + partialName.PartialIdent
            let isQualified fullName = fullName = fullIsland

            (match symbolUse with
             | SymbolUse.Entity (ent, cleanFullNames) when not (cleanFullNames |> List.exists isQualified) ->
                 Some (cleanFullNames, Some ent)
             | SymbolUse.Field f when not (isQualified f.FullName) ->
                 Some ([f.FullName], Some f.DeclaringEntity)
             | SymbolUse.MemberFunctionOrValue mfv when not (isQualified mfv.FullName) ->
                 Some ([mfv.FullName], mfv.EnclosingEntity)
             | SymbolUse.Operator op when not (isQualified op.FullName) ->
                 Some ([op.FullName], op.EnclosingEntity)
             | SymbolUse.ActivePattern ap when not (isQualified ap.FullName) ->
                 Some ([ap.FullName], ap.EnclosingEntity)
             | SymbolUse.ActivePatternCase apc when not (isQualified apc.FullName) ->
                 Some ([apc.FullName], apc.Group.EnclosingEntity)
             | SymbolUse.UnionCase uc when not (isQualified uc.FullName) ->
                 Some ([uc.FullName], Some uc.ReturnType.TypeDefinition)
             | SymbolUse.Parameter p when not (isQualified p.FullName) && p.Type.HasTypeDefinition ->
                 Some ([p.FullName], Some p.Type.TypeDefinition)
             | _ -> None)
            |> Option.map (fun (fullNames, declaringEntity) ->
                 [| for name in fullNames do
                      let partNamespace = getNamespaceInUse fullIsland name
                      yield partNamespace
                    yield! 
                        entityNamespace declaringEntity
                        |> List.map (Option.bind (getNamespaceInUse qualifyingIsland))
                 |])
            |> Option.toArray
            |> Array.concat
            |> Array.choose id
            |> Array.distinct

    type SymbolUseWithFullNames =
        { SymbolUse: FSharpSymbolUse
          FullNames: string[][] }

    type SymbolUse =
        { SymbolUse: FSharpSymbolUse
          PossibleNamespaces: NamespaceUse[] }

    let getSymbolUses (getSourceLineStr: int -> string) (symbolUses: FSharpSymbolUse[]) : SymbolUse[] =
        symbolUses
        |> Array.filter (fun (symbolUse: FSharpSymbolUse) -> 
             not symbolUse.IsFromDefinition //&& 
             //match symbolUse.Symbol with
             //| :? FSharpEntity as e -> not e.IsNamespace
             //| _ -> true
           )
        |> Array.map (fun su ->
            { SymbolUse = su
              PossibleNamespaces = getPossibleNamespaces getSourceLineStr su })

    let getUnusedOpens (checkFileResults: FSharpCheckFileResults, getSourceLineStr: int -> string) : Async<range list> =
        
        let filter (openStatements: OpenStatement list) (symbolUses: SymbolUse[]) : OpenStatement list =
            let rec filterInner acc (openStatements: OpenStatement list) (seenOpenStatements: OpenStatement list) = 
                
                let isUsed (openStatement: OpenStatement) =
                    if openStatement.IsGlobal then true
                    else
                        let usedSomewhere =
                            symbolUses
                            |> Array.exists (fun symbolUse -> 
                                let inScope = rangeContainsRange openStatement.AppliedScope symbolUse.SymbolUse.RangeAlternate
                                if not inScope then false
                                elif openStatement.Idents |> Set.intersect symbolUse.PossibleNamespaces |> Set.isEmpty then false
                                else
                                    let moduleSymbols = openStatement.AllChildSymbols |> Seq.toList
                                    moduleSymbols
                                    |> List.exists (fun x -> x.IsEffectivelySameAs symbolUse.SymbolUse.Symbol))

                        if not usedSomewhere then false
                        else
                            let alreadySeen =
                                seenOpenStatements
                                |> List.exists (fun seenNs ->
                                    // if such open statement has already been marked as used in this or outer module, we skip it 
                                    // (that is, do not mark as used so far)
                                    rangeContainsRange seenNs.AppliedScope openStatement.AppliedScope && 
                                    not (openStatement.Idents |> Set.intersect seenNs.Idents |> Set.isEmpty))
                            not alreadySeen
                
                match openStatements with
                | os :: xs when not (isUsed os) -> 
                    filterInner (os :: acc) xs (os :: seenOpenStatements)
                | os :: xs ->
                    filterInner acc xs (os :: seenOpenStatements)
                | [] -> List.rev acc
            
            filterInner [] openStatements []

        async {
            let! fsharpSymbolUses = checkFileResults.GetAllUsesOfAllSymbolsInFile()
            let symbolUses = getSymbolUses getSourceLineStr fsharpSymbolUses
            let openStatements = getOpenStatements checkFileResults.OpenDeclarations
            return filter openStatements symbolUses |> List.map (fun os -> os.Range)
        }