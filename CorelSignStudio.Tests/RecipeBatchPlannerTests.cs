using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;

namespace CorelSignStudio.Tests;

internal static class TestFolders
{
    public static string Create() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CorelSignStudioTests", Guid.NewGuid().ToString("N"))).FullName;
}

public sealed class RecipeTests
{
    private static Recipe DoorSignRecipe() => RecipeBuilder.FromPlan(
        AutomationTestData.DoorSignPlan(),
        "Employee Door Sign",
        [
            new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz"),
            new RecipeVariableBinding("WIDTH_MM", "200", RecipeVariableType.Number, PropertyName: "widthMm"),
        ]);

    [Fact]
    public void Recipe_built_from_a_plan_replaces_literals_with_variables()
    {
        var recipe = DoorSignRecipe();
        var template = recipe.PlanTemplate.ToJsonString();

        Assert.Equal(["PERSON_NAME", "WIDTH_MM"], recipe.Variables.Select(variable => variable.Name));
        Assert.Contains("{{PERSON_NAME}}", template);
        Assert.Contains("\"widthMm\":\"{{WIDTH_MM}}\"", template);
        Assert.DoesNotContain("Ahmet", template);
        Assert.Equal("Ahmet Yılmaz", recipe.Variables[0].DefaultValue);
        Assert.Equal(RecipeVariableType.Number, recipe.Variables[1].Type);
    }

