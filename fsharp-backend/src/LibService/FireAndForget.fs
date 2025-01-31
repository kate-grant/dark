module LibService.FireAndForget

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Prelude.Tablecloth
open Tablecloth

let fireAndForgetTask
  (executionID : ExecutionID)
  (name : string)
  (f : unit -> Task<'b>)
  : unit =
  task {
    try
      let! (_ : 'b) = f ()
      return ()
    with
    | e ->
      Rollbar.sendException executionID $"fire-and-forget failed: {name}" [] e
      return ()
  }
  |> ignore<Task<unit>>
