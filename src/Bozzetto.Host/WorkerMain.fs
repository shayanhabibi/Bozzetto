/// The per-session F# worker: loads the session's projects, hosts one FSI
/// session through the AppState actor, and answers the daemon over the F#
/// worker wire. Restored from the SageFs worker without its hot-reload,
/// runtime-patching and app-run surfaces.
module Bozzetto.Host.WorkerMain

open System
open System.Threading
open Bozzetto
open Bozzetto.Utils
open Bozzetto.WorkerProtocol
open Bozzetto.AppState

/// The Bozzetto.Core version this worker loaded, reported in status replies:
/// a rebuilt worker can legitimately differ from the daemon supervising it.
let workerCoreVersion : string =
  typeof<BozzettoError>.Assembly.GetName().Version
  |> Option.ofObj
  |> Option.map (fun v -> v.ToString())
  |> Option.defaultValue "unknown"

let toStatusSnapshot
  (state: SessionState)
  (stats: Affordances.EvalStats)
  (statusMsg: string option)
  (projects: ProjectLoading.ClassifiedProject list)
  : WorkerStatusSnapshot =
  let avg =
    match stats.EvalCount > 0 with
    | true -> stats.TotalDuration.TotalMilliseconds / float stats.EvalCount |> int64
    | false -> 0L
  let status =
    match state with
    | SessionState.Uninitialized
    | SessionState.WarmingUp -> SessionStatus.Starting
    | SessionState.Ready -> SessionStatus.Ready
    | SessionState.Evaluating -> SessionStatus.Evaluating
    | SessionState.Faulted -> SessionStatus.Faulted
  { Status = status
    StatusMessage = statusMsg
    EvalCount = stats.EvalCount
    AvgDurationMs = avg
    MinDurationMs = stats.MinDuration.TotalMilliseconds |> int64
    MaxDurationMs = stats.MaxDuration.TotalMilliseconds |> int64
    Projects = projects
    CoreVersion = workerCoreVersion }

let rec private rootCause (e: exn) : exn =
  match box e.InnerException with
  | null -> e
  | _ -> rootCause e.InnerException

/// A structured BozzettoError the actor raised keeps its case; any other
/// exception becomes `fallback`, leading with the root cause.
let private toWorkerError (fallback: string -> BozzettoError) (ex: exn) : BozzettoError =
  match ex with
  | :? BozzettoErrorException as e -> e.Error
  | _ ->
    let root = rootCause ex
    fallback (ErrorMessages.runtimeExceptionSummary (root.GetType().Name) root.Message (ex.ToString()))

