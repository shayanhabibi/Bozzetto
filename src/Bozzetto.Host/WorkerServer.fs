/// The worker's half of the F# worker wire: a Unix-domain stream listener in a
/// private directory, one Hello per accepted connection, and concurrent
/// request handling so a CancelEval can reach a running eval.
module Bozzetto.Host.WorkerServer

open System
open System.IO
open System.Net.Sockets
open System.Threading
open Bozzetto
open Bozzetto.Utils
open Bozzetto.WorkerProtocol

type Server = {
  /// The path the daemon connects to; announced on stdout as WORKER_SOCKET=.
  SocketPath: string
  /// Stop accepting, close connections and remove the private directory.
  Stop: unit -> unit
}

/// A private directory (owner-only on Unix) so no other local user can reach
/// the session's evaluator through the socket.
let private createPrivateDirectory (sessionId: string) =
  let directory = Path.Combine(Path.GetTempPath(), sprintf "boz-fsw-%s-%s" sessionId (Guid.NewGuid().ToString("N").Substring(0, 8)))
  if OperatingSystem.IsWindows() then Directory.CreateDirectory directory |> ignore
  else Directory.CreateDirectory(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
  directory

let private serveConnection
  (sessionId: string)
  (coreVersion: string)
  (handle: WorkerMessage -> Async<WorkerResponse>)
  (onShutdown: unit -> unit)
  (socket: Socket)
  (ct: CancellationToken)
  =
  async {
    use stream = new NetworkStream(socket, true)
    use gate = new SemaphoreSlim(1, 1)
    let write (envelope: WorkerWire.Envelope) =
      async {
        let payload = WorkerWire.encode envelope
        do! gate.WaitAsync(ct) |> Async.AwaitTask
        try do! WorkerWire.writeFrame stream payload
        finally gate.Release() |> ignore
      }
    try
      do! write (WorkerWire.Hello (WorkerWire.ProtocolVersion, sessionId, coreVersion))
      let mutable reading = true
      while reading do
        let! frame = WorkerWire.readFrame stream
        match frame with
        | WorkerWire.FrameRead.Closed -> reading <- false
        | WorkerWire.FrameRead.Broken reason ->
          eprintfn "[WorkerServer] connection broke the wire: %s" reason
          reading <- false
        | WorkerWire.FrameRead.Frame payload ->
          match WorkerWire.decode payload with
          | Ok (WorkerWire.Request (correlation, message)) ->
            // Each request runs on its own: a CancelEval must not queue
            // behind the eval it cancels.
            Async.Start(
              async {
                let! response =
                  async {
                    try return! handle message
                    with error ->
                      return WorkerResponse.WorkerError (BozzettoError.EvalFailed (sprintf "Worker failed handling %s: %s" (WorkerWire.messageName message) error.Message))
                  }
                try
                  do! write (WorkerWire.Reply (correlation, response))
                with error ->
                  eprintfn "[WorkerServer] could not reply to %s: %s" (WorkerWire.messageName message) error.Message
                match message with
                | WorkerMessage.Shutdown -> onShutdown ()
                | _ -> ()
              }, ct)
          | Ok other ->
            eprintfn "[WorkerServer] refusing a connection that sent %A" other
            reading <- false
          | Error reason ->
            eprintfn "[WorkerServer] refusing an undecodable frame: %s" reason
            reading <- false
    with
    | :? OperationCanceledException -> ()
    | :? IOException -> ()
    | :? ObjectDisposedException -> ()
  }

/// Bind the session's socket and start accepting. Every connection is greeted
/// with a Hello naming this session, so a daemon can never route one session's
/// requests to another's worker.
let start
  (sessionId: string)
  (coreVersion: string)
  (handle: WorkerMessage -> Async<WorkerResponse>)
  (onShutdown: unit -> unit)
  : Server =
  if not Socket.OSSupportsUnixDomainSockets then
    raise (PlatformNotSupportedException "The F# worker requires local Unix-domain sockets.")
  let directory = createPrivateDirectory sessionId
  let socketPath = Path.Combine(directory, "worker.sock")
  let listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
  listener.Bind(UnixDomainSocketEndPoint socketPath)
  listener.Listen 8
  let cts = new CancellationTokenSource()
  Async.Start(
    async {
      try
        while not cts.IsCancellationRequested do
          let! socket = listener.AcceptAsync(cts.Token).AsTask() |> Async.AwaitTask
          Async.Start(serveConnection sessionId coreVersion handle onShutdown socket cts.Token, cts.Token)
      with
      | :? OperationCanceledException -> ()
      | :? ObjectDisposedException -> ()
      | :? SocketException as error when cts.IsCancellationRequested -> ignore error
      | error -> Log.error "[WorkerServer] accept loop failed: %s" error.Message
    }, cts.Token)
  let stopped = ref 0
  { SocketPath = socketPath
    Stop = fun () ->
      if Interlocked.Exchange(stopped, 1) = 0 then
        try cts.Cancel() with :? ObjectDisposedException -> ()
        try listener.Dispose() with _ -> ()
        try Directory.Delete(directory, true) with _ -> () }
