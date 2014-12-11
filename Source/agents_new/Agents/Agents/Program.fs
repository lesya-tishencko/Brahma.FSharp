﻿module XXX

open Brahma.Helpers
open OpenCL.Net
open Brahma.OpenCL
open Brahma.FSharp.OpenCL.Core
open Microsoft.FSharp.Quotations
open Brahma.FSharp.OpenCL.Extensions


type GpuConfig =
    val Name: string
    val Workers: int
    new (n,w) = {Name=n;Workers=w}

type MasterConfig =
    val Workers: Option<int>
    val GpuConfigs: array<GpuConfig>
    new (w,gc) = {GpuConfigs=gc;Workers=w}

type msg<'data,'res> =
    | Die of AsyncReplyChannel<unit>
    | Process of 'data*('res -> unit)
    | PostProcess of 'res
    | Fill of 'data*('data -> unit)
    | InitBuffers of array<'data>*AsyncReplyChannel<array<'data>>
    | Get of AsyncReplyChannel<Option<'data>>
    | Enq of 'data

type Reader<'d> (isDataEnd, fillF:'d -> Option<'d>) =
    let isTurnedOff = ref false    
    let inner =
        MailboxProcessor.Start(fun inbox ->
            let rec loopR n =
                async { printfn "reader q %A" inbox.CurrentQueueLength
                        let! msg = inbox.Receive()
                        match msg with
                        | Die ch ->
                            ch.Reply()
                            return ()
                        | Fill (x,cont) ->
                            let filled = fillF x
                            //printfn "FILL"
                            if filled |> Option.isSome
                            then 
                                cont x 
                                return! loopR n
                            else
                                isDataEnd := true
                                isTurnedOff := true
                                printfn "Reader finished"
                                return! loopR n
                        | x -> 
                            printfn "unexpected message for reader: %A" x
                            return! loopR n }
            loopR 0)
    
    member this.Read(a, cont) = inner.Post(Fill(a, cont))
    member this.Die() = inner.PostAndReply((fun reply -> Die reply), timeout = 20000)
    member this.IsTurnedOff () = !isTurnedOff

type Worker<'d,'r>(f: 'd -> 'r) =
    let inner =
        MailboxProcessor.Start(fun inbox ->
            let rec loopW n =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Die ch ->
                            ch.Reply()
                            return ()                            
                        | Process (x,continuation) -> 
                            //printfn "PROCESS"
                            let r = f x
                            continuation r
                            return! loopW n
                        | x -> 
                            printfn "unexpected message for Worker: %A" x
                            return! loopW n }
            loopW 0)
 
    member this.Process(a, continuation) = inner.Post(Process(a,continuation))
    member this.Die() = inner.PostAndReply((fun reply -> Die reply), timeout = 20000)

type DataManager<'d>(readers:array<Reader<'d>>) =
    let dataToProcess = new System.Collections.Concurrent.ConcurrentQueue<_>()
    let dataToFill = new System.Collections.Generic.Queue<_>()
    let dataIsEnd = ref false
    let chForReplyToDie = ref None
    let inner =
        MailboxProcessor.Start(fun inbox ->
            let rec loop n =
                async { 
                        //printfn "Data to fill: %A" dataToFill.Count
                        let cnt = ref 3
                        printfn "to process %A" dataToProcess.Count
                        printfn "to fill %A" dataToFill.Count
                        while dataToFill.Count > 0 && !cnt > 0 do
                            decr cnt
                            printfn "go"
                            let b = dataToFill.Dequeue()
                            if not <| readers.[0].IsTurnedOff()
                            then readers.[0].Read(b, fun a -> dataToProcess.Enqueue a)
                        if inbox.CurrentQueueLength > 0
                        then
                            let! msg = inbox.Receive()
                            match msg with
                            | Die ch ->
                                chForReplyToDie := Some ch
                                if !dataIsEnd 
                                    then
                                        ch.Reply()
                                        return ()
                                return! loop n
                            | InitBuffers (bufs,ch) ->
                                ch.Reply bufs
                                bufs |> Array.iter dataToFill.Enqueue                                
                                return! loop n
                            | Get(ch) -> 
                                //printfn "GET"
                                let b =
                                    let s,r = dataToProcess.TryDequeue()
                                    if s then Some r
                                    elif not <| readers.[0].IsTurnedOff ()
                                    then
                                        let rec go _ =
                                            let s,r = dataToProcess.TryDequeue() 
                                            //System.Threading.Thread.Sleep(100)
                                            if s 
                                            then r
                                            else go ()
                                        go () |> Some
                                    else None
//                                    if dataToProcess.Count > 0
//                                    then dataToProcess.Dequeue() |> Some
//                                    elif not <| readers.[0].IsTurnedOff ()
//                                    then
//                                        while dataToProcess.Count = 0 do 
//                                            System.Threading.Thread.Sleep(100)
//                                            ((*printf "!"*))
//                                        //printfn ""
//                                        dataToProcess.Dequeue() |> Some
//                                    else None
                                ch.Reply b
                                if b.IsSome
                                then 
                                    return! loop n
                                elif chForReplyToDie.Value.IsSome
                                then                                     
                                    chForReplyToDie.Value.Value.Reply()
                                    return ()
                                else
                                    dataIsEnd := true
                                    return! loop n
                            | Enq b ->
                                dataToFill.Enqueue b
                                return! loop n
                            | x ->  
                                printfn "Unexpected message for Worker: %A" x
                                return! loop n
                            else return! loop n }
            loop 0)
 
    member this.InitBuffers(bufs) = inner.PostAndReply((fun reply -> InitBuffers(bufs,reply)), timeout = 20000)
    member this.GetData() = 
        if !dataIsEnd
        then None
        else inner.PostAndReply((fun reply -> Get reply), timeout = 20000)
    member this.Enq(b) = inner.Post(Enq b)
    member this.Die() = inner.PostAndReply((fun reply -> Die reply), timeout = 20000)

