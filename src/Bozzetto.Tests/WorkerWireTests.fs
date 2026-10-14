module Bozzetto.Tests.WorkerWireTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Bozzetto
open Bozzetto.WorkerProtocol

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private genText =
  Gen.oneof [
    ArbMap.defaults |> ArbMap.generate<NonNull<string>> |> Gen.map _.Get
    Gen.elements [ ""; "let x = 42;;"; "λ → 项目"; "a\000b"; String('x', 70000) ] ]

let private genTexts = Gen.listOf genText

let private genSeverity =
  Gen.elements [
    Features.Diagnostics.DiagnosticSeverity.Blocking
    Features.Diagnostics.DiagnosticSeverity.Hidden
    Features.Diagnostics.DiagnosticSeverity.Info
    Features.Diagnostics.DiagnosticSeverity.Warning ]

let private genInt = ArbMap.defaults |> ArbMap.generate<int>

let private genDiagnostic =
  gen {
    let! severity = genSeverity
    let! message = genText
    let! a = genInt
    let! b = genInt
    let! c = genInt
    let! d = genInt
    let! n = genInt
    return { Severity = severity; Message = message; StartLine = a; StartColumn = b; EndLine = c; EndColumn = d; ErrorNumber = n }
  }

let private genBuildDiagnostic =
  gen {
    let! file = Gen.optionOf genText
    let! line = Gen.optionOf genInt
    let! column = Gen.optionOf genInt
    let! severity = Gen.elements [ BuildDiagnosticSeverity.Blocking; BuildDiagnosticSeverity.Warning ]
    let! code = Gen.optionOf genText
    let! message = genText
    return { File = file; Line = line; Column = column; Severity = severity; Code = code; Message = message }
  }

/// Every error case the wire keeps by name.
let private genCarriedError =
  Gen.oneof [
    genText |> Gen.map BozzettoError.EvalFailed
    genText |> Gen.map BozzettoError.ResetFailed
    genText |> Gen.map BozzettoError.HardResetFailed
    Gen.map2 (fun code diagnostics -> BozzettoError.BuildFailed (code, diagnostics)) genInt (Gen.listOf genBuildDiagnostic)
    genText |> Gen.map BozzettoError.ScriptLoadFailed
    genText |> Gen.map BozzettoError.CheckFailed
    Gen.map2 (fun a b -> BozzettoError.CompletionFailed (a, b)) genText genText
    genText |> Gen.map BozzettoError.CancelFailed
    Gen.constant BozzettoError.EvalSupersededByReset
    Gen.map2 (fun a b -> BozzettoError.WarmupOpenFailed (a, b)) genText genText
    Gen.map2 (fun a b -> BozzettoError.WarmupContextFailed (a, b)) genText genText
    genText |> Gen.map BozzettoError.SessionCreationFailed
    genTexts |> Gen.map BozzettoError.NeedsRebuild
    genText |> Gen.map BozzettoError.WorkerSpawnFailed ]

