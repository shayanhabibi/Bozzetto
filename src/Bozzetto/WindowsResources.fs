/// Windows acquisition edge: the Win32 counters Task Manager reads, filled into
/// the same HostSample.Raw and metric names the Linux edge produces, so the
/// resource panel, the occupancy graph and the memory facts read them alike.
/// No timer, policy, subprocess, managed process object, or kill authority
/// lives here.
module Bozzetto.Server.WindowsResources

open System
open System.Diagnostics
open Bozzetto.Server.HostSample

let private processLimit = 8192

let acquire () : Raw =
  let memory = Bozzetto.WindowsHost.memory ()
  let bytes (read: Bozzetto.WindowsHost.Memory -> int64) = memory |> Option.map (read >> float)
  let paging = memory |> Option.bind (fun m -> Bozzetto.WindowsHost.pagingFiles m.PageSize)
  let rows, unreadable, truncated = Bozzetto.WindowsHost.processes processLimit
  let cpu =
    Bozzetto.WindowsHost.cpuTimes ()
    |> Option.map (fun t ->
      // Kernel time includes idle; there is no I/O-wait counter to separate.
      { Total = t.Kernel + t.User; Idle = t.Idle; Wait = None; Count = Environment.ProcessorCount })
  { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Clock = Stopwatch.GetTimestamp()
    Cpu = cpu
    Processes =
      rows |> Array.map (fun p ->
        { Pid = p.ProcessId; Parent = p.ParentId; Start = p.Created; Name = p.Name
          Ticks = p.CpuTime; Resident = p.WorkingSet; Threads = p.Threads })
    // Paging rates need performance counters; they are absent, not zero.
    SwapIn = None; SwapOut = None; SwapPageBytes = memory |> Option.map _.PageSize |> Option.defaultValue 4096L
    Count = rows.Length + unreadable; Unreadable = unreadable; Truncated = truncated
    Scope = "Windows host scope; Swap is paging-file use and the commit charge spans RAM and paging files—do not add them. Working sets include shared pages."
    Metrics =
      [| metric "RAM used (total − available)" "bytes" (memory |> Option.map (fun m -> float (m.TotalPhysical - m.AvailablePhysical)))
         metric "RAM available" "bytes" (bytes _.AvailablePhysical)
         metric "RAM total (OS managed)" "bytes" (bytes _.TotalPhysical)
         metric "File cache" "bytes" (bytes _.SystemCache)
         metric "Swap used" "bytes" (paging |> Option.map (fun p -> float p.InUse))
         metric "Swap total" "bytes" (paging |> Option.map (fun p -> float p.Total))
         metric "Commit charge" "bytes" (bytes _.CommitCharge)
         metric "Commit limit" "bytes" (bytes _.CommitLimit)
         metric "GPU utilization (unsupported)" "percent" None |] }
