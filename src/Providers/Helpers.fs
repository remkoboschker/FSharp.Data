﻿// --------------------------------------------------------------------------------------
// Helpers for writing type providers
// ----------------------------------------------------------------------------------------------

namespace ProviderImplementation

open System
open System.IO
open Microsoft.FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes

// ----------------------------------------------------------------------------------------------

module Seq = 

  /// Merge two sequences by pairing elements for which
  /// the specified predicate returns the same key
  ///
  /// (If the inputs contain the same keys, then the order
  /// of the elements is preserved.)
  let pairBy f first second = 
    let vals1 = [ for o in first -> f o, o ]
    let vals2 = [ for o in second -> f o, o ]
    let d1, d2 = dict vals1, dict vals2
    let k1, k2 = set d1.Keys, set d2.Keys
    let keys = List.map fst vals1 @ (List.ofSeq (k2 - k1))
    let asOption = function true, v -> Some v | _ -> None
    [ for k in keys -> 
        k, asOption (d1.TryGetValue(k)), asOption (d2.TryGetValue(k)) ]

module List = 

  /// Split a non-empty list into a pair consisting of
  /// its head and its tail
  let headAndTail l = 
    match l with 
    | [] -> invalidArg "l" "empty list" 
    | head::tail -> (head, tail)

  /// Split a non-empty list into a list with all elements 
  /// except for the last one and the last element
  let frontAndBack l = 
    let rec loop acc l = 
      match l with
      | [] -> invalidArg "l" "empty list" 
      | [singleton] -> List.rev acc, singleton
      | head::tail -> loop  (head::acc) tail
    loop [] l

// ----------------------------------------------------------------------------------------------

[<AutoOpen>]
module ActivePatterns =

  /// Helper active pattern that can be used when constructing InvokeCode
  /// (to avoid writing pattern matching or incomplete matches):
  ///
  ///    p.InvokeCode <- fun (Singleton self) -> <@ 1 + 2 @>
  ///
  let (|Singleton|) = function [l] -> l | _ -> failwith "Parameter mismatch"

  /// Takes dictionary or a map and succeeds if it contains exactly one value
  let (|SingletonMap|_|) map = 
    if Seq.length map <> 1 then None else
      let (KeyValue(k, v)) = Seq.head map 
      Some(k, v)

// ----------------------------------------------------------------------------------------------

module internal ReflectionHelpers = 

  open Microsoft.FSharp.Quotations

  let makeDelegate (exprfunc:Expr -> Expr) argType = 
    let var = Var.Global("t", argType)
    let convBody = exprfunc (Expr.Var var)
    convBody.Type, Expr.NewDelegate(typedefof<Func<_,_>>.MakeGenericType [| argType; convBody.Type |], [var], convBody)
        
// ----------------------------------------------------------------------------------------------

module ProviderHelpers =

  open System.IO
  open FSharp.Data.RuntimeImplementation.Caching
  open FSharp.Data.RuntimeImplementation.ProviderFileSystem
  open FSharp.Net

  let private webUrisCache, _ = createInternetFileCache "DesignTimeURLs" (TimeSpan.FromMinutes 30.0)

  /// Resolve a location of a file (or a web location) and open it for shared
  /// read, and trigger the specified function whenever the file changes
  let readTextAtDesignTime defaultResolutionFolder invalidate resolutionFolder uri = 
    if isWeb uri then
      let value = 
        match webUrisCache.TryRetrieve uri.OriginalString with
        | Some value -> value
        | None ->
            let value = Http.Request(uri.OriginalString)
            webUrisCache.Set(uri.OriginalString, value)
            value
      new StringReader(value) :> TextReader
    else
      let stream = 
        asyncOpenStreamInProvider true (false, defaultResolutionFolder) (Some invalidate) resolutionFolder uri
        |> Async.RunSynchronously
      new StreamReader(stream) :> TextReader

  let invalidChars = Array.append (Path.GetInvalidPathChars()) (@"{}[],".ToCharArray()) |> set

  let tryGetUri str =
    match Uri.TryCreate(str, UriKind.RelativeOrAbsolute) with
    | false, _ -> None
    | true, uri ->
        if not uri.IsAbsoluteUri && (str |> Seq.exists (fun c -> invalidChars.Contains c)) then
          None
        else
          Some uri

// ----------------------------------------------------------------------------------------------
// Conversions from string to various primitive types
// ----------------------------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type TypeWrapper = None | Option | Nullable

/// Represents type information about primitive property (used mainly in the CSV provider)
/// This type captures the type, unit of measure and handling of missing values (if we
/// infer that the value may be missing, we can generate option<T> or nullable<T>)
type PrimitiveInferedProperty =
  { Name : string
    BasicType : Type
    TypeWithMeasure : Type
    TypeWrapper : TypeWrapper }
  static member Create(name, typ, optional) =
    { Name = name
      BasicType = typ
      TypeWithMeasure = typ
      TypeWrapper = if optional then TypeWrapper.Option else TypeWrapper.None }
  static member Create(name, typ, typWithMeasure, ?wrapper) =
    { Name = name
      BasicType = typ
      TypeWithMeasure = typWithMeasure
      TypeWrapper = defaultArg wrapper TypeWrapper.None }