let private genResult (genOk: Gen<'T>) =
  Gen.oneof [ genOk |> Gen.map Ok; genCarriedError |> Gen.map Error ]

let private genStatus =
  Gen.oneof [
    Gen.elements [ SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating
                   SessionStatus.Faulted; SessionStatus.Restarting; SessionStatus.Stopped ]
    genText |> Gen.map SessionStatus.Building ]

let private genProject =
  gen {
    let! path = genText
    let! role = Gen.elements [ ProjectLoading.ProjectRole.Executable; ProjectLoading.ProjectRole.Library; ProjectLoading.ProjectRole.Test ]
    let! refs = genTexts
    return ({ Path = path; Role = role; PackageRefs = refs } : ProjectLoading.ClassifiedProject)
  }

let private genSnapshot =
  gen {
    let! status = genStatus
    let! message = Gen.optionOf genText
    let! count = genInt
    let! avg = ArbMap.defaults |> ArbMap.generate<int64>
    let! min = ArbMap.defaults |> ArbMap.generate<int64>
    let! max = ArbMap.defaults |> ArbMap.generate<int64>
    let! projects = Gen.listOf genProject
    let! core = genText
    return { Status = status; StatusMessage = message; EvalCount = count; AvgDurationMs = avg
             MinDurationMs = min; MaxDurationMs = max; Projects = projects; CoreVersion = core }
  }

let private genSymbol =
  gen {
    let! name = genText
    let! definition = ArbMap.defaults |> ArbMap.generate<bool>
    let! path = genText
    let! line = genInt
    return { SymbolFullName = name; IsFromDefinition = definition; FilePath = path; Line = line }
  }

let private genMessage =
  Gen.oneof [
    Gen.map2 (fun a b -> WorkerMessage.EvalCode (a, b)) genText genText
    Gen.map2 (fun a b -> WorkerMessage.CheckCode (a, b)) genText genText
    Gen.map3 (fun a b c -> WorkerMessage.TypeCheckWithSymbols (a, b, c)) genText genText genText
    Gen.map3 (fun a b c -> WorkerMessage.GetCompletions (a, b, c)) genText genInt genText
    Gen.constant WorkerMessage.CancelEval
    Gen.map2 (fun a b -> WorkerMessage.LoadScript (a, b)) genText genText
    genText |> Gen.map WorkerMessage.ResetSession
    Gen.map2 (fun a b -> WorkerMessage.HardResetSession (a, b)) (ArbMap.defaults |> ArbMap.generate<bool>) genText
    genText |> Gen.map WorkerMessage.GetStatus
    genText |> Gen.map WorkerMessage.GetLiveValues
    Gen.constant WorkerMessage.Shutdown ]

let private genResponse =
  Gen.oneof [
    gen {
      let! replyId = genText
      let! result = genResult genText
      let! diagnostics = Gen.listOf genDiagnostic
      let! metadata = Gen.listOf (Gen.zip genText genText) |> Gen.map Map.ofList
      return WorkerResponse.EvalResult (replyId, result, diagnostics, metadata)
    }
    Gen.map2 (fun a b -> WorkerResponse.CheckResult (a, b)) genText (Gen.listOf genDiagnostic)
    Gen.map3 (fun a b c -> WorkerResponse.TypeCheckWithSymbolsResult (a, b, c)) genText (Gen.listOf genDiagnostic) (Gen.listOf genSymbol)
    Gen.map2 (fun a b -> WorkerResponse.CompletionResult (a, b)) genText genTexts
    Gen.map2 (fun a b -> WorkerResponse.StatusResult (a, b)) genText genSnapshot
    Gen.map2 (fun a b -> WorkerResponse.LiveValuesResult (a, b)) genText genText
    ArbMap.defaults |> ArbMap.generate<bool> |> Gen.map WorkerResponse.EvalCancelled
    Gen.map2 (fun a b -> WorkerResponse.ResetResult (a, b)) genText (genResult (Gen.constant ()))
    Gen.map2 (fun a b -> WorkerResponse.HardResetResult (a, b)) genText (genResult genText)
    Gen.map2 (fun a b -> WorkerResponse.ScriptLoaded (a, b)) genText (genResult genText)
    Gen.constant WorkerResponse.WorkerReady
    Gen.constant WorkerResponse.WorkerShuttingDown
    genCarriedError |> Gen.map WorkerResponse.WorkerError ]

let private correlation = ArbMap.defaults |> ArbMap.generate<int64>

let private roundTrip envelope = envelope |> WorkerWire.encode |> WorkerWire.decode

let private refusal envelopeBytes =
  match WorkerWire.decode envelopeBytes with
  | Ok envelope -> failtestf "expected a refusal, decoded %A" envelope
  | Error refusal -> WorkerWire.WireRefusal.describe refusal

let private sample = WorkerWire.encode (WorkerWire.Request (7L, WorkerMessage.EvalCode ("1 + 1;;", "r1")))

[<Tests>]
let tests =
  testList "WorkerWire" [
    testPropertyWithConfig config "every carried request round-trips exactly" <|
      Prop.forAll (Arb.fromGen (Gen.zip correlation genMessage)) (fun (id, message) ->
        roundTrip (WorkerWire.Request (id, message)) = Ok (WorkerWire.Request (id, message)))

    testPropertyWithConfig config "every carried reply round-trips exactly" <|
      Prop.forAll (Arb.fromGen (Gen.zip correlation genResponse)) (fun (id, response) ->
        roundTrip (WorkerWire.Reply (id, response)) = Ok (WorkerWire.Reply (id, response)))

    testCase "hello round-trips" <| fun () ->
      roundTrip (WorkerWire.Hello (WorkerWire.ProtocolVersion, "0123abcd", "0.1.0"))
      |> Expect.equal "hello" (Ok (WorkerWire.Hello (WorkerWire.ProtocolVersion, "0123abcd", "0.1.0")))

    testCase "a frame from another protocol is refused before its envelope is read" <| fun () ->
      let foreign = Array.copy sample
      foreign[0] <- byte 'X'
      refusal foreign |> Expect.equal "magic" "not a Bozzetto F# worker frame"

    testCase "another protocol version is refused by number" <| fun () ->
      let other = Array.copy sample
      other[4] <- 2uy
      refusal other |> Expect.equal "version" "protocol version 2, expected 1"

    testCase "trailing bytes are a fault" <| fun () ->
      refusal (Array.append sample [| 0uy |]) |> Expect.equal "trailing" "1 trailing bytes"

    testCase "a truncated frame is a fault" <| fun () ->
      refusal sample[.. sample.Length - 2] |> Expect.equal "truncated" "truncated frame"

    testCase "an unknown message tag is refused by number" <| fun () ->
      // magic(4) version(2) envelope tag(1) correlation(8) then the message tag.
      let unknown = Array.copy sample
      unknown[15] <- 99uy
      refusal unknown |> Expect.equal "tag" "worker message tag 99"

    testCase "a count larger than the remaining payload is refused before allocation" <| fun () ->
      let lying =
        Array.concat [
          sample[.. 14]
          [| 4uy |]                           // GetCompletions
          [| 0uy |]                           // empty code
          [| 1uy; 0uy; 0uy; 0uy |]            // cursor
          [| 0xFFuy; 0xFFuy; 0xFFuy; 0x07uy |] ] // replyId length ~ 16M with nothing behind it
      refusal lying |> Expect.equal "truncated string" "truncated frame"
      let lyingList =
        Array.concat [
          (WorkerWire.encode (WorkerWire.Reply (1L, WorkerResponse.CompletionResult ("r", [])))).[.. 17]
          [| 0xFFuy; 0xFFuy; 0x03uy |] ]
      refusal lyingList |> Expect.stringStarts "count" "count 65535 exceeds"

    testCase "an error the wire does not carry by name arrives as Unexpected and says what it was" <| fun () ->
      let reply = WorkerWire.Reply (3L, WorkerResponse.WorkerError (BozzettoError.PortInUse 47749))
      match roundTrip reply with
      | Ok (WorkerWire.Reply (3L, WorkerResponse.WorkerError (BozzettoError.Unexpected error))) ->
        error.Message |> Expect.stringContains "case name" "PortInUse"
        error.Message |> Expect.stringContains "description" (BozzettoError.describe (BozzettoError.PortInUse 47749))
      | other -> failtestf "expected Unexpected, got %A" other

    testCase "an uncarried response crosses as a named refusal" <| fun () ->
      match roundTrip (WorkerWire.Reply (4L, WorkerResponse.InstrumentationMapsResult ("r", [||]))) with
      | Ok (WorkerWire.Reply (4L, WorkerResponse.WorkerError (BozzettoError.EvalFailed reason))) ->
        reason |> Expect.stringContains "names the case" "InstrumentationMapsResult"
      | other -> failtestf "expected a refusal, got %A" other

    testCase "the wire refuses to encode a request it does not carry" <| fun () ->
      let message = WorkerMessage.RunApp ("app.fsproj", AppRun.PreviousAddress.NoPreviousAddress, "r")
      WorkerWire.carries message |> Expect.isFalse "RunApp is not carried"
      Expect.throwsT<ArgumentException> "encode refuses" (fun () -> WorkerWire.encode (WorkerWire.Request (1L, message)) |> ignore)

    testAsync "uncarried requests are answered locally without touching the socket" {
      let proxy = WorkerSocketClient.proxy "0123abcd" (Path.Combine(Path.GetTempPath(), "no-such-worker", Guid.NewGuid().ToString("N") + ".sock"))
      match! proxy (WorkerMessage.RunApp ("app.fsproj", AppRun.PreviousAddress.NoPreviousAddress, "r")) with
      | WorkerResponse.AppRunResult ("r", Error (BozzettoError.AppRunFailed (_, reason))) ->
        reason |> Expect.stringContains "names the message" "RunApp"
      | other -> failtestf "expected a local refusal, got %A" other
      match! proxy (WorkerMessage.GetInstrumentationMaps "m") with
      | WorkerResponse.InstrumentationMapsResult ("m", maps) -> maps |> Expect.isEmpty "no maps"
      | other -> failtestf "expected empty maps, got %A" other
    }

    testAsync "a carried request to a missing worker raises instead of answering" {
      let proxy = WorkerSocketClient.proxy "0123abcd" (Path.Combine(Path.GetTempPath(), "no-such-worker", Guid.NewGuid().ToString("N") + ".sock"))
      let! raised =
        async {
          try
            let! _ = proxy (WorkerMessage.GetStatus "s")
            return false
          with _ -> return true
        }
      raised |> Expect.isTrue "transport failure raises"
    }

    testList "framing" [
      testAsync "a frame survives the stream" {
        use stream = new MemoryStream()
        do! WorkerWire.writeFrame stream sample
        stream.Position <- 0L
        match! WorkerWire.readFrame stream with
        | WorkerWire.FrameRead.Frame payload -> payload |> Expect.sequenceEqual "payload" sample
        | other -> failtestf "expected a frame, got %A" other
        let! rest = WorkerWire.readFrame stream
        rest |> Expect.equal "clean end" WorkerWire.FrameRead.Closed
      }

      testAsync "an end inside a frame is broken, not closed" {
        use stream = new MemoryStream()
        do! WorkerWire.writeFrame stream sample
        use cut = new MemoryStream(stream.ToArray()[.. int stream.Length - 2])
        match! WorkerWire.readFrame cut with
        | WorkerWire.FrameRead.Broken reason -> reason |> Expect.stringContains "says where" "bytes into a"
        | other -> failtestf "expected Broken, got %A" other
      }

      testAsync "an impossible announced length is refused before allocation" {
        use stream = new MemoryStream(BitConverter.GetBytes(WorkerWire.MaxFrameBytes + 1))
        match! WorkerWire.readFrame stream with
        | WorkerWire.FrameRead.Broken reason -> reason |> Expect.stringContains "bound" "outside 1.."
        | other -> failtestf "expected Broken, got %A" other
      } ]
  ]
