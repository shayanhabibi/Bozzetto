module Bozzetto.Tests.ActorCreationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Bozzetto.ProjectLoading
open Bozzetto.ActorCreation

let mkProject (fileName: string) : ProjectMetadata =
  { ProjectFileName = fileName
    TargetFramework = "net10.0"
    OtherOptions = []
    ReferencedProjects = []
    PackageReferences = []
    TargetPath = ""
    AllProperties = Map.empty }

let private refusalMessage action =
  try
    action () |> ignore
    None
  with :? InvalidOperationException as error -> Some error.Message

let private expectIsolatedRefusal action =
  refusalMessage action
  |> Expect.equal "the isolated host's specific reason is required"
       (Some Bozzetto.SessionKinds.isolatedNotRestored)

/// An Isolated request is refused before any project loading, initialization
/// or warm-up event, whatever its target.
let private assertIsolatedRefused target =
  let mutable initialized = false
  let mutable emitted = false
  let args =
    { mkCommonActorArgs TestInfrastructure.quietLogger false
        (fun _ -> emitted <- true)
        { Bozzetto.Args.ProjectLoadConfig.empty with Targets = [ target ] } with
        InitFunctions = [ fun _ -> initialized <- true; "sentinel", box true ]
        FsiKind = Bozzetto.SessionKinds.Isolated }
  expectIsolatedRefusal (fun () -> createActorImmediate args)
  initialized |> Expect.isFalse "refusal must precede injected initialization"
  emitted |> Expect.isFalse "refusal must precede warmup events"

[<Tests>]
let tests =
  testList "ActorCreation" [
    testList "isolated FSI host boundary" [
      test "an isolated bare session refuses before actor setup" {
        assertIsolatedRefused Bozzetto.SessionProjectTarget.Bare
      }

      test "an isolated project session refuses before its project is read" {
        assertIsolatedRefused (Bozzetto.SessionProjectTarget.Project "never-read.fsproj")
      }

      test "refusal control catches a bypass returning empty metadata" {
        let mutable killed = false
        try expectIsolatedRefusal (fun () -> emptySolution)
        with :? Expecto.AssertException -> killed <- true
        killed |> Expect.isTrue "returning empty metadata must fail the same refusal control"
      }
    ]

    testList "projectDirectories" [
      test "empty solution returns empty list" {
        let result = projectDirectories emptySolution
        result |> Expect.isEmpty "should return empty list for empty solution"
      }

      test "deduplicates same directory from multiple projects" {
        let sln =
          { emptySolution with
              Projects = [
                mkProject (Path.Combine("src", "MyLib", "A.fsproj"))
                mkProject (Path.Combine("src", "MyLib", "B.fsproj"))
              ] }
        let result = projectDirectories sln
        result |> Expect.hasLength "should deduplicate to one directory" 1
      }

      test "skips projects with empty ProjectFileName" {
        let sln =
          { emptySolution with
              Projects = [
                mkProject ""
                mkProject (Path.Combine("src", "MyLib", "A.fsproj"))
              ] }
        let result = projectDirectories sln
        result |> Expect.hasLength "should skip empty and keep one" 1
      }
    ]

    testList "mkCommonActorArgs" [
      let logger = TestInfrastructure.quietLogger
      let onEvent (_: Bozzetto.Features.Events.BozzettoEvent) = ()
      let loadConfig = Bozzetto.Args.ProjectLoadConfig.empty

      test "sets IsBare correctly" {
        let args = mkCommonActorArgs logger false onEvent loadConfig
        args.LoadConfig.IsBare |> Expect.isTrue "IsBare should be true for the explicit bare config"
      }

      test "sets AutoOpenNamespaces to true" {
        let args = mkCommonActorArgs logger false onEvent loadConfig
        args.AutoOpenNamespaces |> Expect.isTrue "AutoOpenNamespaces should default to true"
      }

    ]

    test "commonMiddleware is non-empty" {
      commonMiddleware |> Expect.isNonEmpty "commonMiddleware should contain middleware entries"
    }
  ]
