module LibService.Rollbar

open FSharp.Control.Tasks
open System.Threading.Tasks
open FSharp.Control.Tasks

open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions

open Prelude
open Tablecloth
open Exception

let init (serviceName : string) : unit =
  print "Configuring rollbar"
  let config =
    Rollbar.RollbarConfig(Config.rollbarServerAccessToken |> debug "rbsat")
  config.Enabled <- Config.rollbarEnabled |> debug "rollbar enabled"
  config.CaptureUncaughtExceptions <- true // doesn't seem to work afaict
  config.Environment <- Config.rollbarEnvironment |> debug "rb env"
  config.LogLevel <- Rollbar.ErrorLevel.Error
  config.RethrowExceptionsAfterReporting <- false
  config.ScrubFields <-
    Array.append config.ScrubFields [| "Set-Cookie"; "Cookie"; "Authorization" |]
  // Seems we don't have the ability to set Rollbar.DTOs.Data.DefaultLanguage
  config.Transform <- fun payload -> payload.Data.Language <- "f#"

  let (state : Dictionary.T<string, obj>) = Dictionary.empty ()
  state["service"] <- serviceName
  config.Server <- Rollbar.DTOs.Server(state)
  config.Server.Host <- Config.hostName |> debug "hostdir"
  config.Server.Root <- Config.rootDir
  config.Server.CodeVersion <- Config.buildHash

  Rollbar.RollbarLocator.RollbarInstance.Configure config
  |> ignore<Rollbar.IRollbar>
  print " Configured rollbar"
  ()

// "https://ui.honeycomb.io/dark/datasets/kubernetes-bwd-ocaml?query={\"filters\":[{\"column\":\"rollbar\",\"op\":\"exists\"},{\"column\":\"execution_id\",\"op\":\"=\",\"value\":\"44602511168214071\"}],\"limit\":100,\"time_range\":604800}"
type HoneycombFilter = { column : string; op : string; value : string }

type HoneycombJson =
  { filters : List<HoneycombFilter>
    limit : int
    time_range : int }

let honeycombLinkOfExecutionID (executionID : ExecutionID) : string =
  let query =
    { filters =
        [ { column = "trace.trace_id"; op = "="; value = string executionID } ]
      limit = 100
      // 604800 is 7 days
      time_range = 604800 }

  let queryStr = Json.Vanilla.serialize query
  let dataset = Config.honeycombDataset

  let uri =
    System.Uri($"https://ui.honeycomb.io/dark/datasets/{dataset}?query={queryStr}")

  string uri

let pageableMetadata (e : exn) : List<string * obj> =
  if e :? PageableException then [ "is_pageable", true ] else []



let createState
  (executionID : ExecutionID)
  (message : string)
  (metadata : List<string * obj>)
  : Dictionary.T<string, obj> =

  let (custom : Dictionary.T<string, obj>) = Dictionary.empty ()
  custom["message"] <- message
  // CLEANUP rollbar has a built-in way to do this called "Service links"
  custom["message.honeycomb"] <- honeycombLinkOfExecutionID executionID
  custom["execution_id"] <- string executionID
  List.iter
    (fun (k, v) ->
      Dictionary.add k (v :> obj) custom |> ignore<Dictionary.T<string, obj>>)
    metadata
  custom

// Not an error, let's just be aware a thing happened
let notify
  (executionID : ExecutionID)
  (message : string)
  (metadata : List<string * obj>)
  : unit =
  print $"rollbar: {message}"
  try
    print (string metadata)
  with
  | _ -> ()
  let stacktraceMetadata = ("stacktrace", System.Environment.StackTrace :> obj)
  let metadata = stacktraceMetadata :: metadata
  Telemetry.notify message metadata
  let custom = createState executionID message metadata
  Rollbar.RollbarLocator.RollbarInstance.Info(message, custom)
  |> ignore<Rollbar.ILogger>


// An error but not an exception
let sendError
  (executionID : ExecutionID)
  (message : string)
  (metadata : List<string * obj>)
  : unit =
  print $"rollbar: {message}"
  try
    print (string metadata)
  with
  | _ -> ()
  print System.Environment.StackTrace
  Telemetry.addEvent message metadata
  let custom = createState executionID message metadata
  Rollbar.RollbarLocator.RollbarInstance.Error(message, custom)
  |> ignore<Rollbar.ILogger>

let exceptionMetadata (e : exn) =
  try
    match e with
    | :? DarkException as e -> Exception.toMetadata e
    | _ -> []
  with
  | _ -> []

let printMetadata (metadata : List<string * obj>) =
  try
    print (string metadata)
  with
  | _ -> ()



