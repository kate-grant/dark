module LibExecution.StdLib.LibBytes

open LibExecution.RuntimeTypes
open Prelude
open System.Text

let fn = FQFnName.stdlibName

let fns : List<BuiltInFn> =
  [ { name = fn "Bytes" "base64Encode" 0
      parameters = [ Param.make "bytes" TBytes "" ]
      returnType = TStr
      description =
        "Base64URL encodes `bytes` with `=` padding. Uses URL-safe encoding with `-` and `_` instead of `+` and `/`, as defined in RFC 4648 section 5."
      fn =
        InProcess
          (function
          | _, [ DBytes bytes ] ->
              System.Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_')
              |> DStr
              |> Value
          | args -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = Pure
      deprecated = NotDeprecated }
    { name = fn "Bytes" "hexEncode" 0

      parameters = [ Param.make "bytes" TBytes "" ]
      returnType = TStr
      description =
        "Hex (Base16) encodes `bytes` using an uppercase alphabet. Complies with RFC 4648 section 8."
      fn =
        InProcess
          (function
          | _, [ DBytes bytes ] ->
              let hexUppercaseLookup = "0123456789ABCDEF"
              let len = bytes.Length
              let buf = new StringBuilder(len * 2)

              for i = 0 to len - 1 do
                let byte = bytes.[i] |> int
                buf
                  .Append(hexUppercaseLookup.[((byte >>> 4) &&& 0xF)])
                  .Append(hexUppercaseLookup.[(byte &&& 0xF)])
                  |> ignore

              buf.ToString() |> DStr |> Value
          | args -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = Pure
      deprecated = NotDeprecated }
    { name = fn "Bytes" "length" 0
      parameters = [ Param.make "bytes" TBytes "" ]
      returnType = TInt
      description = "Length of encoded byte string"
      fn =
        InProcess
          (function
          | _, [ DBytes bytes ] -> bytes |> Array.length |> Dval.int |> Value
          | args -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = Pure
      deprecated = NotDeprecated } ]
