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

/// Start a worker for the given session environment.
let private startWorker (projects: string list) =
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
  let deadline = DateTime.UtcNow.AddMinutes 5.0
  let rec awaitSocket () =
    if DateTime.UtcNow > deadline then failtest "F# worker did not report its socket within 5 minutes"
    match proc.StandardOutput.ReadLine() with
    | null -> failtestf "F# worker exited (code %d) before reporting its socket" (proc.WaitForExit 5000 |> ignore; proc.ExitCode)
    | line when line.StartsWith(SessionManager.WorkerSocketPrefix, StringComparison.Ordinal) ->
      line.Substring SessionManager.WorkerSocketPrefix.Length
    | _ -> awaitSocket ()
  let socketPath = awaitSocket ()
  // Keep draining so the worker never blocks on a full stdout pipe.
  Async.Start(async { try while not (isNull (proc.StandardOutput.ReadLine())) do () with _ -> () })
  { Process = proc; SessionId = sessionId; SocketPath = socketPath; Proxy = WorkerSocketClient.proxy sessionId socketPath }

let private ask (worker: RunningWorker) message =
  worker.Proxy message |> Async.RunSynchronously

let private evalOk worker code =
  match ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Ok output, _, _) -> output
  | other -> failtestf "expected %s to evaluate, got %A" code other

let private evalError worker code =
  match ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Error error, _, _) -> BozzettoError.describe error
  | other -> failtestf "expected %s to fail, got %A" code other

/// A refused compile reports its reasons as diagnostics beside the error.
let private evalDiagnostics worker code =
  match ask worker (WorkerMessage.EvalCode (code, "e")) with
  | WorkerResponse.EvalResult ("e", Error _, diagnostics, _) -> diagnostics |> List.map _.Message |> String.concat "
"
  | other -> failtestf "expected %s to be refused, got %A" code other

let private stop (worker: RunningWorker) =
  try
    if not worker.Process.HasExited then worker.Process.Kill true
  with _ -> ()
  WorkerSocketClient.forget worker.SocketPath
  worker.Process.Dispose()