let sendException
  (executionID : ExecutionID)
  (message : string)
  (metadata : List<string * obj>)
  (e : exn)
  : unit =
  try
    print $"rollbar: {message}"
    print e.Message
    print e.StackTrace
    let metadata = metadata @ exceptionMetadata e @ pageableMetadata e
    printMetadata metadata
    Telemetry.addException message e metadata
    let custom = createState executionID message metadata
    Rollbar.RollbarLocator.RollbarInstance.Error(e, custom)
    |> ignore<Rollbar.ILogger>
  with
  | extra ->
    print "Exception when calling rollbar"
    print extra.Message
    print extra.StackTrace
    Telemetry.addException "Exception when calling rollbar" extra []


// Will block for 5 seconds to make sure this exception gets sent. Use for startup
// and other places where the process is intended to end after this call.
let lastDitchBlocking
  (executionID : ExecutionID)
  (message : string)
  (metadata : List<string * obj>)
  (e : exn)
  : unit =
  try
    print $"last ditch rollbar: {message}"
    print e.Message
    print e.StackTrace
    // FSTODO: handle SystemInitializationException with a .Inner
    let metadata = metadata @ exceptionMetadata e @ pageableMetadata e
    Telemetry.addException message e metadata
    let custom = createState executionID message metadata
    Rollbar
      .RollbarLocator
      .RollbarInstance
      .AsBlockingLogger(System.TimeSpan.FromSeconds(5))
      .Error(e, custom)
    |> ignore<Rollbar.ILogger>
    Telemetry.addException message e metadata
  with
  | extra ->
    print "Exception when calling rollbar"
    print extra.Message
    print extra.StackTrace
    if Telemetry.Span.current () = null then
      Telemetry.createRoot "LastDitch" |> ignore<Telemetry.Span.T>
    Telemetry.addException "Exception when calling rollbar" e []

module AspNet =
  open Microsoft.Extensions.DependencyInjection
  open Rollbar.NetCore.AspNet
  open Microsoft.AspNetCore.Builder


  type Person =
    { id : Option<UserID>
      email : Option<string>
      username : Option<UserName.T> }

  let emptyPerson = { id = None; email = None; username = None }


  // Rollbar's ASP.NET core middleware requires an IHttpContextAccessor, which
  // supposedly costs significant performance (couldn't see a cost in practice
  // though). AFAICT, this allows HTTP vars to be shared across the Task using an
  // AsyncContext. This would make sense for a lot of ways to use Rollbar, but we use
  // telemetry for our context and only want to use rollbar for exception tracking.
  type DarkRollbarMiddleware
    (
      nextRequestProcessor : RequestDelegate,
      ctxMetadataFn : HttpContext -> Person * List<string * obj>
    ) =
    member this._nextRequestProcessor : RequestDelegate = nextRequestProcessor
    member this.Invoke(ctx : HttpContext) : Task =
      task {
        try
          do! this._nextRequestProcessor.Invoke(ctx)
        with
        | e ->
          try
            print $"rollbar in http middleware"
            print e.Message
            print e.StackTrace
            print (ctx.Request.GetEncodedUrl())

            let executionID =
              try
                ctx.Items["executionID"] :?> ExecutionID
              with
              | _ -> ExecutionID "unavailable"

            let person, metadata =
              try
                ctxMetadataFn ctx
              with
              | _ -> emptyPerson, [ "exception calling ctxMetadataFn", true ]
            let metadata = metadata @ exceptionMetadata e @ pageableMetadata e
            let custom = createState executionID "http" metadata

            let package : Rollbar.IRollbarPackage =
              new Rollbar.ExceptionPackage(e, e.Message)
            // decorate the http info
            let package = HttpRequestPackageDecorator(package, ctx.Request, true)
            let package =
              new HttpResponsePackageDecorator(package, ctx.Response, true)
            let package =
              Rollbar.PersonPackageDecorator(
                package,
                person.id |> Option.map string |> Option.defaultValue null,
                person.username |> Option.map string |> Option.defaultValue null,
                person.email |> Option.defaultValue null
              )
            Rollbar.RollbarLocator.RollbarInstance.Error(package, custom)
            |> ignore<Rollbar.ILogger>
            print "Rollbar exception sent"
          // No telemetry call here as it should happen automatically
          with
          | re ->
            print "Exception when calling rollbar"
            print re.Message
            print re.StackTrace
            Telemetry.addException "Exception when calling rollbar" re []
          e.Reraise()
      }



  let addRollbarToServices (services : IServiceCollection) : IServiceCollection =
    // Nothing to do here, as rollbar is initialized above
    services

  let addRollbarToApp
    (
      app : IApplicationBuilder,
      ctxMetadataFn : HttpContext -> Person * List<string * obj>
    ) : IApplicationBuilder =
    app.UseMiddleware<DarkRollbarMiddleware>(ctxMetadataFn)
