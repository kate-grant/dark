module BackendOnlyStdLib.LibCrypto

open System
open System.Threading.Tasks
open System.Numerics
open System.Security.Cryptography

open LibExecution.RuntimeTypes
open Prelude

module Errors = LibExecution.Errors

let fn = FQFnName.stdlibFnName

let incorrectArgs = Errors.incorrectArgs


let fns : List<BuiltInFn> =
  [ { name = fn "Crypto" "sha256" 0
      parameters = [ Param.make "data" TBytes "" ]
      returnType = TBytes
      description = "Computes the SHA-256 digest of the given `data`."
      fn =
        (function
        | _, [ DBytes data ] -> SHA256.HashData(ReadOnlySpan data) |> DBytes |> Ply
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = ImpurePreviewable
      deprecated = NotDeprecated }


    { name = fn "Crypto" "sha384" 0
      parameters = [ Param.make "data" TBytes "" ]
      returnType = TBytes
      description = "Computes the SHA-384 digest of the given `data`."
      fn =
        (function
        | _, [ DBytes data ] -> SHA384.HashData(ReadOnlySpan data) |> DBytes |> Ply
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = ImpurePreviewable
      deprecated = NotDeprecated }


    { name = fn "Crypto" "md5" 0
      parameters = [ Param.make "data" TBytes "" ]
      returnType = TBytes
      description =
        "Computes the md5 digest of the given `data`. NOTE: There are multiple security problems with md5, see https://en.wikipedia.org/wiki/MD5#Security"
      fn =
        (function
        | _, [ DBytes data ] -> MD5.HashData(ReadOnlySpan data) |> DBytes |> Ply
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = ImpurePreviewable
      deprecated = NotDeprecated }


    { name = fn "Crypto" "sha256hmac" 0
      parameters = [ Param.make "key" TBytes ""; Param.make "data" TBytes "" ]
      returnType = TBytes
      description =
        "Computes the SHA-256 HMAC (hash-based message authentication code) digest of the given `key` and `data`."
      fn =
        (function
        | _, [ DBytes key; DBytes data ] ->
          let hmac = new HMACSHA256(key)
          data |> hmac.ComputeHash |> DBytes |> Ply
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = ImpurePreviewable
      deprecated = NotDeprecated }


    { name = fn "Crypto" "sha1hmac" 0
      parameters = [ Param.make "key" TBytes ""; Param.make "data" TBytes "" ]
      returnType = TBytes
      description =
        "Computes the SHA1-HMAC (hash-based message authentication code) digest of the given `key` and `data`."
      fn =
        (function
        | _, [ DBytes key; DBytes data ] ->
          let hmac = new HMACSHA1(key)
          data |> hmac.ComputeHash |> DBytes |> Ply
        | _ -> incorrectArgs ())
      sqlSpec = NotYetImplementedTODO
      previewable = ImpurePreviewable
      deprecated = NotDeprecated } ]
