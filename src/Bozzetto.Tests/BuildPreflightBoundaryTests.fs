module Bozzetto.Tests.BuildPreflightBoundaryTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Bozzetto
open Bozzetto.WorkerProtocol
open Bozzetto.Server.DaemonMode

let private refused<'T> () : Result<'T, BozzettoError> =
  Error (BozzettoError.SessionCreationFailed ExternalFSharpService.message)

/// A stand-in supervisor: records every command that reaches it, answers a
/// create with a fixed refusal (so no manifest work follows) and a restart
/// with a fixed success.
type private Supervisor() =
  let received = System.Collections.Concurrent.ConcurrentQueue<SessionManager.SessionCommand>()
  let mailbox =
    MailboxProcessor.Start(fun inbox ->
      let rec loop () = async {
        let! command = inbox.Receive()
        received.Enqueue command
        match command with
        | SessionManager.SessionCommand.CreateSession (_, _, _, _, reply) ->
          reply.Reply(Error (BozzettoError.SessionCreationFailed "supervisor probe"))
        | SessionManager.SessionCommand.RestartSession (_, _, reply) -> reply.Reply(Ok "restarted")
        | _ -> ()
        return! loop () }
      loop ())
  member _.Mailbox = mailbox
  member _.Received = List.ofSeq received

let private emptySnapshot () = SessionManager.QuerySnapshot.empty

let private makeOps (supervisor: Supervisor) recover =
  createSessionOpsWithRecovery supervisor.Mailbox emptySnapshot Unchecked.defaultof<_> recover

let private missingProject = SessionProjectTarget.Project "/missing/Project.fsproj"

[<Tests>]
let tests =
  testSequenced <| testList "F# session admission" [
    testTask "a project whose build outputs are missing is refused as NeedsRebuild before the supervisor" {
      let supervisor = Supervisor()
      let! result = (makeOps supervisor None).CreateSession [ missingProject ] "/missing" WorkflowTypes.SessionWorkflow.Interactive
      match result with
      | Error (BozzettoError.NeedsRebuild missing) ->
        missing |> Expect.contains "names the missing project" (Path.GetFullPath "/missing/Project.fsproj")
      | other -> failtestf "expected NeedsRebuild, got %A" other
      supervisor.Received |> Expect.isEmpty "a refused create never reaches the supervisor"
    }

    testTask "recovery runs once and an output still missing afterwards is still refused" {
      let supervisor = Supervisor()
      let recoveries = ref 0
      let recover = fun _ _ -> task { recoveries.Value <- recoveries.Value + 1; return Ok () }
      let! result = (makeOps supervisor (Some recover)).CreateSession [ missingProject ] "/missing" WorkflowTypes.SessionWorkflow.Interactive
      match result with
      | Error (BozzettoError.NeedsRebuild _) -> ()
      | other -> failtestf "expected NeedsRebuild after recovery, got %A" other
      recoveries.Value |> Expect.equal "recovery is attempted exactly once" 1
      supervisor.Received |> Expect.isEmpty "a still-refused create never reaches the supervisor"
    }

    testTask "a ready create reaches the supervisor with its target" {
      let supervisor = Supervisor()
      let! result = (makeOps supervisor None).CreateSession [ SessionProjectTarget.Bare ] (Path.GetTempPath()) WorkflowTypes.SessionWorkflow.Interactive
      result |> Expect.equal "the supervisor's answer is the caller's answer" (Error (BozzettoError.SessionCreationFailed "supervisor probe"))
      match supervisor.Received with
      | [ SessionManager.SessionCommand.CreateSession (targets, _, _, _, _) ] ->
        targets |> Expect.equal "the target crosses unchanged" [ SessionProjectTarget.Bare ]
      | other -> failtestf "expected one CreateSession, got %A" other
    }

    testTask "restart reaches the supervisor" {
      let supervisor = Supervisor()
      let! result = (makeOps supervisor None).RestartSession (SessionId.newId()) true
      result |> Expect.equal "the supervisor answers the restart" (Ok "restarted")
    }

    testTask "Elm creation and restart reach the supervisor" {
      let supervisor = Supervisor()
      let deps =
        Bozzetto.ElmDaemon.createEffectDeps
          supervisor.Mailbox
          emptySnapshot
          (fun _ -> true)
          (fun _ -> failwith "creating a session must not write configuration")
      let! created =
        deps.CreateSession [ SessionProjectTarget.Bare ] "/missing" WorkflowTypes.SessionWorkflow.Interactive
        |> Async.StartAsTask
      created |> Expect.equal "Elm create is answered by the supervisor" (Error (BozzettoError.SessionCreationFailed "supervisor probe"))
      let! restarted = deps.RestartSession (SessionId.newId()) true |> Async.StartAsTask
      restarted |> Expect.equal "Elm restart is answered by the supervisor" (Ok "restarted")
    }

    testTask "a proxy is handed out only for a session whose worker published its socket" {
      let supervisor = Supervisor()
      let ready = SessionId.newId()
      let starting = SessionId.newId()
      let snapshot () =
        { SessionManager.QuerySnapshot.empty with
            WorkerBaseUrls = Map.ofList [ ready, "/run/boz-fsw/worker.sock" ] }
      let ops = createSessionOpsWithRecovery supervisor.Mailbox snapshot Unchecked.defaultof<_> None
      let! readyProxy = ops.GetProxy ready
      readyProxy |> Expect.isSome "a published socket routes"
      let! startingProxy = ops.GetProxy starting
      startingProxy |> Expect.isNone "a session without a socket has no route"
      let deps = Bozzetto.ElmDaemon.createEffectDeps supervisor.Mailbox snapshot (fun _ -> true) (fun _ -> Ok Unchecked.defaultof<_>)
      deps.GetProxy ready |> Expect.isSome "Elm routes the same published socket"
      deps.GetProxy starting |> Expect.isNone "Elm has no route without a socket"
    }

    testTask "cohort integration refuses before changing its binding or provisioning a worktree" {
      let previous = McpCohortIntegration.cohortIntegrationRef.Value
      let expected : McpCohortIntegration.CohortIntegrationBinding =
        { WorktreePath = "/preserved/worktree"
          Branch = "preserved-branch"
          Session = McpCohortIntegration.IntegrationSession.Pending }
      try
        McpCohortIntegration.cohortIntegrationRef.Value <- Some expected
        let! result = McpCohortIntegration.setIntegrationRef Unchecked.defaultof<_> "test" "HEAD"
        result |> Expect.equal "FSharp integration provisioning is refused before accessing context" (refused ())
        McpCohortIntegration.cohortIntegrationRef.Value
        |> Expect.equal "the existing binding remains untouched" (Some expected)
      finally
        McpCohortIntegration.cohortIntegrationRef.Value <- previous
    }
  ]

