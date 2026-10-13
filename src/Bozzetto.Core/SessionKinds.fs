/// Where an F# session's FSI evaluator runs.
module Bozzetto.SessionKinds

/// InProcess: in the process that owns the session. In production that is the
/// session's own worker (Bozzetto.Host), which is the isolation boundary from
/// the daemon. Isolated: a further FSI host process beneath the worker, as
/// SageFs ran to keep user code off the worker's FSharp.Core build; it is not
/// restored and is refused.
type FsiSessionKind =
  | Isolated
  | InProcess

/// The refusal for an Isolated request.
let isolatedNotRestored =
  "Isolated F# sessions (a separate FSI host beneath the session worker) are not restored. Each session already runs in its own worker process; use InProcess."
