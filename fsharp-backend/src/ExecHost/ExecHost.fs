module ExecHost

// A command to run common tasks in production. Note that most tasks could/should be
// run by creating new functions in LibDarkInternal instead. This should be used for
// cases where that is not appropriate.

// Based on https://andrewlock.net/deploying-asp-net-core-applications-to-kubernetes-part-10-creating-an-exec-host-deployment-for-running-one-off-commands/

// Run with `ExecHost --help` for usage

open FSharp.Control.Tasks
open System.Threading.Tasks

open Prelude
open Tablecloth

module Telemetry = LibService.Telemetry
module Rollbar = LibService.Rollbar

let runMigrations (executionID : ExecutionID) =
  print $"Running migrations"
  LibBackend.Migrations.run executionID


let emergencyLogin (username : string) : Task<unit> =
  task {
    print $"Generating a cookie for {LibBackend.Config.cookieDomain}"
    // validate the user exists
    let username = UserName.create username
    let! _user = LibBackend.Account.getUser username
    let! authData = LibBackend.Session.insert username
    print
      $"See docs/emergency-login.md for instructions. Your values are
  Name = __session
  Value = {authData.sessionKey}
  Domain = {LibBackend.Config.cookieDomain}
  (note: initial dot is _important_)"
    return ()
  }

let help () : unit =
  [ "USAGE:"
    "  ExecHost emergency-login <user>"
    "  ExecHost migrations list"
    "  ExecHost migrations run"
    "  ExecHost trigger-rollbar"
    "  ExecHost help" ]
  |> List.join "\n"
  |> print

let run (executionID : ExecutionID) (args : string []) : Task<int> =
  task {
    try
      use _ = Telemetry.createRoot "execHost run"
      Telemetry.addTags [ "args", args ]
      match args with
      | [| "emergency-login"; username |] ->
        Rollbar.notify executionID "emergencyLogin called" [ "username", username ]
        do! emergencyLogin username
        return 0
      | [| "migrations"; "list" |] ->
        print "Migrations needed:\n"
        LibBackend.Migrations.migrationsToRun ()
        |> List.iter (fun name -> print $" - {name}")
        return 0
      | [| "migrations"; "run" |] ->
        runMigrations executionID
        return 0
      | [| "trigger-rollbar" |] ->
        Rollbar.RollbarLocator.RollbarInstance.Error("test message")
        |> ignore<Rollbar.ILogger>
        return 0
      | [| "help" |] ->
        help ()
        return 0
      | _ ->
        Rollbar.notify
          executionID
          "execHost called"
          [ "args", String.concat "," args ]
        print "Invalid usage!!\n"
        help ()
        return 1
    with
    | :? System.TypeInitializationException as e ->
      print e.Message
      print e.StackTrace
      print e.InnerException.Message
      print e.InnerException.StackTrace
      return 1
    | e ->
      print e.Message
      print e.StackTrace
      return 1
  }

[<EntryPoint>]
let main (args : string []) : int =
  try
    LibService.Init.init "ExecHost"
    Telemetry.Console.loadTelemetry "ExecHost" Telemetry.TraceDBQueries
    let executionID = Telemetry.executionID ()
    (LibBackend.Init.init "ExecHost" false).Result
    (run executionID args).Result
  with
  | e ->
    Rollbar.lastDitchBlocking
      (ExecutionID "execHost")
      "Error running ExecHost"
      [ "args", args ]
      e
    -1
