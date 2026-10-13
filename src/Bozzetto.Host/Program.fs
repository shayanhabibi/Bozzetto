module Bozzetto.Host.Program

/// Entry point for a per-session F# worker. Spawned by the daemon's session
/// supervisor with `<sessionId> <unused>`; the session's projects and flags
/// arrive as environment (Args.WorkerConfig).
[<EntryPoint>]
let main args =
  match args |> Array.tryItem 0 with
  | None ->
    eprintfn "Bozzetto.Host: missing sessionId argument"
    2
  | Some sessionId ->
    match Bozzetto.WorkerProtocol.SessionId.validate sessionId with
    | Error reason ->
      eprintfn "Bozzetto.Host: %s" reason
      2
    | Ok _ ->
      try
        WorkerMain.run sessionId |> Async.RunSynchronously
        0
      with error ->
        eprintfn "Bozzetto.Host: worker %s failed: %s" sessionId (error.ToString())
        1
