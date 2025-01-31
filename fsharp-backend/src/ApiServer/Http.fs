module ApiServer.Http

// Http utilities used by the API server. We used to use Giraffe, but it's a lot to
// know on top of ASP.NET, and we only use a subset. Most of this is taken from
// Giraffe.

open Microsoft.AspNetCore.Http.Extensions
open Microsoft.Extensions.Primitives
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

type ServerTimingMetric = Lib.AspNetCore.ServerTiming.Http.Headers.ServerTimingMetric
type ServerTiming = Lib.AspNetCore.ServerTiming.IServerTiming

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Tablecloth

open LibService.Telemetry

module Canvas = LibBackend.Canvas
module Config = LibBackend.Config
module Session = LibBackend.Session
module Account = LibBackend.Account
module Auth = LibBackend.Authorization

// --------------------
// Telemetry and Server timing metrics
// --------------------
type TraceTimer =
  { next : string -> unit
    stop : unit -> unit
    span : unit -> Span.T }

  interface System.IDisposable with
    member this.Dispose() = this.stop ()

// Returns a value to help tracing and setting ServerTiming headers. It immediately
// starts a Span named [initialName]. It returns a value [t], and when you call
// [t.next name], it automatically ends the current span and starts a new one with
// the new name. It also records a server-timing header for each span when it ends,
// using the duration from the Span. Make sure to use `use` instead of `let`, to
// ensure that `stop` gets called.
let startTimer (initialName : string) (ctx : HttpContext) : TraceTimer =
  let parent = Span.current () // Don't use `use`, we want this to live
  let mutable child = Span.child initialName parent []
  let st =
    ctx.RequestServices.GetService<Lib.AspNetCore.ServerTiming.IServerTiming>()
  let stop () : unit =
    child.Stop()
    let result = child.Duration.TotalMilliseconds |> decimal
    let name = $"%03d{st.Metrics.Count}-{child.DisplayName}"
    if not ctx.Response.HasStarted then
      // This is a header, and headers can't be added after the body has started. We
      // still produce the telemetry events
      st.Metrics.Add(ServerTimingMetric(name, result))
  { next =
      fun name ->
        stop ()
        child <- Span.child name parent []
    span = fun () -> child
    stop = fun () -> stop () }


// --------------------
// HttpContext extensions
// Copied from Giraffe HttpContextExtensions - https://github.com/giraffe-fsharp/Giraffe/blob/f810aa7ab67f4e782393a39b35d5fa221d80200d/src/Giraffe/HttpContextExtensions.fs
// Apache 2.0 license
// Extended with telemetry/server timing info
// --------------------

[<Extension>]
type HttpContextExtensions() =
  [<Extension>]
  static member WriteJsonAsync<'T>
    (
      ctx : HttpContext,
      value : 'T
    ) : Task<option<HttpContext>> =
    task {
      use t = startTimer "serialize-json" ctx
      ctx.Response.ContentType <- "application/json; charset=utf-8"
      let serialized = Json.OCamlCompatible.serialize value
      let bytes = System.ReadOnlyMemory(UTF8.toBytes serialized)
      ctx.Response.ContentLength <- int64 bytes.Length
      t.next "write-json-async"
      let! (_ : System.IO.Pipelines.FlushResult) =
        ctx.Response.BodyWriter.WriteAsync(bytes)
      return Some ctx
    }

  [<Extension>]
  static member WriteTextAsync
    (
      ctx : HttpContext,
      value : string
    ) : Task<option<HttpContext>> =
    task {
      use t = startTimer "text-to-bytes" ctx
      ctx.Response.ContentType <- "text/plain; charset=utf-8"
      let bytes = System.ReadOnlyMemory(UTF8.toBytes value)
      ctx.Response.ContentLength <- int64 bytes.Length
      t.next "write-text-async"
      let! (_ : System.IO.Pipelines.FlushResult) =
        ctx.Response.BodyWriter.WriteAsync(bytes)
      return Some ctx
    }

  [<Extension>]
  static member WriteHtmlAsync
    (
      ctx : HttpContext,
      value : string
    ) : Task<option<HttpContext>> =
    task {
      use t = startTimer "html-to-bytes" ctx
      ctx.Response.ContentType <- "text/html; charset=utf-8"
      let bytes = System.ReadOnlyMemory(UTF8.toBytes value)
      ctx.Response.ContentLength <- int64 bytes.Length
      t.next "write-bytes-async"
      let! (_ : System.IO.Pipelines.FlushResult) =
        ctx.Response.BodyWriter.WriteAsync(bytes)
      return Some ctx
    }

  [<Extension>]
  static member ReadJsonAsync<'T>(ctx : HttpContext) : Task<'T> =
    task {
      use t = startTimer "read-json-async" ctx
      use ms = new System.IO.MemoryStream()
      do! ctx.Request.Body.CopyToAsync(ms)
      let body = ms.ToArray() |> UTF8.ofBytesUnsafe
      t.next "deserialize-json"
      let response = Json.OCamlCompatible.deserialize<'T> body
      return response
    }

  [<Extension>]
  static member SetHeader<'T>(ctx : HttpContext, name : string, value : string) =
    ctx.Response.Headers[ name ] <- StringValues([| value |])

  [<Extension>]
  static member GetHeader<'T>(ctx : HttpContext, key : string) : Option<string> =
    match ctx.Request.Headers.TryGetValue key with
    | true, value -> Some(value.ToString())
    | _ -> None

  [<Extension>]
  static member GetQueryStringValue<'T>
    (
      ctx : HttpContext,
      key : string
    ) : Option<string> =
    match ctx.Request.Query.TryGetValue key with
    | true, value -> Some(value.ToString())
    | _ -> None

  [<Extension>]
  static member GetFormValue(ctx : HttpContext, key : string) : Option<string> =
    match ctx.Request.HasFormContentType with
    | false -> None
    | true ->
      match ctx.Request.Form.TryGetValue key with
      | true, value -> Some(value.ToString())
      | false, _ -> None