[<Tests>]
let cliEntryTests =
  testList "Retired FSharp CLI entry point" [
    testTask "Jupyter exits 2 with the provider refusal before inspecting a missing connection file" {
      return! task {
        let output = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
        let framework = Path.GetFileName output
        let configuration = Directory.GetParent(output).Name
        let cli = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Bozzetto", "bin", configuration, framework, "Bozzetto.dll"))
        File.Exists cli |> Expect.isTrue (sprintf "the CLI output for this test configuration must exist: %s" cli)
        let connectionFile = Path.Combine(output, sprintf "absent-jupyter-%s.json" (Guid.NewGuid().ToString("N")))
        File.Exists connectionFile |> Expect.isFalse "the connection file deliberately does not exist"
        let start = ProcessStartInfo("dotnet")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add cli
        start.ArgumentList.Add "--jupyter"
        start.ArgumentList.Add connectionFile
        use proc = Process.Start start
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
        let stdout = proc.StandardOutput.ReadToEndAsync(deadline.Token)
        let stderr = proc.StandardError.ReadToEndAsync(deadline.Token)
        try
          do! proc.WaitForExitAsync(deadline.Token)
          let! (out: string) = stdout
          let! (error: string) = stderr
          proc.ExitCode |> Expect.equal "retired Jupyter execution is refused" 2
          error.Trim() |> Expect.equal "one actionable refusal" ExternalFSharpService.message
          out |> Expect.equal "the retired entry point cannot start a kernel or daemon" ""
          File.Exists connectionFile |> Expect.isFalse "the retired entry point preserves the absent connection file"
        finally
          if not proc.HasExited then proc.Kill(true)
      }
    }
  ]
