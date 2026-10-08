using System.Text.Json;
using CorelSignStudio.AI;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Tests;

/// <summary>Scripted stand-in for a provider: returns queued answers or throws queued errors, and records what it was asked.</summary>
internal sealed class FakeAiClient : IAiClient
{
    private readonly Queue<object> _script = new();

    public FakeAiClient(params object[] script)
    {
        foreach (var item in script)
        {
            _script.Enqueue(item);
        }
    }

    public List<AiRequest> Requests { get; } = [];

    public string ProviderId => "fake";

    public string Model => "fake-model";

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("The test script has no more answers.");
        return next is Exception exception
            ? Task.FromException<AiResponse>(exception)
            : Task.FromResult(new AiResponse { Content = (string)next, ProviderId = ProviderId, Model = Model, RequestId = "req_" + Requests.Count });
    }
}

internal static class AiTestData
{
    public static ShapeSnapshot Shape(int id, ShapeKind type, string? name = null, string? text = null, double x = 0, double y = 0, double w = 50, double h = 20, string? fill = null) => new()
    {
        Id = LogicalShapeId.FromNativeId(id),
        NativeId = id,
        Type = type,
        Name = name,
        Text = text,
        Bounds = new BoundsMm(x, y, w, h),
        LayerName = "Katman 1",
        Fill = fill is null ? null : new FillInfo(FillKind.Uniform, fill),
        Order = id,
    };

    public static DocumentSnapshot Document(IEnumerable<ShapeSnapshot> shapes, params string[] selected) => new()
    {
        Title = "tabela.cdr",
        SelectedShapeIds = selected,
        Pages = [new PageSnapshot { Index = 1, WidthMm = 500, HeightMm = 700, Layers = [new LayerSnapshot { Name = "Katman 1" }], Shapes = shapes.ToArray() }],
    };

    /// <summary>shape_001 = Logo (group), shape_002 = the title text.</summary>
    public static DocumentSnapshot LogoAndTitle(params string[] selected) => Document(
        [
            Shape(1, ShapeKind.Group, name: "Logo", x: 390, y: 30, w: 60, h: 40),
            Shape(2, ShapeKind.ArtisticText, text: "PERSONEL GİRİŞİ", x: 82, y: 110, w: 330, h: 44, fill: "#000000"),
        ],
        selected);

    public static string Ready(string actionsJson, string explanation = "Plan hazır.", double confidence = 0.9, string warnings = "[]") =>
        $$"""{"status":"ready","explanation":"{{explanation}}","clarificationQuestion":"","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"warnings":{{warnings}},"name":"Düzenleme","description":"Açıklama","target":"activeDocument","actions":{{actionsJson}}}""";

    public static AiCommandPlanner Planner(FakeAiClient client, AiPlannerOptions? options = null) =>
        new(client, options: options, parser: new AiPlanParser(_ => false), delay: (_, _) => Task.CompletedTask);

    public static PlanningRequest Request(string text, DocumentSnapshot? document = null) =>
        new() { UserRequest = text, Document = document ?? LogoAndTitle() };
}

public sealed class AiCommandPlannerTests
{
    [Fact]
    public async Task Scenario_1_shrinks_the_logo_and_centres_the_title()
    {
        var client = new FakeAiClient(AiTestData.Ready(
            """[{"type":"resize","targets":["shape_001"],"scalePercent":80},{"type":"align","targets":["shape_002"],"horizontal":"center","vertical":"center","relativeTo":"page"}]""",
            "Logo %20 küçültülecek ve başlık ortalanacak."));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(
            AiTestData.Request("Logoyu yüzde 20 küçült ve başlığı sayfanın ortasına hizala."));

        Assert.Equal(AiPlanningStatus.Ready, result.Status);
        Assert.True(result.Plan!.Validate().IsValid);
        var resize = Assert.IsType<ResizeAction>(result.Plan.Actions[0]);
        Assert.Equal(["shape_001"], resize.Targets);
        Assert.Equal(80d, resize.ScalePercent);
        var align = Assert.IsType<AlignAction>(result.Plan.Actions[1]);
        Assert.Equal(["shape_002"], align.Targets);
        Assert.Equal((HorizontalAlign.Center, AlignReference.Page), (align.Horizontal, align.RelativeTo));
        Assert.Equal("2 işlem hazırlandı.", result.UserMessage);
        Assert.Equal("Logo %20 küçültülecek ve başlık ortalanacak.", result.Explanation);
        Assert.Equal(0.9, result.Confidence);
        Assert.Equal(("fake", "fake-model", "req_1", 1), (result.Diagnostics.ProviderId, result.Diagnostics.Model, result.Diagnostics.RequestId, result.Diagnostics.Attempts));
        Assert.Equal("Logoyu yüzde 20 küçült ve başlığı sayfanın ortasına hizala.", result.Plan.UserRequest);
        Assert.Equal(DocumentTarget.ActiveDocument, result.Plan.Target);
    }

