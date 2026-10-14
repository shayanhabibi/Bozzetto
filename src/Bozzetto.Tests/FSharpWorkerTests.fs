/// The restored F# worker end to end: a real Bozzetto.Host process, driven only
/// through the daemon's WorkerSocketClient over the F# worker wire.
module Bozzetto.Tests.FSharpWorkerTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open Bozzetto
open Bozzetto.WorkerProtocol
open Bozzetto.Tests.TestInfrastructure

let private overrideWorker () =
  match Environment.GetEnvironmentVariable "BOZZETTO_TEST_FSHARP_WORKER" with
  | null | "" -> None
  | path -> Some path

/// The checkout: the first directory holding Bozzetto.slnx above the test
/// assembly, or above an overriding worker path.
let private repoRoot () =
  let rec root (dir: DirectoryInfo) =
    match dir with
    | null -> None
    | dir when File.Exists(Path.Combine(dir.FullName, "Bozzetto.slnx")) -> Some dir.FullName
    | dir -> root dir.Parent
  [ Some AppContext.BaseDirectory; overrideWorker () |> Option.map Path.GetDirectoryName ]
  |> List.choose id
  |> List.tryPick (DirectoryInfo >> root)
  |> Option.defaultWith (fun () -> failtest "F# worker tests: no Bozzetto.slnx above the test assembly; set BOZZETTO_TEST_FSHARP_WORKER")

/// The built worker: BOZZETTO_TEST_FSHARP_WORKER, else the newest
/// src/Bozzetto.Host build output in this checkout.
let private workerDll () =
  match overrideWorker () with
  | Some path -> path
  | None ->
    let hostBin = Path.Combine(repoRoot (), "src", "Bozzetto.Host", "bin")
    let candidates =
      match Directory.Exists hostBin with
      | false -> [||]
      | true -> Directory.GetFiles(hostBin, "Bozzetto.Host.dll", SearchOption.AllDirectories)
    match candidates |> Array.sortByDescending File.GetLastWriteTimeUtc |> Array.tryHead with
    | Some dll -> dll
    | None -> failtestf "F# worker tests: Bozzetto.Host is not built under %s" hostBin

type private RunningWorker = {
  Process: Process
  SessionId: string
  SocketPath: string
  Proxy: SessionProxy
}

/// Start a worker for the given session environment and wait for its socket.
let private startWorker (projects: string list) = async {
  let sessionId = (SessionId.newId () |> SessionId.value)
  let psi = ProcessStartInfo("dotnet")
  psi.ArgumentList.Add(workerDll ())
  psi.ArgumentList.Add sessionId
  psi.ArgumentList.Add "0"
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  match projects with
  | [] ->
    psi.Environment[Args.WorkerConfig.bareEnvVar] <- "1"
    psi.Environment[Args.WorkerConfig.envVar] <- ""
  | projects ->
    psi.Environment[Args.WorkerConfig.envVar] <- String.concat ";" projects
    psi.WorkingDirectory <- Path.GetDirectoryName(List.head projects)
  psi.Environment[Args.WorkerConfig.daemonPidEnvVar] <- string Environment.ProcessId
  let proc = Process.Start psi
  proc.ErrorDataReceived.Add(fun _ -> ())
  proc.BeginErrorReadLine()
  use deadline = new Threading.CancellationTokenSource(TimeSpan.FromMinutes 5.0)
  let rec awaitSocket () = async {
    let! line =
      async {
        try return! proc.StandardOutput.ReadLineAsync(deadline.Token).AsTask() |> Async.AwaitTask
        with :? OperationCanceledException -> return failtest "F# worker did not report its socket within 5 minutes"
      }
    match line with
    | null ->
      do! proc.WaitForExitAsync() |> Async.AwaitTask
      return failtestf "F# worker exited (code %d) before reporting its socket" proc.ExitCode
    | line when line.StartsWith(SessionManager.WorkerSocketPrefix, StringComparison.Ordinal) ->
      return line.Substring SessionManager.WorkerSocketPrefix.Length
    | _ -> return! awaitSocket ()
  }
  let! socketPath = awaitSocket ()
  // Keep draining so the worker never blocks on a full stdout pipe.
  Async.Start(async {
    try
      let mutable reading = true
      while reading do
        let! line = proc.StandardOutput.ReadLineAsync() |> Async.AwaitTask
        reading <- not (isNull line)
    with _ -> () })
  return { Process = proc; SessionId = sessionId; SocketPath = socketPath; Proxy = WorkerSocketClient.proxy sessionId socketPath }
}