let queryString (queries : List<string * string>) : string =
  queries
  |> List.map (fun (k, v) ->
    let k = System.Web.HttpUtility.UrlEncode k
    let v = System.Web.HttpUtility.UrlEncode v
    $"{k}={v}")
  |> String.concat "&"


// ------------------
// Http types
// ------------------
type HttpHandler = HttpContext -> Task

// ------------------
// Handlers
// ------------------

let unauthorized (ctx : HttpContext) : Task =
  task {
    ctx.Response.StatusCode <- 401
    return! ctx.WriteTextAsync "Not Authorized"
  }

let notFound (ctx : HttpContext) : Task =
  task {
    ctx.Response.StatusCode <- 404
    return! ctx.WriteTextAsync "Not Found"
  }

let htmlHandler (f : HttpContext -> Task<string>) : HttpHandler =
  (fun ctx ->
    task {
      let! result = f ctx
      return! ctx.WriteHtmlAsync result
    })

let jsonHandler (f : HttpContext -> Task<'a>) : HttpHandler =
  (fun ctx ->
    task {
      let! result = f ctx
      return! ctx.WriteJsonAsync result
    })

let jsonOptionHandler (f : HttpContext -> Task<Option<'a>>) : HttpHandler =
  (fun ctx ->
    task {
      match! f ctx with
      | Some result -> return! ctx.WriteJsonAsync result
      | None ->
        ctx.Response.StatusCode <- 404
        return! ctx.WriteTextAsync "Not found"
    })

// --------------------
// Accessing data from a HttpContext
// --------------------

// Don't use strings for this interface
type dataID =
  | UserInfo
  | SessionData
  | CanvasInfo
  | Permission
  | ExecutionID

  override this.ToString() : string =
    match this with
    | UserInfo -> "user"
    | SessionData -> "sessionData"
    | CanvasInfo -> "canvasName"
    | Permission -> "permission"
    | ExecutionID -> "executionID"

let save' (id : dataID) (value : 'a) (ctx : HttpContext) : unit =
  ctx.Items[ string id ] <- value

let load'<'a> (id : dataID) (ctx : HttpContext) : 'a = ctx.Items[string id] :?> 'a

let loadSessionData (ctx : HttpContext) : Session.T =
  load'<Session.T> SessionData ctx

let loadUserInfo (ctx : HttpContext) : Account.UserInfo =
  load'<Account.UserInfo> UserInfo ctx

let loadCanvasInfo (ctx : HttpContext) : Canvas.Meta =
  load'<Canvas.Meta> CanvasInfo ctx

let loadExecutionID (ctx : HttpContext) : ExecutionID =
  load'<ExecutionID> ExecutionID ctx

let loadPermission (ctx : HttpContext) : Option<Auth.Permission> =
  load'<Option<Auth.Permission>> Permission ctx

let saveSessionData (s : Session.T) (ctx : HttpContext) = save' SessionData s ctx
let saveUserInfo (u : Account.UserInfo) (ctx : HttpContext) = save' UserInfo u ctx
let saveCanvasInfo (c : Canvas.Meta) (ctx : HttpContext) = save' CanvasInfo c ctx
let saveExecutionID (id : ExecutionID) (ctx : HttpContext) = save' ExecutionID id ctx

let savePermission (p : Option<Auth.Permission>) (ctx : HttpContext) =
  save' Permission p ctx
