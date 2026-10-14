namespace Bozzetto

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Bozzetto.WorkerProtocol
open Bozzetto.Utils

/// The daemon's half of the F# worker wire: a `SessionProxy` over one
/// Unix-domain stream connection per worker, multiplexed by correlation.
///
/// Transport failure raises, as the HTTP proxy it replaces did, so callers'
/// existing "worker died" handling still fires. A message the wire does not
/// carry is refused locally with a named reason and never sent.
module WorkerSocketClient =

  type private Connection = {
    Socket: Socket
    Stream: NetworkStream
    WriteGate: SemaphoreSlim
    Pending: ConcurrentDictionary<int64, TaskCompletionSource<WorkerResponse>>
    Closed: CancellationTokenSource
    mutable Fault: string option
    mutable NextCorrelation: int64
  }

  let private fail (connection: Connection) (reason: string) =
    lock connection (fun () ->
      if Option.isNone connection.Fault then connection.Fault <- Some reason)
    try connection.Closed.Cancel() with :? ObjectDisposedException -> ()
    for KeyValue (correlation, waiter) in connection.Pending.ToArray() do
      match connection.Pending.TryRemove correlation with
      | true, _ -> waiter.TrySetException(IOException(reason)) |> ignore
      | false, _ -> ()
    try connection.Socket.Dispose() with _ -> ()

  let private readLoop (connection: Connection) (socketPath: string) =
    async {
      let mutable running = true
      while running do
        let! frame = WorkerWire.readFrame connection.Stream
        match frame with
        | WorkerWire.FrameRead.Closed ->
          fail connection (sprintf "F# worker at %s closed its connection" socketPath)
          running <- false
        | WorkerWire.FrameRead.Broken reason ->
          fail connection (sprintf "F# worker at %s broke the wire: %s" socketPath reason)
          running <- false
        | WorkerWire.FrameRead.Frame payload ->
          match WorkerWire.decode payload with
          | Ok (WorkerWire.Reply (correlation, response)) ->
            match connection.Pending.TryRemove correlation with
            | true, waiter -> waiter.TrySetResult response |> ignore
            // A reply after its caller withdrew has nobody to answer.
            | false, _ -> ()
          | Ok other ->
            fail connection (sprintf "F# worker at %s sent an unexpected envelope: %A" socketPath other)
            running <- false
          | Error refusal ->
            fail connection (sprintf "F# worker at %s sent an undecodable frame: %s" socketPath (WorkerWire.WireRefusal.describe refusal))
            running <- false
    }

  /// Connect and require the worker's Hello for exactly this session and
  /// protocol version before any request is sent.
  let private connect (socketPath: string) (sessionId: string) : Async<Connection> =
    async {
      let socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
      try
        let! ct = Async.CancellationToken
        do! socket.ConnectAsync(UnixDomainSocketEndPoint socketPath, ct).AsTask() |> Async.AwaitTask
        let stream = new NetworkStream(socket, true)
        let! first = WorkerWire.readFrame stream
        let hello =
          match first with
          | WorkerWire.FrameRead.Frame payload -> WorkerWire.decode payload |> Result.mapError WorkerWire.WireRefusal.describe
          | WorkerWire.FrameRead.Closed -> Error "the worker closed the connection before its Hello"
          | WorkerWire.FrameRead.Broken reason -> Error reason
        match hello with
        | Ok (WorkerWire.Hello (version, helloSession, coreVersion)) when version = WorkerWire.ProtocolVersion && helloSession = sessionId ->
          Log.debug "[WorkerSocketClient] connected to session %s at %s (worker core %s)" sessionId socketPath coreVersion
          let connection =
            { Socket = socket
              Stream = stream
              WriteGate = new SemaphoreSlim(1, 1)
              Pending = ConcurrentDictionary()
              Closed = new CancellationTokenSource()
              Fault = None
              NextCorrelation = 0L }
          Async.Start(
            async {
              try do! readLoop connection socketPath
              with error -> fail connection (sprintf "F# worker at %s: %s" socketPath error.Message)
            })
          return connection
        | Ok (WorkerWire.Hello (version, helloSession, _)) ->
          return raise (IOException(sprintf "F# worker at %s greeted as session %s protocol %d; expected session %s protocol %d" socketPath helloSession version sessionId WorkerWire.ProtocolVersion))
        | Ok other -> return raise (IOException(sprintf "F# worker at %s opened with %A instead of Hello" socketPath other))
        | Error reason -> return raise (IOException(sprintf "F# worker at %s: %s" socketPath reason))
      with error ->
        socket.Dispose()
        return raise error
    }

  let private send (connection: Connection) (message: WorkerMessage) : Async<WorkerResponse> =
    async {
      match connection.Fault with
      | Some reason -> return raise (IOException(reason))
      | None ->
      let correlation = Interlocked.Increment(&connection.NextCorrelation)
      let waiter = TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
      connection.Pending[correlation] <- waiter
      let payload = WorkerWire.encode (WorkerWire.Request (correlation, message))
      let! ct = Async.CancellationToken
      try
        do! connection.WriteGate.WaitAsync(ct) |> Async.AwaitTask
        try
          do! WorkerWire.writeFrame connection.Stream payload
        finally
          connection.WriteGate.Release() |> ignore
      with error ->
        connection.Pending.TryRemove correlation |> ignore
        match error with
        | :? OperationCanceledException -> return raise error
        | _ ->
          fail connection (sprintf "writing to the F# worker failed: %s" error.Message)
          return raise (IOException(sprintf "writing to the F# worker failed: %s" error.Message, error))
      // Waiting is bounded like the HTTP request it replaces; a caller that
      // withdraws removes its correlation so a late reply is dropped.
      use bounded = CancellationTokenSource.CreateLinkedTokenSource(ct)
      bounded.CancelAfter(Timeouts.workerHttpRequest)
      use _withdraw = bounded.Token.Register(fun () ->
        match connection.Pending.TryRemove correlation with
        | true, _ -> waiter.TrySetCanceled() |> ignore
        | false, _ -> ())
      try
        return! waiter.Task |> Async.AwaitTask
      with
      | :? TaskCanceledException when not ct.IsCancellationRequested ->
        return raise (TimeoutException(sprintf "The F# worker did not answer %s within %O" (WorkerWire.messageName message) Timeouts.workerHttpRequest))
    }

  type private Endpoint = {
    SessionId: string
    Gate: SemaphoreSlim
    mutable Current: Connection option
  }

  /// Keyed by session AND path: a connection is only reused for the session
  /// its Hello proved, never handed to a proxy that names another session.
  let private endpoints = ConcurrentDictionary<struct (string * string), Endpoint>()

  let private connectionFor (socketPath: string) (endpoint: Endpoint) : Async<Connection> =
    async {
      match endpoint.Current with
      | Some connection when Option.isNone connection.Fault -> return connection
      | _ ->
        let! ct = Async.CancellationToken
        do! endpoint.Gate.WaitAsync(ct) |> Async.AwaitTask
        try
          match endpoint.Current with
          | Some connection when Option.isNone connection.Fault -> return connection
          | _ ->
            let! connection = connect socketPath endpoint.SessionId
            endpoint.Current <- Some connection
            return connection
        finally
          endpoint.Gate.Release() |> ignore
    }

  /// The proxy for one worker socket. Connections are cached per session and
  /// socket path and re-established after a fault, so the snapshot's path is
  /// enough to route to a worker without going through the supervisor's mailbox.
  let proxy (sessionId: string) (socketPath: string) : SessionProxy =
    let endpoint =
      endpoints.GetOrAdd(struct (sessionId, socketPath), fun _ -> { SessionId = sessionId; Gate = new SemaphoreSlim(1, 1); Current = None })
    fun message ->
      async {
        match WorkerWire.carries message with
        | false ->
          let refusal = WorkerWire.notCarried message
          return
            match message with
            | WorkerMessage.GetInstrumentationMaps replyId ->
              // The retired worker answered this with no maps too: IL coverage
              // is only produced by live testing, which is not restored.
              WorkerResponse.InstrumentationMapsResult (replyId, [||])
            | WorkerMessage.GetTestDiscovery _ ->
              WorkerResponse.InitialTestDiscovery ([||], [])
            | WorkerMessage.EvalLiveTestFile (_, _, replyId) ->
              WorkerResponse.EvalLiveTestFileResult (replyId, Error (BozzettoError.EvalFailed refusal))
            | WorkerMessage.RunApp (project, _, replyId) ->
              WorkerResponse.AppRunResult (replyId, Error (BozzettoError.AppRunFailed (project, refusal)))
            | WorkerMessage.StopApp (_, replyId)
            | WorkerMessage.AwaitAppChange (_, replyId) ->
              WorkerResponse.AppRunResult (replyId, Error (BozzettoError.AppRunFailed ("", refusal)))
            | _ -> WorkerResponse.WorkerError (BozzettoError.EvalFailed refusal)
        | true ->
          let! connection = connectionFor socketPath endpoint
          return! send connection message
      }

  /// Drop every cached connection to a worker socket once its process is gone.
  let forget (socketPath: string) =
    for KeyValue (struct (_, path) as key, _) in endpoints.ToArray() do
      if path = socketPath then
        match endpoints.TryRemove key with
        | true, endpoint ->
          endpoint.Current |> Option.iter (fun connection -> fail connection "the F# worker was retired")
        | false, _ -> ()
