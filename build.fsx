#r "packages/FAKE/tools/FakeLib.dll"
#load "fsi.fsx"
#load "Steps.fsx"

open Fake
open System.Diagnostics
open Fsi
open Steps

Target "FsiInteractive" (fun _ ->
  StartProcess (fun info ->
    info.FileName <- "packages/FsInteractiveService.Http/tools/FsInteractiveService.exe"
    info.Arguments <- "18082"
  )
)

let mutable currentStep =
  environVarOrDefault "RUNNER_STEP" "1" |> int

let fileName = "MiniSuave.fsx"
let fsiHost = "http://localhost:18082"

let printStep () =
  match List.tryFind (fun s -> s.Id = currentStep) runner.Steps with
  | Some step ->
    tracefn "%s" step.Description
  | None ->
    tracefn "%s" runner.End

let printErrors  =
  List.iter (fun e ->
                sprintf "[%d,%d] %s" (e.Line+1) (e.Start+1) e.Message
                |> traceError )

let handleEvalResult onSuccess = function
| EvalErrors errs -> printErrors errs
| EvalException ex -> traceError ex.Details
| EvalSuccess suc -> onSuccess suc
| _ -> tracefn "something went terribly wrong!"

let rec evalExpressions = function
| [] -> true
| x::xs ->
  match eval fileName fsiHost x with
  | EvalErrors errs -> printErrors errs; false
  | EvalException ex -> traceError ex.Details; false
  | EvalSuccess suc ->
    evalExpressions xs
  | _ -> true

let evalExpected code =
  match eval fileName fsiHost code with
  | EvalSuccess suc ->
    suc.Output.Trim()
  | _ -> ""

let evalOutput code =
  let output = evalExpected code
  output
    .Replace("val it : unit = ()", "")
    .Replace("\r\n", "")

let rec evalAsserts = function
| [] -> true
| x::xs ->
  match eval fileName fsiHost (fst x) with
  | EvalErrors errs ->printErrors errs; false
  | EvalException ex -> traceError ex.Details; false
  | EvalSuccess suc ->
    let actualOutput = suc.Output.Trim()
    let expectedOutput = evalExpected (snd x)
    if actualOutput = expectedOutput then
      evalAsserts xs
    else
      sprintf "Expected %s but found %s" expectedOutput actualOutput
      |> traceError
      false
  | _ -> true

let rec evalExecuteValueAssserts = function
| [] -> true
| x :: xs ->
  match evalExpressions (fst x) with
  | true ->
     match evalAsserts [snd x] with
     | true -> evalExecuteValueAssserts xs
     | _ -> false
  | _ -> false

let eval onSuccess code =
    eval fileName fsiHost code
    |> handleEvalResult onSuccess

let assertStep _ =
  let moveToNext step =
    traceFAKE "**** %s ****" step.Message
    currentStep <- currentStep + 1
    printStep ()
  match List.tryFind (fun s -> s.Id = currentStep) runner.Steps with
  | Some step ->
    match evalExpressions step.Expressions with
    | true ->
      match step.Asserts with
      | Compiler exprs ->
        match evalExpressions exprs with
        | true -> moveToNext step
        | _ -> ()
      | Value asserts ->
        match evalAsserts asserts with
        | true -> moveToNext step
        | _ -> ()
      | ExecuteValue asserts ->
        match evalExecuteValueAssserts asserts with
        | true -> moveToNext step
        | _ -> ()
      | Output (code, expected) ->
        let out = evalOutput code
        printfn "%A" out
        if out = expected then
          moveToNext step
        else
          sprintf "Expected %s but found %s" expected out
          |> traceError
    | false -> ()
  | None -> printStep ()

let watch () =
  use watcher = !! fileName |> WatchChanges (fun changes ->
      printfn "compiling..."
      fileName
      |> System.IO.File.ReadAllText
      |> eval assertStep
  )
  System.Threading.Thread.Sleep(1000)
  printfn ""
  printfn ""
  tracefn "\t\t\t%s" runner.Greeting
  printfn ""
  printfn ""
  printStep ()
  System.Console.ReadLine() |> ignore
  watcher.Dispose()
  killAllCreatedProcesses()

Target "Watch" (fun _ -> watch ())

"FsiInteractive"
==> "Watch"

RunTargetOrDefault "Watch"