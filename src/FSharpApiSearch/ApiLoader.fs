﻿module FSharpApiSearch.ApiLoader

open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpApiSearch.OptionModule
open FSharpApiSearch.SpecialTypes
open System.Text.RegularExpressions
open System.IO
open Nessos.FsPickler

module Hack = FSharpApiSearch.Hack

type FSharpEntity with
  member this.FullIdentity =
    let assemblyName = this.Assembly.SimpleName
    { AssemblyName = assemblyName; Name = LoadingName (this.FullName, []); GenericParameterCount = this.GenericParameters.Count }
  member this.Identity = Identity (FullIdentity this.FullIdentity)
  member this.IsTuple = this.FullName.StartsWith("System.Tuple") && this.DisplayName = "Tuple"
  member this.IsCompilerInternal =
    this.FullName = "Microsoft.FSharp.Core.LanguagePrimitives" || this.FullName = "Microsoft.FSharp.Core.Operators.OperatorIntrinsics"

type FSharpType with
  member this.TryIdentity = this.TryFullIdentity |> Option.map (fun x -> Identity (FullIdentity x))
  member this.TryFullIdentity =
    if Hack.isFloat this then
      Some { this.TypeDefinition.FullIdentity with GenericParameterCount = 0 }
    elif this.HasTypeDefinition then
      Some this.TypeDefinition.FullIdentity
    else
      None