module Conversions = 

  open Microsoft.FSharp.Quotations
  open FSharp.Data.RuntimeImplementation
  open QuotationBuilder

  /// Creates a function that takes Expr<string option> and converts it to 
  /// an expression of other type - the type is specified by `field`
  let convertValue (replacer:AssemblyReplacer) (missingValues, culture) (field:PrimitiveInferedProperty) = 

    let returnTyp = 
      match field.TypeWrapper with
      | TypeWrapper.None -> field.TypeWithMeasure
      | TypeWrapper.Option -> typedefof<option<_>>.MakeGenericType [| field.TypeWithMeasure |]
      | TypeWrapper.Nullable -> typedefof<Nullable<_>>.MakeGenericType [| field.TypeWithMeasure |]

    let returnTypWithoutMeasure = 
      match field.TypeWrapper with
      | TypeWrapper.None -> field.BasicType
      | TypeWrapper.Option -> typedefof<option<_>>.MakeGenericType [| field.BasicType |]
      | TypeWrapper.Nullable -> typedefof<Nullable<_>>.MakeGenericType [| field.BasicType |]

    let typ = field.BasicType

    let convert value =
      let converted = 
        if typ = typeof<int> then <@@ Operations.ConvertInteger(culture, %%value) @@>
        elif typ = typeof<int64> then <@@ Operations.ConvertInteger64(culture, %%value) @@>
        elif typ = typeof<decimal> then <@@ Operations.ConvertDecimal(culture, %%value) @@>
        elif typ = typeof<float> then <@@ Operations.ConvertFloat(culture, missingValues, %%value) @@>
        elif typ = typeof<string> then <@@ Operations.ConvertString(%%value) @@>
        elif typ = typeof<bool> then <@@ Operations.ConvertBoolean(culture, %%value) @@>
        elif typ = typeof<Guid> then <@@ Operations.ConvertGuid(%%value) @@>
        elif typ = typeof<DateTime> then <@@ Operations.ConvertDateTime(culture, %%value) @@>
        else failwith "convertValue: Unsupported primitive type"
      match field.TypeWrapper with
      | TypeWrapper.None -> typeof<Operations>?GetNonOptionalValue (typ) (field.Name, converted, value)
      | TypeWrapper.Option -> converted
      | TypeWrapper.Nullable -> typeof<Operations>?OptionToNullable (typ) converted
      |> replacer.ToRuntime

    let convertBack value = 
      let value = 
        match field.TypeWrapper with
        | TypeWrapper.None -> typeof<Operations>?GetOptionalValue (typ) value
        | TypeWrapper.Option -> value
        | TypeWrapper.Nullable -> typeof<Operations>?NullableToOption (typ) value
      if typ = typeof<int> then <@@ Operations.ConvertIntegerBack(culture, %%value) @@>
      elif typ = typeof<int64> then <@@ Operations.ConvertInteger64Back(culture, %%value) @@>
      elif typ = typeof<decimal> then <@@ Operations.ConvertDecimalBack(culture, %%value) @@>
      elif typ = typeof<float> then <@@ Operations.ConvertFloatBack(culture, missingValues, %%value) @@>
      elif typ = typeof<string> then <@@ Operations.ConvertStringBack(%%value) @@>
      elif typ = typeof<bool> then <@@ Operations.ConvertBooleanBack(culture, %%value) @@>
      elif typ = typeof<Guid> then <@@ Operations.ConvertGuidBack(%%value) @@>
      elif typ = typeof<DateTime> then <@@ Operations.ConvertDateTimeBack(culture, %%value) @@>
      else failwith "convertValue: Unsupported primitive type"
      |> replacer.ToRuntime

    returnTyp, returnTypWithoutMeasure, convert, convertBack

// ----------------------------------------------------------------------------------------------

module AssemblyResolver =

#if SILVERLIGHT

    let onUiThread f = 
        if System.Windows.Deployment.Current.Dispatcher.CheckAccess() then 
            f() 
        else
            let resultTask = System.Threading.Tasks.TaskCompletionSource<'T>()
            System.Windows.Deployment.Current.Dispatcher.BeginInvoke(Action(fun () -> try resultTask.SetResult (f()) with err -> resultTask.SetException err)) |> ignore
            resultTask.Task.Result

    let init (cfg : TypeProviderConfig) = 

        let runtimeAssembly = 
            onUiThread (fun () ->
                let assemblyPart = System.Windows.AssemblyPart()
                let FileStreamReadShim(fileName) = 
                    match System.Windows.Application.GetResourceStream(System.Uri(fileName,System.UriKind.Relative)) with 
                    | null -> System.IO.IsolatedStorage.IsolatedStorageFile.GetUserStoreForApplication().OpenFile(fileName, System.IO.FileMode.Open) :> System.IO.Stream 
                    | resStream -> resStream.Stream
                let assemblyStream = FileStreamReadShim cfg.RuntimeAssembly
            
                assemblyPart.Load(assemblyStream))

        runtimeAssembly, AssemblyReplacer.create []

