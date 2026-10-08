using System.Text.RegularExpressions;
using System.Xml.Linq;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Recipes;

namespace CorelSignStudio.Tests;

public sealed partial class LocalizationTests
{
    private static readonly string SolutionRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string MessagesPath = Path.Combine(SolutionRoot, "CorelSignStudio.Domain", "Localization", "Messages.resx");
    private static readonly string UiPath = Path.Combine(SolutionRoot, "CorelSignStudio.App", "Localization", "Ui.resx");

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value, StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(params string[] projects) =>
        projects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(SolutionRoot, project), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    /// <summary>Every dotted string literal in the sources whose first segment is a catalogue group, e.g. "Corel.NoDocument".</summary>
    private static HashSet<string> ReferencedKeys(IEnumerable<string> files, IEnumerable<string> catalogueKeys)
    {
        var groups = catalogueKeys.Select(key => key.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in KeyLiteral().Matches(text))
            {
                var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (groups.Contains(key.Split('.')[0]))
                {
                    found.Add(key);
                }
            }
        }

        return found;
    }

    [Fact]
    public void Default_language_is_turkish()
    {
        Assert.Equal("tr-TR", Msg.DefaultCultureName);
        Assert.Equal("tr-TR", Msg.Culture.Name);
        Assert.Equal("12,5", Msg.Number(12.5));
        Assert.Equal("500", Msg.Number(500));
    }

    [Fact]
    public void Message_catalogue_has_every_key_the_code_uses()
    {
        var messages = ReadResx(MessagesPath);
        var used = ReferencedKeys(
            SourceFiles("CorelSignStudio.Domain", "CorelSignStudio.Corel", "CorelSignStudio.Storage"), messages.Keys);

        Assert.True(used.Count > 150, $"only {used.Count} keys were found in the sources");
        Assert.Empty(used.Where(key => !messages.ContainsKey(key) && !key.EndsWith('.')));
        Assert.All(messages, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.All(messages.Keys, key => Assert.True(Msg.Has(key), key));
        Assert.Equal("[No.Such.Key]", Msg.Get("No.Such.Key"));
    }

    [Fact]
    public void Ui_catalogue_has_every_key_the_window_and_view_model_use()
    {
        var ui = ReadResx(UiPath);
        var messages = ReadResx(MessagesPath);
        var appFiles = SourceFiles("CorelSignStudio.App").ToArray();

        var used = ReferencedKeys(appFiles, ui.Keys);
        Assert.True(used.Count > 150, $"only {used.Count} keys were found in the sources");
        Assert.Empty(used.Where(key => !ui.ContainsKey(key) && !messages.ContainsKey(key) && !key.EndsWith('.')));
        Assert.All(ui, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));

        // Nothing in the catalogue is dead weight either.
        Assert.Empty(ui.Keys.Where(key => !used.Contains(key) && !key.StartsWith("Recipe.Type.", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dynamically_built_keys_exist_for_every_enum_value()
    {
        var messages = ReadResx(MessagesPath);
        var ui = ReadResx(UiPath);

        Assert.All(Enum.GetNames<ShapeKind>(), name => Assert.Contains("ShapeKind." + name, messages.Keys));
        Assert.All(Enum.GetNames<RecipeVariableType>(), name => Assert.Contains("Recipe.Type." + name, messages.Keys));
        Assert.All(Enum.GetNames<RecipeVariableType>(), name => Assert.Contains("Recipe.Type." + name, ui.Keys));
    }

    [Theory]
    [InlineData("App.Title", "Corel AI Operatörü")]
    [InlineData("Header.Connect", "Bağlan")]
    [InlineData("Tab.Operator", "Operatör")]
    [InlineData("Tab.Document", "Açık Belge")]
    [InlineData("Tab.Recipes", "Otomasyonlar")]
    [InlineData("Tab.Batch", "Toplu İşler")]
    [InlineData("Tab.Assets", "Varlıklar")]
    [InlineData("Tab.History", "Geçmiş")]
    [InlineData("Tab.Settings", "Ayarlar")]
    [InlineData("Operator.ReferenceFiles", "Referans Dosyaları")]
    [InlineData("Operator.RequestTitle", "CorelDRAW ne yapsın?")]
    [InlineData("Common.InspectDocument", "Belgeyi İncele")]
    [InlineData("Operator.PreparePlan", "Planı Hazırla")]
    [InlineData("Operator.ProposedOperations", "Yapılacak İşlemler")]
    [InlineData("Operator.Execute", "CORELDRAW'DA UYGULA")]
    [InlineData("Operator.SaveAsRecipe", "Otomasyon Olarak Kaydet")]
    [InlineData("Operator.Activity", "İşlem Geçmişi")]
    [InlineData("Common.Browse", "Gözat…")]
    [InlineData("Connection.NotConnected", "Bağlı değil")]
    public void Main_ui_texts_are_turkish(string key, string expected) => Assert.Equal(expected, ReadResx(UiPath)[key]);

    [Fact]
    public void Catalogues_contain_no_leftover_english_sentences()
    {
        var english = EnglishWords();
        foreach (var (key, value) in ReadResx(UiPath).Concat(ReadResx(MessagesPath)))
        {
            Assert.False(english.IsMatch(value), $"{key}: {value}");
        }
    }

    [Fact]
    public void Operator_window_has_no_hard_coded_texts()
    {
        var xaml = File.ReadAllText(Path.Combine(SolutionRoot, "CorelSignStudio.App", "OperatorWindow.xaml"));

        var literals = HardCodedXamlText().Matches(xaml).Select(match => match.Groups[1].Value)
            .Where(text => text is not ("X" or "Y" or "CDR" or "PDF" or "PNG" or "SVG"))
            .ToArray();

        Assert.Empty(literals);
        Assert.Contains("{local:Loc Tab.Operator}", xaml);
    }

    [Fact]
    public void Turkish_letters_survive_resources_and_json()
    {
        const string Sample = "GİRİŞ ÇIKIŞ DÖNDÜR ÇOĞALT İŞLEM GÖZAT çğıiöşü ÇĞİIÖŞÜ";
        var plan = new AutomationPlan
        {
            Name = Sample,
            UserRequest = Sample,
            Actions = [new CreateTextAction { Id = "t", Text = Sample, Name = "Başlık" }],
            Parameters = new Dictionary<string, string> { ["KİŞİ_ADI"] = "Şükrü Öğüt" },
        };

        var json = plan.ToJson();
        var restored = AutomationPlan.FromJson(json);

        Assert.Contains(Sample, json); // written as readable UTF-8, not \u escapes
        Assert.Equal(Sample, ((CreateTextAction)restored.Actions[0]).Text);
        Assert.Equal("Şükrü Öğüt", restored.Parameters["KİŞİ_ADI"]);
        Assert.Equal("İşlemi Onayla", ReadResx(UiPath)["Confirm.DestructiveTitle"]);
        Assert.Contains("ğ", Msg.Get("Describe.Duplicate"));
        Assert.Equal(1, File.ReadAllBytes(UiPath).AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 1 : 0); // UTF-8 with BOM
    }

    [Fact]
    public void Every_action_is_described_in_turkish()
    {
        foreach (var action in AutomationTestData.OneOfEveryAction())
        {
            var text = ActionDescriber.Describe(action);
            Assert.DoesNotContain("[", text);
            Assert.False(EnglishWords().IsMatch(text), $"{action.TypeName}: {text}");
        }

        Assert.Equal("shape_001: 10 mm sağa ve 5 mm yukarı taşı",
            ActionDescriber.Describe(new MoveAction { Id = "m", Targets = ["shape_001"], DeltaXMm = 10, DeltaYMm = -5 }));
        Assert.Equal("Seçili nesneler: %120 ölçeğine getir",
            ActionDescriber.Describe(new ResizeAction { Id = "r", Targets = ["selection"], ScalePercent = 120 }));
        Assert.Equal("shape_002: boyutunu 12,5 x 40 mm yap",
            ActionDescriber.Describe(new ResizeAction { Id = "r", Targets = ["shape_002"], WidthMm = 12.5, HeightMm = 40 }));
    }

    [Fact]
    public void Validation_and_result_messages_are_turkish()
    {
        var invalid = new AutomationPlan { Name = "p", Actions = [new CreateRectangleAction { Id = "r", WidthMm = 0, HeightMm = 10 }] };
        Assert.Equal("[r] WidthMm sıfırdan büyük bir sayı olmalı.", Assert.Single(invalid.Validate().Errors).ToString());

        var failed = PlanExecutionEngine.Run(AutomationTestData.DoorSignPlan(), new FakeAutomationSession { FailOnActionId = "name" });
        Assert.StartsWith("3. adım ('name') başarısız oldu:", failed.Summary);

        var ok = PlanExecutionEngine.Run(AutomationTestData.DoorSignPlan(), new FakeAutomationSession());
        Assert.Equal("4 işlem tamamlandı.", ok.Summary);

        var exception = Assert.Throws<NotSupportedException>(() => Domain.References.ReferenceInput.FromFile(@"C:\x\notlar.docx"));
        Assert.StartsWith("'notlar.docx' dosya türü desteklenmiyor.", exception.Message);
    }

    // ---- Planner: Turkish -----------------------------------------------------------------

    private static AutomationPlan Plan(string request)
    {
        var result = new DeterministicCommandPlanner().Plan(new PlanningRequest { UserRequest = request });
        Assert.True(result.Success, $"'{request}' anlaşılamadı: {string.Join(" | ", result.UnrecognizedCommands)}");
        Assert.True(result.Plan!.Validate().IsValid, $"'{request}': {result.Plan.Validate()}");
        return result.Plan;
    }

    [Theory]
    [InlineData("500x700 mm belge oluştur", "createDocument")]
    [InlineData("500x700 mm yeni sayfa oluştur", "createDocument")]
    [InlineData("bu tasarımı 500x700 mm yap", "setPageSize")]
    [InlineData("ortaya TEST yaz", "createText,align")]
    [InlineData("sayfanın ortasına TEST yaz", "createText,align")]
    [InlineData("ortaya GİRİŞ YASAKTIR yaz", "createText,align")]
    [InlineData("100x50 mm dikdörtgen oluştur", "createRectangle,align")]
    [InlineData("80x80 mm daire oluştur", "createEllipse,align")]
    [InlineData("8 sütun 20 satır tablo oluştur", "createTable")]
    [InlineData("20 satır 8 sütun tablo oluştur", "createTable")]
    [InlineData("shape_001'i 10 mm sağa taşı", "move")]
    [InlineData("shape_001'i 20 mm sola taşı", "move")]
    [InlineData("shape_001'i 5 mm yukarı taşı", "move")]
    [InlineData("shape_001'i 15 mm aşağı taşı", "move")]
    [InlineData("shape_001'i 100x50 mm yap", "resize")]
    [InlineData("shape_002'yi 100x50 mm yap", "resize")]
    [InlineData("shape_001'i yüzde 20 büyüt", "resize")]
    [InlineData("shape_001'i yüzde 15 küçült", "resize")]
    [InlineData("shape_001'i 45 derece döndür", "rotate")]
    [InlineData("Seçili nesneleri 45 derece döndür", "rotate")]
    [InlineData("shape_001 metnini GİRİŞ YASAKTIR yap", "setText")]
    [InlineData("shape_001'in yazı tipini Arial yap", "setFont")]
    [InlineData("shape_001'in yazı boyutunu 48 pt yap", "setFont")]
    [InlineData("shape_001'in rengini kırmızı yap", "setFill")]
    [InlineData("shape_003'ün rengini kırmızı yap", "setFill")]
    [InlineData("shape_001'in dolgusunu mavi yap", "setFill")]
    [InlineData("shape_001'in çizgisini siyah yap", "setOutline")]
    [InlineData("shape_001'in çizgi kalınlığını 2 mm yap", "setOutline")]
    [InlineData("shape_001'i sayfanın ortasına hizala", "align")]
    [InlineData("Seçili nesneleri sayfanın ortasına hizala", "align")]
    [InlineData("shape_001'i sola hizala", "align")]
    [InlineData("shape_001'i sağa hizala", "align")]
    [InlineData("shape_001'i yukarı hizala", "align")]
    [InlineData("shape_001'i aşağı hizala", "align")]
    [InlineData("seçili nesneleri yatay dağıt", "distribute")]
    [InlineData("seçili nesneleri dikey dağıt", "distribute")]
    [InlineData("seçili nesneleri grupla", "group")]
    [InlineData("seçili grubu çöz", "ungroup")]
    [InlineData("shape_001'i öne getir", "bringToFront")]
    [InlineData("shape_001'i arkaya gönder", "sendToBack")]
    [InlineData("shape_001'i 5 kere çoğalt", "duplicate")]
    [InlineData("seçili nesneleri 5 kere çoğalt", "duplicate")]
    [InlineData("shape_001'i sil", "delete")]
    [InlineData("shape_001'in adını Logo yap", "renameObject")]
    [InlineData("YeniKatman adında katman oluştur", "createLayer")]
    [InlineData("shape_001'i YeniKatman katmanına taşı", "moveToLayer")]
    [InlineData(@"dosyayı C:\Output\job.cdr olarak kaydet", "saveDocument")]
    [InlineData(@"C:\Output\job.pdf olarak PDF dışa aktar", "exportPdf")]
    [InlineData(@"C:\Output\job.pdf olarak dışa aktar", "exportPdf")]
    [InlineData(@"C:\Output\job.png olarak PNG dışa aktar", "exportPng")]
    [InlineData(@"C:\Output\job.svg olarak SVG dışa aktar", "exportSvg")]
    public void Turkish_commands_are_understood(string request, string expectedActionTypes) =>
        Assert.Equal(expectedActionTypes, string.Join(",", Plan(request).Actions.Select(action => action.TypeName)));

    [Theory]
    [InlineData("shape_001'i")]
    [InlineData("shape_001'ı")]
    [InlineData("shape_001'in")]
    [InlineData("shape_001'ın")]
    [InlineData("shape_001'yi")]
    [InlineData("shape_001'ü")]
    [InlineData("shape_001i")]
    [InlineData("shape_001")]
    [InlineData("SHAPE_001'i")]
    public void Turkish_case_suffixes_on_object_ids_are_tolerated(string target)
    {
        var delete = Assert.IsType<DeleteAction>(Assert.Single(Plan($"{target} sil").Actions));

        Assert.Equal(["shape_001"], delete.Targets);
    }

    [Fact]
    public void Turkish_commands_carry_the_right_values()
    {
        var move = (MoveAction)Plan("shape_001'i 5 mm yukarı taşı").Actions[0];
        Assert.Equal((0, -5), (move.DeltaXMm, move.DeltaYMm));

        Assert.Equal(120, ((ResizeAction)Plan("shape_001'i yüzde 20 büyüt").Actions[0]).ScalePercent);
        Assert.Equal(85, ((ResizeAction)Plan("shape_001'i %15 küçült").Actions[0]).ScalePercent);
        Assert.Equal(45, ((RotateAction)Plan("shape_001'i 45 derece döndür").Actions[0]).AngleDegrees);
        Assert.Equal("GİRİŞ YASAKTIR", ((SetTextAction)Plan("shape_001 metnini GİRİŞ YASAKTIR yap").Actions[0]).Text);
        Assert.Equal("GİRİŞ YASAKTIR", ((CreateTextAction)Plan("ortaya GİRİŞ YASAKTIR yaz").Actions[0]).Text);
        Assert.Equal("#FF0000", ((SetFillAction)Plan("shape_001'in rengini kırmızı yap").Actions[0]).Color);
        Assert.Equal("#FF0000", ((SetFillAction)Plan("shape_001'in rengini KIRMIZI yap").Actions[0]).Color);
        Assert.Equal(("#000000", null), (((SetOutlineAction)Plan("shape_001'in çizgisini siyah yap").Actions[0]).Color, ((SetOutlineAction)Plan("shape_001'in çizgisini siyah yap").Actions[0]).WidthMm));
        Assert.Equal(2.5, ((SetOutlineAction)Plan("shape_001'in çizgi kalınlığını 2,5 mm yap").Actions[0]).WidthMm);
        Assert.Equal(48, ((SetFontAction)Plan("shape_001'in yazı boyutunu 48 pt yap").Actions[0]).FontSizePt);
        Assert.Equal("Arial", ((SetFontAction)Plan("shape_001'in yazı tipini Arial yap").Actions[0]).FontFamily);
        Assert.Equal(5, ((DuplicateAction)Plan("shape_001'i 5 kere çoğalt").Actions[0]).Count);
        Assert.Equal(["selection"], ((GroupAction)Plan("SEÇİLİ NESNELERİ GRUPLA").Actions[0]).Targets);
        Assert.Equal("YeniKatman", ((CreateLayerAction)Plan("YeniKatman adında katman oluştur").Actions[0]).Name);
        Assert.Equal("YeniKatman", ((MoveToLayerAction)Plan("shape_001'i YeniKatman katmanına taşı").Actions[0]).Layer);
        Assert.Equal("Logo", ((RenameObjectAction)Plan("shape_001'in adını Logo yap").Actions[0]).Name);
        Assert.Equal(@"C:\Output\job.cdr", ((SaveDocumentAction)Plan(@"dosyayı C:\Output\job.cdr olarak kaydet").Actions[0]).FilePath);
        Assert.Equal(@"C:\Output\job.pdf", ((ExportPdfAction)Plan(@"C:\Output\job.pdf olarak PDF dışa aktar").Actions[0]).FilePath);

        var table = (CreateTableAction)Plan("20 satır 8 sütun tablo oluştur").Actions[0];
        Assert.Equal((8, 20), (table.Columns, table.Rows));

        var top = (AlignAction)Plan("shape_001'i yukarı hizala").Actions[0];
        Assert.Equal((HorizontalAlign.None, VerticalAlign.Top), (top.Horizontal, top.Vertical));
        var distribute = (DistributeAction)Plan("seçili nesneleri dikey dağıt").Actions[0];
        Assert.Equal(DistributeDirection.Vertical, distribute.Direction);
        Assert.Equal(["name:Logo"], ((DeleteAction)Plan("\"Logo\"yu sil").Actions[0]).Targets);
    }

    [Fact]
    public void A_turkish_multi_line_request_becomes_one_ordered_plan()
    {
        var plan = Plan("""
            500x700 mm belge oluştur
            Ortaya GİRİŞ YASAKTIR yaz
            Onu yüzde 20 büyüt; onu 15 derece döndür
            8 sütun 20 satır tablo oluştur ve metinleri ortala
            Dosyayı C:\Cikti\is.cdr olarak kaydet. C:\Cikti\is.pdf olarak PDF dışa aktar
            """);

        Assert.Equal(
            ["createDocument", "createText", "align", "resize", "rotate", "createTable", "saveDocument", "exportPdf"],
            plan.Actions.Select(action => action.TypeName));
        Assert.Equal(DocumentTarget.NewDocument, plan.Target);
        Assert.Equal(["@text1"], plan.Actions.OfType<ResizeAction>().Single().Targets);
        Assert.Equal(TextAlignment.Center, plan.Actions.OfType<CreateTableAction>().Single().CellAlignment);
    }

    [Fact]
    public void Planner_feedback_is_turkish_and_examples_shown_to_the_user_are_turkish()
    {
        var result = new DeterministicCommandPlanner().Plan(new PlanningRequest
        {
            UserRequest = "shape_001'i sil\nLogoyu daha şık yap\nshape_002'nin rengini fıstık yap",
        });

        Assert.Equal("1 işlem hazırlandı; 2 komut anlaşılamadı.", result.Message);
        Assert.Contains("'fıstık' rengi bilinmiyor", result.UnrecognizedCommands[1]);
        Assert.Equal("Yerleşik test planlayıcısı", new DeterministicCommandPlanner().Name);

        Assert.All(DeterministicCommandPlanner.Examples, example =>
        {
            Assert.False(EnglishWords().IsMatch(example), example);
            Plan(example);
        });
    }

    [Fact]
    public void English_commands_still_work() =>
        Assert.All(DeterministicCommandPlanner.EnglishExamples, example => Plan(example));

    [GeneratedRegex("""(?:"([A-Z][A-Za-z]+(?:\.[A-Za-z0-9]+)+\.?)"|\{local:Loc ([A-Za-z.]+)\})""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"(?:Text|Content|Header|ToolTip|Title)=""([^{""][^""]*)""")]
    private static partial Regex HardCodedXamlText();

    [GeneratedRegex(@"\b(the|and|with|from|file|files|document|recipe|click|press|cannot|failed|please|object|objects|create|delete|move)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishWords();
}