    [Fact]
    public async Task Scenario_2_moves_the_title_up_and_changes_its_text()
    {
        // Property casing as a model might write it ("deltaYmm"), and no action ids.
        var client = new FakeAiClient(AiTestData.Ready(
            """[{"type":"move","targets":["shape_002"],"deltaYmm":-10},{"type":"setText","targets":["shape_002"],"text":"YÜKLEME ALANI"}]"""));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(
            AiTestData.Request("Başlığı 10 mm yukarı taşı ve metnini YÜKLEME ALANI yap."));

        Assert.True(result.IsReady, result.UserMessage);
        var move = Assert.IsType<MoveAction>(result.Plan!.Actions[0]);
        Assert.Equal((0d, -10d, "shape_002"), (move.DeltaXMm, move.DeltaYMm, move.Targets[0]));
        Assert.Equal("YÜKLEME ALANI", Assert.IsType<SetTextAction>(result.Plan.Actions[1]).Text);
        Assert.Equal(["a1", "a2"], result.Plan.Actions.Select(action => action.Id));
    }

    [Fact]
    public async Task Scenario_3_two_logos_need_clarification_and_nothing_is_planned()
    {
        var document = AiTestData.Document(
        [
            AiTestData.Shape(4, ShapeKind.Group, name: "Logo", x: 20, y: 20),
            AiTestData.Shape(9, ShapeKind.Group, name: "Logo", x: 400, y: 640),
        ]);
        const string Question = "Belgede iki logo görünüyor. Hangisini küçülteyim: sol üstteki mi, sağ alttaki mi?";
        var client = new FakeAiClient(
            $$"""{"status":"needs_clarification","explanation":"İki logo var.","clarificationQuestion":"{{Question}}","confidence":0.3,"warnings":[],"name":"","description":"","target":"activeDocument","actions":[]}""");

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu küçült.", document));

        Assert.Equal(AiPlanningStatus.NeedsClarification, result.Status);
        Assert.Null(result.Plan);
        Assert.False(result.IsReady);
        Assert.Equal(Question, result.ClarificationQuestion);
        Assert.Equal(Question, result.UserMessage);
        Assert.Single(client.Requests); // a clarification is an answer, not an error to retry
    }

