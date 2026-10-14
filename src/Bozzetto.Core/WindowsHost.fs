/// The Windows acquisition edge for machine facts: memory, CPU time, paging
/// files and the process table, read through the Win32 calls Task Manager uses.
/// It is the Windows counterpart of reading `/proc`; callers on other platforms
/// never reach it. Every function is a bounded, synchronous read that returns
/// None (or skips one process) instead of throwing.
module Bozzetto.WindowsHost

open System
open System.Runtime.InteropServices

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private MemoryStatusEx =
  val mutable Length: uint32
  val mutable MemoryLoad: uint32
  val mutable TotalPhys: uint64
  val mutable AvailPhys: uint64
  val mutable TotalPageFile: uint64
  val mutable AvailPageFile: uint64
  val mutable TotalVirtual: uint64
  val mutable AvailVirtual: uint64
  val mutable AvailExtendedVirtual: uint64

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private PerformanceInformation =
  val mutable Size: uint32
  val mutable CommitTotal: unativeint
  val mutable CommitLimit: unativeint
  val mutable CommitPeak: unativeint
  val mutable PhysicalTotal: unativeint
  val mutable PhysicalAvailable: unativeint
  val mutable SystemCache: unativeint
  val mutable KernelTotal: unativeint
  val mutable KernelPaged: unativeint
  val mutable KernelNonpaged: unativeint
  val mutable PageSize: unativeint
  val mutable HandleCount: uint32
  val mutable ProcessCount: uint32
  val mutable ThreadCount: uint32

[<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type private ProcessEntry32 =
  val mutable Size: uint32
  val mutable Usage: uint32
  val mutable ProcessId: uint32
  val mutable DefaultHeapId: unativeint
  val mutable ModuleId: uint32
  val mutable Threads: uint32
  val mutable ParentProcessId: uint32
  val mutable PriorityClassBase: int32
  val mutable Flags: uint32
  [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)>]
  val mutable ExeFile: string

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private ProcessMemoryCounters =
  val mutable Size: uint32
  val mutable PageFaultCount: uint32
  val mutable PeakWorkingSetSize: unativeint
  val mutable WorkingSetSize: unativeint
  val mutable QuotaPeakPagedPoolUsage: unativeint
  val mutable QuotaPagedPoolUsage: unativeint
  val mutable QuotaPeakNonPagedPoolUsage: unativeint
  val mutable QuotaNonPagedPoolUsage: unativeint
  val mutable PagefileUsage: unativeint
  val mutable PeakPagefileUsage: unativeint

module private Native =
  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern bool GlobalMemoryStatusEx(MemoryStatusEx& buffer)

  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern bool GetSystemTimes(int64& idle, int64& kernel, int64& user)

  [<DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)>]
  extern bool GetPerformanceInfo(PerformanceInformation& info, uint32 size)

  [<DllImport("ntdll.dll")>]
  extern int NtQuerySystemInformation(int infoClass, nativeint buffer, int length, int& returnLength)

  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern nativeint CreateToolhelp32Snapshot(uint32 flags, uint32 processId)

  [<DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)>]
  extern bool Process32First(nativeint snapshot, ProcessEntry32& entry)

  [<DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)>]
  extern bool Process32Next(nativeint snapshot, ProcessEntry32& entry)

  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern nativeint OpenProcess(uint32 access, bool inheritHandle, uint32 processId)

  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern bool GetProcessTimes(nativeint handle, int64& creation, int64& exit, int64& kernel, int64& user)

  [<DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)>]
  extern bool GetProcessMemoryInfo(nativeint handle, ProcessMemoryCounters& counters, uint32 size)

  [<DllImport("kernel32.dll", SetLastError = true)>]
  extern bool CloseHandle(nativeint handle)

/// Physical memory and the commit charge, in bytes.
type Memory = {
  TotalPhysical: int64
  AvailablePhysical: int64
  /// Memory committed by every process (RAM plus paging-file backing).
  CommitCharge: int64
  /// The most that can be committed: RAM plus paging files.
  CommitLimit: int64
  /// Standby and file cache the OS can repurpose.
  SystemCache: int64
  PageSize: int64
}

/// Cumulative CPU time across all logical processors, in 100 ns units.
/// `Kernel` includes idle time, as Windows reports it.
type CpuTimes = { Idle: int64; Kernel: int64; User: int64 }

/// Paging-file use and size, summed across every paging file, in bytes.
type PagingFiles = { InUse: int64; Total: int64 }

/// One process as the snapshot and its own counters describe it.
type ProcessSample = {
  ProcessId: int
  ParentId: int
  Name: string
  Threads: int
  /// Creation time (FILETIME): with the id, identifies this incarnation.
  Created: int64
  /// Kernel plus user CPU time, in 100 ns units.
  CpuTime: int64
  WorkingSet: int64
}

