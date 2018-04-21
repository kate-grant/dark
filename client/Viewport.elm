module Viewport exposing (..)

import Navigation

-- dark
import Defaults
import Types exposing (..)

toViewport : Model -> Pos -> VPos
toViewport m pos =
  let d = Defaults.defaultModel |> .center in
  { vx = d.x + pos.x - m.center.x, vy = d.y + pos.y - m.center.y}

toAbsolute : Model -> VPos -> Pos
toAbsolute m pos =
  let d = Defaults.defaultModel |> .center in
  { x = pos.vx + m.center.x - d.x, y = pos.vy + m.center.y - d.y}

pageUp : Pos -> Modification
pageUp c =
  {x=c.x, y=c.y - Defaults.pageHeight } |> moveTo

pageDown : Pos -> Modification
pageDown c =
  {x=c.x, y=c.y + Defaults.pageHeight } |> moveTo

pageLeft : Pos -> Modification
pageLeft c =
  {x=c.x - Defaults.pageWidth, y=c.y } |> moveTo

pageRight : Pos -> Modification
pageRight c =
  {x=c.x + Defaults.pageWidth, y=c.y } |> moveTo





moveUp : Pos -> Modification
moveUp c =
  {x=c.x, y=c.y - Defaults.moveSize } |> moveTo

moveDown : Pos -> Modification
moveDown c =
  {x=c.x, y=c.y + Defaults.moveSize } |> moveTo

moveLeft : Pos -> Modification
moveLeft c =
  {x=c.x - Defaults.moveSize, y=c.y } |> moveTo

moveRight : Pos -> Modification
moveRight c =
  {x=c.x + Defaults.moveSize, y=c.y } |> moveTo

moveTo : Pos -> Modification
moveTo pos =
  SetCenter pos


maybeUpdateUrl : Model -> Modification
maybeUpdateUrl m =
  if m.urlState.lastPos /= m.center
  then
    Many [ TweakModel (\m -> { m | urlState = {lastPos = m.center}})
         , MakeCmd (Navigation.modifyUrl (urlForPos m.center))
         ]
  else
    NoChange

urlForPos : Pos -> String
urlForPos pos =
  "/admin/ui#" ++ hashForPos pos

hashForPos : Pos -> String
hashForPos pos =
  "x=" ++ toString pos.x ++ "y=" ++ toString pos.y
