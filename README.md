# Corel AI Operator (Corel Sign Studio)

*Türkçe: [README.tr.md](README.tr.md)*

A .NET 8 WPF control centre that drives **CorelDRAW 2026** through late-bound COM automation.
CorelDRAW stays the design workspace; this application inspects the open document, shows a plan of
operations for review, executes it, and can replay successful jobs as recipes and batches.

It is a general automation platform — not a sign designer and not tied to any page size, company or
kind of job. Requests are planned by an AI planner when an API key is configured, and by a small deterministic planner otherwise.

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
- `CorelSignStudio.AI` — AI provider implementation (Anthropic SDK) and per-user AI settings with a DPAPI-encrypted key.
- `CorelSignStudio.Imaging` — reference previews (SkiaSharp, PDFtoImage/PDFium), SVG size reader, component cropper.
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

## AI planner

Natural-language requests are planned by `AiCommandPlanner` (Domain, provider-neutral) through `IAiClient`.
The first provider is the Claude API via the official Anthropic C# SDK, in `CorelSignStudio.AI`.

- The model only produces a plan. It is shown to the user and executed by the existing executor after approval.
- `DocumentContextBuilder` sends a compact, prioritised view of the inspected document; logical ids are kept.
- The prompt's action reference and the JSON schema are derived by reflection from the `CorelAction` types.
- `AiPlanParser` is the safety gate: unknown action types, unknown properties, shape ids that are not in the
  inspected document and plans failing `AutomationPlan.Validate()` are rejected and never reach the executor.
- Ambiguous requests return `NeedsClarification` with a question; the answer is planned with the earlier turns.
- `PlannerRouter` uses the AI planner when configured and selected, otherwise `DeterministicCommandPlanner`,
  which also takes over when the provider is unreachable and it understands the whole request.
- Settings and the API key live in `%LOCALAPPDATA%\CorelSignStudio\ai-settings.json`; the key is encrypted
  with Windows DPAPI for the current user. `ANTHROPIC_API_KEY` is used when no key is stored.
- Tests use a scripted mock `IAiClient`; no real API call is made.

## Reference vision and reconstruction

```
ReferenceInput -> IReferencePreviewRenderer -> AiReferenceAnalyzer -> ReferenceAnalysis
               -> ReferenceReconstructionPlanner -> AutomationPlan -> existing executor
```

- **What it does.** A reference is analysed into a structured description (`ref_NNN` elements with 0..1 bounds,
  z-order, groups, text, colours, tables, a reconstruction strategy and confidence) and then converted
  deterministically into existing actions: native rectangles, ellipses, lines, polygons, prohibition signs,
  editable text fitted to its analysed width, native tables, groups — created back to front.
- **File types.** JPG/PNG: EXIF-corrected, downscaled preview sent to the vision model. PDF: page preview via
  PDFtoImage (MIT) on PDFium (BSD-3/Apache-2.0); multi-page PDFs require an explicit page. SVG: declared size is
  read. CDR: size is read by CorelDRAW itself (no parser is invented); without CorelDRAW the size is unknown.
- **Vector reuse.** SVG, CDR and single-page PDF are imported, not redrawn, and are never uploaded.
- **Physical size.** Pixels are not millimetres. A size in the request (mm, cm, m) wins; otherwise the page size of
  a PDF/SVG/CDR; otherwise the user is asked. A raster's DPI tag is never trusted.
- **Honesty about artwork.** Logos and complex pictograms are not approximated with invented shapes: a matching
  library asset is used, or a crop of that component when the user asks for the bitmap, or a clearly named
  placeholder with a warning. The whole reference is never pasted in as one bitmap.
- **What is sent.** Only when the user starts an analysis (or prepares a reconstruction plan), only the selected
  reference's downscaled preview, to the configured provider. Image bytes are never logged — only file name,
  MIME type, pixel size and byte count.
- **Provider neutrality.** `AiRequest.Images` / `IAiClient.SupportsImages`; the Anthropic client is the first
  implementation. `IVisualComparisonService` is defined for the next milestone (compare output with reference).
- **Validation.** Unit tests use a scripted client and synthetic fixtures drawn by the tests. The opt-in
  CorelDRAW test rebuilds a sign in the real application and saves CDR/PDF; that part cannot run in the cloud.
- **Limits.** Not a general vector tracer: photographs and complex illustrations are not converted. Fonts are
  matched approximately (exact / likely / fallback is reported). Right-to-left text is created as written and
  flagged for checking. Merged table cells are reported, not reproduced. No automatic visual comparison yet.

## Language

The user interface is Turkish (`tr-TR`) by default. User-facing text is not hard-coded:

- `CorelSignStudio.App/Localization/Ui.resx` — window texts, used from XAML as `{local:Loc Key}` and from view models as `Ui.T/Ui.F`.
- `CorelSignStudio.Domain/Localization/Messages.resx` — plan step descriptions, validation, execution and planner messages (`Msg.Get/Msg.Format`).

To add English, add `Ui.en-US.resx` and `Messages.en-US.resx` and set `Msg.Culture` at start-up. Type names, JSON
contracts and technical log lines stay in English. The test planner understands Turkish and English commands.

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