    [Fact]
    public void Recipe_reproduces_the_layout_with_new_content()
    {
        var plan = DoorSignRecipe().Instantiate(new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya", ["width_mm"] = "250" });

        Assert.True(plan.Validate().IsValid, plan.Validate().ToString());
        Assert.Equal(250, ((CreateDocumentAction)plan.Actions[0]).WidthMm);
        Assert.Equal(80, ((CreateDocumentAction)plan.Actions[0]).HeightMm);
        Assert.Equal(190, ((CreateRectangleAction)plan.Actions[1]).WidthMm); // limited to the bound property
        Assert.Equal("Mehmet Kaya", ((CreateTextAction)plan.Actions[2]).Text);
        Assert.Equal(["doc", "frame", "name", "center"], plan.Actions.Select(action => action.Id));
        Assert.Equal("Mehmet Kaya", plan.Parameters["PERSON_NAME"]);
        Assert.Equal("Create employee door sign for Mehmet Kaya.", plan.UserRequest);
        Assert.Equal("Employee Door Sign", plan.Metadata["recipeName"]);
        Assert.NotEqual(AutomationTestData.DoorSignPlan().Id, plan.Id);
    }

    [Fact]
    public void Recipe_defaults_reproduce_the_original_job()
    {
        var plan = DoorSignRecipe().Instantiate();

        Assert.Equal("Ahmet Yılmaz", ((CreateTextAction)plan.Actions[2]).Text);
        Assert.Equal(200, ((CreateDocumentAction)plan.Actions[0]).WidthMm);
    }

    [Fact]
    public void Recipe_round_trips_through_json()
    {
        var recipe = DoorSignRecipe() with { Category = "Signage", Tags = ["door", "name"] };

        var json = recipe.ToJson();
        var restored = Recipe.FromJson(json);

        Assert.Equal(json, restored.ToJson());
        Assert.Equal("Mehmet Kaya", ((CreateTextAction)restored.Instantiate(new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya" }).Actions[2]).Text);
    }

    [Fact]
    public void Missing_required_variable_is_reported()
    {
        var recipe = DoorSignRecipe() with { Variables = [new RecipeVariable { Name = "PERSON_NAME" }, new RecipeVariable { Name = "WIDTH_MM", Type = RecipeVariableType.Number, DefaultValue = "200" }] };

        var exception = Assert.Throws<RecipeVariableException>(() => recipe.Instantiate());

        Assert.Contains("PERSON_NAME", Assert.Single(exception.Errors));
    }

    [Fact]
    public void Wrong_typed_variable_is_reported()
    {
        var exception = Assert.Throws<RecipeVariableException>(() =>
            DoorSignRecipe().Instantiate(new Dictionary<string, string> { ["WIDTH_MM"] = "wide" }));

        Assert.Contains("sayı olmalı", exception.Errors[0]);
    }

    [Fact]
    public void Binding_a_value_that_is_not_in_the_plan_is_an_error() =>
        Assert.Throws<ArgumentException>(() => RecipeBuilder.FromPlan(
            AutomationTestData.DoorSignPlan(), "X", [new RecipeVariableBinding("COMPANY", "ACME Ltd")]));

    [Fact]
    public void Placeholders_already_in_a_plan_become_required_variables()
    {
        var plan = new AutomationPlan
        {
            Name = "Serial labels",
            Actions =
            [
                new CreateTextAction { Id = "serial", Text = "S/N {{SERIAL_NUMBER}} — {{COMPANY}}" },
                new ExportPdfAction { Id = "pdf", FilePath = @"C:\out\{{OUTPUT_NAME}}.pdf" },
            ],
        };

        var recipe = RecipeBuilder.FromPlan(plan, "Serial label");
        var filled = recipe.Instantiate(new Dictionary<string, string>
        {
            ["SERIAL_NUMBER"] = "000123", ["COMPANY"] = "ACME", ["OUTPUT_NAME"] = "label_000123",
        });

        Assert.Equal(["SERIAL_NUMBER", "COMPANY", "OUTPUT_NAME"], recipe.Variables.Select(variable => variable.Name));
        Assert.All(recipe.Variables, variable => Assert.True(variable.IsRequired));
        Assert.Equal("S/N 000123 — ACME", ((CreateTextAction)filled.Actions[0]).Text);
        Assert.Equal(@"C:\out\label_000123.pdf", ((ExportPdfAction)filled.Actions[1]).FilePath);
    }

    [Fact]
    public void Text_substitution_handles_spacing_case_and_repeats()
    {
        var errors = new List<string>();
        var values = new Dictionary<string, string> { ["NAME"] = "Ali", ["HEIGHT_MM"] = "12,5" };

        var text = VariableSubstitution.Apply("{{ name }} / {{NAME}} / {{MISSING}}", values, errors);

        Assert.Equal("Ali / Ali / {{MISSING}}", text);
        Assert.Equal("'MISSING' değişkeninin değeri yok.", Assert.Single(errors));
        Assert.True(VariableSubstitution.TryParseNumber("12,5", out var number));
        Assert.Equal(12.5, number);
        Assert.True(VariableSubstitution.TryParseBoolean("Evet", out var flag) && flag);
    }

    [Fact]
    public void Number_and_boolean_variables_become_real_json_values()
    {
        var plan = new AutomationPlan
        {
            Name = "Table",
            Actions =
            [
                new CreateTableAction { Id = "t", WidthMm = 100, HeightMm = 50, Columns = 8, Rows = 20 },
                new CreateTextAction { Id = "x", Text = "caption", Bold = true },
            ],
        };
        var recipe = RecipeBuilder.FromPlan(plan, "Table",
        [
            new RecipeVariableBinding("ROWS", "20", RecipeVariableType.Number, "rows"),
            new RecipeVariableBinding("BOLD", "true", RecipeVariableType.Boolean, "bold"),
        ]);

        var filled = recipe.Instantiate(new Dictionary<string, string> { ["ROWS"] = "35", ["BOLD"] = "false" });

        Assert.Equal(35, ((CreateTableAction)filled.Actions[0]).Rows);
        Assert.Equal(8, ((CreateTableAction)filled.Actions[0]).Columns);
        Assert.False(((CreateTextAction)filled.Actions[1]).Bold);
    }

    [Fact]
    public void Variable_names_are_normalized() =>
        Assert.Equal(["PERSON_NAME", "AD_SOYAD", "ISIM", "_2ND"], new[] { "{{person-name}}", "Ad Soyad", "İsim", "2nd" }.Select(RecipeBuilder.NormalizeName));

    [Fact]
    public void Recipe_store_persists_recipes_as_files()
    {
        var folder = TestFolders.Create();
        var store = new JsonRecipeStore(folder);
        var recipe = DoorSignRecipe();

        store.Save(recipe);
        store.Save(recipe with { Description = "updated" });
        var reloaded = new JsonRecipeStore(folder);

        Assert.Equal("updated", Assert.Single(reloaded.GetAll()).Description);
        Assert.Equal(recipe.Id, reloaded.FindByName("employee door sign")!.Id);
        Assert.Equal("Mehmet Kaya", ((CreateTextAction)reloaded.Get(recipe.Id)!.Instantiate(
            new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya" }).Actions[2]).Text);
        Assert.True(reloaded.Delete(recipe.Id));
        Assert.Empty(reloaded.GetAll());
        Assert.False(reloaded.Delete(recipe.Id));
    }
}

public sealed class BatchTests
{
    private static Recipe NameRecipe() => RecipeBuilder.FromPlan(
        AutomationTestData.DoorSignPlan(), "Door Sign", [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz")]);

    private static BatchJob Job(params string[] names) => new()
    {
        Name = "Door signs",
        RecipeId = "r",
        OutputFolder = @"C:\out",
        OutputNamePattern = "{{PERSON_NAME}}",
        Rows = names.Select((name, index) => new BatchRow(index + 1, new Dictionary<string, string> { ["PERSON_NAME"] = name })).ToArray(),
    };

    [Fact]
    public void Each_row_expands_into_its_own_plan_with_outputs()
    {
        var items = BatchExpander.Expand(Job("Mehmet Kaya", "Ayşe Öztürk"), NameRecipe(), new OutputNameAllocator(_ => false));

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.True(item.IsValid, item.Error));
        Assert.Equal("Mehmet_Kaya", items[0].OutputBaseName);
        Assert.Equal("Ayse_Ozturk", items[1].OutputBaseName);
        Assert.Equal([@"C:\out\Ayse_Ozturk.cdr", @"C:\out\Ayse_Ozturk.pdf"], items[1].OutputFiles);

        var plan = items[1].Plan!;
        Assert.Equal("Ayşe Öztürk", plan.Actions.OfType<CreateTextAction>().Single().Text);
        Assert.Equal(["doc", "frame", "name", "center", "batch_cdr", "batch_pdf", "batch_close"], plan.Actions.Select(action => action.Id));
        Assert.Equal(@"C:\out\Ayse_Ozturk.cdr", plan.Actions.OfType<SaveDocumentAction>().Single().FilePath);
        Assert.Equal("002", plan.Parameters[BatchVariables.Row]);
        Assert.NotEqual(items[0].Plan!.Id, plan.Id);
    }

    [Fact]
    public void Duplicate_names_within_a_batch_get_distinct_files()
    {
        var items = BatchExpander.Expand(Job("Ali Can", "Ali Can", "ALI CAN"), NameRecipe(), new OutputNameAllocator(_ => false));

        Assert.Equal(["Ali_Can", "Ali_Can_002", "ALI_CAN_003"], items.Select(item => item.OutputBaseName));
        Assert.Equal(6, items.SelectMany(item => item.OutputFiles).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Existing_files_are_never_overwritten()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\out\Ali_Can.pdf", @"C:\out\Ali_Can_002.cdr" };
        var allocator = new OutputNameAllocator(existing.Contains);

        Assert.Equal("Ali_Can_003", allocator.Allocate(@"C:\out", "Ali Can", [".cdr", ".pdf"]));
        Assert.Equal("Ali_Can_004", allocator.Allocate(@"C:\out", "Ali Can", [".cdr", ".pdf"]));
        Assert.Equal("Ali_Can", allocator.Allocate(@"C:\other", "Ali Can", [".cdr", ".pdf"]));
    }

    [Fact]
    public void Allocator_checks_the_real_file_system_by_default()
    {
        var folder = TestFolders.Create();
        File.WriteAllText(Path.Combine(folder, "label.pdf"), "existing");

        Assert.Equal("label_002", new OutputNameAllocator().Allocate(folder, "label", [".cdr", ".pdf"]));
    }

    [Theory]
    [InlineData("Ayşe Öztürk / Ğ", "Ayse_Ozturk_G")]
    [InlineData("  a:b*c?  ", "a_b_c")]
    [InlineData("İSTANBUL şube №5", "ISTANBUL_sube_5")]
    [InlineData("...", "output")]
    [InlineData("CON", "_CON")]
    [InlineData("v1.2-final", "v1.2-final")]
    public void File_names_are_sanitized(string input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void A_bad_row_does_not_break_the_others()
    {
        var recipe = NameRecipe() with { Variables = [new RecipeVariable { Name = "PERSON_NAME" }] };
        var job = Job("Mehmet Kaya") with
        {
            OutputNamePattern = "sign_{{ROW}}",
            Rows = [new BatchRow(1, new Dictionary<string, string>()), new BatchRow(2, new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya" })],
        };

        var items = BatchExpander.Expand(job, recipe, new OutputNameAllocator(_ => false));

        Assert.False(items[0].IsValid);
        Assert.Contains("PERSON_NAME", items[0].Error);
        Assert.True(items[1].IsValid);
        Assert.Equal("sign_002", items[1].OutputBaseName);
    }

    [Fact]
    public void Default_output_name_and_formats_follow_the_job()
    {
        var job = Job("A", "B") with { OutputNamePattern = null, Formats = [OutputFormat.Png, OutputFormat.Svg], CloseDocumentAfterEachRow = false };

        var items = BatchExpander.Expand(job, NameRecipe(), new OutputNameAllocator(_ => false));

        Assert.Equal(["Door_Sign_001", "Door_Sign_002"], items.Select(item => item.OutputBaseName));
        Assert.IsType<ExportSvgAction>(items[0].Plan!.Actions[^1]);
        Assert.IsType<ExportPngAction>(items[0].Plan!.Actions[^2]);
    }

    [Fact]
    public void The_users_open_document_is_never_closed_by_a_batch()
    {
        var activeDocumentPlan = new AutomationPlan
        {
            Name = "Replace name",
            Actions = [new SetTextAction { Id = "set", Targets = ["name:PersonName"], Text = "Ahmet Yılmaz" }],
        };
        var recipe = RecipeBuilder.FromPlan(activeDocumentPlan, "Replace name", [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz")]);

        var items = BatchExpander.Expand(Job("Mehmet Kaya"), recipe, new OutputNameAllocator(_ => false));

        Assert.DoesNotContain(items[0].Plan!.Actions, action => action is CloseDocumentAction);
        Assert.Equal(["name:PersonName"], ((SetTextAction)items[0].Plan!.Actions[0]).Targets);
    }

    [Fact]
    public async Task Runner_reports_progress_and_per_row_results()
    {
        var executor = new FakeExecutor { ShouldFail = plan => plan.Name.EndsWith("satır 2", StringComparison.Ordinal) };
        var progress = new List<BatchProgress>();

        var result = await new BatchRunner(executor).RunAsync(
            Job("A", "B", "C"), NameRecipe(), new CollectingProgress(progress), new OutputNameAllocator(_ => false));

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Success);
        Assert.False(result.Items[1].Success);
        Assert.Contains("batch_close", result.Items[1].Error);
        Assert.Equal([@"C:\out\A.cdr", @"C:\out\A.pdf"], result.Items[0].ProducedFiles);
        Assert.Equal(3, executor.Plans.Count);
        var last = progress[^1];
        Assert.Equal((3, 3, 2, 1), (last.Total, last.Completed, last.Succeeded, last.Failed));
        Assert.Equal(100, last.Percent);
    }

    [Fact]
    public async Task Runner_can_stop_at_the_first_failure()
    {
        var executor = new FakeExecutor { ShouldFail = _ => true };

        var result = await new BatchRunner(executor).RunAsync(
            Job("A", "B", "C") with { ContinueOnError = false }, NameRecipe(), allocator: new OutputNameAllocator(_ => false));

        Assert.True(result.StoppedOnError);
        Assert.Single(result.Items);
    }

    [Fact]
    public void Csv_rows_become_batch_rows()
    {
        var rows = CsvBatchReader.Parse("Ad Soyad;Company;WIDTH_MM\r\nMehmet Kaya;\"ACME; \"\"Ltd\"\"\";250\r\n\r\nAyşe Öztürk;Beta;300\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Mehmet Kaya", rows[0].Values["AD_SOYAD"]);
        Assert.Equal("ACME; \"Ltd\"", rows[0].Values["company"]);
        Assert.Equal("300", rows[1].Values["WIDTH_MM"]);
        Assert.Equal(2, rows[1].RowNumber);
    }

    [Fact]
    public void Csv_detects_comma_delimiter_and_pads_short_rows()
    {
        var rows = CsvBatchReader.Parse("name,serial\nA,1\nB");

        Assert.Equal("1", rows[0].Values["SERIAL"]);
        Assert.Equal("", rows[1].Values["SERIAL"]);
        Assert.Throws<FormatException>(() => CsvBatchReader.Parse("a,a\n1,2"));
    }

    [Fact]
    public void Batch_job_round_trips_through_json()
    {
        var job = Job("A", "B");

        var restored = AutomationJson.Deserialize<BatchJob>(AutomationJson.Serialize(job));

        Assert.Equal(AutomationJson.Serialize(job), AutomationJson.Serialize(restored));
        Assert.Equal("B", restored.Rows[1].Values["PERSON_NAME"]);
    }

    private sealed class CollectingProgress(List<BatchProgress> reports) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => reports.Add(value);
    }
}

public sealed class DeterministicPlannerTests
{
    private static AutomationPlan Plan(string request, DocumentSnapshot? document = null)
    {
        var result = new DeterministicCommandPlanner().Plan(new PlanningRequest { UserRequest = request, Document = document });
        Assert.True(result.Success, string.Join(" | ", result.UnrecognizedCommands));
        Assert.True(result.Plan!.Validate().IsValid, result.Plan.Validate().ToString());
        return result.Plan;
    }

    [Fact]
    public void Creates_a_document()
    {
        var plan = Plan("Create a 500x700 mm document");

        var action = Assert.IsType<CreateDocumentAction>(Assert.Single(plan.Actions));
        Assert.Equal((500, 700), (action.WidthMm, action.HeightMm));
        Assert.Equal(DocumentTarget.NewDocument, plan.Target);
    }

    [Fact]
    public void Adds_centred_text()
    {
        var plan = Plan("Add text TEST in the center");

        var text = Assert.IsType<CreateTextAction>(plan.Actions[0]);
        var align = Assert.IsType<AlignAction>(plan.Actions[1]);
        Assert.Equal("TEST", text.Text);
        Assert.Equal(["@" + text.Id], align.Targets);
        Assert.Equal((HorizontalAlign.Center, VerticalAlign.Center, AlignReference.Page), (align.Horizontal, align.Vertical, align.RelativeTo));
        Assert.Equal(DocumentTarget.ActiveDocument, plan.Target);
    }

    [Theory]
    [InlineData("Move shape_001 10 mm right", 10, 0)]
    [InlineData("move SHAPE_001 2.5mm left", -2.5, 0)]
    [InlineData("Move shape_001 up by 10 mm", 0, -10)]
    [InlineData("shape_001'i 10 mm aşağı taşı", 0, 10)]
    public void Moves_a_shape(string request, double dx, double dy)
    {
        var move = Assert.IsType<MoveAction>(Assert.Single(Plan(request).Actions));

        Assert.Equal(["shape_001"], move.Targets);
        Assert.Equal((dx, dy), (move.DeltaXMm, move.DeltaYMm));
    }

    [Fact]
    public void Changes_text()
    {
        var action = Assert.IsType<SetTextAction>(Assert.Single(Plan("Change shape_001 text to HELLO").Actions));

        Assert.Equal(("shape_001", "HELLO"), (action.Targets[0], action.Text));
    }

    [Fact]
    public void Resizes_a_shape()
    {
        var action = Assert.IsType<ResizeAction>(Assert.Single(Plan("Resize shape_002 to 100x50 mm").Actions));

        Assert.Equal(("shape_002", 100d, 50d, false), (action.Targets[0], action.WidthMm, action.HeightMm, action.KeepAspectRatio));
    }

    [Fact]
    public void Understands_a_multi_line_request_as_one_ordered_plan()
    {
        var plan = Plan("""
            Create a 500x700 mm document
            Add text TEST in the center
            Make it bigger; rotate it 15 degrees
            Create a table with 8 columns and 20 rows and center all text
            Set fill of "Title" to red
            Align selection left
            Save as C:\Output\job.cdr. Export PDF to C:\Output\job.pdf
            """);

        Assert.Equal(
            ["createDocument", "createText", "align", "resize", "rotate", "createTable", "setFill", "align", "saveDocument", "exportPdf"],
            plan.Actions.Select(action => action.TypeName));
        Assert.Equal(["@text1"], plan.Actions.OfType<ResizeAction>().Single().Targets);
        var table = plan.Actions.OfType<CreateTableAction>().Single();
        Assert.Equal((8, 20, TextAlignment.Center), (table.Columns, table.Rows, table.CellAlignment));
        Assert.Equal(("name:Title", "#FF0000"), (plan.Actions.OfType<SetFillAction>().Single().Targets[0], plan.Actions.OfType<SetFillAction>().Single().Color));
        Assert.Equal(["selection"], plan.Actions.OfType<AlignAction>().Last().Targets);
        Assert.Equal(@"C:\Output\job.pdf", plan.Actions.OfType<ExportPdfAction>().Single().FilePath);
        Assert.Equal(plan.Actions.Count, plan.Actions.Select(action => action.Id).Distinct().Count());
    }

    [Fact]
    public void Sets_the_page_size_of_the_open_design()
    {
        var action = Assert.IsType<SetPageSizeAction>(Assert.Single(Plan("Make this design 500x700 mm.").Actions));

        Assert.Equal((500, 700), (action.WidthMm, action.HeightMm));
    }

    [Fact]
    public void A_table_fills_the_inspected_page_with_a_margin()
    {
        var document = new DocumentSnapshot { Title = "d", Pages = [new PageSnapshot { Index = 1, WidthMm = 200, HeightMm = 100 }] };

        var table = Assert.IsType<CreateTableAction>(Assert.Single(Plan("Create a table with 3 columns and 4 rows", document).Actions));

        Assert.Equal((20, 10, 160, 80), (table.XMm, table.YMm, table.WidthMm, table.HeightMm));
    }

    [Fact]
    public void Unknown_commands_are_reported_not_guessed()
    {
        var result = new DeterministicCommandPlanner().Plan(new PlanningRequest
        {
            UserRequest = "Move shape_001 10 mm right\nMake the logo look more premium\nSet fill of shape_002 to chartreuse",
        });

        Assert.False(result.Success);
        Assert.Single(result.Plan!.Actions);
        Assert.Equal(2, result.UnrecognizedCommands.Count);
        Assert.Contains("premium", result.UnrecognizedCommands[0]);
        Assert.Contains("chartreuse", result.UnrecognizedCommands[1]);
    }

    [Fact]
    public async Task Empty_request_produces_no_plan()
    {
        ICommandPlanner planner = new DeterministicCommandPlanner();

        var result = await planner.PlanAsync(new PlanningRequest { UserRequest = "  " });

        Assert.Null(result.Plan);
        Assert.False(result.Success);
    }

    [Fact]
    public void Every_documented_example_is_understood() =>
        Assert.All(DeterministicCommandPlanner.Examples.Concat(DeterministicCommandPlanner.EnglishExamples), example => Plan(example));
}

public sealed class ReferenceAndAssetTests
{
    [Theory]
    [InlineData("design.CDR", ReferenceFileType.Cdr, true)]
    [InlineData("print.pdf", ReferenceFileType.Pdf, true)]
    [InlineData("icon.svg", ReferenceFileType.Svg, true)]
    [InlineData("photo.jpg", ReferenceFileType.Jpeg, false)]
    [InlineData("photo.JPEG", ReferenceFileType.Jpeg, false)]
    [InlineData("shot.png", ReferenceFileType.Png, false)]
    public void Reference_types_are_detected(string file, ReferenceFileType type, bool vector)
    {
        var reference = ReferenceInput.FromFile(Path.Combine(@"C:\refs", file));

        Assert.Equal(type, reference.FileType);
        Assert.Equal(vector, reference.IsVector);
        Assert.Equal(file, reference.FileName);
    }

    [Fact]
    public void Unsupported_reference_is_rejected() =>
        Assert.Throws<NotSupportedException>(() => ReferenceInput.FromFile(@"C:\refs\notes.docx"));

    [Fact]
    public async Task File_analyzer_reads_png_dimensions_and_flags_vector_reuse()
    {
        var folder = TestFolders.Create();
        var png = Path.Combine(folder, "ref.png");
        File.WriteAllBytes(png,
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x03, 0x20, 0, 0, 0x02, 0x58, 8, 6, 0, 0, 0,
        ]);
        var svg = Path.Combine(folder, "ref.svg");
        File.WriteAllText(svg, "<svg xmlns='http://www.w3.org/2000/svg'/>");
        var analyzer = new FileReferenceAnalyzer();

        var bitmap = await analyzer.AnalyzeAsync(ReferenceInput.FromFile(png));
        var vector = await analyzer.AnalyzeAsync(ReferenceInput.FromFile(svg));

        Assert.Equal((800, 600), (bitmap.PixelWidth, bitmap.PixelHeight));
        Assert.False(bitmap.CanReuseVectorContent);
        Assert.True(vector.CanReuseVectorContent);
        Assert.Equal(AutomationJson.Serialize(vector), AutomationJson.Serialize(AutomationJson.Deserialize<ReferenceAnalysis>(AutomationJson.Serialize(vector))));
    }

    [Fact]
    public void Vector_references_are_reused_by_importing_them()
    {
        var reference = ReferenceInput.FromFile(@"C:\refs\logo.svg");
        var analysis = new ReferenceAnalysis { ReferenceId = reference.Id, AnalyzerName = "test", CanReuseVectorContent = true };

        var plan = new ImportReferencePlanBuilder().BuildPlan(reference, analysis);

        var import = Assert.IsType<ImportFileAction>(Assert.Single(plan.Actions));
        Assert.Equal(reference.FilePath, import.FilePath);
        Assert.True(plan.Validate().IsValid);

        var source = ReferenceInput.FromFile(@"C:\refs\old.cdr", ReferenceRole.SourceDocument);
        var openPlan = new ImportReferencePlanBuilder().BuildPlan(source, analysis);
        Assert.IsType<OpenDocumentAction>(Assert.Single(openPlan.Actions));
        Assert.Equal(DocumentTarget.NewDocument, openPlan.Target);
    }

    [Fact]
    public void Asset_library_stores_searches_and_removes_assets()
    {
        var folder = TestFolders.Create();
        var source = Path.Combine(folder, "ACME logo.svg");
        File.WriteAllText(source, "<svg/>");
        var library = new JsonAssetLibrary(Path.Combine(folder, "library"));

        var logo = library.Add(source, category: "Customer logos", tags: ["acme", "logo", "ACME"],
            metadata: new Dictionary<string, string> { ["customer"] = "ACME Ltd" });
        var second = library.Add(source, name: "ACME logo");
        var sign = library.Add(source, name: "No entry", category: "Traffic signs", tags: ["prohibition"]);

        Assert.Equal("acme_logo", logo.Id);
        Assert.Equal("acme_logo-2", second.Id);
        Assert.Equal(("svg", "Customer logos"), (logo.FileType, logo.Category));
        Assert.Equal(["acme", "logo"], logo.Tags);
        Assert.True(File.Exists(logo.FilePath));

        var reloaded = new JsonAssetLibrary(Path.Combine(folder, "library"));
        Assert.Equal(3, reloaded.GetAll().Count);
        Assert.Equal(["Customer logos", "Genel", "Traffic signs"], reloaded.GetCategories());
        Assert.Equal(sign.Id, Assert.Single(reloaded.Search(new AssetQuery { Text = "entry" })).Id);
        Assert.Equal(logo.Id, Assert.Single(reloaded.Search(new AssetQuery { Tags = ["LOGO"] })).Id);
        Assert.Equal(logo.Id, Assert.Single(reloaded.Search(new AssetQuery { Text = "ltd" })).Id);
        Assert.Equal(3, reloaded.Search(new AssetQuery { FileType = ".svg" }).Count);
        Assert.Empty(reloaded.Search(new AssetQuery { Category = "Fonts" }));

        reloaded.Update(sign with { Tags = ["prohibition", "round"] });
        Assert.Contains("round", reloaded.Get(sign.Id)!.Tags);
        Assert.True(reloaded.Remove(logo.Id));
        Assert.False(File.Exists(logo.FilePath));
        Assert.True(File.Exists(source)); // the user's original is never touched
        Assert.Null(reloaded.Get(logo.Id));
    }

    [Fact]
    public void Execution_history_is_kept_newest_first()
    {
        var store = new JsonExecutionHistoryStore(TestFolders.Create());
        var plan = AutomationTestData.DoorSignPlan();
        var ok = PlanExecutionEngine.Run(plan, new FakeAutomationSession());
        var failed = PlanExecutionEngine.Run(plan, new FakeAutomationSession { FailOnActionId = "name" });

        store.Append(new ExecutionHistoryEntry { Plan = plan, Result = ok, TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-5) });
        store.Append(new ExecutionHistoryEntry { Plan = plan, Result = failed });

        var recent = store.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("name", recent[0].Result.FailedActionId);
        Assert.True(recent[1].Result.Success);
        Assert.Equal(4, recent[1].Plan.Actions.Count);
        Assert.Single(store.GetRecent(1));
    }
}
