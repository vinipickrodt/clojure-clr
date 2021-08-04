﻿namespace Clojure.Collections

open System
open System.Threading
open Clojure.Collections
open System.Collections
open System.Collections.Generic

// A PersistentHashMap consists of a head node representing the map that has a points to a tree of nodes containing the key/value pairs.
// The head node indicated if null is a key and holds the associated value.  
// Thus the tree is guaranteed not to contain a null key, allowing null to be used as an 'empty field' indicator.
// The tree contains three kinds of nodes:
//     ArrayNode
//     BitmapIndexedNode
//     HashCollisionNode
//
// This arrangement seems ideal for a discriminated union, 
//  but the need for mutable fields (required to implement IEditableCollection and the ITransientXxx interfaces in-place)
//  made the code unwieldy.  Perhaps some smarter than me can do this someday.


type KVMangleFn<'T> = obj * obj -> 'T


[<AllowNullLiteral>]
type private INode =
    abstract assoc : shift:int * hash:int * key:obj *  value:obj *  addedLeaf:SillyBox -> INode
    abstract without : shift:int * hash:int * key:obj -> INode
    abstract find : shift:int * hash: int * key:obj -> IMapEntry
    abstract find : shift:int * hash: int * key:obj * notFound:obj -> obj
    abstract getNodeSeq : unit -> ISeq
    abstract assoc : edit: AtomicReference<Thread> *  shift:int * hash:int * key:obj *  value:obj *  addedLeaf:SillyBox -> INode
    abstract without : edit: AtomicReference<Thread> *  shift:int * hash:int * key:obj * removedLeaf:SillyBox-> INode
    abstract kvReduce : fn:IFn *  init:obj -> obj
    abstract fold : combinef:IFn * reducef:IFn * fjtask:IFn * fjfork:IFn * fjjoin:IFn -> obj
    abstract iterator : d:KVMangleFn<obj> -> IEnumerator
    abstract iteratorT : d:KVMangleFn<'T> -> IEnumerator<'T>