#else

    open System.Reflection

    let private (++) a b = Path.Combine(a,b)

    let private referenceAssembliesPath = 
        Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86 
        ++ "Reference Assemblies" 
        ++ "Microsoft" 

    let private fsharpPortableAssembliesPath = 
        referenceAssembliesPath
        ++ "FSharp" 
        ++ "3.0" 
        ++ "Runtime" 
        ++ ".NETPortable"

    let private frameworkPortableAssembliesPath = 
        referenceAssembliesPath
        ++ "Framework" 
        ++ ".NETPortable" 
        ++ "v4.0" 
        ++ "Profile" 
        ++ "Profile47" 

    let private silverlightAssembliesPath = 
        referenceAssembliesPath
        ++ "Framework" 
        ++ "Silverlight" 
        ++ "v5.0" 

    let private silverlightSdkAssembliesPath = 
        Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86 
        ++ "Microsoft SDKs" 
        ++ "Silverlight" 
        ++ "v5.0" 
        ++ "Libraries"
        ++ "Client"

    let private fullAssemblies = 
        AppDomain.CurrentDomain.GetAssemblies()
        |> Seq.map (fun asm -> asm.GetName().Name, asm)
        // If there are dups, Map.ofSeq will take the last one. When the portable version
        // is already loaded, it will be the last one and replace the full version on the
        // map. We don't want that, so we use distinct to only keep the first version of
        // each assembly (assumes CurrentDomain.GetAssemblies() returns assemblies in
        // load order, must check if that's also true for Mono)
        |> Seq.distinctBy fst 
        |> Map.ofSeq

    let private getAssembly (asmName:AssemblyName) reflectionOnly = 
        let folder = 
            let version = 
                if asmName.Version = null // version is null when trying to load the log4net assembly when running tests inside NUnit
                then "" else asmName.Version.ToString()
            match asmName.Name, version with
            | "FSharp.Core", "2.3.5.0" -> fsharpPortableAssembliesPath
            | "System.Xml.Linq", "5.0.5.0" -> silverlightSdkAssembliesPath
            | _, "5.0.5.0" -> silverlightAssembliesPath
            | _, "2.0.5.0" -> frameworkPortableAssembliesPath
            | _, _ -> null
        if folder = null then 
            null
        else
            let assemblyPath = folder ++ (asmName.Name + ".dll")
            if File.Exists assemblyPath then
                if reflectionOnly then Assembly.ReflectionOnlyLoadFrom assemblyPath
                else Assembly.LoadFrom assemblyPath 
            else null

    let mutable private initialized = false    

    let init (cfg : TypeProviderConfig) = 

        if not initialized then
            initialized <- true
            AppDomain.CurrentDomain.add_AssemblyResolve(fun _ args -> getAssembly (AssemblyName args.Name) false)
            AppDomain.CurrentDomain.add_ReflectionOnlyAssemblyResolve(fun _ args -> getAssembly (AssemblyName args.Name) true)
        
        let isPortable = cfg.SystemRuntimeAssemblyVersion = new Version(2, 0, 5, 0)
        let isSilverlight = cfg.SystemRuntimeAssemblyVersion = new Version(5, 0, 5, 0)
                
        let runtimeAssembly = 
            if isSilverlight then Assembly.ReflectionOnlyLoadFrom cfg.RuntimeAssembly
            else Assembly.LoadFrom cfg.RuntimeAssembly

        let runtimeAssemblyPair = Assembly.GetExecutingAssembly(), runtimeAssembly

        let asmMappings = 
            if isPortable || isSilverlight then
                let portableAsmsPairs = 
                    runtimeAssembly.GetReferencedAssemblies()
                    |> Seq.filter (fun asmName -> asmName.Name <> "mscorlib")
                    |> Seq.choose (fun asmName -> 
                        fullAssemblies.TryFind asmName.Name
                        |> Option.bind (fun fullAsm ->
                            // Ideally we would always use reflectionOnly as true, but that creates problems in Windows 8 apps with the System.Core.dll version
                            let portableAsm = getAssembly asmName isSilverlight
                            if portableAsm <> null && portableAsm.FullName <> fullAsm.FullName then Some (fullAsm, portableAsm)
                            else None))
                    |> Seq.toList
                if portableAsmsPairs = [] then
                    failwithf "Something went wrong when creating the assembly mappings"
                runtimeAssemblyPair::portableAsmsPairs
            else
                [runtimeAssemblyPair]

        runtimeAssembly, AssemblyReplacer.create asmMappings

#endif