#I "packages/FAKE/tools"
#r "FakeLib.dll"

open System
open System.IO
open Fake
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper

(* Types

   Types to help declaratively define the Arachne solution, to enable strongly typed
   access to all properties and data required for the varying builds. *)

type Solution =
    { Name: string
      Metadata: Metadata
      Structure: Structure
      VersionControl: VersionControl }

and Metadata =
    { Summary: string
      Description: string
      Authors: string list
      Keywords: string list
      Info: Info }

and Info =
    { ReadMe: string
      License: string
      Notes: string }

and Structure =
    { Solution: string
      Projects: Projects }

and Projects =
    { Source: SourceProject list
      Test: TestProject list }

and SourceProject =
    { Name: string
      Dependencies: Dependency list }

and Dependency =
    | Package of string
    | Local of string

and TestProject =
    { Name: string }

and VersionControl =
    { Source: string
      Raw: string }

(* Data

   The Arachne solution expressed as a strongly typed structure using the previously
   defined type system. *)

let solution =
    { Name = "Arachne"
      Metadata =
        { Summary = "Arachne - Types for HTTP and related RFCs."
          Description = "Arachne - Types for HTTP and related RFCs."
          Authors =
            [ "Andrew Cherry (@kolektiv)"
              "Ryan Riley (@panesofglass)"]
          Keywords =
            [ "parser"
              "web"
              "http" ]
          Info =
            { ReadMe = "README.md"
              License = "LICENSE.txt"
              Notes = "RELEASE_NOTES.md" } }
      Structure =
        { Solution = "Arachne.sln"
          Projects =
            { Source =
                [ { Name = "Arachne.Core"
                    Dependencies =
                        [ Package "FParsec" ] }
                  { Name = "Arachne.Http"
                    Dependencies =
                        [ Package "FParsec"
                          Local "Arachne.Core"
                          Local "Arachne.Language"
                          Local "Arachne.Uri" ] }
                  { Name = "Arachne.Http.Cors"
                    Dependencies =
                        [ Package "FParsec"
                          Local "Arachne.Core"
                          Local "Arachne.Http"
                          Local "Arachne.Uri" ] }
                  { Name = "Arachne.Language"
                    Dependencies =
                        [ Package "FParsec"
                          Local "Arachne.Core" ] }
                  { Name = "Arachne.Uri"
                    Dependencies =
                        [ Package "FParsec"
                          Local "Arachne.Core" ] }
                  { Name = "Arachne.Uri.Template"
                    Dependencies =
                        [ Package "FParsec"
                          Local "Arachne.Core"
                          Local "Arachne.Uri" ] } ]
              Test =
                [ { Name = "Arachne.Http.Tests" }
                  { Name = "Arachne.Http.Cors.Tests" }
                  { Name = "Arachne.Language.Tests" }
                  { Name = "Arachne.Uri.Tests" }
                  { Name = "Arachne.Uri.Template.Tests" } ] } }
      VersionControl =
        { Source = "https://github.com/freya-fs/arachne"
          Raw = "https://raw.github.com/freya-fs" } }

(* Properties

   Computed properties of the build based on existing data structures and/or
   environment variables, creating a derived set of properties. *)

let release =
    parseReleaseNotes (File.ReadAllLines solution.Metadata.Info.Notes)

let assemblyVersion =
    release.AssemblyVersion

let isAppVeyorBuild =
    environVar "APPVEYOR" <> null

let defaultTarget =
    if isAppVeyorBuild then
        let repositoryDir = environVar "APPVEYOR_BUILD_FOLDER"
        if repositoryDir <> null && Git.Information.getBranchName repositoryDir = "master"
        then "Publish"
        else "Default"
    else "Default"

let nugetVersion = 
    if isAppVeyorBuild then
        let nugetVersion =
            let isTagged = Boolean.Parse(environVar "APPVEYOR_REPO_TAG")
            if isTagged then
                environVar "APPVEYOR_REPO_TAG_NAME"
            else
                sprintf "%s-b%03i" release.NugetVersion (int buildVersion)
        Shell.Exec("appveyor", sprintf "UpdateBuild -Version \"%s\"" nugetVersion) |> ignore
        nugetVersion
    else release.NugetVersion

