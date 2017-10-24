port module Main exposing (..)


-- builtins
import Maybe
import Dict

-- lib
import Json.Decode as JSD
import Http
import Keyboard.Event
import Keyboard.Key as Key
import Navigation

-- dark
import RPC exposing (rpc, phantomRpc, saveTest)
import Types exposing (..)
import View
import Defaults
import Graph as G
import Runtime as RT
import Entry
import RandomGraph
import Autocomplete
import Selection
import Viewport
import Window.Events exposing (onWindow)


-----------------------
-- TOP-LEVEL
-----------------------
main : Program Flags Model Msg
main = Navigation.programWithFlags
       LocationChange
       { init = init
       , view = View.view
       , update = update
       , subscriptions = subscriptions}


-----------------------
-- MODEL
-----------------------
flag2function : FlagFunction -> Function
flag2function fn =
  { name = fn.name
  , description = fn.description
  , returnTipe = RT.str2tipe fn.return_type
  , parameters = List.map (\p -> { name = p.name
                                 , tipe = RT.str2tipe p.tipe
                                 , block_args = p.block_args
                                 , optional = p.optional
                                 , description = p.description}) fn.parameters
  }

init : Flags -> Navigation.Location -> ( Model, Cmd Msg )
init {state, complete} location =
  let editor = case state of
            Just e -> e
            Nothing -> Defaults.defaultEditor
      m = Defaults.defaultModel editor
      m2 = { m | complete = Autocomplete.init (List.map flag2function complete)}
  in
    (m2, rpc m FocusNothing [])


-----------------------
-- ports, save Editor state in LocalStorage
-----------------------
port setStorage : Editor -> Cmd a

-----------------------
-- updates
-----------------------

update : Msg -> Model -> (Model, Cmd Msg)
update msg m =
  let mods = update_ msg m
      (newm, newc) = updateMod mods (m, Cmd.none)
  in
    ({ newm | lastMsg = msg
            , lastMod = mods}
     , Cmd.batch [newc, m |> Defaults.model2editor |> setStorage])

updateMod : Modification -> (Model, Cmd Msg) -> (Model, Cmd Msg)
updateMod mod (m, cmd) =
  let (newm, newcmd) =
    case mod of
      Error e -> { m | error = Just e} ! []
      ClearError -> { m | error = Nothing} ! []
      RPC (calls, id) -> m ! [rpc m id calls]
      Phantom ->
        case m.state of
          Entering re cursor ->
            case Entry.submit m re cursor m.complete.value of
              RPC (rpcs, _) -> m ! [phantomRpc m cursor rpcs]
              _ -> m ! []
          _ -> m ! []
      NoChange -> m ! []
      MakeCmd cmd -> m ! [cmd]
      Select id -> { m | state = Selecting id
                       , center = G.getNodeExn m id |> G.pos m} ! []
      Enter re entry -> { m | state = Entering re entry
                            , center = case entry of
                                         Filling n _ -> G.pos m n
                                         Creating p -> m.center -- dont move
                        }
                        ! [Entry.focusEntry]
      ModelMod mm -> mm m ! []
      Deselect -> { m | state = Deselected } ! []
      AutocompleteMod mod ->
        let complete = Autocomplete.update mod m.complete
        in
          ({ m | complete = Autocomplete.update mod m.complete
           }, Autocomplete.focusItem complete.index)
      -- applied from left to right
      ChangeCursor step -> case m.state of
        Selecting id -> let calls = Entry.updatePreviewCursor m id step
                        in m ! [rpc m FocusSame calls]
        Deselected -> m ! []
        Entering _ _ -> m ! []
      Many mods -> List.foldl updateMod (m, Cmd.none) mods
  in
    (newm, Cmd.batch [cmd, newcmd])


