﻿namespace Brahma.TTA.VirtualTTA

[<Measure>] type inPort
[<Measure>] type isFree

(**
 * bool value = true, if this FU is free
 *            = false, if this FU isn't free
 *)
type FunctionUnit = 
    | ADD of int<inPort> * int<inPort> * bool
    | SUB of int<inPort> * int<inPort> * bool
    | DIV of int<inPort> * int<inPort> * bool
    | REGISTER of int<inPort> * bool

type TTA(FUs : array<FunctionUnit * int>, countOfBuses : int) = 
    let board = Array.init FUs.Length (fun i -> Array.init (snd FUs.[i]) (fun _ -> (fst FUs.[i])))

    (**
     * buses.[i] = true, if it is free
     *           = false, if it isn't free
     *)
    let buses = Array.init countOfBuses (fun _ -> true)

    let isFreeFU fu = 
        match fu with
        | ADD(_,_,b) -> b = true
        | SUB(_,_,b) -> b = true
        | DIV(_,_,b) -> b = true
        | REGISTER(_,b) -> b = true

    let setFUAsFree fu = 
        match fu with
        | ADD(x,y,_) -> ADD(x,y,true)
        | SUB(x,y,_) -> SUB(x,y,true)
        | DIV(x,y,_) -> DIV(x,y,true)
        | REGISTER(x,_) -> REGISTER(x,true)

    let setFUAsNonFree fu = 
        match fu with
        | ADD(x,y,_) -> ADD(x,y,false)
        | SUB(x,y,_) -> SUB(x,y,false)
        | DIV(x,y,_) -> DIV(x,y,false)
        | REGISTER(x,_) -> REGISTER(x,false)

    member this.isFreeBus() = 
        Array.exists (fun x -> x = true) buses

    member this.TakeABus() =
        buses.[(Array.findIndex (fun x -> x = true) buses)] <- false

    member this.ReleaseAllBuses() = 
        for i in [0..buses.Length - 1] do
            buses.[i] <- true

    member this.SetFUAsFree (i, j) =
        board.[i].[j] <- (setFUAsFree board.[i].[j])

    member this.SetFUAsNonFree (i, j) = 
        board.[i].[j] <- (setFUAsNonFree board.[i].[j])

    (* Assume, that we have free FU of this type *)
    member this.GetFreeFU fu = 
        let resultIndex = ref (-1,-1)

        for i in [0..board.Length-1] do

            let fuArr = board.[i]

            let elem = 
                match fuArr.[0] with
                    | ADD(_,_,_) -> match fu with
                                        | ADD(_,_,_) ->
                                            Some(Array.findIndex isFreeFU fuArr)
                                        | _ -> None
                    | SUB(_,_,_) -> match fu with
                                        | SUB(_,_,_) ->
                                            Some(Array.findIndex isFreeFU fuArr)
                                        | _ -> None
                    | DIV(_,_,_) -> match fu with
                                        | DIV(_,_,_) ->
                                            Some(Array.findIndex isFreeFU fuArr)
                                        | _ -> None
                    | REGISTER(_,_) -> match fu with
                                        | REGISTER(_,_) ->
                                            Some(Array.findIndex isFreeFU fuArr)
                                        | _ -> None
            match elem with
                | Some(x) -> resultIndex := (i, x) 
                | None -> ()

        !resultIndex

    member this.IsFreeFU fu = 
        let IsHaveFreeFU = ref false

        for fuArr in board do
            let isEmpty = 
                match fuArr.[0] with
                    | ADD(_,_,_) -> match fu with
                                        | ADD(_,_,_) ->
                                            Some(Array.isEmpty (Array.filter isFreeFU fuArr))
                                        | _ -> None
                    | SUB(_,_,_) -> match fu with
                                        | SUB(_,_,_) ->
                                            Some(Array.isEmpty (Array.filter isFreeFU fuArr))
                                        | _ -> None
                    | DIV(_,_,_) -> match fu with
                                        | DIV(_,_,_) ->
                                            Some(Array.isEmpty (Array.filter isFreeFU fuArr))
                                        | _ -> None
                    | REGISTER(_,_) -> match fu with
                                        | REGISTER(_,_) ->
                                            Some(Array.isEmpty (Array.filter isFreeFU fuArr))
                                        | _ -> None

            match isEmpty with
                | Some(x) -> if x = false then IsHaveFreeFU := true
                | None -> ()

        !IsHaveFreeFU