    [Fact]
    public async Task Scenario_4_delete_everything_is_flagged_as_destructive()
    {
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"delete","targets":["shape_001","shape_002"]}]""", warnings: """["Sayfadaki tüm nesneler silinecek."]"""));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Hepsini sil."));

        Assert.True(result.IsReady);
        Assert.True(result.Plan!.HasDestructiveActions);
        Assert.True(result.Plan.Actions[0].IsDestructive);
        Assert.Contains("Sayfadaki tüm nesneler silinecek.", result.Warnings);
        Assert.Contains(result.Warnings, warning => warning.Contains("onayınız istenecek"));
    }

    [Fact]
    public async Task The_model_is_given_the_document_the_request_and_the_policy()
    {
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"bringToFront","targets":["selection"]}]"""));
        var request = AiTestData.Request("Bu logoyu öne getir.", AiTestData.LogoAndTitle("shape_001")) with
        {
            References = [ReferenceInput.FromFile(@"C:\refs\afis.pdf"), ReferenceInput.FromFile(@"C:\refs\foto.jpg")],
        };

        var result = await AiTestData.Planner(client).PlanWithAiAsync(request);

        Assert.True(result.IsReady);
        var sent = Assert.Single(client.Requests);
        Assert.Contains("PAGE: 500 x 700 mm", sent.UserMessage);
        Assert.Contains("SELECTED: shape_001", sent.UserMessage);
        Assert.Contains("shape_001 | Group | name=\"Logo\"", sent.UserMessage);
        Assert.Contains("shape_002 | ArtisticText | text=\"PERSONEL GİRİŞİ\"", sent.UserMessage);
        Assert.Contains("afis.pdf\" | type=PDF | vector", sent.UserMessage);
        Assert.Contains("foto.jpg\" | type=JPEG | bitmap", sent.UserMessage);
        Assert.EndsWith("Bu logoyu öne getir.", sent.UserMessage);
        Assert.Contains("CorelDRAW automation planner", sent.SystemPrompt);
        Assert.Contains("never return code of any kind", sent.SystemPrompt);
        Assert.Contains("Never make up", sent.SystemPrompt);
        Assert.Contains("needs_clarification", sent.SystemPrompt);
        Assert.Contains("Logoyu yüzde 20 küçült", sent.SystemPrompt); // Turkish examples
        Assert.All(ActionTypes.AllNames, name => Assert.Contains("- " + name + ":", sent.SystemPrompt));
        Assert.NotNull(sent.JsonSchema);
    }

    [Fact]
    public async Task A_clarification_answer_is_sent_with_the_earlier_turns()
    {
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"resize","targets":["shape_001"],"scalePercent":120}]"""));
        var request = AiTestData.Request("Sol üstteki.") with
        {
            Conversation = [new PlanningTurn("Logoyu büyüt.", "Hangisini değiştireyim: sol üstteki mi, sağ alttaki mi?")],
        };

        var result = await AiTestData.Planner(client).PlanWithAiAsync(request);

        Assert.True(result.IsReady);
        var message = client.Requests[0].UserMessage;
        Assert.Contains("User: Logoyu büyüt.", message);
        Assert.Contains("You asked: Hangisini değiştireyim", message);
        Assert.True(message.IndexOf("Logoyu büyüt.", StringComparison.Ordinal) < message.LastIndexOf("Sol üstteki.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invented_shape_id_is_rejected_even_after_a_repair_attempt()
    {
        var bad = AiTestData.Ready("""[{"type":"delete","targets":["shape_777"]}]""");
        var client = new FakeAiClient(bad, bad);

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu sil."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains("shape_777", result.UserMessage);
        Assert.Contains("uygulanmayacak", result.UserMessage);
        Assert.Equal(2, client.Requests.Count); // exactly one repair, never a loop
        Assert.Contains("previous answer was rejected", client.Requests[1].UserMessage);
        Assert.Contains("shape_777", client.Requests[1].UserMessage);
    }

    [Fact]
    public async Task A_rejected_answer_can_be_repaired_once()
    {
        var client = new FakeAiClient(
            AiTestData.Ready("""[{"type":"delete","targets":["shape_777"]}]"""),
            AiTestData.Ready("""[{"type":"delete","targets":["shape_001"]}]"""));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu sil."));

        Assert.True(result.IsReady);
        Assert.Equal(["shape_001"], ((DeleteAction)result.Plan!.Actions[0]).Targets);
        Assert.Equal(2, result.Diagnostics.Attempts);
    }

    [Theory]
    [InlineData("Üzgünüm, bunu yapamam.")]
    [InlineData("{ \"status\": \"ready\", \"actions\": [ {\"type\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task Malformed_answers_never_produce_a_plan(string answer)
    {
        var client = new FakeAiClient(answer, answer);

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Başlığı ortala."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("Yapay zekânın yanıtı geçerli bir plan biçiminde değildi. Lütfen yeniden deneyin.", result.UserMessage);
    }

    [Fact]
    public async Task Json_wrapped_in_a_markdown_fence_is_still_read()
    {
        var client = new FakeAiClient("```json\n" + AiTestData.Ready("""[{"type":"sendToBack","targets":["shape_001"]}]""") + "\n```");

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu arkaya gönder."));

        Assert.True(result.IsReady);
    }

    [Theory]
    [InlineData("""[{"type":"runMacro","code":"Shell(\"calc\")"}]""", "runMacro")]
    [InlineData("""[{"type":"executeVba","targets":["shape_001"]}]""", "executeVba")]
    [InlineData("""[{"targets":["shape_001"]}]""", "?")]
    public async Task Unsupported_action_types_are_rejected(string actions, string shownType)
    {
        var client = new FakeAiClient(AiTestData.Ready(actions), AiTestData.Ready(actions));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Makro çalıştır."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains("desteklenmiyor", result.UserMessage);
        Assert.Contains(shownType, result.UserMessage);
    }

    [Fact]
    public async Task Unknown_properties_are_rejected_instead_of_silently_dropped()
    {
        var actions = """[{"type":"move","targets":["shape_001"],"deltaXMm":5,"comMethod":"Shape.Delete"}]""";
        var client = new FakeAiClient(AiTestData.Ready(actions), AiTestData.Ready(actions));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu sağa al."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Contains("comMethod", result.UserMessage);
    }

    [Fact]
    public async Task A_plan_that_fails_domain_validation_is_rejected()
    {
        var actions = """[{"type":"resize","targets":["shape_001"],"scalePercent":-50}]""";
        var client = new FakeAiClient(AiTestData.Ready(actions), AiTestData.Ready(actions));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu küçült."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains("doğrulamadan geçemedi", result.UserMessage);
        Assert.Contains("ScalePercent", result.UserMessage);
    }

    [Fact]
    public async Task Without_an_inspected_document_existing_objects_cannot_be_targeted()
    {
        var actions = """[{"type":"delete","targets":["shape_001"]}]""";
        var client = new FakeAiClient(AiTestData.Ready(actions), AiTestData.Ready(actions));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(new PlanningRequest { UserRequest = "Logoyu sil." });

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Contains("Belgeyi İncele", result.UserMessage);
        Assert.Contains("DOCUMENT: none inspected", client.Requests[0].UserMessage);
    }

    [Fact]
    public async Task New_objects_can_be_created_and_targeted_within_one_plan()
    {
        var client = new FakeAiClient(AiTestData.Ready(
            """[{"type":"createText","id":"yazi","text":"GİRİŞ YASAKTIR","fontFamily":"Arial","fontSizePt":48,"fillColor":"#FF0000"},{"type":"align","targets":["@yazi"],"horizontal":"center","vertical":"center"}]"""));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(new PlanningRequest { UserRequest = "Ortaya GİRİŞ YASAKTIR yaz ve kırmızı yap." });

        Assert.True(result.IsReady, result.UserMessage);
        Assert.Equal("GİRİŞ YASAKTIR", ((CreateTextAction)result.Plan!.Actions[0]).Text);
        Assert.Equal(["@yazi"], ((AlignAction)result.Plan.Actions[1]).Targets);
    }

    [Fact]
    public async Task A_cannot_do_answer_is_reported_without_retrying()
    {
        var client = new FakeAiClient(
            """{"status":"cannot_do","explanation":"Gölge efekti mevcut işlemler arasında yok.","clarificationQuestion":"","confidence":0.9,"warnings":[],"name":"","description":"","target":"activeDocument","actions":[]}""");

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoya gölge ekle."));

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Equal("Bu istek mevcut işlemlerle yapılamıyor: Gölge efekti mevcut işlemler arasında yok.", result.UserMessage);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Low_confidence_selection_and_overwrite_add_warnings()
    {
        var client = new FakeAiClient(AiTestData.Ready(
            """[{"type":"distribute","targets":["selection"],"direction":"horizontal"},{"type":"exportPdf","filePath":"C:\\Cikti\\is.pdf"}]""", confidence: 0.4));
        var planner = new AiCommandPlanner(client, parser: new AiPlanParser(path => path.EndsWith("is.pdf", StringComparison.Ordinal)));

        var result = await planner.PlanWithAiAsync(AiTestData.Request("Seçtiğim nesneleri eşit aralıklarla yatay dağıt ve PDF al."));

        Assert.True(result.IsReady);
        Assert.Contains(result.Warnings, warning => warning.Contains("çok emin değil"));
        Assert.Contains(result.Warnings, warning => warning.Contains("seçili nesne görünmüyor"));
        Assert.Contains(result.Warnings, warning => warning.Contains("üzerine yazılacak") && warning.Contains("is.pdf"));
    }

    // ---- Provider failures -------------------------------------------------------------------

    [Theory]
    [InlineData(AiErrorKind.InvalidApiKey, "API anahtarı geçersiz veya yetkisiz. Ayarlar sekmesinden anahtarı kontrol edin.", 1)]
    [InlineData(AiErrorKind.NotConfigured, "Yapay zekâ yapılandırılmamış. Ayarlar sekmesinden sağlayıcıyı, modeli ve API anahtarını girin.", 1)]
    [InlineData(AiErrorKind.BadRequest, "Yapay zekâ hizmeti isteği kabul etmedi. Ayarlar sekmesinden model adını kontrol edin.", 1)]
    [InlineData(AiErrorKind.ModelNotFound, "Seçilen model bulunamadı. Ayarlar sekmesinden model adını kontrol edin.", 1)]
    [InlineData(AiErrorKind.Refused, "Yapay zekâ bu isteği yanıtlamadı. İsteğinizi farklı biçimde yazmayı deneyin.", 1)]
    [InlineData(AiErrorKind.Network, "Yapay zekâ hizmetine ulaşılamadı. İnternet bağlantınızı ve API ayarlarınızı kontrol edin.", 3)]
    [InlineData(AiErrorKind.Timeout, "Yapay zekâ hizmeti zamanında yanıt vermedi. Lütfen yeniden deneyin.", 3)]
    [InlineData(AiErrorKind.RateLimited, "Yapay zekâ hizmeti şu anda çok fazla istek alıyor. Biraz bekleyip yeniden deneyin.", 3)]
    [InlineData(AiErrorKind.ServiceUnavailable, "Yapay zekâ hizmeti şu anda kullanılamıyor. Biraz sonra yeniden deneyin.", 3)]
    public async Task Provider_errors_become_turkish_messages_with_bounded_retries(AiErrorKind kind, string expectedMessage, int expectedCalls)
    {
        var failure = new AiClientException(kind, "technical: stack and status code");
        var client = new FakeAiClient(failure, failure, failure, failure, failure);

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Başlığı ortala."));

        Assert.Equal(AiPlanningStatus.ProviderError, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(expectedMessage, result.UserMessage);
        Assert.DoesNotContain("technical", result.UserMessage);
        Assert.Equal(expectedCalls, client.Requests.Count);
        Assert.Equal(kind, result.Diagnostics.ErrorKind);
        Assert.Contains("technical", result.Diagnostics.TechnicalError);
    }

    [Fact]
    public async Task A_transient_failure_followed_by_success_yields_a_plan()
    {
        var client = new FakeAiClient(
            new AiClientException(AiErrorKind.ServiceUnavailable, "529"),
            AiTestData.Ready("""[{"type":"bringToFront","targets":["shape_001"]}]"""));

        var result = await AiTestData.Planner(client).PlanWithAiAsync(AiTestData.Request("Logoyu öne getir."));

        Assert.True(result.IsReady);
        Assert.Equal(2, result.Diagnostics.Attempts);
    }

    [Fact]
    public void Every_error_kind_has_a_turkish_message()
    {
        foreach (var kind in Enum.GetValues<AiErrorKind>())
        {
            var message = AiUserMessages.For(kind);
            Assert.False(message.StartsWith('['), kind.ToString());
        }
    }

    [Fact]
    public void Sdk_exceptions_are_classified()
    {
        Assert.Equal(AiErrorKind.Network, AnthropicErrorMapper.Map(new HttpRequestException("dns")).Kind);
        Assert.Equal(AiErrorKind.Timeout, AnthropicErrorMapper.Map(new TimeoutException()).Kind);
        Assert.Equal(AiErrorKind.Unknown, AnthropicErrorMapper.Map(new InvalidOperationException("x")).Kind);
        Assert.Equal(AiErrorKind.NotConfigured, Assert.Throws<AiClientException>(() => new AnthropicAiClient("  ")).Kind);
        Assert.Equal("claude-opus-5-5", new AnthropicAiClient("sk-test").Model);
    }
}

public sealed class DocumentContextBuilderTests
{
    [Fact]
    public void Context_is_compact_and_keeps_ids_names_texts_and_bounds()
    {
        var context = new DocumentContextBuilder().Build(AiTestData.LogoAndTitle("shape_002"), new AiPlannerOptions());

        Assert.Contains("DOCUMENT: tabela.cdr", context.Text);
        Assert.Contains("PAGE: 500 x 700 mm", context.Text);
        Assert.Contains("SELECTED: shape_002", context.Text);
        Assert.Contains("shape_001 | Group | name=\"Logo\" | x=390 y=30 w=60 h=40 | at=top-right", context.Text);
        Assert.Contains("shape_002 | ArtisticText | text=\"PERSONEL GİRİŞİ\" | x=82 y=110 w=330 h=44 | at=top-center | fill=#000000", context.Text);
        Assert.Contains("| SELECTED", context.Text);
        Assert.Equal(["shape_001", "shape_002"], context.DetailedShapeIds.Order());
        Assert.False(context.WasClipped);
        Assert.DoesNotContain("NativeId", context.Text);
        Assert.DoesNotContain("StaticID", context.Text);
    }

    [Fact]
    public void Large_documents_prioritise_selected_then_named_and_never_lose_ids_silently()
    {
        var shapes = Enumerable.Range(1, 400)
            .Select(index => AiTestData.Shape(index, ShapeKind.Curve, name: index == 390 ? "Logo" : null, text: index == 395 ? "BAŞLIK" : null))
            .ToList();
        var document = AiTestData.Document(shapes, "shape_399");

        var context = new DocumentContextBuilder().Build(document, new AiPlannerOptions { MaxDetailedShapes = 10, MaxContextCharacters = 6000 });

        Assert.Equal(10, context.DetailedShapeIds.Count);
        Assert.Equal("shape_399", context.DetailedShapeIds[0]);                        // selected first
        Assert.Contains("shape_390", context.DetailedShapeIds);                         // then named
        Assert.Contains("shape_395", context.DetailedShapeIds);                         // and textual
        Assert.True(context.WasClipped);
        Assert.Equal(400, context.DetailedShapeIds.Count + context.BriefShapeIds.Count + context.OmittedShapeCount);
        Assert.All(context.BriefShapeIds, id => Assert.Contains(id + ":Curve", context.Text));
        Assert.True(context.Text.Length <= 6000 + 400, $"context was {context.Text.Length} characters");
        if (context.OmittedShapeCount > 0)
        {
            Assert.Contains($"NOT LISTED: {context.OmittedShapeCount} further objects (Curve x{context.OmittedShapeCount})", context.Text);
        }
    }

    [Fact]
    public async Task Clipping_is_reported_to_the_user()
    {
        var shapes = Enumerable.Range(1, 60).Select(index => AiTestData.Shape(index, ShapeKind.Rectangle)).ToList();
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"delete","targets":["shape_060"]}]"""));

        var result = await AiTestData.Planner(client, new AiPlannerOptions { MaxDetailedShapes = 5 })
            .PlanWithAiAsync(AiTestData.Request("Son kutuyu sil.", AiTestData.Document(shapes)));

        // shape_060 was only listed briefly, but it is a real id, so the plan is accepted — with a warning.
        Assert.True(result.IsReady, result.UserMessage);
        Assert.Contains(result.Warnings, warning => warning.Contains("5 nesnenin ayrıntısı") && warning.Contains("55 nesne"));
    }

    [Fact]
    public void Long_texts_are_shortened_and_quotes_are_neutralised()
    {
        var document = AiTestData.Document([AiTestData.Shape(1, ShapeKind.ParagraphText, name: "A \"B\"", text: new string('x', 300))]);

        var context = new DocumentContextBuilder().Build(document, new AiPlannerOptions());

        Assert.Contains("name=\"A 'B'\"", context.Text);
        Assert.Contains(new string('x', 79) + "…", context.Text);
        Assert.DoesNotContain(new string('x', 81), context.Text);
    }
}