update_ : Msg -> Model -> Modification
update_ msg m =
  case (msg, m.state) of

    (NodeClick node, _) ->
      Select node.id

    (RecordClick event, _) ->
      if event.button == Defaults.leftButton
      then Many [ AutocompleteMod ACReset
                , Enter False <| Creating (Viewport.toAbsolute m event.pos)]
      else NoChange

    ------------------------
    -- entry node
    ------------------------
    (EntrySubmitMsg, _) ->
      NoChange -- just keep this here to prevent the page from loading

    (GlobalKeyPress event, state) ->
      if event.ctrlKey && (event.keyCode == Key.Z || event.keyCode == Key.Y)
      then
        case event.keyCode of
          Key.Z -> RPC ([Undo], FocusNothing)
          Key.Y -> RPC ([Redo], FocusNothing)
          _ -> NoChange
      else
        case state of
          Selecting id_ ->
            -- quick error checking, in case the focus has gone bad
            if not <| G.hasNode m id_ then Entry.createFindSpace m else let id = id_ in
            case event.keyCode of
              Key.Backspace ->
                let prev = G.incomingNodes m (G.getNodeExn m id)
                    next = G.outgoingNodes m (G.getNodeExn m id) in
                -- FocusSame gets called later, after the RPC, so just doesn't change anything
                Many [ RPC (Entry.withNodePositioning m [DeleteNode id], FocusSame)
                    , case List.head (List.append prev next) of
                        Just n -> Select n.id
                        Nothing -> Deselect
                    ]
              Key.Up -> Selection.selectNextNode m id (\n o -> G.posy m n > G.posy m o)
              Key.Down -> Selection.selectNextNode m id (\n o -> G.posy m n < G.posy m o)
              Key.Left -> if event.altKey
                then
                  ChangeCursor -1
                else
                  Selection.selectNextNode m id (\n o -> G.posx m n > G.posx m o)
              Key.Right -> if event.altKey
                then
                  ChangeCursor 1
                else
                  Selection.selectNextNode m id (\n o -> G.posx m n < G.posx m o)
              Key.Enter -> Entry.enterExact m (G.getNodeExn m id)
              Key.One -> Entry.reenter m id 0
              Key.Two -> Entry.reenter m id 1
              Key.Three -> Entry.reenter m id 2
              Key.Four -> Entry.reenter m id 3
              Key.Five -> Entry.reenter m id 4
              Key.Six -> Entry.reenter m id 5
              Key.Seven -> Entry.reenter m id 6
              Key.Eight -> Entry.reenter m id 7
              Key.Nine -> Entry.reenter m id 8
              Key.Zero -> Entry.reenter m id 9
              Key.Escape -> Deselect
              code -> Selection.selectByLetter m code

          Entering re cursor ->
            if event.ctrlKey then
              case event.keyCode of
                Key.P -> AutocompleteMod ACSelectUp
                Key.N -> AutocompleteMod ACSelectDown
                _ -> NoChange
            else
              case event.keyCode of
                Key.Up -> AutocompleteMod ACSelectUp
                Key.Down -> Many [ AutocompleteMod (ACOpen True)
                                , AutocompleteMod ACSelectDown]
                Key.Right ->
                  let sp = Autocomplete.sharedPrefix m.complete in
                  if sp == "" then NoChange
                  else
                    AutocompleteMod <| ACQuery sp
                Key.Enter ->
                  let name = case Autocomplete.highlighted m.complete of
                              Just item -> Autocomplete.asName item
                              Nothing -> m.complete.value
                  in
                    Entry.submit m re cursor name

                Key.Escape ->
                  case cursor of
                    Creating _ -> Many [Deselect, AutocompleteMod ACReset]
                    Filling node _ -> Many [ Select node.id
                                          , AutocompleteMod ACReset]
                key ->
                  AutocompleteMod <| ACQuery m.complete.value

          Deselected ->
            case event.keyCode of
              Key.Enter -> Entry.createFindSpace m
              Key.Up -> ModelMod Viewport.moveUp
              Key.Down -> ModelMod Viewport.moveDown
              Key.Left -> ModelMod Viewport.moveLeft
              Key.Right -> ModelMod Viewport.moveRight
              _ -> Selection.selectByLetter m event.keyCode

    (EntryInputMsg target, _) ->
      Entry.updateValue target

    (ClearGraph, _) ->
      Many [ RPC ([DeleteAll], FocusNothing), Deselect]

    (SaveTestButton, _) ->
      MakeCmd saveTest

    (AddRandom, _) ->
      Many [ RandomGraph.makeRandomChange m, Deselect]

    (RPCCallBack focus calls (Ok (nodes)), _) ->
      let m2 = { m | nodes = nodes }
          m3 = G.reposition m2
      in Many [ ModelMod (\_ -> m3)
              , AutocompleteMod ACReset
              , ClearError
              , case focus of
                  FocusNext id -> Entry.enterNext m3 (G.getNodeExn m3 id)
                  FocusExact id -> Entry.enterExact m3 (G.getNodeExn m3 id)
                  FocusSame -> NoChange
                  FocusNothing -> Deselect
              ]

    (PhantomCallBack _ _ (Ok (nodes)), _) ->
      ModelMod (\_ -> { m | phantoms = nodes } )

    (SaveTestCallBack (Ok msg), _) ->
      Error <| "Success! " ++ msg



    ------------------------
    -- plumbing
    ------------------------
    (RPCCallBack _ _ (Err (Http.BadStatus error)), _) ->
      Error <| "Error: " ++ error.body

    (RPCCallBack _ _ (Err (Http.NetworkError)), _) ->
      Error <| "Network error: is the server running?"

    (PhantomCallBack _ _ (Err (Http.BadStatus error)), _) ->
      ModelMod (\_ -> { m | phantoms = Dict.empty } )

    (PhantomCallBack _ _ (Err (Http.NetworkError)), _) ->
      Error <| "Network error: is the server running?"

    (SaveTestCallBack (Err err), _) ->
      Error <| "Error: " ++ (toString err)


    (FocusEntry _, _) ->
      NoChange

    (FocusAutocompleteItem _, _) ->
      NoChange

    t -> Error <| "Dark Client Error: nothing for " ++ (toString t)

    ------------------------
    -- datastores
    -------------------------
    -- (ADD_DS, SubmitMsg, _) ->
    --   ({ m | state = ADD_DS_FIELD_NAME
    --    }, rpc m <| AddDatastore m.inputValue m.clickPos)

    -- (ADD_DS_FIELD_NAME, SubmitMsg, _) ->
    --     ({ m | state = ADD_DS_FIELD_TYPE
    --          , tempFieldName = m.inputValue
    --      }, Cmd.none)

    -- (ADD_DS_FIELD_TYPE, SubmitMsg, Just id) ->
    --   ({ m | state = ADD_DS_FIELD_NAME
    --    }, rpc m <| AddDatastoreField id m.tempFieldName m.inputValue)



-----------------------
-- SUBSCRIPTIONS
-----------------------
subscriptions : Model -> Sub Msg
subscriptions m =
  let keySubs =
        [onWindow "keydown"
           (JSD.map GlobalKeyPress Keyboard.Event.decodeKeyboardEvent)]
  in Sub.batch
    (List.concat [keySubs])