/// Answer one carried WorkerMessage through the actor.
let handleMessage
  (actor: AppActor)
  (getState: unit -> SessionState)
  (getStats: unit -> Affordances.EvalStats)
  (getStatusMessage: unit -> string option)
  (projects: ProjectLoading.ClassifiedProject list)
  (msg: WorkerMessage)
  : Async<WorkerResponse> =
  async {
    match msg with
    | WorkerMessage.EvalCode(code, replyId) ->
      let request = { Code = code; Args = Map.empty }
      // Bounded like the daemon's own wait: a wedged actor must not hold an
      // eval forever after its caller has gone.
      use cts = new CancellationTokenSource(Timeouts.workerHttpRequest)
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Repl
      let diags = response.Diagnostics |> Array.map WorkerDiagnostic.ofDiagnostic |> Array.toList
      let result = response.EvaluationResult |> Result.mapError (toWorkerError BozzettoError.EvalFailed)
      // Live-testing metadata carried runtime closures; none of it crosses.
      return WorkerResponse.EvalResult(replyId, result, diags, Map.empty)

    | WorkerMessage.CheckCode(code, replyId) ->
      let! diags =
        actor.PostAndAsyncReply(fun rc -> GetDiagnostics(code, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      return WorkerResponse.CheckResult(replyId, diags |> Array.map WorkerDiagnostic.ofDiagnostic |> Array.toList)

    | WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> GetTypeCheckWithSymbols(code, filePath, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Check
      let diags = result.Diagnostics |> Array.map WorkerDiagnostic.ofDiagnostic |> Array.toList
      let symbols = result.SymbolRefs |> List.map WorkerSymbolRef.fromDomain
      return WorkerResponse.TypeCheckWithSymbolsResult(replyId, diags, symbols)

    | WorkerMessage.GetCompletions(code, cursorPos, replyId) ->
      let! completions =
        actor.PostAndAsyncReply(fun rc -> Autocomplete(code, cursorPos, "", rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Completion
      return WorkerResponse.CompletionResult(replyId, completions |> List.map (fun c -> c.DisplayText))

    | WorkerMessage.CancelEval ->
      let! cancelled = actor.PostAndAsyncReply(fun rc -> CancelEval rc)
      return WorkerResponse.EvalCancelled cancelled

    | WorkerMessage.LoadScript(filePath, replyId) ->
      let request = { Code = sprintf "#load @\"%s\"" filePath; Args = Map.empty }
      use cts = new CancellationTokenSource(Timeouts.workerHttpRequest)
      let! response =
        actor.PostAndAsyncReply(fun rc -> Eval(request, cts.Token, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Repl
      let result = response.EvaluationResult |> Result.mapError (toWorkerError BozzettoError.ScriptLoadFailed)
      return WorkerResponse.ScriptLoaded(replyId, result)

    | WorkerMessage.ResetSession replyId ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> ResetSession rc)
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
      return WorkerResponse.ResetResult(replyId, result)

    | WorkerMessage.HardResetSession(rebuild, replyId) ->
      let! result =
        actor.PostAndAsyncReply(fun rc -> HardResetSession(rebuild, rc))
        |> Instrumentation.tracedActorPost Instrumentation.EvalCategory.Warmup
      return WorkerResponse.HardResetResult(replyId, result)

    | WorkerMessage.GetStatus replyId ->
      return WorkerResponse.StatusResult(replyId, toStatusSnapshot (getState ()) (getStats ()) (getStatusMessage ()) projects)

    | WorkerMessage.GetLiveValues replyId ->
      let! json = actor.PostAndAsyncReply(fun rc -> GetLiveValues rc)
      return WorkerResponse.LiveValuesResult(replyId, json)

    | WorkerMessage.Shutdown ->
      return WorkerResponse.WorkerShuttingDown

    | other ->
      return WorkerResponse.WorkerError (BozzettoError.EvalFailed (WorkerWire.notCarried other))
  }

let private enableStdoutAutoFlush () =
  try
    let stdout = new IO.StreamWriter(Console.OpenStandardOutput())
    stdout.AutoFlush <- true
    Console.SetOut stdout
  with ex ->
    Log.warn "[WorkerMain] Could not enable stdout autoflush: %s" ex.Message

/// Assemblies the worker itself needs at their own versions.
let private workerRuntimeCriticalAssemblyNames =
  set [
    "fsharp.core.dll"
    "fsharp.systemtextjson.dll"
    "mono.cecil.dll"
    "mono.cecil.rocks.dll"
    "mono.cecil.mdb.dll"
    "mono.cecil.pdb.dll"
  ]

let shouldQuarantineAssembly (assemblyName: string) =
  not (assemblyName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
  && not (workerRuntimeCriticalAssemblyNames |> Set.contains (assemblyName.ToLowerInvariant()))

/// The newest build output directory of each project.
let private projectBinDirs (projects: string list) =
  projects
  |> List.choose (fun projPath ->
    let binDir = IO.Path.Combine(IO.Path.GetDirectoryName(IO.Path.GetFullPath projPath), "bin")
    match IO.Directory.Exists binDir with
    | false -> None
    | true ->
      IO.Directory.EnumerateDirectories binDir
      |> Seq.sortByDescending IO.Directory.GetLastWriteTimeUtc
      |> Seq.tryHead
      |> Option.map IO.Path.GetFullPath)

/// Make native libraries in each project's `runtimes/<rid>/native/` resolvable
/// for P/Invoke from code loaded outside its build output (NativeResolution).
let private installNativeResolution (projects: string list) (binDirs: string list) =
  let roots =
    projects
    |> List.collect (fun projPath ->
      let binDir = IO.Path.Combine(IO.Path.GetDirectoryName(IO.Path.GetFullPath projPath), "bin")
      match IO.Directory.Exists binDir with
      | false -> []
      | true ->
        IO.Directory.EnumerateDirectories binDir
        |> Seq.collect IO.Directory.EnumerateDirectories
        |> Seq.map IO.Path.GetFullPath
        |> Seq.toList)
  NativeResolution.install (fun m -> Log.info "%s" m) (roots @ binDirs @ [ AppContext.BaseDirectory ])

/// The worker directory bundles Bozzetto's own dependency versions. When the
/// project ships a different version of the same assembly, FSI would bind the
/// worker's copy first and `#r` fails with 0x80131040. Move colliding,
/// non-critical worker copies aside before FSI starts.
let private quarantineCollisions (sessionId: string) (binDirs: string list) =
  match binDirs with
  | [] -> ()
  | dirs ->
    let projectBinNames =
      dirs
      |> List.collect (fun dir ->
        IO.Directory.EnumerateFiles(dir, "*.dll", IO.SearchOption.TopDirectoryOnly)
        |> Seq.map IO.Path.GetFileName
        |> Seq.toList)
      |> Set.ofList
    let baseDir = AppContext.BaseDirectory
    let quarantineDir = IO.Path.Combine(baseDir, "_bozzetto_quarantine")
    let quarantined =
      projectBinNames
      |> Seq.filter shouldQuarantineAssembly
      |> Seq.choose (fun name ->
        let own = IO.Path.Combine(baseDir, name)
        match IO.File.Exists own with
        | false -> None
        | true ->
          try
            IO.Directory.CreateDirectory quarantineDir |> ignore
            let dest = IO.Path.Combine(quarantineDir, name)
            if IO.File.Exists dest then IO.File.Delete dest
            IO.File.Move(own, dest)
            Some name
          with _ -> None)
      |> Seq.toList
    if not (List.isEmpty quarantined) then
      Log.info "Worker %s: quarantined %d worker-bundled assembly(ies) that collide with project deps: %s"
        sessionId quarantined.Length (String.concat ", " quarantined)

let run (sessionId: string) = async {
  enableStdoutAutoFlush ()
  let workerConfig = Args.WorkerConfig.fromEnvironment sessionId 0
  let loadConfig = Args.ProjectLoadConfig.fromWorkerConfig workerConfig
  let projects = SessionProjectTarget.paths loadConfig.Targets
  let binDirs = projectBinDirs projects
  installNativeResolution projects binDirs
  quarantineCollisions sessionId binDirs

  let logger =
    { new Utils.ILogger with
        member _.LogInfo msg = Log.info "%s" msg
        member _.LogDebug msg = Log.debug "%s" msg
        member _.LogWarning msg = Log.warn "%s" msg
        member _.LogError msg = Log.error "%s" msg }
  let onEvent (evt: Features.Events.BozzettoEvent) =
    match evt with
    | Features.Events.BozzettoEvent.SessionWarmUpProgress p ->
      match WarmUp.WarmupProgressLine.tryFormatLine p.Step p.Total p.Message with
      | Some line ->
        Console.Out.WriteLine line
        Console.Out.Flush()
      | None ->
        Log.warn "[WorkerMain] Skipping invalid warmup progress %d/%d %s" p.Step p.Total p.Message
    | _ -> ()

  // This process is the session's isolation boundary: FSI runs in-process
  // here, never in the daemon.
  let actorArgs : ActorCreation.ActorArgs = {
    Middleware = ActorCreation.commonMiddleware
    InitFunctions = ActorCreation.commonInitFunctions
    FsiKind = SessionKinds.InProcess
    Logger = logger
    OutStream = IO.TextWriter.Null
    UseAsp = false
    LoadConfig = loadConfig
    AutoOpenNamespaces = workerConfig.AutoOpenNamespaces
    OnEvent = onEvent
    Workflow = workerConfig.Workflow
  }

  let! result = ActorCreation.createActor actorArgs |> Async.AwaitTask
  let handler =
    handleMessage result.Actor result.GetSessionState result.GetEvalStats result.GetStatusMessage result.ProjectRoles

  use cts = new CancellationTokenSource()
  let stop () = try cts.Cancel() with :? ObjectDisposedException -> ()
  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    stop ())
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> stop ())

  // Parent-death watchdog, fenced on (pid, start time): a hard-killed daemon
  // sends no Shutdown, and the worker must not outlive it (issue #126).
  match workerConfig.DaemonPid with
  | Some daemonPid ->
    let owner : OwnerMonitor.Owner = { Pid = daemonPid; StartTimeTicks = workerConfig.DaemonStartTicks }
    Log.info "Worker %s monitoring daemon PID %d (fenced=%b)" sessionId daemonPid (Option.isSome workerConfig.DaemonStartTicks)
    Async.Start(OwnerMonitor.run OwnerMonitor.getProcessById owner cts (fun msg -> Log.warn "%s" msg))
  | None ->
    Log.debug "Worker %s has no daemon PID; parent-death watchdog disabled" sessionId

  let server = WorkerServer.start sessionId workerCoreVersion handler stop
  try
    Console.Out.WriteLine(SessionManager.WorkerSocketPrefix + server.SocketPath)
    Console.Out.Flush()
    let stopped = Threading.Tasks.TaskCompletionSource<unit>()
    use _registration = cts.Token.Register(fun () -> stopped.TrySetResult() |> ignore)
    do! stopped.Task |> Async.AwaitTask
  finally
    server.Stop ()
}