let notes =
    String.concat Environment.NewLine release.Notes

(* Targets

   FAKE targets expressing the components of a Arachne build, to be assembled
   in to specific usable targets subsequently. *)

(* Publish *)

let dependencies (x: SourceProject) =
    x.Dependencies 
    |> List.map (function | Package x -> x, GetPackageVersion "packages" x
                          | Local x -> x, nugetVersion)

let extensions =
    [ "dll"
      "pdb"
      "xml" ]

let files (x: SourceProject) =
    extensions
    |> List.map (fun ext ->
         sprintf @"..\src\%s\bin\Release\%s.%s" x.Name x.Name ext,
         Some "lib/net45", 
         None)

let projectFile (x: SourceProject) =
    sprintf @"src/%s/%s.fsproj" x.Name x.Name

let tags (s: Solution) =
    String.concat " " s.Metadata.Keywords

#if MONO
#else
#load "packages/SourceLink.Fake/tools/SourceLink.fsx"

open SourceLink

Target "Publish.Debug" (fun _ ->
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" solution.VersionControl.Raw (solution.Name.ToLowerInvariant ())

    solution.Structure.Projects.Source
    |> List.iter (fun project ->
        let release = VsProj.LoadRelease (projectFile project)
        let files = release.Compiles -- "**/AssemblyInfo.fs"
        SourceLink.Index files release.OutputFilePdb __SOURCE_DIRECTORY__ baseUrl))
#endif

Target "Publish.Pack" (fun _ ->
    Paket.Pack (fun x ->
        { x with
            OutputPath = "bin"
            Version = nugetVersion
            ReleaseNotes = notes }))

Target "Publish.Push" (fun _ ->
    Paket.Push (fun p ->
        { p with WorkingDir = "bin" }))

(* Source *)

let assemblyInfo (x: SourceProject) =
    sprintf @"src/%s/AssemblyInfo.fs" x.Name

let testAssembly (x: TestProject) =
    sprintf "tests/%s/bin/Release/%s.dll" x.Name x.Name

Target "Source.AssemblyInfo" (fun _ ->
    solution.Structure.Projects.Source
    |> List.iter (fun project ->
        CreateFSharpAssemblyInfo (assemblyInfo project)
            [ Attribute.Company (String.concat "," solution.Metadata.Authors)
              Attribute.Description solution.Metadata.Summary
              Attribute.FileVersion assemblyVersion
              Attribute.InformationalVersion nugetVersion
              Attribute.Product project.Name
              Attribute.Title project.Name
              Attribute.Version assemblyVersion ]))

Target "Source.Build" (fun _ ->
    build (fun x ->
        { x with
            Properties =
                [ "Optimize",      environVarOrDefault "Build.Optimize"      "True"
                  "DebugSymbols",  environVarOrDefault "Build.DebugSymbols"  "True"
                  "Configuration", environVarOrDefault "Build.Configuration" "Release" ]
            Targets =
                [ "Build" ]
            Verbosity = Some Quiet }) solution.Structure.Solution)

Target "Source.Clean" (fun _ ->
    CleanDirs [
        "bin"
        "temp" ])

Target "Source.Test" (fun _ ->
    try
        solution.Structure.Projects.Test
        |> List.map (fun project -> testAssembly project)
        |> NUnit (fun x ->
            { x with
                DisableShadowCopy = true
                TimeOut = TimeSpan.FromMinutes 20.
                OutputFile = "bin/TestResults.xml" })
    finally
        AppVeyor.UploadTestResultsXml AppVeyor.TestResultsType.NUnit "bin")

(* Builds

   Specifically defined dependencies to produce usable builds for varying scenarios,
   such as CI, documentation, etc. *)

Target "Default" DoNothing
Target "Source" DoNothing
Target "Publish" DoNothing

(* Publish *)

"Default"
==> "Publish.Push"
==> "Publish"

(* Default *)

"Source"
=?> ("Publish.Debug", not isMono)
==> "Publish.Pack"
==> "Default"

(* Source *)

"Source.Clean"
==> "Source.AssemblyInfo"
==> "Source.Build"
==> "Source.Test"
==> "Source"

(* Run *)

RunTargetOrDefault defaultTarget
