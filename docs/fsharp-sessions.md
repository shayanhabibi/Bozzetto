# F# sessions

Bozzetto hosts F# REPL sessions again. The SageFs session path that the
2026-10-01 retirement removed (`8cdb9fa`, `e971e3f`, `77cfa4c`, `80fd69e`) is
restored; runtime-patching surfaces have not yet been restored. This page is
the authority for what is restored, how it is shaped and what remains open.

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
| Hot reload, Harmony, detours, value-read probes, DevReload | Not restored; permitted for back-porting into F# session workers |
| Isolated FsiHost beneath the worker | Not restored; `SessionKinds.isolatedNotRestored` |
| `.bozzetto/config.fsx`, Jupyter, cohort integration refs | Still retired (`ExternalFSharpService`) |

## Evidence

Windows 11, SDK 10.0.402 with the implicit FSharp.Core pinned to 10.1.401, built
against the Forgejo heads of Fidelity.Data, BAREWire, Fidelity.PSG,
Fidelity.FSharp.Incremental and Calque (2026-10-14):

- **Default tier:** 8524/8524 ran. The same 21 failures occur at the base
  commit `0ad00d8` on this machine and nothing else fails; they are
  pre-existing Windows behaviour (paths, process environment, managed build),
  not F# session work.
- **`--integration-host`:** 103/103 ran, both `F# worker` journeys pass (a bare
  worker through eval, state, stdout, errors, check, completions, cancel,
  reset, Hello binding and shutdown; a project worker running the
  `FSharpCoreIdentityFixture` build). The 13 failures are identical at the
  base commit.
- **Live daemon:** an installed build on 47749 created a bare session and a
  project session over MCP, evaluated and checked code in each, refused a
  project with missing build outputs as `NeedsRebuild`, and after an edit
  `hard_reset_fsi_session rebuild=true` served the rebuilt code.
- A mutation removing the Hello session check is caught by the worker journey.

## Hot-reload back-port assessment (2026-10-15)

Source inspection of Bozzetto `28124c4` and
[SageFs `7f785465`](https://github.com/WillEhrendreich/SageFs/tree/7f7854652edd3db3f9827c72c273434b5ff895b3)
supports a selective back-port into the existing F# worker. Upstream
[`HostAgent`](https://github.com/WillEhrendreich/SageFs/blob/7f7854652edd3db3f9827c72c273434b5ff895b3/SageFs.Core/HostAgent.fs#L1-L61)
owns its method registry where user assemblies execute. Its after-evaluation
boundary fits Bozzetto.Host; the current typed worker wire can carry outcomes.
The removed Bozzetto implementation is also available at `1c7d5e4`, before
`e971e3f`, for local namespace and lifecycle comparisons.

A faithful port needs the patch registry, generic/closure and value-read
instrumentation, changed-declaration planning/emission, save routing and
explicit patch/restart outcomes together. Preserve the existing worker's
evaluation serialization, cancellation and process lifetime. Retain honest
restart requirements for incompatible type or mutable-state changes and
startup side effects. Restoring the old host wholesale would also restore
retired transport and product surfaces.

Upstream Core and Host support `net10.0` as well as `net11.0`. They use the
committed custom `SageFs.Harmony` `2.4.2-sagefs.1` package and patched MonoMod,
not an interchangeable public Harmony package. Hot-reload FSI uses
`--multiemit-` and matching unoptimized project/session settings, which affect
type redefinition. Dependency selection and those session semantics need an
explicit implementation design. Live testing is additional worker-wire work;
the separate metadata-delta app-runner route is additional scope.

This is pinned source evidence, not an executed hot-reload result. The existing
project-worker integration failure (`Repro.assemblyDirectory()` returns null)
still needs resolution before using that journey as port acceptance evidence.

## Open work

1. Re-admit the retired SageFs-era journeys (`Integration.retireFSharp`) one at a
   time, starting with session creation, resume and multi-client routing.
2. Carry warm-up context and test discovery on the wire, then live testing.
3. Decide whether the FSharp.Core build-mismatch protection the Isolated
   FsiHost gave (SageFs #141) is needed; the project fixture currently passes
   without it.