public sealed class AiSchemaTests
{
    [Fact]
    public void Schema_is_derived_from_the_domain_actions()
    {
        using var schema = JsonDocument.Parse(AiActionSchema.BuildResponseSchema());
        var items = schema.RootElement.GetProperty("properties").GetProperty("actions").GetProperty("items");
        var types = items.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(element => element.GetString()!).ToArray();

        Assert.Equal(ActionTypes.AllNames.Order(StringComparer.Ordinal), types.Order(StringComparer.Ordinal));
        Assert.False(items.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("number", items.GetProperty("properties").GetProperty("deltaXMm").GetProperty("type").GetString());
        Assert.Equal("array", items.GetProperty("properties").GetProperty("targets").GetProperty("type").GetString());
        Assert.Contains("center", items.GetProperty("properties").GetProperty("horizontal").GetProperty("enum").EnumerateArray().Select(element => element.GetString()));
        Assert.DoesNotContain("isDestructive", items.GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Every_sample_action_uses_only_properties_the_schema_allows()
    {
        foreach (var action in AutomationTestData.OneOfEveryAction())
        {
            using var json = JsonDocument.Parse(AutomationJson.Serialize(action));
            var allowed = AiActionSchema.AllowedProperties(action.TypeName);
            Assert.All(json.RootElement.EnumerateObject(), property => Assert.Contains(property.Name, allowed));
        }
    }

    [Fact]
    public void Prompt_reference_marks_required_properties()
    {
        var reference = AiActionSchema.DescribeForPrompt();

        Assert.Contains("- move: targets*:string[]", reference);
        Assert.Contains("- createDocument: heightMm*:number, widthMm*:number", reference);
        Assert.Contains("horizontal:none|left|center|right", reference);
    }

    [Fact]
    public void Every_sample_action_round_trips_through_the_parser()
    {
        var document = AiTestData.Document([AiTestData.Shape(1, ShapeKind.Rectangle), AiTestData.Shape(42, ShapeKind.Rectangle)]);
        foreach (var action in AutomationTestData.OneOfEveryAction().Where(action => !action.TargetRefs.Any(TargetRef.IsActionRef) && !action.TargetRefs.Any(TargetRef.IsName)))
        {
            var answer = AiTestData.Ready("[" + AutomationJson.Serialize(action) + "]");

            var parsed = new AiPlanParser(_ => false).Parse(answer, new PlanningRequest { UserRequest = "x", Document = document });

            Assert.True(parsed.Status == AiPlanningStatus.Ready, $"{action.TypeName}: {parsed.UserError}");
            Assert.Equal(action.GetType(), parsed.Plan!.Actions[0].GetType());
        }
    }
}

public sealed class PlannerRouterTests
{
    private static PlannerRouter Router(IAiCommandPlanner? ai, PlannerMode mode) =>
        new(new DeterministicCommandPlanner(), () => ai, () => mode);

    [Fact]
    public async Task Ai_is_used_when_selected_and_configured()
    {
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"bringToFront","targets":["shape_001"]}]"""));
        var router = Router(AiTestData.Planner(client), PlannerMode.Ai);

        var result = await router.PlanAsync(AiTestData.Request("Bu logoyu öne getir."));

        Assert.True(router.IsAiActive);
        Assert.Equal("Yapay zekâ planlayıcısı", router.ActivePlannerName);
        Assert.True(result.IsReady);
        Assert.Equal(nameof(AiCommandPlanner), result.Diagnostics.PlannerUsed);
    }

    [Theory]
    [InlineData(PlannerMode.Deterministic, true)]
    [InlineData(PlannerMode.Ai, false)]
    public async Task Built_in_planner_is_used_when_selected_or_when_ai_is_not_configured(PlannerMode mode, bool aiConfigured)
    {
        var client = new FakeAiClient();
        var router = Router(aiConfigured ? AiTestData.Planner(client) : null, mode);

        var result = await router.PlanAsync(AiTestData.Request("shape_001'i 10 mm sağa taşı"));

        Assert.False(router.IsAiActive);
        Assert.Equal("Yerleşik test planlayıcısı", router.ActivePlannerName);
        Assert.True(result.IsReady);
        Assert.IsType<MoveAction>(result.Plan!.Actions[0]);
        Assert.Empty(client.Requests);
        Assert.Equal(nameof(DeterministicCommandPlanner), result.Diagnostics.PlannerUsed);
    }

    [Fact]
    public async Task Built_in_planner_takes_over_when_the_provider_is_down_and_it_understands_the_request()
    {
        var down = new AiClientException(AiErrorKind.Network, "no route");
        var router = Router(AiTestData.Planner(new FakeAiClient(down, down, down)), PlannerMode.Ai);

        var result = await router.PlanAsync(AiTestData.Request("shape_001'i 10 mm sağa taşı"));

        Assert.True(result.IsReady);
        Assert.Equal(nameof(DeterministicCommandPlanner), result.Diagnostics.PlannerUsed);
        Assert.Contains("yerleşik test planlayıcısı kullanıldı", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Provider_error_is_reported_when_the_built_in_planner_cannot_help()
    {
        var down = new AiClientException(AiErrorKind.InvalidApiKey, "401");
        var router = Router(AiTestData.Planner(new FakeAiClient(down)), PlannerMode.Ai);

        var result = await router.PlanAsync(AiTestData.Request("Logoyu biraz küçült ve sağ üst köşeye al."));

        Assert.Equal(AiPlanningStatus.ProviderError, result.Status);
        Assert.Null(result.Plan);
        Assert.StartsWith("API anahtarı geçersiz", result.UserMessage);
    }

    [Fact]
    public async Task Built_in_planner_reports_what_it_did_not_understand()
    {
        var result = await Router(null, PlannerMode.Deterministic).PlanAsync(AiTestData.Request("shape_001'i sil\nLogoyu daha şık yap"));

        Assert.True(result.IsReady);
        Assert.Contains("Logoyu daha şık yap", Assert.Single(result.Warnings));

        var nothing = await Router(null, PlannerMode.Deterministic).PlanAsync(AiTestData.Request("Logoyu daha şık yap"));
        Assert.Equal(AiPlanningStatus.Invalid, nothing.Status);
    }

    [Fact]
    public async Task An_ai_plan_is_only_ever_a_plan_execution_still_needs_the_executor()
    {
        // The planner returns data; nothing runs until a plan is handed to an executor, and the
        // destructive flag that drives the confirmation dialog survives the trip.
        var client = new FakeAiClient(AiTestData.Ready("""[{"type":"delete","targets":["shape_001"]}]"""));
        var result = await Router(AiTestData.Planner(client), PlannerMode.Ai).PlanAsync(AiTestData.Request("Logoyu sil."));
        var session = new FakeAutomationSession();

        Assert.True(result.Plan!.HasDestructiveActions);
        Assert.Empty(session.Executed);

        var execution = PlanExecutionEngine.Run(result.Plan, session);
        Assert.True(execution.Success);
        Assert.Equal(["a1"], session.Executed);
    }
}

public sealed class AiSettingsAndLoggingTests
{
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string secret) => "enc:" + new string(secret.Reverse().ToArray());

        public string? Unprotect(string protectedSecret) =>
            protectedSecret.StartsWith("enc:", StringComparison.Ordinal) ? new string(protectedSecret[4..].Reverse().ToArray()) : null;
    }

    private static AiSettingsStore Store(out string path, Func<string, string?>? environment = null)
    {
        path = Path.Combine(TestFolders.Create(), "ai-settings.json");
        return new AiSettingsStore(path, new ReversingProtector(), environment ?? (_ => null));
    }

    [Fact]
    public void Settings_persist_and_the_api_key_is_never_written_in_plain_text()
    {
        var store = Store(out var path);
        Assert.False(store.IsConfigured(store.Load()));
        Assert.Equal(PlannerMode.Ai, store.Load().PlannerMode);
        Assert.Equal("claude-opus-5-5", store.Load().Model);

        store.Save(new AiSettings { Model = "claude-sonnet-5-5", PlannerMode = PlannerMode.Deterministic, DebugLogging = true });
        store.SaveApiKey("anthropic", "sk-ant-GIZLI-ANAHTAR");

        var reloaded = new AiSettingsStore(path, new ReversingProtector(), _ => null);
        Assert.Equal(("claude-sonnet-5-5", PlannerMode.Deterministic, true), (reloaded.Load().Model, reloaded.Load().PlannerMode, reloaded.Load().DebugLogging));
        Assert.Equal("sk-ant-GIZLI-ANAHTAR", reloaded.GetApiKey("anthropic"));
        Assert.Equal(ApiKeySource.Stored, reloaded.GetApiKeySource("anthropic"));
        Assert.True(reloaded.IsConfigured(reloaded.Load()));
        Assert.DoesNotContain("GIZLI", File.ReadAllText(path));

        reloaded.SaveApiKey("anthropic", "");
        Assert.Null(reloaded.GetApiKey("anthropic"));
        Assert.Equal("claude-sonnet-5-5", reloaded.Load().Model); // removing the key keeps the other settings
    }

    [Fact]
    public void Environment_variable_is_used_when_no_key_is_stored()
    {
        var store = Store(out _, name => name == "ANTHROPIC_API_KEY" ? " sk-env " : null);

        Assert.Equal("sk-env", store.GetApiKey("anthropic"));
        Assert.Equal(ApiKeySource.EnvironmentVariable, store.GetApiKeySource("anthropic"));

        store.SaveApiKey("anthropic", "sk-stored");
        Assert.Equal("sk-stored", store.GetApiKey("anthropic"));
    }

    [Fact]
    public void A_damaged_settings_file_falls_back_to_defaults()
    {
        var store = Store(out var path);
        File.WriteAllText(path, "{ not json");

        Assert.Equal(new AiSettings(), store.Load());
        Assert.Null(store.GetApiKey("anthropic"));
    }

    [Fact]
    public void Dpapi_round_trips_for_the_current_user_and_rejects_foreign_data()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiSecretProtector();
        var encrypted = protector.Protect("sk-ant-şifreli-anahtar");

        Assert.DoesNotContain("sk-ant", encrypted);
        Assert.Equal("sk-ant-şifreli-anahtar", protector.Unprotect(encrypted));
        Assert.Null(protector.Unprotect("not-base64!"));
        Assert.Null(protector.Unprotect(Convert.ToBase64String([1, 2, 3, 4])));
    }

    [Fact]
    public void Provider_registry_creates_clients_and_explains_missing_keys()
    {
        Assert.Equal("anthropic", AiProviders.Get("ANTHROPIC").Id);
        Assert.Equal("anthropic", AiProviders.Get("something-else").Id);
        Assert.IsType<AnthropicAiClient>(AiProviders.CreateClient(new AiSettings(), "sk-test"));
        Assert.Equal(AiErrorKind.NotConfigured, Assert.Throws<AiClientException>(() => AiProviders.CreateClient(new AiSettings(), null)).Kind);
    }

    [Fact]
    public async Task Logging_never_contains_document_content_unless_debug_is_on()
    {
        var debug = false;
        var lines = new List<string>();
        var client = new LoggingAiClient(new FakeAiClient("cevap-GIZLI", "cevap-GIZLI", new AiClientException(AiErrorKind.RateLimited, "429")), lines.Add, () => debug);
        var request = new AiRequest { SystemPrompt = "sistem", UserMessage = "müşteri-adı-GIZLI" };

        await client.CompleteAsync(request);
        Assert.DoesNotContain(lines, line => line.Contains("GIZLI"));
        Assert.Contains(lines, line => line.Contains("fake/fake-model") && line.Contains("user 17 chars"));

        debug = true;
        await client.CompleteAsync(request);
        Assert.Contains(lines, line => line.Contains("müşteri-adı-GIZLI"));
        Assert.Contains(lines, line => line.Contains("cevap-GIZLI"));

        await Assert.ThrowsAsync<AiClientException>(() => client.CompleteAsync(request));
        Assert.Contains(lines, line => line.Contains("AI error") && line.Contains("RateLimited"));
    }
}
