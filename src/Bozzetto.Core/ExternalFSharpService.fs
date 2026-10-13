/// The boundary for the SageFs F# surfaces that stay retired after F# sessions
/// were restored: .bozzetto/config.fsx evaluation, Jupyter execution and cohort
/// integration refs. Each needs its own design before it returns.
module Bozzetto.ExternalFSharpService

let message =
  "This F# surface is not restored. Bozzetto hosts F# sessions in per-session workers (create_project_session, create_solution_session or create_bare_session, then send_fsharp_code), but .bozzetto/config.fsx evaluation, Jupyter execution and cohort integration refs remain retired. Clef projects use Composer: open an explicit .fidproj with composer_open_project, reserve with composer_reserve_edit, build with composer_build and execute through composer_run_current."

let refuse<'T> () : Result<'T, Bozzetto.BozzettoError> =
  Result.Error (Bozzetto.BozzettoError.SessionCreationFailed message)
