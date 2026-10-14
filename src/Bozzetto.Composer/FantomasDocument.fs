// Adapted from Calque.Incremental/DocumentFormatter.fs at
// d0f614329aa6b54eb54a9d9b080fc8fd9f23e794; modified to use Fantomas.Core.
// Copyright (c) 2013 Anh-Dung Phan. Licensed under Apache-2.0.
// https://www.apache.org/licenses/LICENSE-2.0

namespace Bozzetto.Providers.Formatting

open System
open System.Collections.Generic
open Fantomas.Core
open Fidelity.FSharp.Incremental
open Fidelity.FSharp.Incremental.Hosting

/// <summary>A document label and its immutable open incarnation.</summary>
type DocumentIdentity = { Uri: string; Incarnation: Guid }

/// <summary>Complete immutable source and configuration identity for a formatting preview.</summary>
[<NoComparison>]
type SourceSnapshot = {
  Document: DocumentIdentity
  Revision: uint64
  ConfigurationRevision: uint64
  IsSignature: bool
  Source: string
  Config: FormatConfig
}

/// <summary>Document epoch and positive limits for commands and independently released demands.</summary>
type Settings = { Epoch: uint64; CommandCapacity: int; MaxDemands: int }
/// <summary>A formatter refusal with a stable machine-readable code.</summary>
type Diagnostic = { Code: string; Message: string }

/// <summary>Formatted code or a source-specific formatter refusal.</summary>
[<RequireQualifiedAccess; NoComparison>]
type FormatOutcome =
  | Formatted of FormatResult
  | Refused of Diagnostic

/// <summary>The formatter outcome bound to its exact immutable input.</summary>
[<NoComparison>]
type Preview = { Snapshot: SourceSnapshot; Outcome: FormatOutcome }

/// <summary>Admission, observation and physical host cleanup failures.</summary>
[<RequireQualifiedAccess>]
type FormatterError =
  | InvalidSettings of string
  | InvalidSnapshot of string
  | ForeignDocument
  | ConflictingSnapshot
  | RevisionNotIncreasing
  | DemandCapacity
  | NotStarted
  | Closed
  | Superseded
  | Released
  | Cancelled
  | Foundation of MailboxError
  | HostFailure of Failure

