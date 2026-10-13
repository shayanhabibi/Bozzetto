namespace Bozzetto

open System
open System.IO
open System.Text
open Bozzetto.WorkerProtocol

/// The typed binary wire between the daemon and a per-session F# worker
/// (Bozzetto.Host), carried over a local Unix-domain stream socket.
///
/// One explicit profile, no reflection and no JSON: little-endian fixed-width
/// integers, 7-bit-encoded counts, UTF-8 length-prefixed strings, an explicit
/// tag per union case. Every frame binds the magic and protocol version before
/// its envelope is interpreted, and a decode must consume the payload exactly.
///
/// The wire carries the REPL surface of `WorkerMessage`: eval, check,
/// completions, cancel, script load, reset, hard reset, status, live values
/// and shutdown. Live-testing and app-run messages are not carried yet;
/// `carries` names them so the client can refuse them loudly instead of
/// sending something the worker cannot answer.
module WorkerWire =

  [<Literal>]
  let ProtocolVersion = 1us

  /// Upper bound on one frame. Eval output and live-value snapshots are the
  /// large payloads; anything past this is a fault, not a message.
  [<Literal>]
  let MaxFrameBytes = 67108864

  let private magic = "BZFW"B

  type Envelope =
    /// The worker's first frame on every accepted connection.
    | Hello of protocolVersion: uint16 * sessionId: string * coreVersion: string
    | Request of correlation: int64 * message: WorkerMessage
    | Reply of correlation: int64 * response: WorkerResponse

  exception private WireFault of string

  let private fault message = raise (WireFault message)

  /// Messages this wire carries. The rest belong to live testing and app runs,
  /// which the restored F# worker does not serve yet.
  let carries (message: WorkerMessage) =
    match message with
    | WorkerMessage.EvalCode _
    | WorkerMessage.CheckCode _
    | WorkerMessage.TypeCheckWithSymbols _
    | WorkerMessage.GetCompletions _
    | WorkerMessage.CancelEval
    | WorkerMessage.LoadScript _
    | WorkerMessage.ResetSession _
    | WorkerMessage.HardResetSession _
    | WorkerMessage.GetStatus _
    | WorkerMessage.GetLiveValues _
    | WorkerMessage.Shutdown -> true
    | WorkerMessage.RunTests _
    | WorkerMessage.GetTestDiscovery _
    | WorkerMessage.EvalLiveTestFile _
    | WorkerMessage.GetInstrumentationMaps _
    | WorkerMessage.RunApp _
    | WorkerMessage.StopApp _
    | WorkerMessage.AwaitAppChange _ -> false

  let private caseName (value: obj) =
    match Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, value.GetType(), Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic) with
    | case, _ -> case.Name

  /// The case name of a message, for refusals and timeouts.
  let messageName (message: WorkerMessage) = caseName (box message)

  /// The refusal for a message the wire does not carry.
  let notCarried (message: WorkerMessage) =
    sprintf "The F# worker wire does not carry %s yet: live testing and app runs are not restored for F# sessions." (messageName message)

  // ---- primitives ---------------------------------------------------------

  let private writeString (w: BinaryWriter) (value: string) =
    w.Write(match value with null -> "" | value -> value)

  let private writeList (write: BinaryWriter -> 'T -> unit) (w: BinaryWriter) (values: 'T list) =
    w.Write7BitEncodedInt(List.length values)
    for value in values do write w value

  let private writeOption (write: BinaryWriter -> 'T -> unit) (w: BinaryWriter) (value: 'T option) =
    match value with
    | None -> w.Write false
    | Some value ->
      w.Write true
      write w value

  let private writeInt (w: BinaryWriter) (value: int) = w.Write value

  let private readCount (r: BinaryReader) =
    let count = r.Read7BitEncodedInt()
    let remaining = r.BaseStream.Length - r.BaseStream.Position
    // Every element costs at least one byte, so a count past the remaining
    // payload is a lie told before allocation, not a large list.
    if count < 0 || int64 count > remaining then fault (sprintf "count %d exceeds the remaining %d bytes" count remaining)
    count

  let private readList (read: BinaryReader -> 'T) (r: BinaryReader) : 'T list =
    let count = readCount r
    List.init count (fun _ -> read r)

  let private readBool (r: BinaryReader) =
    match r.ReadByte() with
    | 0uy -> false
    | 1uy -> true
    | other -> fault (sprintf "boolean byte %d" other)

  let private readOption (read: BinaryReader -> 'T) (r: BinaryReader) : 'T option =
    match readBool r with
    | false -> None
    | true -> Some (read r)

  let private readString (r: BinaryReader) = r.ReadString()
  let private readInt (r: BinaryReader) = r.ReadInt32()

  let private readTag (r: BinaryReader) = int (r.ReadByte())

  // ---- payload types ------------------------------------------------------

  let private writeSeverity (w: BinaryWriter) (severity: Features.Diagnostics.DiagnosticSeverity) =
    w.Write(
      match severity with
      | Features.Diagnostics.DiagnosticSeverity.Blocking -> 0uy
      | Features.Diagnostics.DiagnosticSeverity.Hidden -> 1uy
      | Features.Diagnostics.DiagnosticSeverity.Info -> 2uy
      | Features.Diagnostics.DiagnosticSeverity.Warning -> 3uy)

  let private readSeverity (r: BinaryReader) =
    match readTag r with
    | 0 -> Features.Diagnostics.DiagnosticSeverity.Blocking
    | 1 -> Features.Diagnostics.DiagnosticSeverity.Hidden
    | 2 -> Features.Diagnostics.DiagnosticSeverity.Info
    | 3 -> Features.Diagnostics.DiagnosticSeverity.Warning
    | tag -> fault (sprintf "diagnostic severity tag %d" tag)

  let private writeDiagnostic (w: BinaryWriter) (d: WorkerDiagnostic) =
    writeSeverity w d.Severity
    writeString w d.Message
    w.Write d.StartLine
    w.Write d.StartColumn
    w.Write d.EndLine
    w.Write d.EndColumn
    w.Write d.ErrorNumber

  let private readDiagnostic (r: BinaryReader) : WorkerDiagnostic =
    let severity = readSeverity r
    let message = readString r
    let startLine = readInt r
    let startColumn = readInt r
    let endLine = readInt r
    let endColumn = readInt r
    let errorNumber = readInt r
    { Severity = severity; Message = message
      StartLine = startLine; StartColumn = startColumn
      EndLine = endLine; EndColumn = endColumn
      ErrorNumber = errorNumber }

  let private writeSymbolRef (w: BinaryWriter) (s: WorkerSymbolRef) =
    writeString w s.SymbolFullName
    w.Write s.IsFromDefinition
    writeString w s.FilePath
    w.Write s.Line

  let private readSymbolRef (r: BinaryReader) : WorkerSymbolRef =
    let name = readString r
    let isDefinition = readBool r
    let filePath = readString r
    let line = readInt r
    { SymbolFullName = name; IsFromDefinition = isDefinition; FilePath = filePath; Line = line }

  let private writeProject (w: BinaryWriter) (p: ProjectLoading.ClassifiedProject) =
    writeString w p.Path
    w.Write(
      match p.Role with
      | ProjectLoading.ProjectRole.Executable -> 0uy
      | ProjectLoading.ProjectRole.Library -> 1uy
      | ProjectLoading.ProjectRole.Test -> 2uy)
    writeList writeString w p.PackageRefs

  let private readProject (r: BinaryReader) : ProjectLoading.ClassifiedProject =
    let path = readString r
    let role =
      match readTag r with
      | 0 -> ProjectLoading.ProjectRole.Executable
      | 1 -> ProjectLoading.ProjectRole.Library
      | 2 -> ProjectLoading.ProjectRole.Test
      | tag -> fault (sprintf "project role tag %d" tag)
    let packageRefs = readList readString r
    { Path = path; Role = role; PackageRefs = packageRefs }

  let private writeStatus (w: BinaryWriter) (status: SessionStatus) =
    match status with
    | SessionStatus.Starting -> w.Write 0uy
    | SessionStatus.Ready -> w.Write 1uy
    | SessionStatus.Evaluating -> w.Write 2uy
    | SessionStatus.Building reason ->
      w.Write 3uy
      writeString w reason
    | SessionStatus.Faulted -> w.Write 4uy
    | SessionStatus.Restarting -> w.Write 5uy
    | SessionStatus.Stopped -> w.Write 6uy

  let private readStatus (r: BinaryReader) =
    match readTag r with
    | 0 -> SessionStatus.Starting
    | 1 -> SessionStatus.Ready
    | 2 -> SessionStatus.Evaluating
    | 3 -> SessionStatus.Building (readString r)
    | 4 -> SessionStatus.Faulted
    | 5 -> SessionStatus.Restarting
    | 6 -> SessionStatus.Stopped
    | tag -> fault (sprintf "session status tag %d" tag)

  let private writeSnapshot (w: BinaryWriter) (s: WorkerStatusSnapshot) =
    writeStatus w s.Status
    writeOption writeString w s.StatusMessage
    w.Write s.EvalCount
    w.Write s.AvgDurationMs
    w.Write s.MinDurationMs
    w.Write s.MaxDurationMs
    writeList writeProject w s.Projects
    writeString w s.CoreVersion

  let private readSnapshot (r: BinaryReader) : WorkerStatusSnapshot =
    let status = readStatus r
    let message = readOption readString r
    let evalCount = readInt r
    let avg = r.ReadInt64()
    let min = r.ReadInt64()
    let max = r.ReadInt64()
    let projects = readList readProject r
    let coreVersion = readString r
    { Status = status; StatusMessage = message; EvalCount = evalCount
      AvgDurationMs = avg; MinDurationMs = min; MaxDurationMs = max
      Projects = projects; CoreVersion = coreVersion }

  let private writeBuildDiagnostic (w: BinaryWriter) (d: BuildDiagnostic) =
    writeOption writeString w d.File
    writeOption writeInt w d.Line
    writeOption writeInt w d.Column
    w.Write(match d.Severity with BuildDiagnosticSeverity.Blocking -> 0uy | BuildDiagnosticSeverity.Warning -> 1uy)
    writeOption writeString w d.Code
    writeString w d.Message

  let private readBuildDiagnostic (r: BinaryReader) : BuildDiagnostic =
    let file = readOption readString r
    let line = readOption readInt r
    let column = readOption readInt r
    let severity =
      match readTag r with
      | 0 -> BuildDiagnosticSeverity.Blocking
      | 1 -> BuildDiagnosticSeverity.Warning
      | tag -> fault (sprintf "build diagnostic severity tag %d" tag)
    let code = readOption readString r
    let message = readString r
    { File = file; Line = line; Column = column; Severity = severity; Code = code; Message = message }

  /// Errors a worker produces keep their case. Any other case crosses as its
  /// name and description and arrives as `Unexpected`, which says so.
  let private writeError (w: BinaryWriter) (error: BozzettoError) =
    let text tag (value: string) =
      w.Write(byte tag)
      writeString w value
    match error with
    | BozzettoError.EvalFailed reason -> text 1 reason
    | BozzettoError.ResetFailed reason -> text 2 reason
    | BozzettoError.HardResetFailed reason -> text 3 reason
    | BozzettoError.BuildFailed (exitCode, diagnostics) ->
      w.Write 4uy
      w.Write exitCode
      writeList writeBuildDiagnostic w diagnostics
    | BozzettoError.ScriptLoadFailed reason -> text 5 reason
    | BozzettoError.CheckFailed reason -> text 6 reason
    | BozzettoError.CompletionFailed (sessionId, reason) ->
      w.Write 7uy
      writeString w sessionId
      writeString w reason
    | BozzettoError.CancelFailed reason -> text 8 reason
    | BozzettoError.EvalSupersededByReset -> w.Write 9uy
    | BozzettoError.WarmupOpenFailed (name, reason) ->
      w.Write 10uy
      writeString w name
      writeString w reason
    | BozzettoError.WarmupContextFailed (sessionId, reason) ->
      w.Write 11uy
      writeString w sessionId
      writeString w reason
    | BozzettoError.SessionCreationFailed reason -> text 12 reason
    | BozzettoError.NeedsRebuild missing ->
      w.Write 13uy
      writeList writeString w missing
    | BozzettoError.WorkerSpawnFailed reason -> text 14 reason
    | other ->
      w.Write 0uy
      writeString w (caseName (box other))
      writeString w (BozzettoError.describe other)

  let private readError (r: BinaryReader) : BozzettoError =
    match readTag r with
    | 0 ->
      let name = readString r
      let description = readString r
      BozzettoError.Unexpected (exn (sprintf "%s (worker error %s)" description name))
    | 1 -> BozzettoError.EvalFailed (readString r)
    | 2 -> BozzettoError.ResetFailed (readString r)
    | 3 -> BozzettoError.HardResetFailed (readString r)
    | 4 ->
      let exitCode = readInt r
      BozzettoError.BuildFailed (exitCode, readList readBuildDiagnostic r)
    | 5 -> BozzettoError.ScriptLoadFailed (readString r)
    | 6 -> BozzettoError.CheckFailed (readString r)
    | 7 ->
      let sessionId = readString r
      BozzettoError.CompletionFailed (sessionId, readString r)
    | 8 -> BozzettoError.CancelFailed (readString r)
    | 9 -> BozzettoError.EvalSupersededByReset
    | 10 ->
      let name = readString r
      BozzettoError.WarmupOpenFailed (name, readString r)
    | 11 ->
      let sessionId = readString r
      BozzettoError.WarmupContextFailed (sessionId, readString r)
    | 12 -> BozzettoError.SessionCreationFailed (readString r)
    | 13 -> BozzettoError.NeedsRebuild (readList readString r)
    | 14 -> BozzettoError.WorkerSpawnFailed (readString r)
    | tag -> fault (sprintf "error tag %d" tag)

  let private writeResult (writeOk: BinaryWriter -> 'T -> unit) (w: BinaryWriter) (result: Result<'T, BozzettoError>) =
    match result with
    | Ok value ->
      w.Write 0uy
      writeOk w value
    | Error error ->
      w.Write 1uy
      writeError w error

  let private readResult (readOk: BinaryReader -> 'T) (r: BinaryReader) : Result<'T, BozzettoError> =
    match readTag r with
    | 0 -> Ok (readOk r)
    | 1 -> Error (readError r)
    | tag -> fault (sprintf "result tag %d" tag)

  // ---- messages -----------------------------------------------------------

  let private writeMessage (w: BinaryWriter) (message: WorkerMessage) =
    match message with
    | WorkerMessage.EvalCode (code, replyId) ->
      w.Write 1uy
      writeString w code
      writeString w replyId
    | WorkerMessage.CheckCode (code, replyId) ->
      w.Write 2uy
      writeString w code
      writeString w replyId
    | WorkerMessage.TypeCheckWithSymbols (code, filePath, replyId) ->
      w.Write 3uy
      writeString w code
      writeString w filePath
      writeString w replyId
    | WorkerMessage.GetCompletions (code, cursorPos, replyId) ->
      w.Write 4uy
      writeString w code
      w.Write cursorPos
      writeString w replyId
    | WorkerMessage.CancelEval -> w.Write 5uy
    | WorkerMessage.LoadScript (filePath, replyId) ->
      w.Write 6uy
      writeString w filePath
      writeString w replyId
    | WorkerMessage.ResetSession replyId ->
      w.Write 7uy
      writeString w replyId
    | WorkerMessage.HardResetSession (rebuild, replyId) ->
      w.Write 8uy
      w.Write rebuild
      writeString w replyId
    | WorkerMessage.GetStatus replyId ->
      w.Write 9uy
      writeString w replyId
    | WorkerMessage.GetLiveValues replyId ->
      w.Write 10uy
      writeString w replyId
    | WorkerMessage.Shutdown -> w.Write 11uy
    | other -> invalidArg "message" (notCarried other)

  let private readMessage (r: BinaryReader) : WorkerMessage =
    match readTag r with
    | 1 ->
      let code = readString r
      WorkerMessage.EvalCode (code, readString r)
    | 2 ->
      let code = readString r
      WorkerMessage.CheckCode (code, readString r)
    | 3 ->
      let code = readString r
      let filePath = readString r
      WorkerMessage.TypeCheckWithSymbols (code, filePath, readString r)
    | 4 ->
      let code = readString r
      let cursorPos = readInt r
      WorkerMessage.GetCompletions (code, cursorPos, readString r)
    | 5 -> WorkerMessage.CancelEval
    | 6 ->
      let filePath = readString r
      WorkerMessage.LoadScript (filePath, readString r)
    | 7 -> WorkerMessage.ResetSession (readString r)
    | 8 ->
      let rebuild = readBool r
      WorkerMessage.HardResetSession (rebuild, readString r)
    | 9 -> WorkerMessage.GetStatus (readString r)
    | 10 -> WorkerMessage.GetLiveValues (readString r)
    | 11 -> WorkerMessage.Shutdown
    | tag -> fault (sprintf "worker message tag %d" tag)

  let private writePair (w: BinaryWriter) (key: string, value: string) =
    writeString w key
    writeString w value

  let private readPair (r: BinaryReader) =
    let key = readString r
    key, readString r

  let private writeResponse (w: BinaryWriter) (response: WorkerResponse) =
    match response with
    | WorkerResponse.EvalResult (replyId, result, diagnostics, metadata) ->
      w.Write 1uy
      writeString w replyId
      writeResult writeString w result
      writeList writeDiagnostic w diagnostics
      writeList writePair w (Map.toList metadata)
    | WorkerResponse.CheckResult (replyId, diagnostics) ->
      w.Write 2uy
      writeString w replyId
      writeList writeDiagnostic w diagnostics
    | WorkerResponse.TypeCheckWithSymbolsResult (replyId, diagnostics, symbols) ->
      w.Write 3uy
      writeString w replyId
      writeList writeDiagnostic w diagnostics
      writeList writeSymbolRef w symbols
    | WorkerResponse.CompletionResult (replyId, completions) ->
      w.Write 4uy
      writeString w replyId
      writeList writeString w completions
    | WorkerResponse.StatusResult (replyId, snapshot) ->
      w.Write 5uy
      writeString w replyId
      writeSnapshot w snapshot
    | WorkerResponse.LiveValuesResult (replyId, json) ->
      w.Write 6uy
      writeString w replyId
      writeString w json
    | WorkerResponse.EvalCancelled wasRunning ->
      w.Write 7uy
      w.Write wasRunning
    | WorkerResponse.ResetResult (replyId, result) ->
      w.Write 8uy
      writeString w replyId
      writeResult (fun _ () -> ()) w result
    | WorkerResponse.HardResetResult (replyId, result) ->
      w.Write 9uy
      writeString w replyId
      writeResult writeString w result
    | WorkerResponse.ScriptLoaded (replyId, result) ->
      w.Write 10uy
      writeString w replyId
      writeResult writeString w result
    | WorkerResponse.WorkerReady -> w.Write 11uy
    | WorkerResponse.WorkerShuttingDown -> w.Write 12uy
    | WorkerResponse.WorkerError error ->
      w.Write 13uy
      writeError w error
    | WorkerResponse.TestRunResults _
    | WorkerResponse.InitialTestDiscovery _
    | WorkerResponse.EvalLiveTestFileResult _
    | WorkerResponse.InstrumentationMapsResult _
    | WorkerResponse.AppRunResult _ ->
      // Total by construction: a response this wire cannot carry crosses as a
      // named refusal rather than silently vanishing.
      w.Write 13uy
      writeError w (BozzettoError.EvalFailed (sprintf "The F# worker wire does not carry %s." (caseName (box response))))

  let private readResponse (r: BinaryReader) : WorkerResponse =
    match readTag r with
    | 1 ->
      let replyId = readString r
      let result = readResult readString r
      let diagnostics = readList readDiagnostic r
      let metadata = readList readPair r |> Map.ofList
      WorkerResponse.EvalResult (replyId, result, diagnostics, metadata)
    | 2 ->
      let replyId = readString r
      WorkerResponse.CheckResult (replyId, readList readDiagnostic r)
    | 3 ->
      let replyId = readString r
      let diagnostics = readList readDiagnostic r
      WorkerResponse.TypeCheckWithSymbolsResult (replyId, diagnostics, readList readSymbolRef r)
    | 4 ->
      let replyId = readString r
      WorkerResponse.CompletionResult (replyId, readList readString r)
    | 5 ->
      let replyId = readString r
      WorkerResponse.StatusResult (replyId, readSnapshot r)
    | 6 ->
      let replyId = readString r
      WorkerResponse.LiveValuesResult (replyId, readString r)
    | 7 -> WorkerResponse.EvalCancelled (readBool r)
    | 8 ->
      let replyId = readString r
      WorkerResponse.ResetResult (replyId, readResult ignore r)
    | 9 ->
      let replyId = readString r
      WorkerResponse.HardResetResult (replyId, readResult readString r)
    | 10 ->
      let replyId = readString r
      WorkerResponse.ScriptLoaded (replyId, readResult readString r)
    | 11 -> WorkerResponse.WorkerReady
    | 12 -> WorkerResponse.WorkerShuttingDown
    | 13 -> WorkerResponse.WorkerError (readError r)
    | tag -> fault (sprintf "worker response tag %d" tag)

  // ---- envelopes ----------------------------------------------------------

  /// Encode one envelope as a frame payload (without the length prefix).
  let encode (envelope: Envelope) : byte array =
    use buffer = new MemoryStream()
    (use w = new BinaryWriter(buffer, UTF8Encoding(false, true), true)
     w.Write magic
     w.Write ProtocolVersion
     match envelope with
     | Hello (version, sessionId, coreVersion) ->
       w.Write 1uy
       w.Write version
       writeString w sessionId
       writeString w coreVersion
     | Request (correlation, message) ->
       w.Write 2uy
       w.Write correlation
       writeMessage w message
     | Reply (correlation, response) ->
       w.Write 3uy
       w.Write correlation
       writeResponse w response)
    let bytes = buffer.ToArray()
    if bytes.Length > MaxFrameBytes then
      invalidOp (sprintf "worker frame of %d bytes exceeds the %d byte bound" bytes.Length MaxFrameBytes)
    bytes

  /// Decode one frame payload. A wrong magic or version is refused before the
  /// envelope is interpreted; trailing bytes are a fault.
  let decode (payload: byte array) : Result<Envelope, string> =
    try
      use buffer = new MemoryStream(payload, false)
      use r = new BinaryReader(buffer, UTF8Encoding(false, true), true)
      let head = r.ReadBytes magic.Length
      if head <> magic then fault "not a Bozzetto F# worker frame"
      let version = r.ReadUInt16()
      if version <> ProtocolVersion then
        fault (sprintf "protocol version %d, expected %d" version ProtocolVersion)
      let envelope =
        match readTag r with
        | 1 ->
          let helloVersion = r.ReadUInt16()
          let sessionId = readString r
          Hello (helloVersion, sessionId, readString r)
        | 2 ->
          let correlation = r.ReadInt64()
          Request (correlation, readMessage r)
        | 3 ->
          let correlation = r.ReadInt64()
          Reply (correlation, readResponse r)
        | tag -> fault (sprintf "envelope tag %d" tag)
      if buffer.Position <> buffer.Length then
        fault (sprintf "%d trailing bytes" (buffer.Length - buffer.Position))
      Ok envelope
    with
    | WireFault message -> Error message
    | :? EndOfStreamException -> Error "truncated frame"
    | :? DecoderFallbackException -> Error "invalid UTF-8 string"
    | :? FormatException as e -> Error e.Message

  // ---- framing ------------------------------------------------------------

  /// Write one length-prefixed frame. Callers serialize writers per stream.
  let writeFrame (stream: Stream) (payload: byte array) : Async<unit> =
    async {
      let header = BitConverter.GetBytes(payload.Length)
      if not BitConverter.IsLittleEndian then Array.Reverse header
      let! ct = Async.CancellationToken
      do! stream.WriteAsync(header, 0, header.Length, ct) |> Async.AwaitTask
      do! stream.WriteAsync(payload, 0, payload.Length, ct) |> Async.AwaitTask
      do! stream.FlushAsync(ct) |> Async.AwaitTask
    }

  /// Outcome of reading one frame.
  [<RequireQualifiedAccess>]
  type FrameRead =
    | Frame of byte array
    /// The peer closed the stream between frames.
    | Closed
    /// The stream ended inside a frame, or announced an impossible length.
    | Broken of reason: string

  let private readExactly (stream: Stream) (buffer: byte array) : Async<int> =
    async {
      let! ct = Async.CancellationToken
      let mutable offset = 0
      let mutable ended = false
      while not ended && offset < buffer.Length do
        let! read = stream.ReadAsync(buffer, offset, buffer.Length - offset, ct) |> Async.AwaitTask
        if read = 0 then ended <- true else offset <- offset + read
      return offset
    }

  /// Read one length-prefixed frame, bounding the length before allocating.
  let readFrame (stream: Stream) : Async<FrameRead> =
    async {
      let header = Array.zeroCreate<byte> 4
      let! got = readExactly stream header
      match got with
      | 0 -> return FrameRead.Closed
      | 4 ->
        if not BitConverter.IsLittleEndian then Array.Reverse header
        let length = BitConverter.ToInt32(header, 0)
        if length <= 0 || length > MaxFrameBytes then
          return FrameRead.Broken (sprintf "announced frame length %d is outside 1..%d" length MaxFrameBytes)
        else
          let payload = Array.zeroCreate<byte> length
          let! read = readExactly stream payload
          if read = length then return FrameRead.Frame payload
          else return FrameRead.Broken (sprintf "stream ended %d bytes into a %d byte frame" read length)
      | partial -> return FrameRead.Broken (sprintf "stream ended %d bytes into a frame header" partial)
    }