let memory () : Memory option =
  try
    let mutable status = MemoryStatusEx()
    status.Length <- uint32 (Marshal.SizeOf<MemoryStatusEx>())
    let mutable info = PerformanceInformation()
    info.Size <- uint32 (Marshal.SizeOf<PerformanceInformation>())
    match Native.GlobalMemoryStatusEx(&status), Native.GetPerformanceInfo(&info, info.Size) with
    | true, true ->
      let page = int64 info.PageSize
      Some { TotalPhysical = int64 status.TotalPhys
             AvailablePhysical = int64 status.AvailPhys
             CommitCharge = int64 info.CommitTotal * page
             CommitLimit = int64 info.CommitLimit * page
             SystemCache = int64 info.SystemCache * page
             PageSize = page }
    | true, false ->
      Some { TotalPhysical = int64 status.TotalPhys
             AvailablePhysical = int64 status.AvailPhys
             CommitCharge = int64 (status.TotalPageFile - status.AvailPageFile)
             CommitLimit = int64 status.TotalPageFile
             SystemCache = 0L
             PageSize = int64 Environment.SystemPageSize }
    | false, _ -> None
  with _ -> None

let cpuTimes () : CpuTimes option =
  try
    let mutable idle = 0L
    let mutable kernel = 0L
    let mutable user = 0L
    match Native.GetSystemTimes(&idle, &kernel, &user) with
    | true -> Some { Idle = idle; Kernel = kernel; User = user }
    | false -> None
  with _ -> None

/// SystemPageFileInformation entries: NextEntryOffset, TotalSize and
/// TotalInUse (pages) lead each record.
let pagingFiles (pageSize: int64) : PagingFiles option =
  let systemPageFileInformation = 18
  let statusInfoLengthMismatch = 0xC0000004
  let rec query (length: int) =
    let buffer = Marshal.AllocHGlobal length
    try
      let mutable needed = 0
      match Native.NtQuerySystemInformation(systemPageFileInformation, buffer, length, &needed) with
      | 0 when needed = 0 -> Some { InUse = 0L; Total = 0L }
      | 0 ->
        let rec walk (offset: int) (inUse: int64) (total: int64) =
          let next = Marshal.ReadInt32(buffer, offset)
          let size = int64 (uint32 (Marshal.ReadInt32(buffer, offset + 4)))
          let used = int64 (uint32 (Marshal.ReadInt32(buffer, offset + 8)))
          let inUse, total = inUse + used * pageSize, total + size * pageSize
          if next = 0 || offset + next >= length then { InUse = inUse; Total = total }
          else walk (offset + next) inUse total
        Some (walk 0 0L 0L)
      | status when status = statusInfoLengthMismatch && length < (1 <<< 20) -> query (length * 4)
      | _ -> None
    finally
      Marshal.FreeHGlobal buffer
  try query 1024 with _ -> None

/// The process table, capped at `limit` entries, with how many processes this
/// caller may not query (counted, not sampled) and whether the cap cut the
/// table short.
let processes (limit: int) : ProcessSample array * int * bool =
  let snapshotProcesses = 0x2u
  let queryLimitedInformation = 0x1000u
  let invalid = nativeint -1
  let snapshot = try Native.CreateToolhelp32Snapshot(snapshotProcesses, 0u) with _ -> invalid
  if snapshot = invalid || snapshot = 0n then [||], 0, false
  else
    try
      let rows = ResizeArray<ProcessSample>()
      let mutable unreadable = 0
      let mutable seen = 0
      let mutable entry = ProcessEntry32()
      entry.Size <- uint32 (Marshal.SizeOf<ProcessEntry32>())
      let mutable more = Native.Process32First(snapshot, &entry)
      while more do
        seen <- seen + 1
        if rows.Count < limit then
          let handle = Native.OpenProcess(queryLimitedInformation, false, entry.ProcessId)
          let mutable creation = 0L
          let mutable exit = 0L
          let mutable kernel = 0L
          let mutable user = 0L
          let mutable counters = ProcessMemoryCounters()
          counters.Size <- uint32 (Marshal.SizeOf<ProcessMemoryCounters>())
          let readable =
            handle <> 0n
            && Native.GetProcessTimes(handle, &creation, &exit, &kernel, &user)
            && Native.GetProcessMemoryInfo(handle, &counters, counters.Size)
          if handle <> 0n then Native.CloseHandle handle |> ignore
          if readable then
            rows.Add {
              ProcessId = int entry.ProcessId
              ParentId = int entry.ParentProcessId
              Name = entry.ExeFile
              Threads = int entry.Threads
              Created = creation
              CpuTime = kernel + user
              WorkingSet = int64 counters.WorkingSetSize }
          else unreadable <- unreadable + 1
        more <- Native.Process32Next(snapshot, &entry)
      rows.ToArray(), unreadable, seen > limit
    finally
      Native.CloseHandle snapshot |> ignore
