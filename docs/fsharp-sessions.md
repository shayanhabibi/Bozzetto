# F# sessions

Bozzetto hosts F# REPL sessions again. The SageFs session path that the
2026-10-01 retirement removed (`8cdb9fa`, `e971e3f`, `77cfa4c`, `80fd69e`) is
restored, minus every runtime-patching surface. This page is the authority for
what is restored, how it is shaped and what remains open.

## Shape

```text
MCP / browser / editor
        |
Bozzetto daemon (47749)
  SessionManager supervisor ── spawns ──> Bozzetto.Host (one per session)
        |                                   Ionide.ProjInfo project load
        |   F# worker wire                  shadow copy, warm-up
        |   Unix-domain socket,             AppState actor + FsiEvaluationSession
        +-- typed binary frames ----------> (InProcess, inside the worker)
```

- **Worker.** `src/Bozzetto.Host` is the per-session worker process. The daemon
  launches it from `host/` beside its own assembly (`Args.resolveHostLaunch`),
  rolled forward to the runtime the session's projects need
  (`RuntimeSelection`). The worker is the session's isolation boundary: FSI runs
  in it, never in the daemon. A hard-killed daemon is noticed through
  `OwnerMonitor` and the worker exits.
- **Wire.** `src/Bozzetto.Core/WorkerWire.fs` is an explicit binary profile
  (magic `BZFW`, protocol version, explicit tags, exact consumption, bounded
  lengths) for the REPL subset of `WorkerMessage`/`WorkerResponse`. It follows
  the owner's typed-binary ruling for direct interchange; the deleted HTTP/JSON
  worker transport is not restored. The worker binds the socket inside a
  private (owner-only) directory and greets every connection with a `Hello`
  naming its session; `WorkerSocketClient` refuses any other session.
- **Lifecycle lines.** The worker's stdout carries only `WARMUP_PROGRESS=` and
  `WORKER_SOCKET=`, which `SessionManager.awaitWorkerPort` supervises with the
  existing inactivity and absolute warm-up bounds. Requests never use stdout.
- **Project loading.** `ProjectLoading.Workspace` evaluates explicit targets with
  Ionide.ProjInfo and translates them to `ProjectMetadata` (`toMetadata`), so
  Ionide's types stay inside the loader. The manual fsproj parse is the fallback
  when MSBuild evaluation is unavailable. A bare target loads nothing.
- **Admission.** `DaemonMode.createSessionOpsWithRecovery` refuses a create whose
  build outputs are missing as `NeedsRebuild` before the supervisor sees it:
  the caller builds under a lease. `hard_reset_fsi_session rebuild=true` builds
  through `SessionBuild` while the old worker serves, then swaps workers.

## Carried and not carried

| Surface | State |
| --- | --- |
| create (project, solution, bare), resume, stop, restart, rebuild | Restored |
| eval, check, type-check with symbols, completions, cancel, `#load`, soft reset, status, live values | Restored over the wire |
| Warm-up context (`/warmup-context`) | Not carried; the daemon helpers return nothing for socket workers |
| Live testing, test discovery, coverage maps | Not carried; refused by name (`WorkerWire.notCarried`), discovery answers empty |
| `run_app` / app runs | Not carried; refused by name |
| Hot reload, Harmony, detours, value-read probes, DevReload | Not restored. Runtime patching stays banned |
| Isolated FsiHost beneath the worker | Not restored; `SessionKinds.isolatedNotRestored` |
| `.bozzetto/config.fsx`, Jupyter, cohort integration refs | Still retired (`ExternalFSharpService`) |

## Evidence

Verified on Windows (2026-10-14) for `Bozzetto.Core`, `Bozzetto.Host` and the
new tests, built against nuget.org packages with a locally written stand-in for
Fidelity.Data's JSON surface: `WorkerWireTests` (round-trip properties and
refusal controls), `FSharpWorkerTests` (a real bare worker: eval, state, stdout,
runtime and compile errors, check, completions, cancel of a running eval,
reset, Hello binding, shutdown; a real project worker running the
`FSharpCoreIdentityFixture` build) and `ActorCreationTests`: 28/28. A mutation
removing the Hello session check is caught.

The daemon (`src/Bozzetto`) and `Bozzetto.Tests` were not compiled there: their
BAREWire/PSG/Fidelity.Data closure was not available. Their changes are
restorations of pre-retirement code plus the admission and description tests;
the first build on the owner's machine is their first compile.

## Open work

1. Build and run the unfiltered suite on the reference machine; commit the
   regenerated `packages.lock.json` files (Ionide.ProjInfo was added and
   `Bozzetto.Host` is a new project).
2. Re-admit the retired SageFs-era journeys (`Integration.retireFSharp`) one at a
   time, starting with session creation, resume and multi-client routing.
3. Carry warm-up context and test discovery on the wire, then live testing.
4. Decide whether the FSharp.Core build-mismatch protection the Isolated
   FsiHost gave (SageFs #141) is needed; the project fixture currently passes
   without it.
