module MkSerializer

open TypeShape.Core
open TypeShape.Core.Utils
open MongoDB.Bson
open MongoDB.Bson.IO
open MongoDB.Bson.Serialization


type Enc<'t> = IBsonWriter -> 't -> unit

type Dec<'t> = IBsonReader -> 't

type EncDec<'t> =
    { enc : 't Enc
      dec : 't Dec }

type FieldEncDec<'t> =
    { memberName : string
      memberEnc : // TODO read and throwaway the value
                  IBsonWriter -> 't -> unit
      memberDec : IBsonReader -> 't -> 't }

 // 't = 'a option
  // document
   // array of documents with k,v fields
   open System.Reflection

let noDec : 't Dec = function
    | _ -> Unchecked.defaultof<'t>

let rec mkBsonSerializer<'t>() : 't EncDec =
    let ctx = new TypeCache()
    mkBsonSerializerCached<'t> ctx

and private mkBsonSerializerCached<'t> (ctx : TypeCache) : 't EncDec =
    match ctx.TryFind<'t EncDec>() with
    | Some p -> p
    | None ->
        let p = mkBsonSerializerAux<'t> ctx
        ctx.ForceAdd p
        p

and private mkBsonSerializerAux<'t> (ctx : TypeCache) : 't EncDec =
    let codec (enc : 'a Enc) (dec : 'a Dec) : 't EncDec =
        { enc = enc
          dec = dec }
        |> unbox

    let mkMemberSerializer (field : IShapeWriteMember<'DeclaringType>) =
        field.Accept { new IWriteMemberVisitor<'DeclaringType, 'DeclaringType FieldEncDec> with
                           member __.Visit(shape : ShapeWriteMember<'DeclaringType, 'Field>) =
                               let p = mkBsonSerializerCached<'Field> ctx

                               let enc (w : IBsonWriter) c =
                                   let m = shape.Project c
                                   p.enc w m

                               let dec (r : IBsonReader) c =
                                   let m = p.dec r
                                   shape.Inject c m

                               { memberName = field.Label
                                 memberEnc = enc
                                 memberDec = dec } }

    let readIgnore (r : IBsonReader) =
        ()

    let combineMemberSerializers (init : unit -> 'a)
        (members : 'a FieldEncDec []) =
        let decs =
            members
            |> Array.map (fun m -> m.memberName, m.memberDec)
            |> Map.ofArray

        let encs = members |> Array.map (fun m -> m.memberName, m.memberEnc)

        let enc (w : IBsonWriter) (c : 'a) =
            w.WriteStartDocument()
            for (name, enc) in encs do
                w.WriteName(name)
                enc w c
            w.WriteEndDocument()

        let dec (r : IBsonReader) =
            let mutable c = init()
            r.ReadStartDocument()
            while (r.GetCurrentBsonType() <> BsonType.EndOfDocument) do
                let name = r.ReadName()
                match decs |> Map.tryFind name with
                | None -> readIgnore r
                | Some dec -> c <- dec r c
            r.ReadEndDocument()
            c

        { enc = enc
          dec = dec }

    let writeSeq (tp : 'a Enc) (w : IBsonWriter) (ts : 'a seq) =
        w.WriteStartArray()
        ts |> Seq.iter (fun t -> tp w t)
        w.WriteEndArray()

    let readSeq (tp : 'a Dec) (r : IBsonReader) =
        seq {
            do r.ReadStartArray()
            while r.CurrentBsonType <> BsonType.EndOfDocument do
                yield tp r
            do r.ReadEndArray()
        }

    let shape = shapeof<'t>
    printfn "shapeof<'t> is %A" shape
    match shape with
    | Shape.Unit -> codec (fun w () -> w.WriteNull()) (fun r -> r.ReadNull())
    | Shape.Bool ->
        codec (fun w v -> w.WriteBoolean v) (fun r -> r.ReadBoolean())
    | Shape.Int32 -> codec (fun w v -> w.WriteInt32 v) (fun r -> r.ReadInt32())
    | Shape.FSharpOption s ->
        s.Accept { new IFSharpOptionVisitor<'t EncDec> with
                       member __.Visit<'a>() =
                           let tp = mkBsonSerializerCached<'a> ctx
                           codec (fun w v ->
                               match v with
                               | None -> w.WriteNull()
                               | Some t -> tp.enc w t) (fun r ->
                               if r.CurrentBsonType = BsonType.Null then None
                               else tp.dec r |> Some) }
    | Shape.FSharpList s ->
        s.Accept
            { new IFSharpListVisitor<'t EncDec> with
                  member __.Visit<'t>() =
                      let tp = mkBsonSerializerCached<'t> ctx
                      codec (fun w (ts : 't list) -> writeSeq tp.enc w ts)
                          (fun r -> readSeq tp.dec r |> List.ofSeq) }
    | Shape.Array s ->
        s.Accept
            { new IArrayVisitor<'t EncDec> with
                  member __.Visit<'t> rank =
                      let tp = mkBsonSerializerCached<'t> ctx
                      codec (fun w (ts : 't array) -> writeSeq tp.enc w ts)
                          (fun r -> readSeq tp.dec r |> Array.ofSeq) }
    | Shape.FSharpSet s ->
        s.Accept
            { new IFSharpSetVisitor<'t EncDec> with
                  member __.Visit<'t when 't : comparison>() =
                      let tp = mkBsonSerializerCached<'t> ctx
                      codec (fun w (ts : 't Set) -> writeSeq tp.enc w ts)
                          (fun r -> readSeq tp.dec r |> Set.ofSeq) }
    | Shape.FSharpMap s ->
        s.Accept { new IFSharpMapVisitor<'t EncDec> with
                       member __.Visit<'k, 'v when 'k : comparison>() =
                           let kp = mkBsonSerializerCached<'k> ctx
                           let vp = mkBsonSerializerCached<'v> ctx
                           if typeof<'k> <> typeof<string> then
                               codec (fun w m ->
                                   w.WriteStartDocument()
                                   m
                                   |> Map.iter (fun k v ->
                                          w.WriteName k
                                          vp.enc w v)
                                   w.WriteEndDocument()) noDec
                           else
                               codec (fun w m ->
                                   w.WriteStartArray()
                                   let mutable i = 0
                                   for KeyValue(k, v) in m do
                                       i <- i + 1
                                       i
                                       |> string
                                       |> w.WriteStartDocument
                                       w.WriteName "k"
                                       kp.enc w k
                                       w.WriteName "v"
                                       vp.enc w v
                                       w.WriteEndDocument()
                                       ()
                                   w.WriteEndArray()) noDec }
    | Shape.Tuple(:? ShapeTuple<'t> as shape) ->
        let elemSerializers = shape.Elements |> Array.map mkMemberSerializer
        codec (fun w (t : 't) ->
            w.WriteStartArray()
            elemSerializers |> Seq.iter (fun ep -> ep.memberEnc w t)
            w.WriteEndArray()) noDec
    | Shape.FSharpRecord(:? ShapeFSharpRecord<'t> as shape) ->
        shape.Fields
        |> Array.map mkMemberSerializer
        |> combineMemberSerializers (fun () -> shape.CreateUninitialized())
    | Shape.Poco(:? ShapePoco<'t> as shape) ->
        shape.Fields
        |> Array.map mkMemberSerializer
        |> combineMemberSerializers (fun () -> shape.CreateUninitialized())
    | Shape.FSharpUnion(:? ShapeFSharpUnion<'t> as shape) ->
        let mkUnionCaseSerializer (s : ShapeFSharpUnionCase<'t>) =
            let fieldSerializers = s.Fields |> Array.map mkMemberSerializer
            fun (w : IBsonWriter) (u : 't) ->
                w.WriteStartArray()
                w.WriteString("1", s.CaseInfo.Name)
                match fieldSerializers with
                | [||] -> ()
                | [| fp |] ->
                    w.WriteName "2"
                    fp.memberEnc w u
                | fps ->
                    fps
                    |> Seq.iteri (fun i fp ->
                           i + 2
                           |> sprintf "%i"
                           |> w.WriteName
                           fp.memberEnc w u)
                w.WriteEndArray()

        let caseSerializers =
            shape.UnionCases |> Array.map mkUnionCaseSerializer
        codec (fun w (u : 't) ->
            let enc = caseSerializers.[shape.GetTag u]
            enc w u) noDec
    | _ -> failwithf "unsupported type '%O'" typeof<'t>

type TypeShapeSerializer<'t>() =
    let codec = mkBsonSerializer<'t>()
    interface IBsonSerializer<'t> with
        member x.Serialize(context : BsonSerializationContext,
                           args : BsonSerializationArgs, value : 't) : unit =
            codec.enc context.Writer value

        member x.Serialize(context : BsonSerializationContext,
                           args : BsonSerializationArgs, value : obj) : unit =
            value
            |> unbox
            |> codec.enc context.Writer

        member x.Deserialize(context : BsonDeserializationContext,
                             args : BsonDeserializationArgs) : 't =
            codec.dec context.Reader
        member x.Deserialize(context : BsonDeserializationContext,
                             args : BsonDeserializationArgs) : obj =
            codec.dec context.Reader |> box
        member x.ValueType = typedefof<'t>

type TypeShapeSerializerProvider() =
    member __.MkBsonSerializer<'t>() = TypeShapeSerializer<'t>()
    member __.MkBsonSerializer(ty : System.Type) : IBsonSerializer =
        typedefof<TypeShapeSerializerProvider>.GetTypeInfo()
            .GetMethod("MkBsonSerializer").MakeGenericMethod(ty)
            .Invoke(__, null) |> unbox
    interface MongoDB.Bson.Serialization.IBsonSerializationProvider with
        member __.GetSerializer(ty : System.Type) = __.MkBsonSerializer ty
