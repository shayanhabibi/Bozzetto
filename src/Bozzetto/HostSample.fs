/// The platform-neutral host sample and its pure interval calculations. Each
/// platform's acquisition edge (LinuxResources, WindowsResources) fills `Raw`;
/// `project` turns two consecutive samples into what the resource panel shows.
/// No timer, policy, subprocess, managed process object or kill authority
/// lives here.
module Bozzetto.Server.HostSample

open System
open System.Diagnostics
open Bozzetto.Web.Shared.Protocol

/// Cumulative CPU counters summed across logical processors, in the
/// platform's own unit. Total includes idle. Wait is I/O wait where the
/// platform reports it, and None where it does not.
type Cpu = { Total: int64; Idle: int64; Wait: int64 option; Count: int }
/// One process incarnation: Pid plus Start identifies it. Ticks share the CPU
/// counters' unit, so a delta over the CPU total is a share of the machine.
type ProcessCounter = {
  Pid: int; Parent: int; Start: int64; Name: string
  Ticks: int64; Resident: int64; Threads: int
}
type Raw = {
  At: int64; Clock: int64; Cpu: Cpu option; Processes: ProcessCounter array
  Metrics: ResourceMetric array; SwapIn: int64 option; SwapOut: int64 option
  /// Bytes per counted swap page, for SwapIn/SwapOut.
  SwapPageBytes: int64
  Count: int; Unreadable: int; Truncated: bool
  /// What this platform's figures mean and where they overlap.
  Scope: string
}

let metric name unit value : ResourceMetric = { Name = name; Unit = unit; Value = value }

/// Busy and wait percentages over an interval, with the interval's CPU total.
let cpuDelta (previous: Cpu) (current: Cpu) =
  let total = current.Total - previous.Total
  let idle = current.Idle - previous.Idle
  let wait = Option.map2 (fun now old -> now - old) current.Wait previous.Wait
  let waited = Option.defaultValue 0L wait
  if total <= 0L || idle < 0L || waited < 0L || idle + waited > total || previous.Count <> current.Count then None
  else Some(100. * float (total - idle - waited) / float total, wait |> Option.map (fun w -> 100. * float w / float total), total)

let processPercent totalDelta cpus (previous: ProcessCounter option) (current: ProcessCounter) =
  previous |> Option.bind (fun p ->
    if totalDelta <= 0L || p.Pid <> current.Pid || p.Start <> current.Start || current.Ticks < p.Ticks then None
    else Some(100. * float (current.Ticks - p.Ticks) / float totalDelta * float cpus))

let project (previous: Raw option) (current: Raw) : HostResources =
  let interval = previous |> Option.map (fun p -> Stopwatch.GetElapsedTime(p.Clock,current.Clock).TotalMilliseconds) |> Option.defaultValue 0.
  let delta = Option.bind (fun p -> Option.bind (fun old -> current.Cpu |> Option.bind (cpuDelta old)) p.Cpu) previous
  let oldProcesses = previous |> Option.map (fun p -> p.Processes |> Array.map (fun p -> p.Pid,p) |> Map.ofArray) |> Option.defaultValue Map.empty
  let processes = current.Processes |> Array.map (fun p ->
    { ResourceProcess.ProcessId = p.Pid; ParentId = p.Parent; StartTicks = string p.Start; Name = p.Name; Context = "external/unattributed"
      CpuPercent = delta |> Option.bind (fun (_,_,total) -> processPercent total (current.Cpu.Value.Count) (Map.tryFind p.Pid oldProcesses) p)
      ResidentBytes = float p.Resident; Threads = p.Threads })
  let selected = Array.append
                   (processes |> Array.sortByDescending (fun p -> Option.defaultValue -1. p.CpuPercent) |> Array.truncate 12)
                   (processes |> Array.sortByDescending _.ResidentBytes |> Array.truncate 12)
                 |> Array.distinctBy _.ProcessId
  let swap previousValue currentValue =
    Option.bind (fun p -> Option.map2 (fun old now -> old,now) (previousValue p) currentValue) previous
    |> Option.bind (fun (old,now) -> if now < old || interval <= 0. then None else Some(float (now-old) * float current.SwapPageBytes * 1000. / interval))
  { SampledAtMs = current.At; IntervalMs = interval; SampleCostMs = 0.
    Sequence = 0L; Status = "observing"
    Metrics = Array.append
      [| metric "Host CPU busy" "percent" (delta |> Option.map (fun (busy,_,_) -> busy))
         metric "Host CPU I/O wait" "percent" (delta |> Option.bind (fun (_,wait,_) -> wait))
         metric "Logical CPUs" "count" (current.Cpu |> Option.map (fun c -> float c.Count))
         metric "Swap in" "bytes/s" (swap _.SwapIn current.SwapIn)
         metric "Swap out" "bytes/s" (swap _.SwapOut current.SwapOut) |] current.Metrics
    Processes = selected; ProcessCount = current.Count; UnreadableCount = current.Unreadable
    OmittedCount = max 0 (current.Processes.Length - selected.Length)
    Note = current.Scope + " First CPU/rate reading needs a second observation. "
           + (if current.Truncated then "Process scan capped at 8192. " else "")
           + "No lease attribution, leak verdict or automatic termination in this slice." }