/// <summary>Typed document projection over shared foundation demand and work ownership.</summary>
[<RequireQualifiedAccess>]
module DocumentFormatter =
  [<NoEquality; NoComparison>]
  type private Current = {
    Snapshot: SourceSnapshot
    Generation: uint64
    Input: ValueToken
    mutable Outcome: (ValueToken * Preview) option
    mutable Admitted: bool
  }

  /// <summary>An admitted demand whose observation and release are independent.</summary>
  [<NoEquality; NoComparison>]
  type Request = private {
    Owner: Guid
    Snapshot: SourceSnapshot
    Generation: uint64
    Demand: DemandId
    Operations: AsyncMailbox.Operation list
    AdmissionError: FormatterError option
    mutable Released: bool
  }

  /// <summary>Retained acknowledgements for a demand release or scope withdrawal.</summary>
  [<NoEquality; NoComparison>]
  type ControlOperation = private {
    Operations: AsyncMailbox.Operation list
    AdmissionError: FormatterError option
  }

  [<NoEquality; NoComparison>]
  type private Store = {
    Gate: obj
    mutable Current: Current option
    mutable Last: SourceSnapshot option
    mutable Generation: uint64
    mutable NextDemand: uint64
    mutable Started: bool
    mutable Closing: bool
    Demands: Dictionary<DemandId, Request>
  }

  /// <summary>A cold or explicitly started document coordinator with owned cleanup.</summary>
  [<NoEquality; NoComparison>]
  type Handle = private {
    Owner: Guid
    Document: DocumentIdentity
    Settings: Settings
    Store: Store
    Host: AsyncMailbox.Handle
  }

  /// <summary>A retained join for a sealed document and its physical work.</summary>
  [<NoEquality; NoComparison>]
  type CloseOperation = private { Store: Store; Operation: AsyncMailbox.CloseOperation }

  let private scope, work, input = ScopeId 1UL, WorkId 1UL, InputId 1UL

  let private foundation error = FormatterError.Foundation error

  let private collect (handle: Handle) =
    // The adapter is the single observation owner. Results/refusals live in the
    // typed current slot, so shared-host event buffers need no historical copy.
    AsyncMailbox.drainEvents handle.Host |> ignore

  let private operations actions (handle: Handle) =
    let rec admit retained = function
      | [] -> List.rev retained, None
      | action :: remaining ->
        match AsyncMailbox.admit action handle.Host with
        | Ok operation -> admit (operation :: retained) remaining
        | Error error -> List.rev retained, Some (foundation error)
    admit [] actions

  let private observeOperations retained admissionError = async {
    let rec loop = function
      | [] -> async { return admissionError |> Option.map Error |> Option.defaultValue (Ok ()) }
      | operation :: remaining -> async {
          let! answer = AsyncMailbox.observe operation
          match answer with
          | Error error -> return Error (foundation error)
          | Ok _ -> return! loop remaining
        }
    return! loop retained
  }

  let private createWithOutcome (settings: Settings) (document: DocumentIdentity) evaluate =
    if settings.CommandCapacity < 6 then
      Error (FormatterError.InvalidSettings "CommandCapacity must leave room for four input commands and two controls.")
    elif settings.MaxDemands < 1 then
      Error (FormatterError.InvalidSettings "MaxDemands must be positive.")
    elif String.IsNullOrWhiteSpace document.Uri || document.Incarnation = Guid.Empty then
      Error (FormatterError.InvalidSnapshot "A document URI and nonempty incarnation are required.")
    else
      let store: Store = {
        Gate = obj (); Current = None; Last = None; Generation = 0UL; NextDemand = 0UL
        Started = false; Closing = false; Demands = Dictionary()
      }
      let evaluator invocation cancellation = async {
        let snapshot =
          match invocation with
          | StepInvocation.Start request ->
            lock store.Gate (fun () ->
              match request.Reads, store.Current with
              | [{ Value = ReadValue.Input(_, _, token) }], Some current when current.Input = token && not store.Closing ->
                Some (current.Generation, current.Snapshot)
              | _ -> None)
          | StepInvocation.Resume _ -> None
        match snapshot with
        | None -> return StepOutcome.Complete Completion.Cancelled
        | Some _ when WorkCancellation.isRequested cancellation -> return StepOutcome.Complete Completion.Cancelled
        | Some (generation, source) ->
          // The evaluator joins its branches before returning a result or stop.
          let! outcome = evaluate source cancellation
          let completion =
            lock store.Gate (fun () ->
              match outcome, store.Current with
              | Some outcome, Some current when current.Generation = generation && not store.Closing && not (WorkCancellation.isRequested cancellation) ->
                let token = ValueToken (generation * 2UL + 1UL)
                current.Outcome <- Some (token, { Snapshot = source; Outcome = outcome })
                Completion.Succeeded token
              | _ -> Completion.Cancelled)
          return StepOutcome.Complete completion
      }
      let hostSettings: AsyncMailbox.Settings = {
        Epoch = EpochId settings.Epoch; MaxConcurrency = 1; CommandCapacity = settings.CommandCapacity
      }
      match AsyncMailbox.create hostSettings evaluator with
      | Error error -> Error (FormatterError.InvalidSettings (sprintf "%A" error))
      | Ok host -> Ok { Owner = Guid.NewGuid(); Document = document; Settings = settings; Store = store; Host = host }

  /// <summary>Creates a cold document with an injected owned evaluator.</summary>
  let internal createWith settings document evaluate =
    createWithOutcome settings document (fun source cancellation -> async {
      let! result = evaluate source cancellation
      return Some result
    })

  /// <summary>Creates a cold document using Fantomas and bounded shared demand.</summary>
  /// <remarks>Cancellation withdraws eligibility; physical formatting joins before cleanup completes.</remarks>
  let create settings document =
    let evaluate (snapshot: SourceSnapshot) cancellation = async {
      if WorkCancellation.isRequested cancellation then return None
      else
        try
          let! result = CodeFormatter.FormatDocumentAsync(snapshot.IsSignature, snapshot.Source, snapshot.Config)
          return
            if WorkCancellation.isRequested cancellation then None
            else Some (FormatOutcome.Formatted result)
        with
        | :? Fantomas.Core.ParseException as error ->
          return Some (FormatOutcome.Refused { Code = "FANTOMAS_PARSE"; Message = error.Message })
        | :? Fantomas.Core.FormatException as error ->
          return Some (FormatOutcome.Refused { Code = "FANTOMAS_FORMAT"; Message = error.Message })
    }
    createWithOutcome settings document evaluate

  /// <summary>Starts the coordinator once; closed handles refuse to restart.</summary>
  let start (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      if handle.Store.Closing then Error FormatterError.Closed
      else
        match AsyncMailbox.start handle.Host with
        | Error error -> Error (foundation error)
        | Ok () -> handle.Store.Started <- true; Ok ())

  let private validate (snapshot: SourceSnapshot) (handle: Handle) =
    if snapshot.Document <> handle.Document then Error FormatterError.ForeignDocument
    elif isNull snapshot.Source then Error (FormatterError.InvalidSnapshot "Source cannot be null.")
    else
      match handle.Store.Last with
      | Some last when snapshot.Revision < last.Revision || snapshot.ConfigurationRevision < last.ConfigurationRevision ->
        Error FormatterError.RevisionNotIncreasing
      | Some last when
          (snapshot.Revision = last.Revision && (snapshot.Source <> last.Source || snapshot.IsSignature <> last.IsSignature))
          || (snapshot.ConfigurationRevision = last.ConfigurationRevision && snapshot.Config <> last.Config) ->
        Error FormatterError.ConflictingSnapshot
      | _ -> Ok ()

  /// <summary>Admits a demand immediately and fences older snapshots before returning.</summary>
  let request snapshot (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      let store = handle.Store
      collect handle
      if store.Closing then Error FormatterError.Closed
      elif not store.Started then Error FormatterError.NotStarted
      elif store.Demands.Count >= handle.Settings.MaxDemands then Error FormatterError.DemandCapacity
      elif store.Generation >= UInt64.MaxValue / 2UL || store.NextDemand = UInt64.MaxValue then
        Error (FormatterError.InvalidSettings "Document identities are exhausted; close this host.")
      else
        match validate snapshot handle with
        | Error error -> Error error
        | Ok () ->
          let shared = store.Current |> Option.exists (fun current -> current.Snapshot = snapshot && current.Admitted)
          let commandCount = if shared then 1 else 4
          // This handle exclusively owns admission. Reserve ordinary capacity
          // for controls before changing the current snapshot or queueing a batch.
          if (AsyncMailbox.snapshot handle.Host).QueuedCommands > handle.Settings.CommandCapacity - commandCount - 2 then
            Error (foundation MailboxError.QueueFull)
          else
            store.NextDemand <- store.NextDemand + 1UL
            let demand = DemandId store.NextDemand
            let generation, actions =
              if shared then
                let current = store.Current.Value
                current.Generation, [Action.Demand(demand, work)]
              else
                store.Generation <- store.Generation + 1UL
                let generation = store.Generation
                let token = ValueToken (generation * 2UL)
                store.Last <- Some snapshot
                store.Current <- Some {
                  Snapshot = snapshot; Generation = generation; Input = token; Outcome = None; Admitted = false
                }
                generation, [
                  Action.ReserveScope(scope, RevisionId generation)
                  Action.SetInputs [{ Input = input; Stamp = InputStamp generation; Value = token }]
                  Action.ReplaceScope(scope, RevisionId generation, [ScopeEntry.Define {
                    Work = work; Stamp = DefinitionStamp generation
                    Reads = [{ Slot = ReadSlotId 1UL; Source = ReadSource.Input input }]
                  }])
                  Action.Demand(demand, work)
                ]
            let retained, admissionError = operations actions handle
            store.Current |> Option.iter (fun current -> current.Admitted <- admissionError.IsNone)
            let accepted = {
              Owner = handle.Owner; Snapshot = snapshot; Generation = generation; Demand = demand
              Operations = retained; AdmissionError = admissionError; Released = false
            }
            store.Demands.Add(demand, accepted)
            Ok accepted)

  let private check (request: Request) (handle: Handle) =
    if request.Owner <> handle.Owner then Error FormatterError.ForeignDocument
    elif handle.Store.Closing then Error FormatterError.Closed
    elif request.Released then Error FormatterError.Released
    else
      match handle.Store.Current with
      | Some current when current.Generation = request.Generation && current.Snapshot = request.Snapshot -> Ok current
      | _ -> Error FormatterError.Superseded

  /// <summary>Observes the exact admitted demand; observer cancellation leaves demand owned.</summary>
  let observe (request: Request) (handle: Handle) = async {
    if request.Owner <> handle.Owner then return Error FormatterError.ForeignDocument
    else
      let! admitted = observeOperations request.Operations request.AdmissionError
      match admitted with
      | Error error -> return Error error
      | Ok () ->
        let rec currentResult () = async {
          let state, changed = AsyncMailbox.watch handle.Host
          let answer = lock handle.Store.Gate (fun () ->
            collect handle
            match check request handle with
            | Error error -> Some (Error error)
            | Ok current ->
              match AsyncMailbox.tryResult work handle.Host, current.Outcome with
              | Some result, Some (token, preview) when result.Value = token && AsyncMailbox.isEligible result handle.Host ->
                Some (Ok preview)
              | _ ->
                state.Graph.Works
                |> List.tryFind (fun item -> item.Work = work)
                |> Option.bind (fun item ->
                  match item.Status with
                  | WorkStatus.Failed failure -> Some (Error (FormatterError.HostFailure failure))
                  | WorkStatus.Cancelled -> Some (Error FormatterError.Cancelled)
                  | _ -> None))
          match answer with
          | Some result -> return result
          | None when state.IsClosing -> return Error FormatterError.Closed
          | None ->
            do! changed
            return! currentResult ()
        }
        return! currentResult ()
  }

  /// <summary>Withdraws a demand immediately and retains its foundation acknowledgement.</summary>
  let release (request: Request) (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      if request.Owner <> handle.Owner then Error FormatterError.ForeignDocument
      elif handle.Store.Closing then Error FormatterError.Closed
      else
        request.Released <- true
        let retained, admissionError = operations [Action.Release request.Demand] handle
        if admissionError.IsNone then handle.Store.Demands.Remove request.Demand |> ignore
        collect handle
        Ok { Operations = retained; AdmissionError = admissionError })

  /// <summary>Withdraws current presentation eligibility and retains scope reservation acknowledgement.</summary>
  let cancelCurrent (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      if handle.Store.Closing then Error FormatterError.Closed
      elif not handle.Store.Started then Error FormatterError.NotStarted
      elif handle.Store.Generation >= UInt64.MaxValue / 2UL then
        Error (FormatterError.InvalidSettings "Document identities are exhausted; close this host.")
      else
        handle.Store.Current <- None
        handle.Store.Generation <- handle.Store.Generation + 1UL
        let retained, admissionError = operations [Action.ReserveScope(scope, RevisionId handle.Store.Generation)] handle
        collect handle
        Ok { Operations = retained; AdmissionError = admissionError })

  /// <summary>Coldly observes retained control acknowledgements.</summary>
  let observeControl (operation: ControlOperation) = observeOperations operation.Operations operation.AdmissionError

  /// <summary>Seals admission immediately and retains physical cleanup ownership.</summary>
  let beginClose (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      handle.Store.Closing <- true
      handle.Store.Current <- None
      { Store = handle.Store; Operation = AsyncMailbox.beginClose handle.Host })

  /// <summary>Joins physical work and cleanup; cancellation detaches only this observer.</summary>
  let awaitClose (operation: CloseOperation) = async {
    let! outcome = AsyncMailbox.awaitClose operation.Operation
    lock operation.Store.Gate (fun () ->
      operation.Store.Current <- None
      operation.Store.Last <- None
      operation.Store.Demands.Clear())
    return outcome |> Result.mapError FormatterError.HostFailure
  }

  /// <summary>Checks current input identity and foundation result eligibility at this instant.</summary>
  let isCurrent snapshot (handle: Handle) =
    lock handle.Store.Gate (fun () ->
      match handle.Store.Current with
      | Some current when current.Snapshot = snapshot && not handle.Store.Closing ->
        match AsyncMailbox.tryResult work handle.Host, current.Outcome with
        | Some result, Some (token, _) -> result.Value = token && AsyncMailbox.isEligible result handle.Host
        | _ -> false
      | _ -> false)

  /// <summary>Transfers typed host failure evidence to the document owner.</summary>
  let drainDiagnostics (handle: Handle) = AsyncMailbox.drainDiagnostics handle.Host