let private ask (worker: RunningWorker) message = worker.Proxy message

let private evalOk worker code = async {
  match! ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Ok output, _, _) -> return output
  | other -> return failtestf "expected %s to evaluate, got %A" code other
}

let private evalError worker code = async {
  match! ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Error error, _, _) -> return BozzettoError.describe error
  | other -> return failtestf "expected %s to fail, got %A" code other
}

/// A refused compile reports its reasons as diagnostics beside the error.
let private evalDiagnostics worker code = async {
  match! ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Error _, diagnostics, _) -> return diagnostics |> List.map _.Message |> String.concat "\n"
  | other -> return failtestf "expected %s to be refused, got %A" code other
}

/// Whether a request raises instead of answering.
let private raises (request: Async<WorkerResponse>) = async {
  try
    let! _ = request
    return false
  with _ -> return true
}

let private stop (worker: RunningWorker) =
  try
    if not worker.Process.HasExited then worker.Process.Kill true
  with _ -> ()
  WorkerSocketClient.forget worker.SocketPath
  worker.Process.Dispose()

/// One worker serves the whole journey: warm-up is the expensive part.
let private journey = async {
  let! worker = startWorker []
  try
    match! ask worker (WorkerMessage.GetStatus "s") with
    | WorkerResponse.StatusResult ("s", snapshot) ->
      snapshot.Status |> Expect.equal "the worker reports Ready once it has named its socket" SessionStatus.Ready
      snapshot.CoreVersion |> Expect.isNotEmpty "the worker names the Core it loaded"
    | other -> failtestf "expected a status, got %A" other

    let! defined = evalOk worker "let answer = 40 + 2;;"
    defined |> Expect.stringContains "a definition reports its value" "42"
    let! used = evalOk worker "answer * 2;;"
    used |> Expect.stringContains "state persists between evals" "84"
    let! printed = evalOk worker "printfn \"to stdout\";;"
    printed |> Expect.stringContains "stdout is captured" "to stdout"

    let! thrown = evalError worker "failwith<int> \"boom\";;"
    thrown |> Expect.stringContains "a runtime failure names its cause" "boom"
    let! mistyped = evalDiagnostics worker "let broken : int = \"text\";;"
    mistyped |> Expect.stringContains "a type error is refused with its diagnostic" "string"

    match! ask worker (WorkerMessage.CheckCode ("let checkedOnly : int = \"text\"", "c")) with
    | WorkerResponse.CheckResult ("c", diagnostics) ->
      diagnostics
      |> List.exists (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking)
      |> Expect.isTrue "check reports the type error without evaluating"
    | other -> failtestf "expected a check result, got %A" other
    let! unchecked = evalDiagnostics worker "checkedOnly;;"
    unchecked |> Expect.stringContains "check did not define anything" "checkedOnly"

    match! ask worker (WorkerMessage.GetCompletions ("System.Console.Wr", 17, "k")) with
    | WorkerResponse.CompletionResult ("k", names) ->
      names |> Expect.contains "completions come from the session's checker" "WriteLine"
    | other -> failtestf "expected completions, got %A" other

    // Cancel reaches a running eval: the request is served concurrently.
    let! longEval = ask worker (WorkerMessage.EvalCode ("System.Threading.Thread.Sleep 120000;;", "long")) |> Async.StartChild
    do! Async.Sleep 1500
    let started = Stopwatch.StartNew()
    match! ask worker WorkerMessage.CancelEval with
    | WorkerResponse.EvalCancelled wasRunning -> wasRunning |> Expect.isTrue "cancel found the running eval"
    | other -> failtestf "expected EvalCancelled, got %A" other
    let! _cancelled = longEval
    started.Elapsed < TimeSpan.FromSeconds 60.0 |> Expect.isTrue "the cancelled eval returns well before the sleep would end"
    let! survived = evalOk worker "answer;;"
    survived |> Expect.stringContains "the session survives a cancel" "42"

    match! ask worker (WorkerMessage.ResetSession "r") with
    | WorkerResponse.ResetResult ("r", Ok ()) -> ()
    | other -> failtestf "expected a reset, got %A" other
    let! forgotten = evalDiagnostics worker "answer;;"
    forgotten |> Expect.stringContains "reset forgets definitions" "answer"
    let! fresh = evalOk worker "1 + 1;;"
    fresh |> Expect.stringContains "the reset session evaluates" "2"

    // A connection that names the wrong session is refused at Hello.
    let impostor = WorkerSocketClient.proxy "ffffffff" worker.SocketPath
    let! refused = raises (impostor (WorkerMessage.GetStatus "x"))
    refused |> Expect.isTrue "Hello binds the session"

    match! ask worker WorkerMessage.Shutdown with
    | WorkerResponse.WorkerShuttingDown -> ()
    | other -> failtestf "expected WorkerShuttingDown, got %A" other
    use exitDeadline = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 30.0)
    do! worker.Process.WaitForExitAsync(exitDeadline.Token) |> Async.AwaitTask
    Directory.Exists(Path.GetDirectoryName worker.SocketPath) |> Expect.isFalse "the worker removes its private socket directory"
  finally
    stop worker
}