/// One worker serves the whole journey: warm-up is the expensive part.
let private journey () =
  let worker = startWorker []
  try
    match ask worker (WorkerMessage.GetStatus "s") with
    | WorkerResponse.StatusResult ("s", snapshot) ->
      snapshot.Status |> Expect.equal "the worker reports Ready once it has named its socket" SessionStatus.Ready
      snapshot.CoreVersion |> Expect.isNotEmpty "the worker names the Core it loaded"
    | other -> failtestf "expected a status, got %A" other

    evalOk worker "let answer = 40 + 2;;" |> Expect.stringContains "a definition reports its value" "42"
    evalOk worker "answer * 2;;" |> Expect.stringContains "state persists between evals" "84"
    evalOk worker "printfn \"to stdout\";;" |> Expect.stringContains "stdout is captured" "to stdout"

    evalError worker "failwith<int> \"boom\";;" |> Expect.stringContains "a runtime failure names its cause" "boom"
    evalDiagnostics worker "let broken : int = \"text\";;" |> Expect.stringContains "a type error is refused with its diagnostic" "string"

    match ask worker (WorkerMessage.CheckCode ("let checkedOnly : int = \"text\"", "c")) with
    | WorkerResponse.CheckResult ("c", diagnostics) ->
      diagnostics
      |> List.exists (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking)
      |> Expect.isTrue "check reports the type error without evaluating"
    | other -> failtestf "expected a check result, got %A" other
    evalDiagnostics worker "checkedOnly;;" |> Expect.stringContains "check did not define anything" "checkedOnly"

    match ask worker (WorkerMessage.GetCompletions ("System.Console.Wr", 17, "k")) with
    | WorkerResponse.CompletionResult ("k", names) ->
      names |> Expect.contains "completions come from the session's checker" "WriteLine"
    | other -> failtestf "expected completions, got %A" other

    // Cancel reaches a running eval: the request is served concurrently.
    let longEval = worker.Proxy (WorkerMessage.EvalCode ("System.Threading.Thread.Sleep 120000;;", "long")) |> Async.StartAsTask
    Threading.Thread.Sleep 1500
    let started = Stopwatch.StartNew()
    match ask worker WorkerMessage.CancelEval with
    | WorkerResponse.EvalCancelled wasRunning -> wasRunning |> Expect.isTrue "cancel found the running eval"
    | other -> failtestf "expected EvalCancelled, got %A" other
    longEval.Wait(TimeSpan.FromSeconds 30.0) |> Expect.isTrue "the cancelled eval returns promptly"
    started.Elapsed < TimeSpan.FromSeconds 60.0 |> Expect.isTrue "well before the sleep would end"
    evalOk worker "answer;;" |> Expect.stringContains "the session survives a cancel" "42"

    match ask worker (WorkerMessage.ResetSession "r") with
    | WorkerResponse.ResetResult ("r", Ok ()) -> ()
    | other -> failtestf "expected a reset, got %A" other
    evalDiagnostics worker "answer;;" |> Expect.stringContains "reset forgets definitions" "answer"
    evalOk worker "1 + 1;;" |> Expect.stringContains "the reset session evaluates" "2"

    // A connection that names the wrong session is refused at Hello.
    let impostor = WorkerSocketClient.proxy "ffffffff" worker.SocketPath
    Expect.throws "Hello binds the session" (fun () -> impostor (WorkerMessage.GetStatus "x") |> Async.RunSynchronously |> ignore)

    match ask worker WorkerMessage.Shutdown with
    | WorkerResponse.WorkerShuttingDown -> ()
    | other -> failtestf "expected WorkerShuttingDown, got %A" other
    worker.Process.WaitForExit 30000 |> Expect.isTrue "the worker exits after Shutdown"
    Directory.Exists(Path.GetDirectoryName worker.SocketPath) |> Expect.isFalse "the worker removes its private socket directory"
  finally
    stop worker

/// A project target is evaluated through MSBuild and its compiled IL is what
/// the session runs. The fixture uses the SDK's implicit FSharp.Core, the
/// configuration of every ordinary project (SageFs #141).
let private projectJourney () =
  let fixture = Path.Combine(repoRoot (), "src", "Bozzetto.Tests", "fixtures", "FSharpCoreIdentityFixture", "FSharpCoreIdentityFixture.fsproj")
  match Directory.Exists(Path.Combine(Path.GetDirectoryName fixture, "bin")) with
  | false -> failtestf "F# worker tests: build %s first (Bozzetto.Tests references it)" fixture
  | true -> ()
  let worker = startWorker [ fixture ]
  try
    match ask worker (WorkerMessage.GetStatus "s") with
    | WorkerResponse.StatusResult ("s", snapshot) ->
      snapshot.Status |> Expect.equal "the project session is Ready" SessionStatus.Ready
      snapshot.Projects
      |> List.map (fun p -> Path.GetFileName p.Path)
      |> Expect.contains "the worker reports the project it loaded" "FSharpCoreIdentityFixture.fsproj"
    | other -> failtestf "expected a status, got %A" other
    evalOk worker "Repro.useInTask ();;" |> Expect.stringContains "the project's compiled code runs" "1"
    // Sessions load shadow copies so a rebuild never meets a locked DLL.
    evalOk worker "System.IO.File.Exists(System.IO.Path.Combine(Repro.assemblyDirectory (), \"FSharpCoreIdentityFixture.dll\"));;"
    |> Expect.stringContains "the loaded assembly is a copy of the fixture's build" "true"
  finally
    stop worker

[<Tests>]
let tests =
  Integration.hostList "F# worker" [
    testCase "a bare worker evaluates, checks, completes, cancels, resets and shuts down" journey
    testCase "a project worker loads the project and runs its compiled code" projectJourney
  ]
