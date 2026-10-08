# Corel AI Operator (Corel Sign Studio)

A .NET 8 WPF control centre that drives **CorelDRAW 2026** through late-bound COM automation.
CorelDRAW stays the design workspace; this application inspects the open document, shows a plan of
operations for review, executes it, and can replay successful jobs as recipes and batches.

It is a general automation platform — not a sign designer and not tied to any page size, company or
kind of job. No external AI service is connected yet; the planner is a small deterministic placeholder
behind `ICommandPlanner`.

```
User request ─► ICommandPlanner ─► AutomationPlan ─► ICorelActionExecutor ─► CorelDRAW
                (deterministic        (inspectable        (STA thread,
                 today, AI later)      JSON)               undo group)
Reference file ─► IReferenceAnalyzer ─► ReferenceAnalysis ─► IReferencePlanBuilder ─► AutomationPlan
Recipe + rows  ─► BatchExpander ─► one AutomationPlan per row ─► BatchRunner ─► CDR / PDF / PNG / SVG
```

## Solution boundaries

- `CorelSignStudio.Domain` — application-neutral models and logic; no CorelDRAW, WPF or AI dependencies.
  - `Inspection/` — `DocumentSnapshot` (pages, layers, shapes, text, bounds, fill, outline, groups) and `ICorelDocumentInspector`.
  - `Automation/` — the `CorelAction` hierarchy, `AutomationPlan`, validation, `PlanExecutionEngine`, results, history.
  - `Recipes/` — `Recipe`, `{{VARIABLE}}` substitution, `RecipeBuilder`.
  - `Batch/` — `BatchJob`, `BatchExpander`, `BatchRunner`, duplicate-safe output naming.
  - `References/` — `ReferenceInput`, `ReferenceAnalysis`, analyzer and plan-builder interfaces.
  - `Planning/` — `ICommandPlanner` and the temporary `DeterministicCommandPlanner`.
  - `Assets/` — the general asset library model.
- `CorelSignStudio.Corel` — the only project that touches CorelDRAW COM. Every call runs on one dedicated STA thread.
  `CorelAutomationService` (connection, verified save/export), `CorelDocumentInspector`, `CorelActionExecutor`.
- `CorelSignStudio.Storage` — JSON stores for recipes, assets and history; CSV batch reader; file-based reference analyzer.
- `CorelSignStudio.App` — the operator window (Operator, Current document, Automation recipes, Batch jobs, Assets, History, Settings).
  The original sign-template window is still available from Settings.
- `CorelSignStudio.Templates` — the original parametric sign templates (unchanged).
- `CorelSignStudio.Tests` — unit tests plus opt-in tests against the installed CorelDRAW.

## Conventions

- **Units and coordinates:** millimetres, measured from the page's top-left corner, Y downwards
  (CorelDRAW itself measures Y upwards; the Corel project converts).
- **Logical object ids:** `shape_017` is derived from CorelDRAW's persistent `StaticID`, so an object keeps
  its id across inspections and across save/reopen.
- **Targets:** an action points at objects with `shape_017`, `@actionId` (what an earlier step in the same
  plan created), `name:Logo` (object name) or `selection` (what was selected when the plan started).
- **Undo:** a plan that modifies an already-open document runs inside one CorelDRAW command group. If a step
  fails, the group is undone; if it succeeds, the whole plan is a single Ctrl+Z. Files already written, and
  documents a plan created or opened itself, are not rolled back.
- **Recipes:** any value in a plan can be a `{{VARIABLE}}`; Number/Boolean variables become real JSON values,
  so sizes and counts can be variables too.

## Running

```
dotnet build CorelSignStudio.sln
dotnet test  CorelSignStudio.sln                       # unit tests only; CorelDRAW not required
$env:COREL_INTEGRATION = "1"; dotnet test CorelSignStudio.sln   # also drives the installed CorelDRAW 2026
dotnet run --project CorelSignStudio.App
```

`CorelSignStudio.App.exe --screenshots <folder>` renders every tab to PNG and exits (for unattended UI review).

## Known limitations

- The built-in planner understands only a short list of test commands (shown in the Operator tab).
- Bitmap references (JPG/PNG) are only imported as a backdrop; recreating them as vectors needs the future AI analyzer.
- Coordinates are relative to the active page; multi-page documents are inspected fully but edited on the active page.
- PNG export covers the artwork's bounding box on the active page, not the full page rectangle.
- Recipe variables replace values; they cannot yet express formulas (for example "centre = width / 2") —
  use align/distribute actions for layout that must adapt.
- CSV is supported for batch data; Excel is a future reader producing the same `BatchRow` list.
- A CorelDRAW instance that is busy or showing a dialog blocks automation until the dialog is closed. With the
  CorelDRAW **trial**, hidden automation instances can be blocked by trial pop-ups and do not exit after `Quit()`;
  the operator window therefore always works with a visible CorelDRAW and never closes it.
