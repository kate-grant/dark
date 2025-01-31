open Core_kernel
module Util = Libexecution.Util
open Libexecution
module RTT = Libexecution.Types.RuntimeT
open Libbackend
open Libcommon
module Db = Libbackend_basics.Db

(* Validate that data can be loaded from:
 * - stored events
 * - function arguments
 * - function results
 * - canvases (and thus toplevel_oplists) *)

let usage () : unit =
  Format.printf
    "Usage: %s\n\n  Use DARK_CONFIG_DB_DBNAME=prodclone to check prodclone.
     Supported flags:
       -h, --help:   show usage information
       --validate-stored-events=false: don't test stored events
       --validate-function-arguments=false: don't test function arguments
       --validate-function-results=false: don't test function results
       --validate-canvases=false: don't test canvases
       --validate-user-data=false: don't test user data (from user DBs)"
    Sys.argv.(0) ;
  exit 1


let validate_row (table : string) (values : string list) : unit =
  match values with
  | [canvas_name; value; trace_id] ->
      let params =
        [("table", table); ("canvas", canvas_name); ("trace_id", trace_id)]
      in
      ( try
          let (_ : RTT.dval) = Dval.of_internal_roundtrippable_v0 value in
          Log.infO "successful roundtrip" ~params
        with _ ->
          let params = ("value", value) :: params in
          Log.erroR "Failed to roundtrip stored event" ~params )
  | _ ->
      Exception.internal "Impossible: wrong # of cols"


let () =
  let validate_stored_events = ref true in
  let validate_function_arguments = ref true in
  let validate_function_results = ref true in
  let validate_canvases = ref true in
  let validate_user_data = ref true in
  Sys.argv
  |> Array.to_list
  |> List.tl
  |> Option.value ~default:[]
  |> List.iter ~f:(function
         | "-h" ->
             usage ()
         | "--help" ->
             usage ()
         | "--validate-stored-events=false" ->
             validate_stored_events := false
         | "--validate-function-arguments=false" ->
             validate_function_arguments := false
         | "--validate-function-results=false" ->
             validate_function_results := false
         | "--validate-canvases=false" ->
             validate_canvases := false
         | "--validate-user-data=false" ->
             validate_user_data := false
         | other ->
             Format.printf "Unsupported argument: %s" other ;
             exit (-1)) ;
  Format.printf "Validating stored events: %b\n" !validate_stored_events ;
  Format.printf
    "Validating function arguments: %b\n"
    !validate_function_arguments ;
  Format.printf "Validating function results: %b\n" !validate_function_results ;
  Format.printf "Validating canvases: %b\n" !validate_canvases ;
  Format.printf "Validating user data: %b\n" !validate_user_data ;
  if !validate_stored_events
  then (
    Log.infO "Next: get_stored_events" ;
    Db.iter_with_cursor
      ~name:"get stored_events"
      "SELECT canvases.name, value, trace_id
       FROM stored_events_v2
       JOIN canvases ON canvas_id = canvases.id"
      ~params:[]
      ~f:(validate_row "stored_events_v2") ) ;
  if !validate_function_arguments
  then (
    Log.infO "Next: get_function_arguments" ;
    Db.iter_with_cursor
      ~name:"get function_arguments"
      "SELECT canvases.name, arguments_json, trace_id
       FROM function_arguments
       JOIN canvases ON canvas_id = canvases.id"
      ~params:[]
      ~f:(validate_row "function_arguments") ) ;
  if !validate_function_results
  then (
    Log.infO "Next: get_function_results" ;
    Db.iter_with_cursor
      ~name:"get function_results"
      "SELECT canvases.name, value, trace_id
       FROM function_results_v3
       JOIN canvases ON canvas_id = canvases.id"
      ~params:[]
      ~f:(validate_row "function_results_v3") ) ;
  if !validate_canvases
  then (
    Log.infO "Next: get_all_canvases" ;
    Db.iter_with_cursor
      ~name:"get all canvases"
      "SELECT name from canvases"
      ~params:[]
      ~f:(function
        | [h] ->
          ( try
              ignore
                ( Canvas.load_all h []
                |> Result.map_error ~f:(String.concat ~sep:", ")
                |> Prelude.Result.ok_or_internal_exception "Canvas load error"
                ) ;
              Log.infO "successful canvas load" ~params:[("host", h)]
            with e ->
              Log.erroR
                "failed canvas load"
                ~params:
                  [ ("host", h)
                  ; ("exn", Exception.to_string e)
                  ; ( "bt"
                    , Base.Backtrace.to_string (Backtrace.Exn.most_recent ()) )
                  ] )
        | _ ->
            Exception.internal "wrong # of fields in db resultset") ) ;
  if !validate_user_data
  then (
    Log.infO "Next: get_all_user_data" ;
    Db.iter_with_cursor
      ~name:"get all canvases"
      "SELECT name FROM canvases"
      ~params:[]
      ~f:(function
        | [host] ->
            let c =
              Canvas.load_all host []
              |> Result.map_error ~f:(String.concat ~sep:", ")
              |> Prelude.Result.ok_or_internal_exception "Canvas load error"
            in
            let dbs = !c.dbs |> Toplevel.dbs in
            dbs
            |> List.iter ~f:(fun (db : Libexecution.Types.RuntimeT.DbT.db) ->
                   let dbname =
                     match db.name with
                     | Filled (id, str) | Partial (id, str) ->
                         str
                     | Blank _ ->
                         "no name"
                   in
                   let state : RTT.exec_state =
                     { tlid = db.tlid
                     ; callstack = Tc.StrSet.empty
                     ; account_id = !c.owner
                     ; canvas_id = !c.id
                     ; user_fns = []
                     ; user_tipes = []
                     ; package_fns = []
                     ; dbs
                     ; secrets = []
                     ; context = Real
                     ; trace = (fun ~on_execution_path _ _ -> ())
                     ; trace_tlid = (fun _ -> ())
                     ; on_execution_path = true
                     ; exec = (fun ~state _ _ -> DNull)
                     ; execution_id = Types.id_of_int 0
                     ; fail_fn = None
                     ; executing_fnname = ""
                     ; load_fn_result = (fun _ _ -> None)
                     ; load_fn_arguments = (fun _ -> [])
                     ; store_fn_result = (fun _ _ _ -> ())
                     ; store_fn_arguments = (fun _ _ -> ()) }
                   in
                   try
                     ignore (Libbackend_basics.User_db.get_all ~state db) ;
                     Log.infO
                       "user data"
                       ~params:[("db", dbname); ("host", host)]
                   with e ->
                     Log.erroR
                       "failed to load user_data"
                       ~params:
                         [ ("db", dbname)
                         ; ("host", host)
                         ; ("exn", Exception.to_string e) ])
        | _ ->
            Exception.internal "bad db result") ) ;
  ()