/// A project target is evaluated through MSBuild and its compiled IL is what
/// the session runs. The fixture uses the SDK's implicit FSharp.Core, the
/// configuration of every ordinary project (SageFs #141).
let private projectJourney = async {
  let fixture = Path.Combine(repoRoot (), "src", "Bozzetto.Tests", "fixtures", "FSharpCoreIdentityFixture", "FSharpCoreIdentityFixture.fsproj")
  match Directory.Exists(Path.Combine(Path.GetDirectoryName fixture, "bin")) with
  | false -> failtestf "F# worker tests: build %s first (Bozzetto.Tests references it)" fixture
  | true -> ()
  let! worker = startWorker [ fixture ]
  try
    match! ask worker (WorkerMessage.GetStatus "s") with
    | WorkerResponse.StatusResult ("s", snapshot) ->
      snapshot.Status |> Expect.equal "the project session is Ready" SessionStatus.Ready
      snapshot.Projects
      |> List.map (fun p -> Path.GetFileName p.Path)
      |> Expect.contains "the worker reports the project it loaded" "FSharpCoreIdentityFixture.fsproj"
    | other -> failtestf "expected a status, got %A" other
    let! ran = evalOk worker "Repro.useInTask ();;"
    ran |> Expect.stringContains "the project's compiled code runs" "1"
    // Sessions load shadow copies so a rebuild never meets a locked DLL.
    let! loaded = evalOk worker "System.IO.File.Exists(System.IO.Path.Combine(Repro.assemblyDirectory (), \"FSharpCoreIdentityFixture.dll\"));;"
    loaded |> Expect.stringContains "the loaded assembly is a copy of the fixture's build" "true"
  finally
    stop worker
}

[<Tests>]
let tests =
  Integration.hostList "F# worker" [
    testAsync "a bare worker evaluates, checks, completes, cancels, resets and shuts down" { do! journey }
    testAsync "a project worker loads the project and runs its compiled code" { do! projectJourney }
  ]