module private INodeOps =

    // INode[] manipulation

    let cloneAndSet(arr : 'T[], i: int, a : 'T) : 'T[] =
        let clone : 'T[] = downcast arr.Clone()
        clone.[i] <- a
        clone


    let cloneAndSet2(arr: 'T[], i: int, a: 'T, j: int, b: 'T ) : 'T[] =
        let clone : 'T[] = downcast arr.Clone()
        clone.[i] <- a
        clone.[j] <- b
        clone

    let removePair(arr: 'T[], i: int) : 'T[] =
        let newArr : 'T[] = Array.zeroCreate <| arr.Length-2 
        Array.Copy(arr,0,newArr,0,2*i)
        Array.Copy(arr,2*(i+1),newArr,2*i,newArr.Length-2*i)
        newArr

    // Random goodness

    let hash(k) = Util.hasheq(k)
    let bitPos(hash, shift) = 1 <<< Util.mask(hash,shift)
    let bitIndex(bitmap,bit) = Util.bitCount(bitmap &&& (bit-1))

    let findIndex(key:obj, items: obj[], count: int) : int =
       seq { 0 .. 2 .. 2*count-1 }
       |> Seq.tryFindIndex (fun i -> Util.equiv(key,items.[i]))
       |> Option.defaultValue -1


open INodeOps


[<AllowNullLiteral>]
type PersistentHashMap private (meta, count, root, hasNull, nullValue) =
    inherit APersistentMap()

    let meta : IPersistentMap = meta
    let count : int = count
    let root : INode = root
    let hasNull : bool = hasNull
    let nullValue: obj = nullValue

    internal new(count,root,hasNull,nullValue) = PersistentHashMap(null,count,root,hasNull,nullValue)

    member internal x.Meta = meta
    member internal x.Count = count
    member internal x.Root = root
    member internal x.HasNull = hasNull
    member internal x.NullValue = nullValue

    static member Empty = PersistentHashMap(null,0,null,false,null)
    static member private notFoundValue = obj()

    // factories

    static member create( other: IDictionary) : IPersistentMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        for e in other |> Seq.cast<DictionaryEntry> do
            ret <- ret.assoc(e.Key,e.Value)
        ret.persistent()

    static member create( [<ParamArray>] init : obj[]) : PersistentHashMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        for i in 0 .. 2 .. init.Length-1 do
            ret <- ret.assoc(init.[i],init.[i+1])
        downcast ret.persistent()
 
    static member createWithCheck( [<ParamArray>] init : obj[]) : PersistentHashMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        for i in 0 .. 2 .. init.Length-1 do
            ret <- ret.assoc(init.[i],init.[i+1])
            if ret.count() <> i/2+1 then raise <| ArgumentException("init","Duplicate key: " + init.[i].ToString())
        downcast ret.persistent()

    static member create1(init:IList) : PersistentHashMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        let ie = init.GetEnumerator()
        while ie.MoveNext() do
            let key = ie.Current
            if not (ie.MoveNext()) then raise <| ArgumentException("init","No value supplied for " + key.ToString())
            let value = ie.Current
            ret <- ret.assoc(key,value)
        downcast ret.persistent()

    static member createWithCheck(items: ISeq) : PersistentHashMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        let rec step (i:int) (s:ISeq) =
            if not (isNull s) 
            then
                if isNull (s.next()) then raise <| ArgumentException("items","No value supplied for key: "+ items.first().ToString())
                ret <- ret.assoc(items.first(),RT.second(items))
                if ret.count() <> i+1 then raise <| ArgumentException("items", "Duplicate key: "  + items.first().ToString())
                step (i+1) (s.next().next())
        step 0 items
        downcast ret.persistent()        

    static member create(items: ISeq) : PersistentHashMap =
        let mutable ret = (PersistentHashMap.Empty:>IEditableCollection).asTransient() :?> ITransientMap
        let rec step (i:int) (s:ISeq) =
            if not (isNull s) 
            then
                if isNull (s.next()) then raise <| ArgumentException("items","No value supplied for key: "+ items.first().ToString())
                ret <- ret.assoc(items.first(),RT.second(items))
                step (i+1) (s.next().next())
        step 0 items
        downcast ret.persistent()


    static member create(meta:IPersistentMap, [<ParamArray>] init : obj[]) : PersistentHashMap =
        (PersistentHashMap.create(init):>IObj).withMeta(meta) :?> PersistentHashMap

     
    interface IMeta with    
        override x.meta() = meta

    interface IObj with
        override x.withMeta(m) = 
            if m = meta then upcast x else upcast PersistentHashMap(m,count,root,hasNull,nullValue)

    interface Counted with  
        override x.count() = count

    interface ILookup with
        override x.valAt(k) = (x:>ILookup).valAt(k,null)
        override x.valAt(k,nf) =
            if isNull k then 
                if hasNull then nullValue else nf
            elif isNull root then 
                null
            else root.find(0,hash(k),k,nf)
            
    interface Associative with
        override x.containsKey(k) = 
            if isNull k 
            then hasNull 
            else not (isNull root) && root.find(0,hash(k),k,PersistentHashMap.notFoundValue) <> PersistentHashMap.notFoundValue
        override x.entryAt(k) =
            if isNull k
            then if hasNull then upcast MapEntry.create(null,nullValue) else null
            elif isNull root then null
            else root.find(0,hash(k),k)


       
    interface Seqable with
        override x.seq() = 
            let s = if isNull root then null else root.getNodeSeq()
            if hasNull then upcast Cons(MapEntry.create(null,nullValue),s) else s


    interface IPersistentCollection with
        override x.count()  = count
        override x.empty() =  (PersistentHashMap.Empty:>IObj).withMeta(meta) :?> IPersistentCollection


    interface IPersistentMap with
        override x.assoc(k,v) =
            if isNull k then
                if hasNull && v = nullValue then 
                    upcast x 
                else
                    upcast PersistentHashMap(meta, (if hasNull then count else count+1), root, true, v)
            else 
                let addedLeaf = SillyBox()
                let rootToUse = if isNull root then BitmapIndexedNode.Empty else root
                let newRoot = rootToUse.assoc(0,hash(k),k,v,addedLeaf)
                if newRoot = root then 
                    upcast x 
                else 
                    upcast PersistentHashMap(meta,(if addedLeaf.isSet then count+1 else count), newRoot, hasNull, nullValue)

        override x.assocEx(k,v) =
            if (x:>Associative).containsKey(k) then raise <| InvalidOperationException("Key already present")
            (x:>IPersistentMap).assoc(k,v)

        override x.without(k) =
            if isNull k then
                if hasNull then 
                    upcast PersistentHashMap(meta,count-1,root,false,null)
                else
                    upcast x
            elif isNull root then
                upcast x
            else
                let newRoot = root.without(0,hash(k),k)
                if newRoot = root then
                    upcast x
                else
                    upcast PersistentHashMap(meta,count-1,newRoot,hasNull,nullValue)
    

    interface IEditableCollection with  
        member x.asTransient() = upcast TransientHashMap(x)
    
    interface ITransientCollection with
        member x.conj(o)  = raise <| NotImplementedException()
        member x.persistent()  = raise <| NotImplementedException()

    interface ITransientAssociative with
        member x.assoc(k,v) = raise <| NotImplementedException()

    interface ITransientMap with
        member x.assoc(k,v) = raise <| NotImplementedException()
        member x.without(k) = raise <| NotImplementedException()
        member x.persistent()  = raise <| NotImplementedException()


    
    static member emptyEnumerator() =    Seq.empty.GetEnumerator()
    static member nullEnumerator(d:KVMangleFn<obj>, nullValue:obj, root:IEnumerator) =
        let s = 
            seq { 
                yield d(null,nullValue)
                while root.MoveNext() do
                    yield root.Current
            }
        s.GetEnumerator()

    static member nullEnumeratorT<'T>(d:KVMangleFn<'T>, nullValue:obj, root:IEnumerator<'T>) =
        let s = 
            seq { 
                yield d(null,nullValue)
                while root.MoveNext() do
                    yield root.Current
            }
        s.GetEnumerator()

    member x.MakeEnumerator( d: KVMangleFn<Object> ) : IEnumerator =
        let rootIter = if isNull root then PersistentHashMap.emptyEnumerator() else root.iteratorT(d)
        if hasNull then upcast PersistentHashMap.nullEnumerator(d, nullValue, rootIter) else upcast rootIter
        
    member x.MakeEnumeratorT<'T>( d: KVMangleFn<'T> ) =
        let rootIter = if isNull root then PersistentHashMap.emptyEnumerator() else root.iteratorT(d)
        if hasNull then PersistentHashMap.nullEnumeratorT(d, nullValue, rootIter) else rootIter

    interface IMapEnumerable with
        member x.keyEnumerator() = x.MakeEnumerator (fun (k, v) -> k)
        member x.valEnumerator() = x.MakeEnumerator (fun (k, v) -> v)
        
  
    interface IEnumerable<IMapEntry> with   
        member x.GetEnumerator() =  x.MakeEnumeratorT<IMapEntry> (fun (k, v) -> upcast MapEntry.create(k,v))
    
    interface IEnumerable with   
        member x.GetEnumerator() =  x.MakeEnumerator (fun (k, v) -> upcast MapEntry.create(k,v))

    interface IKVReduce with
        member x.kvreduce(f,init) =
            let init = if hasNull then f.invoke(init,null,nullValue) else init
            match init with                                                     // in original, call to RT.isReduced
            | :? Reduced as r -> (r:>IDeref).deref()
            | _ -> 
                if not (isNull root) then
                    match root.kvReduce(f,init) with                            // in original, call to RT.isReduced
                    | :? Reduced as r -> (r:>IDeref).deref()
                    | _ as r -> r
                else
                    init

    member x.fold(n:int64, combinef:IFn, reducef:IFn, fjinvoke:IFn, fjtask:IFn, fjfork:IFn, fjjoin:IFn) : obj =
        // JVM: we are ignoreing n for now
        let top : Func<obj> = Func<obj>( (fun () -> 
            let mutable ret = combinef.invoke()     
            if not (isNull root) then 
                ret <- combinef.invoke(ret,root.fold(combinef,reducef,fjtask,fjfork,fjjoin))
            if hasNull then 
                combinef.invoke(ret,reducef.invoke(combinef.invoke(),null,nullValue))
            else
                ret ))
        fjinvoke.invoke(top)






and private TransientHashMap(e,r,c,hn,nv) =
    inherit ATransientMap()

    let [<NonSerialized>] edit : AtomicReference<Thread> = e
    let [<VolatileField>] mutable root : INode = r
    let [<VolatileField>] mutable count : int = c
    let [<VolatileField>] mutable hasNull : bool  = hn
    let [<VolatileField>] mutable nullValue : obj = nv
    let leafFlag : SillyBox = SillyBox()

    new(m:PersistentHashMap) = TransientHashMap(AtomicReference(Thread.CurrentThread),m.Root,m.Count,m.HasNull,m.NullValue)
    
    override x.doAssoc(k,v) = 
        if isNull k then  
            if nullValue <> v then nullValue <- v
            if not hasNull then
                count <- count+1
                hasNull <- true
        else
            leafFlag.reset()
            let n = (if isNull root then BitmapIndexedNode.Empty else root).assoc(edit,0,hash(k),k,v,leafFlag)
            if n <> root then root <- n
            if leafFlag.isSet then count <- count+1
        upcast x

    override x.doWithout(k) =
        if isNull k then
            if hasNull then
                hasNull <- false
                nullValue <- null
                count <- count-1
        elif not (isNull root) then
            leafFlag.reset()
            let n = root.without(edit,0,hash(k),k,leafFlag)
            if n <> root then root <- n
            if leafFlag.isSet then count <- count-1 
        upcast x

    override x.doPersistent() =
        edit.Set(null)
        upcast PersistentHashMap(count,root,hasNull,nullValue)

    override x.doValAt(k,nf) = 
        if isNull k then
            if hasNull then nullValue else nf
        elif isNull root then
            nf
        else
            root.find(0,hash(k),k,nf)

    override x.ensureEditable() =
        if  edit.Get() |> isNull then raise <| InvalidOperationException("Transient used after persistent! call")



and [<Sealed>] private ArrayNode(e,c,a) =
    let mutable count: int = c
    let array : INode[] = a
    [<NonSerialized>]   
    let edit: AtomicReference<Thread> = e

    member private x.setNode(i,n) = array.[i] <- n
    member private x.incrementCount() = count <- count+1
    member private x.decrementCount() = count <- count-1

    // TODO: Do this with some sequence functions?
    member x.pack(edit,idx) : INode =
        let newArray : obj[] = Array.zeroCreate <| 2*(count-1)
        let mutable j = 1
        let mutable bitmap = 0
        for i = 0 to idx-1 do
            if not (isNull array.[i]) then
                newArray.[j] <- upcast array.[i]
                bitmap <- bitmap ||| 1 <<< i
                j <- j+2
        for i = idx+1 to array.Length-1 do
            if not (isNull array.[i]) then
                newArray.[j] <- upcast array.[i]
                bitmap <- bitmap ||| 1 <<< i
                j <- j+2
        upcast BitmapIndexedNode(edit,bitmap,newArray)

    
    member x.ensureEditable(e) =
        if edit = e then x else ArrayNode(e,count,array.Clone():?>INode[])

    member x.editAndSet(e,i,n) =
        let editable = x.ensureEditable(e)
        editable.setNode(i,n)
        editable
    

    interface INode with
        member x.assoc(shift,hash,key,value,addedLeaf) =
            let idx = Util.mask(hash,shift)
            let node = array.[idx]
            if isNull node then
                upcast ArrayNode(null,count+1,cloneAndSet(array,idx,BitmapIndexedNode.Empty.assoc(shift+5,hash,key, value,addedLeaf)))
            else
                let n = node.assoc(shift+5,hash,key,value,addedLeaf)
                if n = node then upcast x else upcast ArrayNode(null,count,cloneAndSet(array,idx,n))

        member x.without(shift,hash,key) =
            let idx = Util.mask(hash,shift)
            let node = array.[idx]
            if isNull node then
                upcast x
            else
                let n = node.without(shift+5,hash,key)
                if n = node then
                    upcast x
                elif isNull n then
                    if count <= 8 then // shrink
                        x.pack(null,idx)
                    else 
                        upcast ArrayNode(null,count-1,cloneAndSet(array,idx,n))
                else 
                    upcast ArrayNode(null, count, cloneAndSet(array,idx,n))       
                    
        member x.find(shift,hash,key) = 
            let idx = Util.mask(hash,shift)
            let node = array.[idx]
            match node with
            | null -> null
            | _ -> node.find(shift+5,hash,key)

        member x.find(shift,hash,key,nf) =
            let idx = Util.mask(hash,shift)
            let node = array.[idx]
            match node with
            | null -> nf
            | _ -> node.find(shift+5,hash,key,nf)

        member x.getNodeSeq() = ArrayNodeSeq.create(array)

        member x.assoc(e,shift,hash,key,value,addedLeaf) =
            let idx =  Util.mask(hash,shift)
            let node = array.[idx]
            if isNull node then 
                let editable = x.editAndSet(edit,idx,BitmapIndexedNode.Empty.assoc(e,shift+5,hash,key,value,addedLeaf))
                editable.incrementCount()
                upcast editable
            else
                let n = node.assoc(e,shift+5,hash,key,value,addedLeaf)
                if n = node then upcast x else upcast x.editAndSet(e,idx,n)
    
        member x.without(e,shift,hash,key,removedLeaf) =            
            let idx =  Util.mask(hash,shift)
            let node = array.[idx]
            if isNull node then
                upcast x
            else
                let n = node.without(e,shift+5,hash,key,removedLeaf)
                if n = node then
                    upcast x
                elif isNull n then
                    if count <= 8 then // shrink
                        x.pack(e,idx)
                    else
                        let editable = x.editAndSet(e,idx,n)
                        editable.decrementCount()
                        upcast editable
                else
                    upcast x.editAndSet(e,idx,n)

        member x.kvReduce(f,init) =
            let rec step  (i:int) (v:obj) =
                if i >= array.Length then
                    v
                else
                    let n = array.[i]
                    let nextv = n.kvReduce(f,v)
                    if RT.isReduced(nextv) then
                        nextv
                    else 
                        step (i+1) nextv
            step 0 init

        member x.fold(combinef,reducef,fjtask,fjfork,fjjoin) =
            let tasks =
                array
                |> Array.map (fun node -> Func<obj>((fun () -> node.fold(combinef,reducef,fjtask,fjfork,fjjoin))))
            ArrayNode.foldTasks(tasks,combinef,fjtask,fjfork,fjjoin)

    static member foldTasks(tasks: Func<obj>[], combinef:IFn, fjtask:IFn ,fjfork: IFn,fjjoin:IFn) =
        match tasks.Length with
        | 0 -> combinef.invoke()
        | 1 -> tasks.[0].Invoke()
        | _ ->
            let halves = tasks |> Array.splitInto 2
            let fn = Func<obj>( fun () -> ArrayNode.foldTasks(halves.[1],combinef,fjtask,fjfork,fjjoin))
            let forked = fjfork.invoke(fjtask.invoke(fn))
            combinef.invoke(ArrayNode.foldTasks(halves.[0],combinef,fjtask,fjfork,fjjoin), fjjoin.invoke(forked))
            

and private ArrayNodeSeq(m,ns,i,s) =
    inherit ASeq(m)

    let nodes : INode[] = ns
    let i : int = i
    let s : ISeq = s

    static member create(meta: IPersistentMap, nodes: INode[], i: int, s: ISeq) : ISeq = 
        match s with
        | null -> 
            let result = 
                nodes
                |> Seq.skip (i-1)
                |> Seq.indexed
                |> Seq.filter (fun (j,node) -> not (isNull node))

                |> Seq.tryPick (fun (j,node) -> 
                    let ns = node.getNodeSeq()  
                    if (isNull ns) then None else ArrayNodeSeq(meta,nodes,j+1,ns) |> Some )
            match result with
            | Some s -> upcast s
            | None -> null

    interface IObj with
        override x.withMeta(m) = 
            if m = (x:>IMeta).meta() then upcast x else upcast ArrayNodeSeq(m,nodes,i,s)

    interface ISeq with
        member x.first() = s.first()
        member x.next() = ArrayNodeSeq.create(null,nodes,i,s.next())






         

        | _ -> upcast ArrayNodeSeq(meta,nodes,i,s)

    static member create(nodes : INode[]) = ArrayNodeSeq.create(null,nodes,0,null)





            
        

            










and [<Sealed>] internal BitmapIndexedNode(e:AtomicReference<Thread>,b,a) =
    let edit : AtomicReference<Thread> = e
    let bitmap : int = b
    let array : obj[] = a

    static member Empty = upcast BitmapIndexedNode(null,0,Array.empty)

    interface INode with
        member x.assoc(shift,hsh,key,value,addedLeaf) = invalidOp "Not implemented"


//////////////////////////////////
//////////////////////////////////
//////////////////////////////////




//           #region Node factories

//           private static INode CreateNode(int shift, object key1, object val1, int key2hash, object key2, object val2)
//           {
//               int key1hash = Hash(key1);
//               if (key1hash == key2hash)
//                   return new HashCollisionNode(null, key1hash, 2, new object[] { key1, val1, key2, val2 });
//               Box _ = new Box(null);
//               AtomicReference<Thread> edit = new AtomicReference<Thread>();
//               return BitmapIndexedNode.EMPTY
//                   .Assoc(edit, shift, key1hash, key1, val1, _)
//                   .Assoc(edit, shift, key2hash, key2, val2, _);
//           }

//           private static INode CreateNode(AtomicReference<Thread> edit, int shift, Object key1, Object val1, int key2hash, Object key2, Object val2)
//           {
//               int key1hash = Hash(key1);
//               if (key1hash == key2hash)
//                   return new HashCollisionNode(null, key1hash, 2, new Object[] { key1, val1, key2, val2 });
//               Box _ = new Box(null);
//               return BitmapIndexedNode.EMPTY
//                   .Assoc(edit, shift, key1hash, key1, val1, _)
//                   .Assoc(edit, shift, key2hash, key2, val2, _);
//           }

//           #endregion



//               #region iterators

//               public IEnumerator Iterator(KVMangleDel<Object> d)
//               {
//                   foreach (INode node in _array)
//                   {
//                       if (node != null)
//                       {
//                           IEnumerator ie = node.Iterator(d);

//                           while (ie.MoveNext())
//                               yield return ie.Current;
//                       }
//                   }
//               }

//               public IEnumerator<T> IteratorT<T>(KVMangleDel<T> d)
//               {
//                   foreach (INode node in _array)
//                   {
//                       if (node != null)
//                       {
//                           IEnumerator<T> ie = node.IteratorT(d);

//                           while (ie.MoveNext())
//                               yield return ie.Current;
//                       }
//                   }
//               }

//               #endregion
//           }

//           #endregion

//           #region BitmapIndexNode

//           /// <summary>
//           ///  Represents an internal node in the trie, not full.
//           /// </summary>
//           [Serializable]
//           sealed class BitmapIndexedNode : INode
//           {
//               #region Data

//               internal static readonly BitmapIndexedNode EMPTY = new BitmapIndexedNode(null, 0, Array.Empty<object>());

//               int _bitmap;
//               object[] _array;
//               [NonSerialized]
//               readonly AtomicReference<Thread> _edit;

//               #endregion

//               #region Calculations

//               int Index(int bit)
//               {
//                   return Util.BitCount(_bitmap & (bit - 1));
//               }

//               #endregion

//               #region C-tors & factory methods

//               internal BitmapIndexedNode(AtomicReference<Thread> edit, int bitmap, object[] array)
//               {
//                   _bitmap = bitmap;
//                   _array = array;
//                   _edit = edit;
//               }

//               #endregion

//               #region INode Members

//               public INode Assoc(int shift, int hash, object key, object val, Box addedLeaf)
//               {
//                   int bit = Bitpos(hash, shift);
//                   int idx = Index(bit);
//                   if ((_bitmap & bit) != 0)
//                   {
//                       object keyOrNull = _array[2 * idx];
//                       object valOrNode = _array[2 * idx + 1];
//                       if (keyOrNull == null)
//                       {
//                           INode n = ((INode)valOrNode).Assoc(shift + 5, hash, key, val, addedLeaf);
//                           if (n == valOrNode)
//                               return this;
//                           return new BitmapIndexedNode(null, _bitmap, CloneAndSet(_array, 2 * idx + 1, n));
//                       }
//                       if ( Util.equiv(key,keyOrNull))
//                       {
//                           if ( val == valOrNode)
//                               return this;
//                           return new BitmapIndexedNode(null,_bitmap,CloneAndSet(_array,2*idx+1,val));
//                       }
//                       addedLeaf.Val = addedLeaf;
//                       return new BitmapIndexedNode(null,_bitmap,
//                           CloneAndSet(_array,
//                                       2*idx,
//                                       null,
//                                       2*idx+1,
//                                       CreateNode(shift+5,keyOrNull,valOrNode,hash,key,val)));
//                   }
//                   else
//                   {
//                       int n = Util.BitCount(_bitmap);
//                       if ( n >= 16 )
//                       {
//                           INode [] nodes = new INode[32];
//                           int jdx = Util.Mask(hash,shift);
//                           nodes[jdx] = EMPTY.Assoc(shift+5,hash,key,val,addedLeaf);
//                           int j=0;
//                           for ( int i=0; i < 32; i++ )
//                               if ( ( (_bitmap >>i) & 1) != 0 )
//                               {
//                                   if ( _array[j] ==  null )
//                                      nodes[i] = (INode) _array[j+1];
//                                   else
//                                       nodes[i] = EMPTY.Assoc(shift+5,Hash(_array[j]),_array[j],_array[j+1], addedLeaf);
//                                   j += 2;
//                               }
//                           return new ArrayNode(null,n+1,nodes);
//                       }
//                       else
//                       {
//                           object[] newArray = new object[2*(n+1)];
//                           Array.Copy(_array, 0, newArray, 0, 2*idx);
//                           newArray[2*idx] = key;
//                           addedLeaf.Val = addedLeaf;
//                           newArray[2*idx+1] = val;
//                           Array.Copy(_array, 2*idx, newArray, 2*(idx + 1), 2*(n - idx));
//                           return new BitmapIndexedNode(null, _bitmap | bit, newArray);
//                       }           
//                   }
//               }


//               public INode Without(int shift, int hash, object key)
//               {
//                   int bit = Bitpos(hash, shift);
//                   if ((_bitmap & bit) == 0)
//                       return this;

//                   int idx = Index(bit);
//                   object keyOrNull = _array[2 * idx];
//                   object valOrNode = _array[2 * idx + 1];
//                   if ( keyOrNull == null )
//                   {
//                       INode n = ((INode)valOrNode).Without(shift+5,hash,key);
//                       if ( n == valOrNode)
//                           return this;
//                       if ( n != null )
//                           return new BitmapIndexedNode(null,_bitmap,CloneAndSet(_array,2*idx+1,n));
//                       if ( _bitmap == bit )
//                           return null;
//                       return new BitmapIndexedNode(null,_bitmap^bit,RemovePair(_array,idx));
//                   }
//                   if (Util.equiv(key, keyOrNull))
//                   {
//                       if (_bitmap == bit)
//                           return null;
//                       return new BitmapIndexedNode(null, _bitmap ^ bit, RemovePair(_array, idx));
//                   }
//                   return this;
//               }

//               public IMapEntry Find(int shift, int hash, object key)
//               {
//                   int bit = Bitpos(hash, shift);
//                   if ((_bitmap & bit) == 0)
//                       return null;
//                   int idx = Index(bit);
//                    object keyOrNull = _array[2 * idx];
//                   object valOrNode = _array[2 * idx + 1];
//                   if ( keyOrNull == null )
//                       return ((INode)valOrNode).Find(shift+5,hash,key);
//                   if ( Util.equiv(key,keyOrNull))
//                       return (IMapEntry)MapEntry.create(keyOrNull, valOrNode);
//                   return null;
//               }


//               public Object Find(int shift, int hash, Object key, Object notFound)
//               {
//                   int bit = Bitpos(hash, shift);
//                   if ((_bitmap & bit) == 0)
//                       return notFound;
//                   int idx = Index(bit);
//                   Object keyOrNull = _array[2 * idx];
//                   Object valOrNode = _array[2 * idx + 1];
//                   if (keyOrNull == null)
//                       return ((INode)valOrNode).Find(shift + 5, hash, key, notFound);
//                   if (Util.equiv(key, keyOrNull))
//                       return valOrNode;
//                   return notFound;
//               }

//               public ISeq GetNodeSeq()
//               {
//                   return NodeSeq.Create(_array);
//               }

//               public INode Assoc(AtomicReference<Thread> edit, int shift, int hash, object key, object val, Box addedLeaf)
//               {
//                   int bit = Bitpos(hash, shift);
//                   int idx = Index(bit);
//                   if ((_bitmap & bit) != 0)
//                   {
//                       object keyOrNull = _array[2 * idx];
//                       object valOrNode = _array[2 * idx + 1];
//                       if (keyOrNull == null)
//                       {
//                           INode n = ((INode)valOrNode).Assoc(edit, shift + 5, hash, key, val, addedLeaf);
//                           if (n == valOrNode)
//                               return this;
//                           return EditAndSet(edit, 2 * idx + 1, n);
//                       }
//                       if (Util.equiv(key, keyOrNull))
//                       {
//                           if (val == valOrNode)
//                               return this;
//                           return EditAndSet(edit, 2 * idx + 1, val);
//                       }
//                       addedLeaf.Val = addedLeaf;
//                       return EditAndSet(edit,
//                           2*idx,null,
//                           2*idx+1,CreateNode(edit,shift+5,keyOrNull,valOrNode,hash,key,val));
//                   }
//                   else
//                   {int n = Util.BitCount(_bitmap);
//                       if ( n*2 < _array.Length )
//                       {
//                           addedLeaf.Val = addedLeaf;
//                           BitmapIndexedNode editable = EnsureEditable(edit);
//                           Array.Copy(editable._array,2*idx,editable._array,2*(idx+1),2*(n-idx));
//                           editable._array[2*idx] = key;
//                           editable._array[2*idx+1] = val;
//                           editable._bitmap |= bit;
//                           return editable;
//                       }
//                       if ( n >= 16 )
//                       {
//                           INode[] nodes = new INode[32];
//                           int jdx = Util.Mask(hash,shift);
//                           nodes[jdx] = EMPTY.Assoc(edit,shift+5,hash,key,val,addedLeaf);
//                           int j=0;
//                           for ( int i=0; i<32; i++ )
//                               if (((_bitmap>>i) & 1) != 0 )
//                               {
//                                   if ( _array[j] == null )
//                                       nodes[i] = (INode)_array[j+1];
//                                   else
//                                       nodes[i] = EMPTY.Assoc(edit,shift+5,Hash(_array[j]), _array[j], _array[j+1], addedLeaf);
//                                   j += 2;
//                               }
//                           return new ArrayNode(edit,n+1,nodes);
//                       }
//                       else
//                       {
//                           object[] newArray = new object[2*(n+4)];
//                           Array.Copy(_array,0,newArray,0,2*idx);
//                           newArray[2*idx] = key;
//                           addedLeaf.Val = addedLeaf;
//                           newArray[2 * idx + 1] = val;
//                           Array.Copy(_array,2*idx,newArray,2*(idx+1),2*(n-idx));
//                           BitmapIndexedNode editable = EnsureEditable(edit);
//                           editable._array = newArray;
//                           editable._bitmap |= bit;
//                           return editable;
//                       }
//                   }
//               }



//               public INode Without(AtomicReference<Thread> edit, int shift, int hash, object key, Box removedLeaf)
//               {
//                   int bit = Bitpos(hash, shift);
//                   if ((_bitmap & bit) == 0)
//                       return this;
//                   int idx = Index(bit);
//                   Object keyOrNull = _array[2 * idx];
//                   Object valOrNode = _array[2 * idx + 1];
//                   if (keyOrNull == null)
//                   {
//                       INode n = ((INode)valOrNode).Without(edit, shift + 5, hash, key, removedLeaf);
//                       if (n == valOrNode)
//                           return this;
//                       if (n != null)
//                           return EditAndSet(edit, 2 * idx + 1, n);
//                       if (_bitmap == bit)
//                           return null;
//                       return EditAndRemovePair(edit, bit, idx);
//                   }
//                   if (Util.equiv(key, keyOrNull))
//                   {
//                       removedLeaf.Val = removedLeaf;
//                       // TODO: collapse
//                       return EditAndRemovePair(edit, bit, idx);
//                   }
//                   return this;
//               }

//               public object KVReduce(IFn f, object init)
//               {
//                   return NodeSeq.KvReduce(_array, f, init);
//               }

//               public object Fold(IFn combinef, IFn reducef, IFn fjtask, IFn fjfork, IFn fjjoin)
//               {
//                   return NodeSeq.KvReduce(_array, reducef, combinef.invoke());
//               }


//               #endregion

//               #region Implementation

//               private BitmapIndexedNode EditAndSet(AtomicReference<Thread> edit, int i, Object a)
//               {
//                   BitmapIndexedNode editable = EnsureEditable(edit);
//                   editable._array[i] = a;
//                   return editable;
//               }

//               private BitmapIndexedNode EditAndSet(AtomicReference<Thread> edit, int i, Object a, int j, Object b)
//               {
//                   BitmapIndexedNode editable = EnsureEditable(edit);
//                   editable._array[i] = a;
//                   editable._array[j] = b;
//                   return editable;
//               }

//               private BitmapIndexedNode EditAndRemovePair(AtomicReference<Thread> edit, int bit, int i)
//               {
//                   if (_bitmap == bit)
//                       return null;
//                   BitmapIndexedNode editable = EnsureEditable(edit);
//                   editable._bitmap ^= bit;
//                   Array.Copy(editable._array, 2 * (i + 1), editable._array, 2 * i, editable._array.Length - 2 * (i + 1));
//                   editable._array[editable._array.Length - 2] = null;
//                   editable._array[editable._array.Length - 1] = null;
//                   return editable;
//               }

//               BitmapIndexedNode EnsureEditable(AtomicReference<Thread> edit)
//               {
//                   if (_edit == edit)
//                       return this;
//                   int n = Util.BitCount(_bitmap);
//                   object[] newArray = new Object[n >= 0 ? 2 * (n + 1) : 4];  // make room for next assoc
//                   Array.Copy(_array, newArray, 2 * n);
//                   return new BitmapIndexedNode(edit, _bitmap, newArray);
//               }

//               #endregion

//               #region iterators

//               public IEnumerator Iterator(KVMangleDel<Object> d)
//              { 
//                   return NodeIter.GetEnumerator(_array, d);
//               }

//               public IEnumerator<T> IteratorT<T>(KVMangleDel<T> d)
//               {
//                   return NodeIter.GetEnumeratorT(_array, d);
//               }

//               #endregion
//           }

//           #endregion

//           #region HashCollisionNode

//           /// <summary>
//           /// Represents a leaf node corresponding to multiple map entries, all with keys that have the same hash value.
//           /// </summary>
//           [Serializable]
//           sealed class HashCollisionNode : INode
//           {
//               #region Data

//               readonly int _hash;
//               int _count;
//               object[] _array;
//               [NonSerialized]
//               readonly AtomicReference<Thread> _edit;

//               #endregion

//               #region C-tors

//               public HashCollisionNode(AtomicReference<Thread> edit, int hash, int count, params object[] array)
//               {
//                   _edit = edit;
//                   _hash = hash;
//                   _count = count;
//                   _array = array;
//               }

//               #endregion

//               #region details

//               int FindIndex(object key)
//               {
//                   for (int i = 0; i < 2 * _count; i += 2)
//                   {
//                       if (Util.equiv(key, _array[i]))
//                           return i;
//                   }
//                   return -1;
//               }

//               #endregion

//               #region INode Members

//               public INode Assoc(int shift, int hash, object key, object val, Box addedLeaf)
//               {
//                   if (_hash == hash)
//                   {
//                       int idx = FindIndex(key);
//                       if (idx != -1)
//                       {
//                           if (_array[idx + 1] == val)
//                               return this;
//                           return new HashCollisionNode(null, hash, _count, CloneAndSet(_array, idx + 1, val));
//                       }
//                       Object[] newArray = new Object[2 * (_count + 1)];
//                       Array.Copy(_array, 0, newArray, 0, 2 * _count);
//                       newArray[2 * _count] = key;
//                       newArray[2 * _count + 1] = val;
//                       addedLeaf.Val = addedLeaf;
//                       return new HashCollisionNode(_edit, hash, _count + 1, newArray);
//                   }
//                   // nest it in a bitmap node
//                   return new BitmapIndexedNode(null, Bitpos(_hash, shift), new object[] { null, this })
//                       .Assoc(shift, hash, key, val, addedLeaf);
//               }

//               public INode Without(int shift, int hash, object key)
//               {
//                   int idx = FindIndex(key);
//                   if (idx == -1)
//                       return this;
//                   if (_count == 1)
//                       return null;
//                   return new HashCollisionNode(null, hash, _count - 1, RemovePair(_array, idx / 2));
//               }

//               public IMapEntry Find(int shift, int hash, object key)
//               {
//                   int idx = FindIndex(key);
//                   if (idx < 0)
//                       return null;
//                   return (IMapEntry)MapEntry.create(_array[idx], _array[idx + 1]);

//               }

//               public Object Find(int shift, int hash, Object key, Object notFound)
//               {
//                   int idx = FindIndex(key);
//                   if (idx < 0)
//                       return notFound;
//                   return _array[idx + 1];
//               }

//               public ISeq GetNodeSeq()
//               {
//                   return NodeSeq.Create(_array);
//               }

//               public INode Assoc(AtomicReference<Thread> edit, int shift, int hash, Object key, Object val, Box addedLeaf)
//               {
//                   if (hash == _hash)
//                   {
//                       int idx = FindIndex(key);
//                       if (idx != -1)
//                       {
//                           if (_array[idx + 1] == val)
//                               return this;
//                           return EditAndSet(edit, idx + 1, val);
//                       }
//                       if (_array.Length > 2 * _count)
//                       {
//                           addedLeaf.Val = addedLeaf;
//                           HashCollisionNode editable = EditAndSet(edit, 2 * _count, key, 2 * _count + 1, val);
//                           editable._count++;
//                           return editable;
//                       }
//                       object[] newArray = new object[_array.Length + 2];
//                       Array.Copy(_array, 0, newArray, 0, _array.Length);
//                       newArray[_array.Length] = key;
//                       newArray[_array.Length + 1] = val;
//                       addedLeaf.Val = addedLeaf;
//                       return EnsureEditable(edit, _count + 1, newArray);
//                   }
//                   // nest it in a bitmap node
//                   return new BitmapIndexedNode(edit, Bitpos(_hash, shift), new object[] { null, this, null, null })
//                       .Assoc(edit, shift, hash, key, val, addedLeaf);
//               }

//               public INode Without(AtomicReference<Thread> edit, int shift, int hash, Object key, Box removedLeaf)
//               {
//                   int idx = FindIndex(key);
//                   if (idx == -1)
//                       return this;
//                   removedLeaf.Val = removedLeaf;
//                   if (_count == 1)
//                       return null;
//                   HashCollisionNode editable = EnsureEditable(edit);
//                   editable._array[idx] = editable._array[2 * _count - 2];
//                   editable._array[idx + 1] = editable._array[2 * _count - 1];
//                   editable._array[2 * _count - 2] = editable._array[2 * _count - 1] = null;
//                   editable._count--;
//                   return editable;
//               }

//               public object KVReduce(IFn f, object init)
//               {
//                   return NodeSeq.KvReduce(_array, f, init);
//               }

//               public object Fold(IFn combinef, IFn reducef, IFn fjtask, IFn fjfork, IFn fjjoin)
//               {
//                   return NodeSeq.KvReduce(_array, reducef, combinef.invoke());
//               }

//               #endregion

//               #region Implementation

//               HashCollisionNode EnsureEditable(AtomicReference<Thread> edit)
//               {
//                   if (_edit == edit)
//                       return this;
//                   object[] newArray = new Object[2 * (_count + 1)];  // make room for next assoc
//                   System.Array.Copy(_array, 0, newArray, 0, 2 * _count);
//                   return new HashCollisionNode(edit, _hash, _count, newArray);
//               }

//               HashCollisionNode EnsureEditable(AtomicReference<Thread> edit, int count, Object[] array)
//               {
//                   if (_edit == edit)
//                   {
//                       _array = array;
//                       _count = count;
//                       return this;
//                   }
//                   return new HashCollisionNode(edit, _hash, count, array);
//               }

//               HashCollisionNode EditAndSet(AtomicReference<Thread> edit, int i, Object a)
//               {
//                   HashCollisionNode editable = EnsureEditable(edit);
//                   editable._array[i] = a;
//                   return editable;
//               }

//               HashCollisionNode EditAndSet(AtomicReference<Thread> edit, int i, Object a, int j, Object b)
//               {
//                   HashCollisionNode editable = EnsureEditable(edit);
//                   editable._array[i] = a;
//                   editable._array[j] = b;
//                   return editable;
//               }

//               #endregion

//               #region iterators

//               public IEnumerator Iterator(KVMangleDel<Object> d)
//               {
//                   return NodeIter.GetEnumerator(_array, d);
//               }

//               public IEnumerator<T> IteratorT<T>(KVMangleDel<T> d)
//               {
//                   return NodeIter.GetEnumeratorT(_array, d);
//               }

//               #endregion

//           }

//           #endregion

//           #region NodeIter

//           static class NodeIter
//           {
//               public static IEnumerator GetEnumerator(object[] array, KVMangleDel<Object> d)
//               {
//                   for ( int i=0; i< array.Length; i+=2)
//                   {
//                       object key = array[i];
//                       object nodeOrVal = array[i+1];
//                       if (key != null)
//                           yield return d(key, nodeOrVal);
//                       else if ( nodeOrVal != null )
//                       {
//                           IEnumerator ie = ((INode)nodeOrVal).Iterator(d);
//                           while (ie.MoveNext())
//                               yield return ie.Current;
//                       }
//                   }
//               }

//               public static IEnumerator<T> GetEnumeratorT<T>(object[] array, KVMangleDel<T> d)
//               {
//                   for (int i = 0; i < array.Length; i += 2)
//                   {
//                       object key = array[i];
//                       object nodeOrVal = array[i + 1];
//                       if (key != null)
//                           yield return d(key, nodeOrVal);
//                       else if (nodeOrVal != null)
//                       {
//                           IEnumerator<T> ie = ((INode)nodeOrVal).IteratorT(d);
//                           while (ie.MoveNext())
//                               yield return ie.Current;
//                       }
//                   }
//               }
//           }

//           #endregion

//           #region NodeSeq

//           [Serializable]
//           sealed class NodeSeq : ASeq
//           {

//               #region Data

//               readonly object[] _array;
//               readonly int _i;
//               readonly ISeq _s;

//               #endregion

//               #region Ctors

//               [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "<Pending>")]
//               NodeSeq(object[] array, int i)
//                   : this(null, array, i, null)
//               {
//               }

//               public static ISeq Create(object[] array)
//               {
//                   return Create(array, 0, null);
//               }

//               private static ISeq Create(object[] array, int i, ISeq s)
//               {
//                   if (s != null)
//                       return new NodeSeq(null, array, i, s);
//                   for (int j = i; j < array.Length; j += 2)
//                   {
//                       if (array[j] != null)
//                           return new NodeSeq(null, array, j, null);
//                       INode node = (INode)array[j + 1];
//                       if (node != null)
//                       {
//                           ISeq nodeSeq = node.GetNodeSeq();
//                           if (nodeSeq != null)
//                               return new NodeSeq(null, array, j + 2, nodeSeq);
//                       }
//                   }
//                   return null;
//               }

//               NodeSeq(IPersistentMap meta, Object[] array, int i, ISeq s)
//                   : base(meta)
//               {
//                   _array = array;
//                   _i = i;
//                   _s = s;
//               }

//               #endregion

//               #region IObj methods

//               public override IObj withMeta(IPersistentMap meta)
//               {
//                   if (_meta == meta)
//                       return this;

//                   return new NodeSeq(meta, _array, _i, _s);
//               }

//               #endregion

//               #region ISeq methods

//               public override object first()
//               {
//                   if (_s != null)
//                       return _s.first();
//                   return MapEntry.create(_array[_i], _array[_i + 1]);
//               }

//               public override ISeq next()
//               {
//                   if (_s != null)
//                       return Create(_array, _i, _s.next());
//                   return Create(_array, _i + 2, null);
//               }
//               #endregion

//               #region KvReduce

//               static public object KvReduce(object[] array, IFn f, object init)
//               {
//                   for (int i = 0; i < array.Length; i += 2)
//                   {
//                       if (array[i] != null)
//                           init = f.invoke(init, array[i], array[i + 1]);
//                       else
//                       {
//                           INode node = (INode)array[i + 1];
//                           if (node != null)
//                               init = node.KVReduce(f, init);
//                       }
//                       if (RT.isReduced(init))
//                           return init;
//                   }
//                   return init;
//               }

//               #endregion
//           }

//           #endregion
//       }
//}




//    // Node factories

//    let EmptyBitmapNode  = BitmapIndexNode(ref 0, ref Array.empty<obj>,null)

//    //           internal static readonly BitmapIndexedNode EMPTY = new BitmapIndexedNode(null, 0, Array.Empty<object>());

//    //       private static INode CreateNode(int shift, object key1, object val1, int key2hash, object key2, object val2)
//    let createNode(shift:int, key1:obj, val1:obj, key2hash:int, key2:obj, val2:obj) =
//        let key1hash = hash(key1)
//        if key1hash = key2hash then HashCollisionNode(key1hash,ref 2,ref [|key1;val1;key2;val2|],null)
//        else assoc(EmptyBitmapNode,shift,key1hash,key1,val1).assoc(shift,key2hash,key2,val2)

// //       private static INode CreateNode(int shift, object key1, object val1, int key2hash, object key2, object val2)
// //       {
// //           int key1hash = Hash(key1);
// //           if (key1hash == key2hash)
// //               return new HashCollisionNode(null, key1hash, 2, new object[] { key1, val1, key2, val2 });
// //           Box _ = new Box(null);
// //           AtomicReference<Thread> edit = new AtomicReference<Thread>();
// //           return BitmapIndexedNode.EMPTY
// //               .Assoc(edit, shift, key1hash, key1, val1, _)
// //               .Assoc(edit, shift, key2hash, key2, val2, _);
// //       }
 
// //       private static INode CreateNode(AtomicReference<Thread> edit, int shift, Object key1, Object val1, int key2hash, Object key2, Object val2)
// //       {
// //           int key1hash = Hash(key1);
// //           if (key1hash == key2hash)
// //               return new HashCollisionNode(null, key1hash, 2, new Object[] { key1, val1, key2, val2 });
// //           Box _ = new Box(null);
// //           return BitmapIndexedNode.EMPTY
// //               .Assoc(edit, shift, key1hash, key1, val1, _)
// //               .Assoc(edit, shift, key2hash, key2, val2, _);
// //       }
    






////           INode Assoc(int shift, int hash, object key, object val, Box addedLeaf);
////           INode Without(int shift, int hash, object key);
////           IMapEntry Find(int shift, int hash, object key);
////           object Find(int shift, int hash, object key, object notFound);
////           ISeq GetNodeSeq();
////           INode Assoc(AtomicReference<Thread> edit, int shift, int hash, object key, object val, Box addedLeaf);
////           INode Without(AtomicReference<Thread> edit, int shift, int hash, object key, Box removedLeaf);
////           object KVReduce(IFn f, Object init);
////           object Fold(IFn combinef, IFn reducef, IFn fjtask, IFn fjfork, IFn fjjoin);
////       public delegate T KVMangleDel<T>(object k, object v);
////           IEnumerator Iterator(KVMangleDel<Object> d);
////           IEnumerator<T> IteratorT<T>(KVMangleDel<T> d);


//    let without(node, shift, hash, key) = raise <| NotImplementedException("TODO")
//    let find(node, shift, hash, key) = raise <| NotImplementedException("TODO")
//    let find(node, shift, hash, key, notFound) = raise <| NotImplementedException("TODO")
//    let getNodeSeq() = raise <| NotImplementedException("TODO")
//    let gehHash() = raise <| NotImplementedException("TODO")
//    let assoc(node, edit, shift, hash, key, value, addedLeaf) = raise <| NotImplementedException("TODO")
//    let without(node, edit, shift, hash, key) = raise <| NotImplementedException("TODO")
//    let kvReduce(node, f, init) = raise <| NotImplementedException("TODO")
//    let fold(node, combinef, reducef, jftask, fjfork, fjjoin) = raise <| NotImplementedException("TODO") 
//    type KVMangleDel<'T> = delegate of obj * obj -> 'T

//    let iterator(node, d:KVMangleDel<obj>) : IEnumerator  = raise <| NotImplementedException("TODO") 
//    let iteratorT(node, d:KVMangleDel<'T>) : IEnumerator<'T>  = raise <| NotImplementedException("TODO") 
    
//    let rec assoc(node:INode, shift:int, hash:int, key:obj, value:obj) : INode * bool = 
//        match node with
//        | Empty ->  raise <| NotImplementedException("TODO") 
//        | ArrayNode(rcount,nodes,edit) as an -> 
//            let idx = Util.mask(hash,shift)
//            let node = nodes.[idx]
//            if isNull node 
//            then 
//                let newNode, leafAdded = assoc(EmptyBitmapNode,shift+5,hash,key,value) 
//                ArrayNode(ref (!rcount+1), cloneAndSet(nodes,idx,newNode),null), leafAdded
//            else
//                let newNode, leafAdded  = assoc(node,shift+5,hash,key,value)
//                if newNode = node then an, leafAdded
//                else ArrayNode(ref !rcount, cloneAndSet(nodes,idx,newNode),null), leafAdded
//        | BitmapIndexNode(rbitmap,robjs,edit) as bn ->
//            let bit = bitPos(hash,shift)
//            let idx = bitIndex(!rbitmap,bit)
//            if (!rbitmap &&& bit) <> 0 then
//                let keyOrNull = !robjs.[2*idx]
//                let valOrNode = !robjs.[2*idx+1]
//                if isNull keyOrNull then
//                    let n , leafAdded = assoc((valOrNode:>INode),shift+5,hash,key,value)
//                    if n = valOrNode 
//                    then bn, leafAdded
//                    else BitmapIndexNode(ref !rbitmap, cloneAndSet(!objs,2*idx1,n),null), leafAdded
//                elif Util.equiv(key,keyOrNull) then
//                    if value = valOrNode 
//                    then bn, false
//                    else BitmapIndexNode(ref !rbitmap, cloneAndSet(!objs,2*idx1,n,value),null), false
//                else 
//                    BitmapIndexNode(ref !rbitmap, cloneAndSet(!objs,2*idx,null,2*idx+1,createNode(shift+5,keyOrNull,valorNode, hash,key,value)),null), true
//            else 
//                let n = Util.bitCount(!rbitmap)
//                if  n >= 16 
//                then
//                    let newNodes : INode[] = Array.create 32 Empty
//                    int jdx = Util.Mask(hash,shift)
//                    let newNode, leafAdded = assoc(EmptyBitmapNode,shift+5,hash,key,value)
//                    newNodexs.[jdx] <- newNode
//                    let mutable j = 0
//                    for i = 0 to 31 do
//                        if  ((!rbitmap) >>> i) &&& 1 <> 0 
//                        then   
//                            if isNull (!rnodes).[j] 
//                            then (!rnodes).[i] <- downcast (!robsj).[j+1]
//                            else 
//                                let newNode, leafadded =  assoc(EmptyBitmapNode,shift+5,hash((!robjs).[j]),(!robjs).[j],(!robjs).[j+1])
//                                (!rnodes).[i] <- newNode
//                            j <- j+2
//                    ArrayNode(ref (n+1), newNodes, null), leafAdded
//                else

//        | HashCollisionNode(nhash, rcount, robjs, edit) as hn ->
//            if hash = nhash
//            then 
//                let idx = findIndex(key,!robjs,!rcount)
//                if idx <> -1
//                then
//                    if (!robjs).[idx+1] = value
//                    then hn, false
//                    else HashCollisionNode(hash, ref !rcount, ref (cloneAndSet(!robjs, idx+1, value)),null), false
//                else
//                    let newObjs = Array.zeroCreate (2*(!rcount)+1)
//                    Array.Copy(!robjs,0,newObjs,0,2*(!rcount))
//                    newObjs.[2*(!rcount)] <- key
//                    newObjs.[2*(!rcount)+1] <- value
//                    HashCollisionNode(hash,ref ((!rcount)+1),ref newObjs,null),true
//            else BitmapIndexNode( ref (bitPos(nhash,shift)), ref ([| null, hn|]), null), false

        


        
//        //           public INode Assoc(int shift, int hash, object key, object val, Box addedLeaf)
//        //           {
//        //               if (_hash == hash)
//        //               {
//        //                   int idx = FindIndex(key);
//        //                   if (idx != -1)
//        //                   {
//        //                       if (_array[idx + 1] == val)
//        //                           return this;
//        //                       return new HashCollisionNode(null, hash, _count, CloneAndSet(_array, idx + 1, val));
//        //                   }
//        //                   Object[] newArray = new Object[2 * (_count + 1)];
//        //                   Array.Copy(_array, 0, newArray, 0, 2 * _count);
//        //                   newArray[2 * _count] = key;
//        //                   newArray[2 * _count + 1] = val;
//        //                   addedLeaf.Val = addedLeaf;
//        //                   return new HashCollisionNode(_edit, hash, _count + 1, newArray);
//        //               }
//        //               // nest it in a bitmap node
//        //               return new BitmapIndexedNode(null, Bitpos(_hash, shift), new object[] { null, this })
//        //                   .Assoc(shift, hash, key, val, addedLeaf);
//        //           }