let rows = 1100
let columns = 1100

type Master<'d,'r,'fr>((*config:MasterConfig,*) workers:array<Worker<'d,'r>>, fill: 'd -> Option<'d>, bufs:ResizeArray<'d>, postProcessF:Option<'r->'fr>) =        
    
    let isDataEnd = ref false
    let reader = new Reader<_>(isDataEnd, fill)
    let mutable isEnd = false
            
    let dataManager = new DataManager<'d>([|reader|])

    let postprocessor =
        match postProcessF with
        | Some f -> Some <| new Worker<'r,'fr>(f)
        | None -> None

    let bufers = dataManager.InitBuffers(bufs.ToArray())
    let freeWorkers = new System.Collections.Concurrent.ConcurrentQueue<_>(workers)
    let inner =
        MailboxProcessor.Start(fun inbox ->
            let rec loop n =
                async {
                        if not freeWorkers.IsEmpty
                        then 
                            let success,w = freeWorkers.TryDequeue()
                            if success
                            then
                                let b = dataManager.GetData()
                                if b.IsSome
                                then
                                    w.Process
                                        (b.Value
                                        , fun a -> 
                                            match postprocessor with Some p -> p.Process(a,fun _ -> ()) | None -> ()
                                            freeWorkers.Enqueue w
                                            dataManager.Enq b.Value)
                        if inbox.CurrentQueueLength > 0
                        then
                            let! msg = inbox.Receive()
                            match msg with
                            | Die ch ->
                                //System.Threading.Thread.Sleep(5000)//while freeWorkers.Count < workers.Length do ()
                                reader.Die()
                                dataManager.Die()                                
                                workers |> Array.iter (fun w -> w.Die())
                                match postprocessor with Some p -> p.Die() | None -> ()
                                isEnd <- true
                                ch.Reply()
                                return ()
                            | x ->
                                printfn "unexpected message for Worker: %A" x
                                return! loop n 
                        else return! loop n}
            loop 0)

    member this.Die() = inner.PostAndReply((fun reply -> Die reply))
    member this.IsDataEnd() = !isDataEnd


//let workerF platformName deviceType localWorkSize i =        
//
//    let provider =
//        try  ComputeProvider.Create(platformName, deviceType)
//        with 
//        | ex -> failwith ex.Message
//
//    //printfn "Using %A" provider
//
//    let commandQueue = new CommandQueue(provider, provider.Devices |> Seq.nth i)    
//
//    let cParallel = Array.zeroCreate(rows * columns)
//    let aValues = Array.zeroCreate(rows * columns)
//    let bValues = Array.zeroCreate(rows * columns)    
//
//    let command rows columns = 
//        <@
//            fun (r:_2D) (a:array<_>) (b:array<_>) (c:array<_>) -> 
//                let tx = r.GlobalID0
//                let ty = r.GlobalID1
//                let mutable buf = c.[ty * columns + tx]
//                for k in 0 .. columns - 1 do
//                    buf <- buf + (a.[ty * columns + k] * b.[k * columns + tx])
//                c.[ty * columns + tx] <- buf
//        @>
//
//    let kernel, kernelPrepare, kernelRun = provider.Compile <| command rows columns
//    let d =(new _2D(rows, columns, localWorkSize, localWorkSize))
//    do kernelPrepare d aValues bValues cParallel
//
//    let f = fun (x,y) ->
//        let _ = commandQueue.Add(aValues.ToGpu(provider, x))
//        let _ = commandQueue.Add(bValues.ToGpu(provider, y))
//        let _ = commandQueue.Add(kernelRun())
//        let _ = commandQueue.Add(cParallel.ToHost provider).Finish()
//        [|cParallel.Length|]
//    f, aValues, bValues
//
//
//let platformName = "NVIDIA*"
//
//let deviceType = DeviceType.Gpu    
//
//let bufs = new ResizeArray<_>()
//for i in 0 ..5 do bufs.Add(Array.zeroCreate(rows * columns),Array.zeroCreate(rows * columns))
//let workers platformName deviceType localWorkSize = 
//    Array.init 4 
//        (fun i -> 
//            let f,a,b = workerF platformName deviceType localWorkSize (i%2)
//            bufs.Add(a,b)
//            new Worker(f))
//
//
//let random = new System.Random()    
//
//let count = ref 1000
//
//let fill (x,y) =
//    if !count > 0
//    then
//        Array.fill x 0 (rows * columns) (float32 (random.NextDouble()))
//        Array.fill y 0 (rows * columns) (float32 (random.NextDouble()))
//        decr count
//        Some (x,y)
//    else None
//
//let start = System.DateTime.Now
//let master = new Master(workers platformName deviceType 10, fill, bufs, Some (fun x -> printfn "%A" x))
//while not <| master.IsDataEnd() do ()
//master.Die()
//printfn "Time = %A" <| System.DateTime.Now - start