type FSharpMemberOrFunctionOrValue with
  member this.IsStaticMember = not this.IsInstanceMember
  member this.IsMethod = this.FullType.IsFunctionType && not this.IsPropertyGetterMethod && not this.IsPropertySetterMethod
  member this.IsConstructor = this.CompiledName = ".ctor"
  member this.IsCSharpExtensionMember = this.Attributes |> Seq.exists (fun attr -> attr.AttributeType.FullIdentity = SpecialTypes.FullIdentity.ExtensionAttribute)
  member this.MemberModifier = if this.IsStaticMember then MemberModifier.Static else MemberModifier.Instance
  member this.PropertyKind =
    if not this.IsProperty then
      failwith "It is not property."
    elif this.HasGetterMethod && this.HasSetterMethod then
      PropertyKind.GetSet
    elif this.HasGetterMethod then
      PropertyKind.Get
    else
      PropertyKind.Set
  member this.TargetSignatureConstructor = fun declaringType member' ->
    if this.IsCSharpExtensionMember then
      ApiSignature.ExtensionMember member'
    elif this.IsStaticMember then
      ApiSignature.StaticMember (declaringType, member')
    else
      ApiSignature.InstanceMember (declaringType, member')
  member this.GenericParametersAsTypeVariable =
    this.GenericParameters |> Seq.map (fun x -> x.DisplayName : TypeVariable) |> Seq.toList
      
type FSharpField with
  member this.TargetSignatureConstructor = fun declaringType member' ->
    if this.IsStatic then
      ApiSignature.StaticMember (declaringType, member')
    else
      ApiSignature.InstanceMember (declaringType, member')

let genericParameters (e: FSharpEntity) =
  e.GenericParameters |> Seq.map (fun p -> p.DisplayName : TypeVariable) |> Seq.toList

let rec toSignature (t: FSharpType) =
  if Hack.isMeasure t then
    None
  elif t.IsFunctionType then
    option {
      let! xs = toFlatArrow t
      return Arrow xs
    }
  elif t.IsGenericParameter then
    Some (Variable (VariableSource.Target, t.GenericParameter.Name))
  elif t.IsTupleType then
    option {
      let! args = listSignature t.GenericArguments
      return Tuple args
    }
  elif t.HasTypeDefinition then
    let signature =
      match Hack.genericArguments t with
      | [] ->
        option {
          let! id = t.TryIdentity
          return id
        }
      | xs -> 
        option {
          let! xs = listSignature xs
          let! id = t.TryIdentity
          return Generic (id, xs)
        }
    option {
      let! signature = signature
      if Hack.isAbbreviation t then
        let! original = abbreviationRoot t
        return TypeAbbreviation { Abbreviation = signature; Original = original }
      else
        return signature
    }
  else
    None
and abbreviationRoot (t: FSharpType) =
  if t.IsAbbreviation then
    abbreviationRoot t.AbbreviatedType
  elif Hack.isFloat t then
    Some (Identity (Identity.ofDotNetType typeof<System.Double>))
  elif t.IsFunctionType then
    option {
      let! xs = toFlatArrow t
      return Arrow xs
    }
  else
    toSignature t
and toFlatArrow (t: FSharpType): LowType list option =
  match Seq.toList t.GenericArguments with
  | [ x; y ] when y.IsFunctionType ->
    option {
      let! xSig = toSignature x
      let! ySigs = toFlatArrow y
      return xSig :: ySigs
    }
  | [ x; y ] ->
    option {
      let! xSig = toSignature x
      let! ySig = toSignature y
      return [ xSig; ySig ]
    }
  | _ -> None
and listSignature (ts: FSharpType seq) =
  let f (t: FSharpType) (acc: LowType list option) =
    option {
      let! acc = acc
      let! signature = toSignature t
      return signature :: acc
    }
  Seq.foldBack f ts (Some [])
and fsharpEntityToSignature (x: FSharpEntity) =
  let identity = x.Identity
  let args = x |> genericParameters |> List.map (fun v -> Variable (VariableSource.Target, v))
  match args with
  | [] -> identity
  | xs -> Generic (identity, xs)

let collectTypeConstraints (genericParamters: seq<FSharpGenericParameter>): TypeConstraint list =
  genericParamters
  |> Seq.collect (fun p ->
    let variable = p.Name
    p.Constraints
    |> Seq.choose (fun c -> 
      if c.IsCoercesToConstraint then
        option {
          let! parent = toSignature c.CoercesToTarget
          return { Variables = [ variable ]; Constraint = SubtypeConstraints parent }
        }
      elif c.IsSupportsNullConstraint then
        Some { Variables = [ variable ]; Constraint = NullnessConstraints }
      elif c.IsMemberConstraint then
        option {
          let data = c.MemberConstraintData
          let modifier = if data.MemberIsStatic then MemberModifier.Static else MemberModifier.Instance
          let! returnType = toSignature data.MemberReturnType
          let! args = listSignature data.MemberArgumentTypes
          let args =
            if data.MemberIsStatic then
              if args.Length = 0 then
                [ LowType.unit ] // Core.Unit is removed if the argument is only Core.Unit.
              else
                args
            else
              if args.Length = 1 then
                [ LowType.unit ] // Core.Unit is removed if the argument is only Core.Unit.
              else
                List.tail args // instance member contains receiver
          let variables = data.MemberSources |> Seq.map (fun x -> x.GenericParameter.Name) |> Seq.toList
          let name = data.MemberName
          let member' = { Name = name; Kind = MemberKind.Method; Arguments = args; ReturnType = returnType; IsCurried = false; GenericParameters = [] }
          return { Variables = variables; Constraint = MemberConstraints (modifier, member') }
        }
      elif c.IsNonNullableValueTypeConstraint then
        Some { Variables = [ variable ]; Constraint = ValueTypeConstraints }
      elif c.IsReferenceTypeConstraint then
        Some { Variables = [ variable ]; Constraint = ReferenceTypeConstraints }
      elif c.IsRequiresDefaultConstructorConstraint then
        Some { Variables = [ variable ]; Constraint = DefaultConstructorConstraints }
      elif c.IsEqualityConstraint then
        Some { Variables = [ variable ]; Constraint = EqualityConstraints }
      elif c.IsComparisonConstraint then
        Some { Variables = [ variable ]; Constraint = ComparisonConstraints }
      elif c.IsEnumConstraint then
        Some { Variables = [ variable ]; Constraint = EnumerationConstraints }
      elif c.IsDelegateConstraint then
        Some { Variables = [ variable ]; Constraint = DelegateConstraints }
      elif c.IsUnmanagedConstraint then
        Some { Variables = [ variable ]; Constraint = UnmanagedConstraints }
      else
        None
    )
  )
  |> Seq.toList
  |> List.distinct

let parameterSignature (t: FSharpMemberOrFunctionOrValue) =
  let xs = [
    for group in t.CurriedParameterGroups do
      for parameter in group do
        yield parameter.Type
  ]
  listSignature xs

let methodMember (x: FSharpMemberOrFunctionOrValue) =
  option {
    let name =
      let n = x.DisplayName
      if Regex.IsMatch(n, @"^\( .* \)$") then
        x.CompiledName
      else
        n
    let! args =
      if x.CurriedParameterGroups.Count = 1 && x.CurriedParameterGroups.[0].Count = 0 then
        toSignature x.FullType.GenericArguments.[0] |> Option.map List.singleton
      else
        parameterSignature x

    let! returnType = toSignature x.ReturnParameter.Type
    let isCurried = x.CurriedParameterGroups.Count >= 2
    let genericParams = x.GenericParametersAsTypeVariable
    return { Name = name; Kind = MemberKind.Method; GenericParameters = genericParams; Arguments = args; IsCurried = isCurried; ReturnType = returnType }
  }

let propertyMember (x: FSharpMemberOrFunctionOrValue) =
  option {
    let memberKind = MemberKind.Property x.PropertyKind
    let! args = parameterSignature x
    let! returnType = toSignature x.ReturnParameter.Type
    let genericParams = x.GenericParametersAsTypeVariable
    return { Name = x.DisplayName; Kind = memberKind; GenericParameters = genericParams; Arguments = args; IsCurried = false; ReturnType = returnType }
  }

let toModuleValue (declaringModuleName: FriendlyName) (x: FSharpMemberOrFunctionOrValue) =
  option {
    let name = { DisplayName = x.DisplayName; GenericParameterCount = 0 } :: declaringModuleName
    let! signature = (toSignature x.FullType)
    let target =
      if x.IsActivePattern then
        let kind = if x.DisplayName.Contains("|_|") then ActivePatternKind.PartialActivePattern else ActivePatternKind.ActivePattern
        ApiSignature.ActivePatten (kind, signature)
      else
        match signature with
        | Arrow xs -> ApiSignature.ModuleFunction xs
        | x -> ApiSignature.ModuleValue x
    return { Name = FriendlyName name; Signature = target; TypeConstraints = collectTypeConstraints x.GenericParameters }
  }

let toTypeExtension (x: FSharpMemberOrFunctionOrValue) =
  option {
    let existingType = fsharpEntityToSignature x.LogicalEnclosingEntity
    let modifier = x.MemberModifier

    let! member' =
      if x.IsPropertyGetterMethod || x.IsPropertySetterMethod then
        None
      elif x.IsProperty then
        propertyMember x
      else
        let existingTypeParameters = x.LogicalEnclosingEntity.GenericParameters |> Seq.map (fun x -> x.DisplayName) |> Seq.toArray
        let removeExistingTypeParameters xs = xs |> List.filter (fun p -> existingTypeParameters |> Array.exists ((=)p) = false)
        methodMember x
        |> Option.map (fun m -> { m with GenericParameters = removeExistingTypeParameters m.GenericParameters })

    let declaration =
      let name =
        if x.IsProperty then
          if x.HasGetterMethod then
            x.GetterMethod.EnclosingEntity.FullName
          else
            x.SetterMethod.EnclosingEntity.FullName
        else
          x.EnclosingEntity.FullName
      LoadingName (name, [])

    let signature = ApiSignature.TypeExtension { ExistingType = existingType; Declaration = declaration; MemberModifier = modifier; Member = member' }
    let name =
      let memberTypeName = x.LogicalEnclosingEntity.FullName
      let memberName = { DisplayName = member'.Name; GenericParameterCount = 0 }
      LoadingName (memberTypeName, [ memberName ])
    return { Name = name; Signature = signature; TypeConstraints = collectTypeConstraints x.GenericParameters }
  }

let toFSharpApi (declaringSignatureName: FriendlyName) (x: FSharpMemberOrFunctionOrValue) =
  if x.IsExtensionMember then
    toTypeExtension x
  else
    toModuleValue declaringSignatureName x

let constructorSignature (declaringSignatureName: FriendlyName) declaringSignature (x: FSharpMemberOrFunctionOrValue) =
  option {
    let! target = methodMember x
    let target = { target with Name = declaringSignatureName.Head.DisplayName; ReturnType = declaringSignature }
    return { Name = FriendlyName declaringSignatureName; Signature = ApiSignature.Constructor (declaringSignature, target); TypeConstraints = collectTypeConstraints x.GenericParameters }
  }

let memberSignature (loadMember: FSharpMemberOrFunctionOrValue -> Member option) (declaringSignatureName: FriendlyName) (declaringEntity: FSharpEntity) declaringSignature (x: FSharpMemberOrFunctionOrValue) =
  option {
    let! member' = loadMember x
    let name = { DisplayName = member'.Name; GenericParameterCount = 0 } :: declaringSignatureName
    let typeConstraints = Seq.append declaringEntity.GenericParameters x.GenericParameters |> collectTypeConstraints
    return { Name = FriendlyName name; Signature = x.TargetSignatureConstructor declaringSignature member'; TypeConstraints = typeConstraints }
  }

let toTypeMemberApi (declaringSignatureName: FriendlyName) (declaringEntity: FSharpEntity) (declaringSignature: LowType) (x: FSharpMemberOrFunctionOrValue) =
  if x.IsConstructor then
    constructorSignature declaringSignatureName declaringSignature x
  elif x.IsMethod then
    memberSignature methodMember declaringSignatureName declaringEntity declaringSignature x
  elif x.IsProperty then
    memberSignature propertyMember declaringSignatureName declaringEntity declaringSignature x
  else
    None

let toFieldApi (accessPath: FriendlyName) (declaringEntity: FSharpEntity) (declaringSignature: LowType) (field: FSharpField) =
  option {
    let! returnType = toSignature field.FieldType
    let member' = { Name = field.Name; Kind = MemberKind.Field; GenericParameters = []; Arguments = []; IsCurried = false; ReturnType = returnType }
    let apiName = { DisplayName = field.Name; GenericParameterCount = 0 } :: accessPath
    return { Name = FriendlyName apiName; Signature = field.TargetSignatureConstructor declaringSignature member'; TypeConstraints = collectTypeConstraints declaringEntity.GenericParameters }
  }

let resolveConflictGenericArgumnet (replacementVariables: LowType list) (m: FSharpMemberOrFunctionOrValue) =
  m.GenericParameters
  |> Seq.choose (fun p ->
    let name = p.Name.TrimStart(''')
    let isConflict = replacementVariables |> List.exists (function Variable (VariableSource.Target, n) -> n = name | _ -> false)
    if isConflict then
      let confrictVariable = name
      let newVariable = Variable (VariableSource.Target, name + "1")
      Some (confrictVariable, newVariable)
    else None
  )
  |> Seq.toList

let genericParametersAndArguments (t: FSharpType) =
  Seq.zip t.TypeDefinition.GenericParameters t.GenericArguments
  |> Seq.choose (fun (parameter, arg) -> option {
    let! s = toSignature arg
    let v = parameter.Name.TrimStart(''')
    return v, s
  })
  |> Seq.toList

let updateInterfaceDeclaringType (declaringSignatureName: FriendlyName) declaringSignature api =
  let target =
    match api.Signature with
    | ApiSignature.InstanceMember (_, m) -> ApiSignature.InstanceMember (declaringSignature, m)
    | ApiSignature.StaticMember (_, m) -> ApiSignature.StaticMember (declaringSignature, m)
    | _ -> failwith "It is not a member of interface."
  let name =
    match api.Name with
    | LoadingName (_, name :: _) -> name :: declaringSignatureName
    | FriendlyName (name :: _) -> name :: declaringSignatureName
    | _ -> declaringSignatureName
  { api with Name = FriendlyName name; Signature = target }

let collectTypeAbbreviationDefinition (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  let typeAbbreviationName = { DisplayName = e.DisplayName; GenericParameterCount = e.GenericParameters.Count } :: accessPath
  option {
    let abbreviation = fsharpEntityToSignature e
    let! abbreviatedAndOriginal = toSignature e.AbbreviatedType
    let abbreviated, original =
      match abbreviatedAndOriginal with
      | TypeAbbreviation t -> t.Abbreviation, t.Original
      | original -> original, original
    let t: TypeAbbreviationDefinition = { Abbreviation = abbreviation; Abbreviated = abbreviated; Original = original }
    let target = ApiSignature.TypeAbbreviation t
    return { Name = FriendlyName typeAbbreviationName; Signature = target; TypeConstraints = collectTypeConstraints e.GenericParameters }
  }
  |> Option.toList
  |> List.toSeq

let boolToConstraintStatus = function
  | true -> Satisfy
  | false -> NotSatisfy

let supportNull (e: FSharpEntity) =
  let hasAllowLiteralAttribute (x: FSharpEntity) = x.Attributes |> Seq.exists (fun attr -> attr.AttributeType.TryFullName = Some "Microsoft.FSharp.Core.AllowNullLiteralAttribute")
  if e.IsArrayType then
    true
  elif e.IsFSharp then
    if e.IsInterface || e.IsClass then
      hasAllowLiteralAttribute e
    else
      false
  elif e.IsTuple then
    false
  else
    not (e.IsEnum || e.IsValueType)

let isStruct (e: FSharpEntity) = e.IsValueType

let hasDefaultConstructor (xs: Member seq) =
  xs
  |> Seq.exists (function
    | { Arguments = [ LowType.Patterns.Unit ] } -> true
    | _ -> false)

let conditionalEquality (e:FSharpEntity) =
  let vs =
    e.GenericParameters
    |> Seq.filter (fun x ->
      x.Attributes
      |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.EqualityConditionalOnAttribute")
    )
    |> Seq.map (fun p -> p.DisplayName)
    |> Seq.toList
  match vs with
  | [] -> Satisfy
  | _ -> Dependence vs

let rec equality (cache: Map<FullIdentity, ConstraintStatus>) (e: FSharpEntity) =
  let identity = e.FullIdentity
  let updateCache cache result =
    let cache = Map.add identity result cache
    (cache, result)
  match Map.tryFind identity cache with
  | Some x -> (cache, x)
  | None ->
    if e.Attributes |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.CustomEqualityAttribute") then
      updateCache cache (conditionalEquality e)
    elif e.Attributes |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.NoEqualityAttribute") then
      updateCache cache NotSatisfy
    elif e.IsTuple then
      let vs = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
      let eq =
        match vs with
        | [] -> Satisfy
        | _ -> Dependence vs
      updateCache cache eq
    elif e.IsArrayType then
      let v = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
      updateCache cache (Dependence v)
    else
      match updateCache cache (basicEquality e) with
      | cache, NotSatisfy -> (cache, NotSatisfy)
      | cache, basicResult ->
        match foldDependentTypeEquality cache e with
        | cache, Satisfy -> updateCache cache basicResult
        | cache, _ -> updateCache cache NotSatisfy
and basicEquality (e: FSharpEntity) =
  if e.IsFSharp && (e.IsFSharpRecord || e.IsFSharpUnion || e.IsValueType) then
    let vs = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
    match vs with
    | [] -> Satisfy
    | _ -> Dependence vs
  else
    conditionalEquality e
and foldDependentTypeEquality cache (e: FSharpEntity) =
  let fields =
    seq {
      if e.IsFSharp && (e.IsFSharpRecord || e.IsValueType) then
        yield! e.FSharpFields
      elif e.IsFSharpUnion then
        for unionCase in e.UnionCases do
          yield! unionCase.UnionCaseFields
    }
    |> Seq.map (fun field -> field.FieldType)
  foldFsharpTypeEquality cache fields
and foldFsharpTypeEquality cache (ts: FSharpType seq) =
  let cache, results =
    ts
    |> Seq.fold (fun (cache, results) t ->
      let newCache, result = fsharpTypeEquality cache t
      (newCache, result :: results)
    ) (cache, [])
  let result = if results |> Seq.forall ((=)Satisfy) then Satisfy else NotSatisfy
  (cache, result)
and fsharpTypeEquality cache (t: FSharpType) =
  if t.IsGenericParameter then
    cache, Satisfy
  elif t.IsFunctionType then
    cache, NotSatisfy
  else
    let rec getRoot (t: FSharpType) = if t.IsAbbreviation then getRoot t.AbbreviatedType else t
    let root = getRoot t
    if Seq.isEmpty root.GenericArguments then
      equality cache root.TypeDefinition
    elif root.IsTupleType then
      foldFsharpTypeEquality cache root.GenericArguments
    elif root.IsFunctionType then
      cache, NotSatisfy
    else
      let cache, rootEquality = equality cache root.TypeDefinition
      match rootEquality with
      | Satisfy -> cache, Satisfy
      | NotSatisfy -> cache, NotSatisfy
      | Dependence dependenceVariables ->
        let testArgs =
          root.TypeDefinition.GenericParameters
          |> Seq.map (fun x -> x.DisplayName)
          |> Seq.zip root.GenericArguments
          |> Seq.choose (fun (t, v) -> if Seq.exists ((=)v) dependenceVariables then Some t else None)
        foldFsharpTypeEquality cache testArgs

let conditionalComparison (e:FSharpEntity) =
  let vs =
    e.GenericParameters
    |> Seq.filter (fun x ->
      x.Attributes
      |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.ComparisonConditionalOnAttribute")
    )
    |> Seq.map (fun p -> p.DisplayName)
    |> Seq.toList
  match vs with
  | [] -> Satisfy
  | _ -> Dependence vs

let rec existsInterface identity (e: FSharpEntity) =
  e.DeclaredInterfaces
  |> Seq.exists (fun i -> i.TypeDefinition.FullIdentity = identity || existsInterface identity i.TypeDefinition)

let rec comparison (cache: Map<FullIdentity, ConstraintStatus>) (e: FSharpEntity) =
  let identity = e.FullIdentity
  let updateCache cache result =
    let cache = Map.add identity result cache
    (cache, result)
  match Map.tryFind identity cache with
  | Some x -> (cache, x)
  | None ->
    if e.Attributes |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.CustomComparisonAttribute") then
      updateCache cache (conditionalComparison e)
    elif e.Attributes |> Seq.exists (fun attr -> attr.AttributeType.FullName = "Microsoft.FSharp.Core.NoComparisonAttribute") then
      updateCache cache NotSatisfy
    elif identity = FullIdentity.IntPtr || identity = FullIdentity.UIntPtr then
      updateCache cache Satisfy
    elif e.IsTuple then
      let vs = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
      let eq =
        match vs with
        | [] -> Satisfy
        | _ -> Dependence vs
      updateCache cache eq
    elif e.IsArrayType then
      let v = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
      updateCache cache (Dependence v)
    else
      match updateCache cache (basicComparison e) with
      | cache, NotSatisfy -> (cache, NotSatisfy)
      | cache, basicResult ->
        match foldDependentTypeComparison cache e with
        | cache, Satisfy -> updateCache cache basicResult
        | cache, _ -> updateCache cache NotSatisfy
and basicComparison (e: FSharpEntity) =
  if e.IsFSharp && (e.IsFSharpRecord || e.IsFSharpUnion || e.IsValueType) then
    let vs = e.GenericParameters |> Seq.map (fun p -> p.DisplayName) |> Seq.toList
    match vs with
    | [] -> Satisfy
    | _ -> Dependence vs
  elif existsInterface FullIdentity.IComparable e || existsInterface FullIdentity.IStructuralComparable e then
    conditionalComparison e
  else
    NotSatisfy
and foldDependentTypeComparison cache (e: FSharpEntity) =
  let fields =
    seq {
      if e.IsFSharp && (e.IsFSharpRecord || e.IsValueType) then
        yield! e.FSharpFields
      elif e.IsFSharpUnion then
        for unionCase in e.UnionCases do
          yield! unionCase.UnionCaseFields
    }
    |> Seq.map (fun field -> field.FieldType)
  foldFsharpTypeComparison cache fields
and foldFsharpTypeComparison cache (ts: FSharpType seq) =
  let cache, results =
    ts
    |> Seq.fold (fun (cache, results) t ->
      let newCache, result = fsharpTypeComparison cache t
      (newCache, result :: results)
    ) (cache, [])
  let result = if results |> Seq.forall ((=)Satisfy) then Satisfy else NotSatisfy
  (cache, result)
and fsharpTypeComparison cache (t: FSharpType) =
  if t.IsGenericParameter then
    cache, Satisfy
  elif t.IsFunctionType then
    cache, NotSatisfy
  else
    let rec getRoot (t: FSharpType) = if t.IsAbbreviation then getRoot t.AbbreviatedType else t
    let root = getRoot t
    if Seq.isEmpty root.GenericArguments then
      comparison cache root.TypeDefinition
    elif root.IsTupleType then
      foldFsharpTypeComparison cache root.GenericArguments
    elif root.IsFunctionType then
      cache, NotSatisfy
    else
      let cache, rootComparison = comparison cache root.TypeDefinition
      match rootComparison with
      | Satisfy -> cache, Satisfy
      | NotSatisfy -> cache, NotSatisfy
      | Dependence dependenceVariables ->
        let testArgs =
          root.TypeDefinition.GenericParameters
          |> Seq.map (fun x -> x.DisplayName)
          |> Seq.zip root.GenericArguments
          |> Seq.choose (fun (t, v) -> if Seq.exists ((=)v) dependenceVariables then Some t else None)
        foldFsharpTypeComparison cache testArgs

let fullTypeDef (name: FriendlyName) (e: FSharpEntity) members =
  option {
    let identity = { e.FullIdentity with Name = FriendlyName name }
    let baseType =
      if not e.IsInterface then
        e.BaseType |> Option.bind toSignature
      else
        None
    let instanceMembers =
      members
      |> Seq.choose (function
        | { Signature = ApiSignature.InstanceMember (_, m) } -> Some m
        | _ -> None)
      |> Seq.toList
    let staticMembers =
      members
      |> Seq.choose (function
        | { Signature = ApiSignature.StaticMember (_, m) } -> Some m
        | _ -> None)
      |> Seq.toList

    let implicitInstanceMembers, implicitStaticMembers = CompilerOptimization.implicitMembers identity

    let typeDef = {
      Name = name
      FullName = e.FullName
      AssemblyName = identity.AssemblyName
      BaseType = baseType
      AllInterfaces = e.DeclaredInterfaces |> Seq.filter (fun x -> x.TypeDefinition.Accessibility.IsPublic) |> Seq.choose toSignature |> Seq.toList
      GenericParameters = genericParameters e
      TypeConstraints = e.GenericParameters |> collectTypeConstraints
      InstanceMembers = instanceMembers
      StaticMembers = staticMembers

      ImplicitInstanceMembers = implicitInstanceMembers
      ImplicitStaticMembers = implicitStaticMembers

      SupportNull = supportNull e |> boolToConstraintStatus
      ReferenceType = isStruct e |> not |> boolToConstraintStatus
      ValueType = isStruct e |> boolToConstraintStatus
      DefaultConstructor = 
        let constructors = members |> Seq.choose (function { Signature = ApiSignature.Constructor (_, m)} -> Some m | _ -> None)
        hasDefaultConstructor constructors
        |> boolToConstraintStatus
      Equality = equality Map.empty e |> snd
      Comparison = comparison Map.empty e |> snd
    }
    return { Name = FriendlyName name; Signature = ApiSignature.FullTypeDefinition typeDef; TypeConstraints = typeDef.TypeConstraints }
  }

let rec collectApi (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  seq {
    if e.IsNamespace then
      let accessPath = { DisplayName = e.DisplayName; GenericParameterCount = 0 } :: accessPath
      yield! collectFromNestedEntities accessPath e
    elif e.IsFSharpModule && not e.IsCompilerInternal then
      yield! collectFromModule accessPath e
    elif e.IsFSharpAbbreviation && not e.IsMeasure then
      yield! collectTypeAbbreviationDefinition accessPath e
    elif e.IsClass || e.IsValueType || e.IsFSharpRecord || e.IsFSharpUnion || e.IsArrayType then
      yield! collectFromType accessPath e
    elif e.IsInterface then
      yield! collectFromInterface accessPath e
  }
and collectFromNestedEntities (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  e.NestedEntities
  |> Seq.filter (fun n -> n.Accessibility.IsPublic)
  |> Seq.collect (collectApi accessPath)
and collectFromModule (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  let moduleName = { DisplayName = e.DisplayName; GenericParameterCount = 0 } :: accessPath
  seq {
    yield! e.MembersFunctionsAndValues
            |> Seq.filter (fun x -> x.Accessibility.IsPublic)
            |> Seq.choose (toFSharpApi accessPath)
    yield! collectFromNestedEntities moduleName e
  }
and collectFromType (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  let typeName = { DisplayName = e.DisplayName; GenericParameterCount = e.GenericParameters.Count } :: accessPath
  seq {
    let declaringSignature = fsharpEntityToSignature e

    let members =
      e.MembersFunctionsAndValues
      |> Seq.filter (fun x -> x.Accessibility.IsPublic && not x.IsCompilerGenerated)
      |> Seq.choose (toTypeMemberApi typeName e declaringSignature)
      |> Seq.cache

    let fields =
      e.FSharpFields
      |> Seq.filter (fun x -> x.Accessibility.IsPublic && not x.IsCompilerGenerated)
      |> Seq.choose (toFieldApi typeName e declaringSignature)
      |> Seq.cache

    match fullTypeDef typeName e (Seq.append members fields) with
    | Some d ->
      yield d
      yield! members
      yield! fields
    | None -> ()
    yield! collectFromNestedEntities typeName e
  }
and collectInterfaceMembers (declaringSignatureName: FriendlyName) (inheritArgs: (TypeVariable * LowType) list) (e: FSharpEntity): Api seq =
  let replaceVariable replacements = function
    | ApiSignature.InstanceMember (declaringType, member') ->
      let apply = LowType.applyVariable VariableSource.Target replacements
      let declaringType = apply declaringType
      let genericParameters =
        member'.GenericParameters
        |> List.map (fun p ->
          match replacements |> Map.tryFind p with
          | Some (Variable (_, replacement)) -> replacement
          | Some _ -> failwith "Generic parameter replacement should be variable."
          | None -> p)
      let member' =
        { member' with
            GenericParameters = genericParameters
            Arguments = member'.Arguments |> List.map apply
            ReturnType = member'.ReturnType |> apply
        }
      ApiSignature.InstanceMember (declaringType, member')
    | _ -> failwith "It is not interface member."
  
  seq {
    let declaringSignature = fsharpEntityToSignature e
    let replacementVariables = inheritArgs |> List.map snd |> List.collect LowType.collectVariables
    yield! e.MembersFunctionsAndValues
            |> Seq.filter (fun x -> x.Accessibility.IsPublic && not x.IsCompilerGenerated)
            |> Seq.choose (fun member' -> option {
              let! api = toTypeMemberApi declaringSignatureName e declaringSignature member'
              let variableReplacements =
                List.append (resolveConflictGenericArgumnet replacementVariables member') inheritArgs
                |> Map.ofList
              let api = { api with Signature = replaceVariable variableReplacements api.Signature }
              return api
            })

    for parentInterface in e.DeclaredInterfaces |> Seq.filter (fun x -> x.TypeDefinition.Accessibility.IsPublic) do
      let inheritArgs = genericParametersAndArguments parentInterface
      yield! collectInterfaceMembers declaringSignatureName inheritArgs parentInterface.TypeDefinition
              |> Seq.map (updateInterfaceDeclaringType declaringSignatureName declaringSignature)
  }
and collectFromInterface (accessPath: FriendlyName) (e: FSharpEntity): Api seq =
  let interfaceName = { DisplayName = e.DisplayName; GenericParameterCount = e.GenericParameters.Count } :: accessPath
  seq {
    let members = collectInterfaceMembers interfaceName [] e |> Seq.cache

    match fullTypeDef interfaceName e members with
    | Some d ->
      yield d
      yield! members
    | None -> ()
  }

let collectTypeAbbreviations (xs: Api seq) =
  xs
  |> Seq.choose (function { Signature = ApiSignature.TypeAbbreviation t } -> Some t | _ -> None)
  |> Seq.map (fun t ->
    {
      Abbreviation = t.Abbreviation
      Original = t.Original
    }: TypeAbbreviation
  )

let load (assembly: FSharpAssembly): ApiDictionary =
  let api =
    assembly.Contents.Entities
    |> Seq.filter (fun e -> e.Accessibility.IsPublic)
    |> Seq.collect (fun e -> collectApi (FriendlyName.ofString e.AccessPath) e)
    |> Seq.toArray
  let types =
    api
    |> Seq.choose (function { Signature = ApiSignature.FullTypeDefinition full } -> Some full | _ -> None)
    |> Seq.toArray
  let typeAbbreviations =
    collectTypeAbbreviations api
    |> Seq.toArray
  { AssemblyName = assembly.SimpleName; Api = api; TypeDefinitions = types; TypeAbbreviations = typeAbbreviations }

let databaseName = "database"

let save path (dictionaries: ApiDictionary[]) =
  use file = File.OpenWrite(path)
  let serializer = FsPickler.CreateBinarySerializer()
  serializer.Serialize(file, dictionaries)

let loadFromFile path =
  use file = File.OpenRead(path)
  let serializer = FsPickler.CreateBinarySerializer()
  serializer.Deserialize<ApiDictionary[]>(file)