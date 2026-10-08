using System.Globalization;
using System.Text.RegularExpressions;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Planning;

public sealed record PlanningRequest
{
    public required string UserRequest { get; init; }

    /// <summary>The current document, when one was inspected; lets planners resolve "the title", page centre, etc.</summary>
    public DocumentSnapshot? Document { get; init; }

    public IReadOnlyList<ReferenceInput> References { get; init; } = [];
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Earlier turns of the same planning conversation, oldest first — typically the original request and
    /// the planner's clarification question, with <see cref="UserRequest"/> being the user's answer.
    /// </summary>
    public IReadOnlyList<PlanningTurn> Conversation { get; init; } = [];
}

/// <summary>One earlier exchange: what the user asked and, if the planner needed more, what it asked back.</summary>
public sealed record PlanningTurn(string UserText, string? PlannerQuestion = null);

public sealed record PlanningResult
{
    public AutomationPlan? Plan { get; init; }
    public bool Success => Plan is not null && UnrecognizedCommands.Count == 0;

    /// <summary>Parts of the request the planner could not turn into actions.</summary>
    public IReadOnlyList<string> UnrecognizedCommands { get; init; } = [];

    public string? Message { get; init; }
}

/// <summary>
/// Turns a user request into an <see cref="AutomationPlan"/>. The executor only ever sees plans, so
/// replacing <see cref="DeterministicCommandPlanner"/> with an AI planner needs no other change.
/// </summary>
public interface ICommandPlanner
{
    string Name { get; }

    Task<PlanningResult> PlanAsync(PlanningRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Temporary, rule-based planner that understands a fixed vocabulary of Turkish and English test
/// commands. It exists to exercise the whole pipeline before an AI planner is connected.
/// </summary>
public sealed partial class DeterministicCommandPlanner : ICommandPlanner
{
    private const string Number = @"(-?\d+(?:[.,]\d+)?)";
    private const string Size = Number + @"\s*(?:mm)?\s*[x×*]\s*" + Number;
    private const string Target = @"(shape_\d+|the\s+selection|selection|(?:the\s+)?selected\s+(?:objects?|shapes?)|""[^""]+""|'[^']+'|it)";

    // Turkish target: the object itself plus an optional case suffix, with or without an apostrophe:
    // shape_001'i, shape_002'yi, shape_003'ün, shape_001'in, "Logo"yu, seçili nesneleri, seçili grubu, onu …
    private const string TrTarget =
        @"(shape_\d+|seçili\s+(?:nesneler|nesne|şekiller|şekil|grup|grub)|seçim|""[^""]+""|bunu|onu|bu|o)" +
        @"(?:'?(?:n[ıiuü]n|[ıiuü]n|y[ıiuü]|[ıiuü]))?";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly IReadOnlyDictionary<string, string> NamedColors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["black"] = "#000000", ["white"] = "#FFFFFF", ["red"] = "#FF0000", ["green"] = "#00A651", ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00", ["orange"] = "#FF8000", ["gray"] = "#808080", ["grey"] = "#808080",
        ["purple"] = "#800080", ["pink"] = "#FF69B4", ["brown"] = "#8B4513",
        ["siyah"] = "#000000", ["beyaz"] = "#FFFFFF", ["kırmızı"] = "#FF0000", ["yeşil"] = "#00A651", ["mavi"] = "#0000FF",
        ["sarı"] = "#FFFF00", ["turuncu"] = "#FF8000", ["gri"] = "#808080", ["mor"] = "#800080", ["pembe"] = "#FF69B4",
        ["kahverengi"] = "#8B4513", ["lacivert"] = "#000080",
    };

    private static readonly IReadOnlyList<Rule> Rules = BuildRules();

    public string Name => Msg.Get("Planner.Name");

    /// <summary>Example commands shown in the UI (Turkish, the application's default language).</summary>
    public static IReadOnlyList<string> Examples { get; } =
    [
        "500x700 mm belge oluştur",
        "Ortaya TEST yaz",
        "shape_001'i 10 mm sağa taşı",
        "shape_001 metnini MERHABA yap",
        "shape_002'yi 100x50 mm yap",
        "8 sütun 20 satır tablo oluştur",
        "Bu tasarımı 500x700 mm yap",
        "Seçili nesneleri 45 derece döndür",
        "shape_003'ün rengini kırmızı yap",
        "Seçili nesneleri sayfanın ortasına hizala",
        "shape_001'i yüzde 20 büyüt",
        "Seçili nesneleri grupla",
        "shape_001'i 5 kere çoğalt",
        "YeniKatman adında katman oluştur",
        "Dosyayı C:\\Cikti\\is.cdr olarak kaydet",
        "C:\\Cikti\\is.pdf olarak PDF dışa aktar",
    ];

    /// <summary>The English commands the planner also understands (kept for tests and a future English UI).</summary>
    public static IReadOnlyList<string> EnglishExamples { get; } =
    [
        "Create a 500x700 mm document",
        "Add text TEST in the center",
        "Move shape_001 10 mm right",
        "Change shape_001 text to HELLO",
        "Resize shape_002 to 100x50 mm",
        "Create a table with 8 columns and 20 rows and center all text",
        "Make this design 500x700 mm",
        "Rotate selection 45 degrees",
        "Set fill of shape_003 to red",
        "Align selection center on page",
        "Save as C:\\Output\\job.cdr",
        "Export PDF to C:\\Output\\job.pdf",
    ];

    public Task<PlanningResult> PlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Plan(request));
    }

    public PlanningResult Plan(PlanningRequest request)
    {
        var context = new Context(request.Document);
        var unrecognized = new List<string>();
        foreach (var command in SplitCommands(request.UserRequest ?? ""))
        {
            var handled = false;
            foreach (var rule in Rules)
            {
                var match = rule.Match(command);
                if (match is null)
                {
                    continue;
                }

                try
                {
                    context.Actions.AddRange(rule.Build(match, context));
                }
                catch (FormatException exception)
                {
                    unrecognized.Add($"{command} ({exception.Message})");
                }

                handled = true;
                break;
            }

            if (!handled)
            {
                unrecognized.Add(command);
            }
        }

        if (context.Actions.Count == 0)
        {
            return new PlanningResult
            {
                UnrecognizedCommands = unrecognized,
                Message = Msg.Get(unrecognized.Count == 0 ? "Planner.TypeRequest" : "Planner.NoneUnderstood"),
            };
        }

        var plan = new AutomationPlan
        {
            Name = Summarize(request.UserRequest ?? ""),
            UserRequest = request.UserRequest,
            Target = context.Actions[0].OpensDocument ? DocumentTarget.NewDocument : DocumentTarget.ActiveDocument,
            Actions = context.Actions.ToArray(),
            ReferenceFiles = request.References,
            Parameters = request.Parameters,
            Metadata = new Dictionary<string, string> { ["planner"] = nameof(DeterministicCommandPlanner) },
        };

        return new PlanningResult
        {
            Plan = plan,
            UnrecognizedCommands = unrecognized,
            Message = unrecognized.Count == 0
                ? Msg.Format("Planner.Prepared", plan.Actions.Count)
                : Msg.Format("Planner.PreparedPartial", plan.Actions.Count, unrecognized.Count),
        };
    }

    public static IReadOnlyList<string> SplitCommands(string request) =>
        CommandSeparator().Split(request)
            .Select(part => part.Trim().TrimEnd('.', '!').Trim())
            .Where(part => part.Length > 0)
            .ToArray();

    private static List<Rule> BuildRules() =>
    [
        // ===================================== ENGLISH =====================================

        // --- Document -------------------------------------------------------------------------
        new($@"^(?:create|make|new)\s+(?:a\s+)?(?:new\s+)?{Size}\s*(?:mm)?\s+(?:document|page|design|file)$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:create|make|new)\s+(?:a\s+)?(?:new\s+)?(?:document|page|design|file)\s+(?:of\s+|with\s+size\s+|sized\s+)?{Size}\s*(?:mm)?$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:make|set|resize)\s+(?:this\s+|the\s+)?(?:design|page|document)(?:\s+size)?(?:\s+to)?\s+{Size}\s*(?:mm)?$", (m, c) =>
            [new SetPageSizeAction { Id = c.NextId("page"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),

        // --- Create ---------------------------------------------------------------------------
        new(@"^(?:add|create|write|insert)\s+(?:the\s+)?text\s+(.+?)\s+(?:in|at|to)\s+(?:the\s+)?(?:page\s+)?(?:center|centre|middle)(?:\s+of\s+the\s+page)?$", (m, c) =>
            CenteredText(c, Unquote(m.Groups[1].Value))),
        new($@"^(?:add|create|write|insert)\s+(?:the\s+)?text\s+(.+?)\s+at\s+{Number}\s*(?:mm)?\s*[,; ]\s*{Number}\s*(?:mm)?$", (m, c) =>
            [c.Remember(new CreateTextAction { Id = c.NextId("text"), Text = Unquote(m.Groups[1].Value), XMm = Num(m, 2), YMm = Num(m, 3) })]),
        new($@"^(?:add|create|draw|insert)\s+(?:a\s+|an\s+)?(rectangle|ellipse|circle)\s+(?:of\s+)?{Size}\s*(?:mm)?(?:\s+at\s+{Number}\s*(?:mm)?\s*[,; ]\s*{Number}\s*(?:mm)?)?$", (m, c) =>
            Shape(c, m.Groups[1].Value.Equals("rectangle", StringComparison.OrdinalIgnoreCase), Num(m, 2), Num(m, 3),
                m.Groups[4].Success ? (Num(m, 4), Num(m, 5)) : null)),
        new(@"^(?:add|create|draw|insert|make)\s+(?:a\s+)?table\s+(?:with|of)\s+(\d+)\s+columns?\s+(?:and|by|x)\s+(\d+)\s+rows?(\s+and\s+(?:center|centre)\s+(?:all\s+)?(?:the\s+)?text)?$", (m, c) =>
            Table(c, Int(m, 1), Int(m, 2), m.Groups[3].Success)),
        new(@"^(?:add|create|draw|insert|make)\s+(?:a\s+)?table\s+(?:with|of)\s+(\d+)\s+rows?\s+(?:and|by|x)\s+(\d+)\s+columns?(\s+and\s+(?:center|centre)\s+(?:all\s+)?(?:the\s+)?text)?$", (m, c) =>
            Table(c, Int(m, 2), Int(m, 1), m.Groups[3].Success)),
        new(@"^import\s+(?:the\s+)?(?:file\s+)?(.+)$", (m, c) =>
            [c.Remember(new ImportFileAction { Id = c.NextId("import"), FilePath = Unquote(m.Groups[1].Value) })]),

        // --- Transform ------------------------------------------------------------------------
        new($@"^(?:move|shift|nudge)\s+{Target}\s+(?:by\s+)?{Number}\s*(?:mm)?\s+(?:to\s+the\s+)?(right|left|up|upwards?|down|downwards?)$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 2), m.Groups[3].Value)]),
        new($@"^(?:move|shift|nudge)\s+{Target}\s+(right|left|up|upwards?|down|downwards?)\s+(?:by\s+)?{Number}\s*(?:mm)?$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 3), m.Groups[2].Value)]),
        new($@"^move\s+{Target}\s+to\s+{Number}\s*(?:mm)?\s*[,; ]\s*{Number}\s*(?:mm)?$", (m, c) =>
            [new MoveAction { Id = c.NextId("move"), Targets = [c.Target(m.Groups[1].Value)], ToXMm = Num(m, 2), ToYMm = Num(m, 3) }]),
        new($@"^(?:resize|scale)\s+{Target}\s+to\s+{Size}\s*(?:mm)?$", (m, c) =>
            [new ResizeAction { Id = c.NextId("resize"), Targets = [c.Target(m.Groups[1].Value)], WidthMm = Num(m, 2), HeightMm = Num(m, 3), KeepAspectRatio = false }]),
        new($@"^(?:resize|scale)\s+{Target}\s+(?:to|by)\s+{Number}\s*%$", (m, c) =>
            [new ResizeAction { Id = c.NextId("resize"), Targets = [c.Target(m.Groups[1].Value)], ScalePercent = Num(m, 2) }]),
        new($@"^make\s+{Target}\s+(smaller|bigger|larger)$", (m, c) =>
            [new ResizeAction { Id = c.NextId("resize"), Targets = [c.Target(m.Groups[1].Value)], ScalePercent = m.Groups[2].Value.Equals("smaller", StringComparison.OrdinalIgnoreCase) ? 80 : 125 }]),
        new($@"^rotate\s+{Target}\s+(?:by\s+)?{Number}\s*(?:degrees?|deg|°)?$", (m, c) =>
            [new RotateAction { Id = c.NextId("rotate"), Targets = [c.Target(m.Groups[1].Value)], AngleDegrees = Num(m, 2) }]),

        // --- Content and style ----------------------------------------------------------------
        new($@"^(?:change|set|replace)\s+{Target}\s+text\s+(?:to|with)\s+(.+)$", (m, c) =>
            [new SetTextAction { Id = c.NextId("settext"), Targets = [c.Target(m.Groups[1].Value)], Text = Unquote(m.Groups[2].Value) }]),
        new($@"^(?:change|set|replace)\s+(?:the\s+)?text\s+of\s+{Target}\s+(?:to|with)\s+(.+)$", (m, c) =>
            [new SetTextAction { Id = c.NextId("settext"), Targets = [c.Target(m.Groups[1].Value)], Text = Unquote(m.Groups[2].Value) }]),
        new($@"^(?:change|set)\s+(?:the\s+)?font\s+size\s+of\s+{Target}\s+to\s+{Number}\s*(?:pt)?$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontSizePt = Num(m, 2) }]),
        new($@"^(?:change|set)\s+(?:the\s+)?font\s+of\s+{Target}\s+to\s+(.+?)(?:\s+{Number}\s*pt)?$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontFamily = Unquote(m.Groups[2].Value), FontSizePt = m.Groups[3].Success ? Num(m, 3) : null }]),
        new($@"^(?:set|change)\s+(?:the\s+)?(?:fill|colou?r)\s+of\s+{Target}\s+to\s+(\S+)$", (m, c) =>
            [new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^(?:fill|colou?r)\s+{Target}\s+(?:with\s+)?(\S+)$", (m, c) =>
            [new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^remove\s+(?:the\s+)?(fill|outline)\s+(?:of|from)\s+{Target}$", (m, c) =>
            [RemoveStyle(c, m.Groups[2].Value, m.Groups[1].Value.Equals("fill", StringComparison.OrdinalIgnoreCase))]),
        new($@"^(?:set|change)\s+(?:the\s+)?outline\s+of\s+{Target}\s+to\s+(\S+)(?:\s+{Number}\s*mm)?$", (m, c) =>
            [new SetOutlineAction { Id = c.NextId("outline"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value), WidthMm = m.Groups[3].Success ? Num(m, 3) : null }]),

        // --- Arrange --------------------------------------------------------------------------
        new($@"^(?:center|centre)\s+{Target}(?:\s+(?:on|in)\s+(?:the\s+)?page)?$", (m, c) =>
            [Center(c, c.Target(m.Groups[1].Value))]),
        new($@"^align\s+{Target}\s+(?:to\s+(?:the\s+)?)?(left|center|centre|right|top|middle|bottom)(?:\s+(?:on|of|in)\s+(?:the\s+)?page)?$", (m, c) =>
            [Align(c, c.Target(m.Groups[1].Value), m.Groups[2].Value)]),
        new($@"^distribute\s+{Target}\s+(horizontally|vertically)$", (m, c) =>
            [Distribute(c, c.Target(m.Groups[1].Value), m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase))]),
        new($@"^(?:duplicate|copy)\s+{Target}(?:\s+{Number}\s+times)?$", (m, c) =>
            [Duplicate(c, c.Target(m.Groups[1].Value), m.Groups[2].Success ? (int)Num(m, 2) : 1)]),
        new($@"^(?:delete|remove)\s+{Target}$", (m, c) =>
            [new DeleteAction { Id = c.NextId("delete"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^group\s+{Target}$", (m, c) =>
            [c.Remember(new GroupAction { Id = c.NextId("group"), Targets = [c.Target(m.Groups[1].Value)] })]),
        new($@"^ungroup\s+{Target}$", (m, c) =>
            [new UngroupAction { Id = c.NextId("ungroup"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^bring\s+{Target}\s+to\s+(?:the\s+)?front$", (m, c) =>
            [new BringToFrontAction { Id = c.NextId("front"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^send\s+{Target}\s+to\s+(?:the\s+)?back$", (m, c) =>
            [new SendToBackAction { Id = c.NextId("back"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^rename\s+{Target}\s+to\s+(.+)$", (m, c) =>
            [new RenameObjectAction { Id = c.NextId("rename"), Targets = [c.Target(m.Groups[1].Value)], Name = Unquote(m.Groups[2].Value) }]),
        new(@"^(?:create|add)\s+(?:a\s+)?(?:new\s+)?layer\s+(?:named\s+|called\s+)?(.+)$", (m, c) =>
            [new CreateLayerAction { Id = c.NextId("layer"), Name = Unquote(m.Groups[1].Value) }]),
        new($@"^move\s+{Target}\s+to\s+(?:the\s+)?layer\s+(.+)$", (m, c) =>
            [new MoveToLayerAction { Id = c.NextId("tolayer"), Targets = [c.Target(m.Groups[1].Value)], Layer = Unquote(m.Groups[2].Value) }]),

        // --- Output ---------------------------------------------------------------------------
        new(@"^save\s+(?:the\s+)?(?:document\s+|file\s+)?(?:as|to)\s+(.+\.cdr)$", (m, c) =>
            [new SaveDocumentAction { Id = c.NextId("save"), FilePath = Unquote(m.Groups[1].Value) }]),
        new(@"^export\s+(?:a\s+|as\s+)?(pdf|png|svg)\s+(?:as|to)\s+(.+)$", (m, c) =>
            [Export(c, m.Groups[1].Value, Unquote(m.Groups[2].Value))]),

        // ===================================== TÜRKÇE ======================================

        // --- Belge ----------------------------------------------------------------------------
        new($@"^{Size}\s*(?:mm)?\s+(?:yeni\s+)?(?:bir\s+)?(?:belge|doküman|dosya|sayfa|tasarım)\s+(?:oluştur|aç|yap)$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:bu\s+)?(?:tasarımı|sayfayı|belgeyi|sayfa\s+boyutunu)\s+{Size}\s*(?:mm)?\s+yap$", (m, c) =>
            [new SetPageSizeAction { Id = c.NextId("page"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),

        // --- Oluşturma ------------------------------------------------------------------------
        new(@"^(?:sayfanın\s+)?(?:ortasına|ortaya|merkezine|merkeze)\s+(.+?)\s+(?:yaz|metni\s+ekle|ekle)$", (m, c) =>
            CenteredText(c, Unquote(m.Groups[1].Value))),
        new($@"^{Size}\s*(?:mm)?\s+(?:bir\s+)?(dikdörtgen|kare|daire|elips|çember)\s+(?:oluştur|çiz|ekle)$", (m, c) =>
            Shape(c, IsOneOf(m.Groups[3].Value, "dikdörtgen", "kare"), Num(m, 1), Num(m, 2), null)),
        new(@"^(\d+)\s+sütun(?:lu)?\s*(?:ve|,|x)?\s*(\d+)\s+satır(?:lı|lık)?\s+(?:bir\s+)?tablo\s+(?:oluştur|ekle|yap|çiz)(\s+ve\s+(?:tüm\s+)?metinleri\s+ortala)?$", (m, c) =>
            Table(c, Int(m, 1), Int(m, 2), m.Groups[3].Success)),
        new(@"^(\d+)\s+satır(?:lı)?\s*(?:ve|,|x)?\s*(\d+)\s+sütun(?:lu|luk)?\s+(?:bir\s+)?tablo\s+(?:oluştur|ekle|yap|çiz)(\s+ve\s+(?:tüm\s+)?metinleri\s+ortala)?$", (m, c) =>
            Table(c, Int(m, 2), Int(m, 1), m.Groups[3].Success)),
        new(@"^(.+?)\s+dosyasını\s+içe\s+aktar$", (m, c) =>
            [c.Remember(new ImportFileAction { Id = c.NextId("import"), FilePath = Unquote(m.Groups[1].Value) })]),

        // --- Dönüştürme -----------------------------------------------------------------------
        new($@"^{TrTarget}\s+{Number}\s*(?:mm)?\s+(sağa|sola|yukarıya|yukarı|aşağıya|aşağı)\s+(?:taşı|kaydır|al)$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 2), m.Groups[3].Value)]),
        new($@"^{TrTarget}\s+{Size}\s*(?:mm)?\s+yap$", (m, c) =>
            [new ResizeAction { Id = c.NextId("resize"), Targets = [c.Target(m.Groups[1].Value)], WidthMm = Num(m, 2), HeightMm = Num(m, 3), KeepAspectRatio = false }]),
        new($@"^{TrTarget}\s+(?:yüzde\s+|%\s*){Number}\s+(büyüt|küçült)$", (m, c) =>
            [new ResizeAction { Id = c.NextId("resize"), Targets = [c.Target(m.Groups[1].Value)], ScalePercent = IsOneOf(m.Groups[3].Value, "büyüt") ? 100 + Num(m, 2) : 100 - Num(m, 2) }]),
        new($@"^{TrTarget}\s+{Number}\s*(?:derece|°)\s+döndür$", (m, c) =>
            [new RotateAction { Id = c.NextId("rotate"), Targets = [c.Target(m.Groups[1].Value)], AngleDegrees = Num(m, 2) }]),

        // --- İçerik ve biçim ------------------------------------------------------------------
        new($@"^{TrTarget}\s+metnini\s+(.+?)\s+yap$", (m, c) =>
            [new SetTextAction { Id = c.NextId("settext"), Targets = [c.Target(m.Groups[1].Value)], Text = Unquote(m.Groups[2].Value) }]),
        new($@"^{TrTarget}\s+yazı\s+boyutunu\s+{Number}\s*(?:pt|punto)?\s+yap$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontSizePt = Num(m, 2) }]),
        new($@"^{TrTarget}\s+yazı\s+tipini\s+(.+?)\s+yap$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontFamily = Unquote(m.Groups[2].Value) }]),
        new($@"^{TrTarget}\s+çizgi\s+kalınlığını\s+{Number}\s*(?:mm)?\s+yap$", (m, c) =>
            [new SetOutlineAction { Id = c.NextId("outline"), Targets = [c.Target(m.Groups[1].Value)], WidthMm = Num(m, 2) }]),
        new($@"^{TrTarget}\s+(?:rengini|dolgusunu|dolgu\s+rengini)\s+(\S+)\s+yap$", (m, c) =>
            [new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^{TrTarget}\s+(?:çizgisini|çizgi\s+rengini|konturunu)\s+(\S+)\s+yap$", (m, c) =>
            [new SetOutlineAction { Id = c.NextId("outline"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^{TrTarget}\s+(dolgusunu|çizgisini|konturunu)\s+kaldır$", (m, c) =>
            [RemoveStyle(c, m.Groups[1].Value, IsOneOf(m.Groups[2].Value, "dolgusunu"))]),

        // --- Düzenleme ------------------------------------------------------------------------
        new($@"^{TrTarget}\s+(?:sayfanın\s+ortasına\s+(?:hizala|al|getir)|ortala)$", (m, c) =>
            [Center(c, c.Target(m.Groups[1].Value))]),
        new($@"^{TrTarget}\s+(sola|sağa|yukarıya|yukarı|üste|aşağıya|aşağı|alta|ortaya)\s+hizala$", (m, c) =>
            [Align(c, c.Target(m.Groups[1].Value), m.Groups[2].Value)]),
        new($@"^{TrTarget}\s+(yatay|dikey)(?:\s+olarak)?\s+dağıt$", (m, c) =>
            [Distribute(c, c.Target(m.Groups[1].Value), IsOneOf(m.Groups[2].Value, "yatay"))]),
        new($@"^{TrTarget}\s+grupla$", (m, c) =>
            [c.Remember(new GroupAction { Id = c.NextId("group"), Targets = [c.Target(m.Groups[1].Value)] })]),
        new($@"^{TrTarget}\s+(?:grubunu\s+)?çöz$", (m, c) =>
            [new UngroupAction { Id = c.NextId("ungroup"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^{TrTarget}\s+(?:en\s+)?öne\s+getir$", (m, c) =>
            [new BringToFrontAction { Id = c.NextId("front"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^{TrTarget}\s+(?:en\s+)?arkaya\s+gönder$", (m, c) =>
            [new SendToBackAction { Id = c.NextId("back"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^{TrTarget}\s+(?:{Number}\s+(?:kere|kez|defa|adet)\s+)?(?:çoğalt|kopyala)$", (m, c) =>
            [Duplicate(c, c.Target(m.Groups[1].Value), m.Groups[2].Success ? (int)Num(m, 2) : 1)]),
        new($@"^{TrTarget}\s+sil$", (m, c) =>
            [new DeleteAction { Id = c.NextId("delete"), Targets = [c.Target(m.Groups[1].Value)] }]),
        new($@"^{TrTarget}\s+adını\s+(.+?)\s+yap$", (m, c) =>
            [new RenameObjectAction { Id = c.NextId("rename"), Targets = [c.Target(m.Groups[1].Value)], Name = Unquote(m.Groups[2].Value) }]),
        new(@"^(.+?)\s+ad(?:ında|lı)\s+(?:yeni\s+)?(?:bir\s+)?katman\s+oluştur$", (m, c) =>
            [new CreateLayerAction { Id = c.NextId("layer"), Name = Unquote(m.Groups[1].Value) }]),
        new($@"^{TrTarget}\s+(.+?)\s+katmanına\s+taşı$", (m, c) =>
            [new MoveToLayerAction { Id = c.NextId("tolayer"), Targets = [c.Target(m.Groups[1].Value)], Layer = Unquote(m.Groups[2].Value) }]),

        // --- Çıktı ----------------------------------------------------------------------------
        new(@"^(?:dosyayı\s+|belgeyi\s+)?(.+\.cdr)\s+olarak\s+kaydet$", (m, c) =>
            [new SaveDocumentAction { Id = c.NextId("save"), FilePath = Unquote(m.Groups[1].Value) }]),
        new(@"^(.+\.(pdf|png|svg))\s+olarak\s+(?:(?:pdf|png|svg)\s+)?(?:olarak\s+)?(?:dışa\s+aktar|kaydet)$", (m, c) =>
            [Export(c, m.Groups[2].Value, Unquote(m.Groups[1].Value))]),
    ];

    private static IEnumerable<CorelAction> CenteredText(Context context, string text)
    {
        var create = new CreateTextAction { Id = context.NextId("text"), Text = text, FontFamily = "Arial", FontSizePt = 48 };
        context.Remember(create);
        return [create, Center(context, TargetRef.ForAction(create.Id))];
    }

    private static IEnumerable<CorelAction> Shape(Context context, bool rectangle, double width, double height, (double X, double Y)? position)
    {
        var (x, y) = position ?? (0d, 0d);
        CreateShapeAction shape = rectangle
            ? new CreateRectangleAction { Id = context.NextId("rect"), XMm = x, YMm = y, WidthMm = width, HeightMm = height }
            : new CreateEllipseAction { Id = context.NextId("ellipse"), XMm = x, YMm = y, WidthMm = width, HeightMm = height };
        context.Remember(shape);

        // Without an explicit position the new shape goes to the middle of the page.
        return position is null ? [shape, Center(context, TargetRef.ForAction(shape.Id))] : [shape];
    }

    private static IEnumerable<CorelAction> Table(Context context, int columns, int rows, bool centerText)
    {
        // Leave a 10% margin when the page size is known; otherwise use a sensible default block.
        var page = context.Document?.ActivePage;
        var (x, y, width, height) = page is null
            ? (10d, 10d, Math.Max(40, columns * 25d), Math.Max(20, rows * 8d))
            : (page.WidthMm * 0.1, page.HeightMm * 0.1, page.WidthMm * 0.8, page.HeightMm * 0.8);
        return
        [
            context.Remember(new CreateTableAction
            {
                Id = context.NextId("table"),
                XMm = x,
                YMm = y,
                WidthMm = width,
                HeightMm = height,
                Columns = columns,
                Rows = rows,
                CellAlignment = centerText ? TextAlignment.Center : TextAlignment.Left,
            }),
        ];
    }

    private static MoveAction Move(Context context, string target, double distance, string direction)
    {
        var (dx, dy) = Lower(direction) switch
        {
            "right" or "sağa" => (distance, 0d),
            "left" or "sola" => (-distance, 0d),
            "up" or "upward" or "upwards" or "yukarı" or "yukarıya" => (0d, -distance),
            _ => (0d, distance),
        };
        return new MoveAction { Id = context.NextId("move"), Targets = [context.Target(target)], DeltaXMm = dx, DeltaYMm = dy };
    }

    private static AlignAction Center(Context context, string targetRef) => new()
    {
        Id = context.NextId("align"),
        Targets = [targetRef],
        Horizontal = HorizontalAlign.Center,
        Vertical = VerticalAlign.Center,
    };

    private static AlignAction Align(Context context, string targetRef, string edge)
    {
        var (horizontal, vertical) = Lower(edge) switch
        {
            "left" or "sola" => (HorizontalAlign.Left, VerticalAlign.None),
            "right" or "sağa" => (HorizontalAlign.Right, VerticalAlign.None),
            "center" or "centre" or "ortaya" => (HorizontalAlign.Center, VerticalAlign.None),
            "top" or "yukarı" or "yukarıya" or "üste" => (HorizontalAlign.None, VerticalAlign.Top),
            "bottom" or "aşağı" or "aşağıya" or "alta" => (HorizontalAlign.None, VerticalAlign.Bottom),
            _ => (HorizontalAlign.None, VerticalAlign.Center),
        };
        return new AlignAction { Id = context.NextId("align"), Targets = [targetRef], Horizontal = horizontal, Vertical = vertical };
    }

    private static DistributeAction Distribute(Context context, string targetRef, bool horizontal) => new()
    {
        Id = context.NextId("distribute"),
        Targets = [targetRef],
        Direction = horizontal ? DistributeDirection.Horizontal : DistributeDirection.Vertical,
    };

    private static DuplicateAction Duplicate(Context context, string targetRef, int count) =>
        context.Remember(new DuplicateAction { Id = context.NextId("duplicate"), Targets = [targetRef], OffsetXMm = 10, OffsetYMm = 10, Count = count });

    private static CorelAction RemoveStyle(Context context, string target, bool fill) => fill
        ? new SetFillAction { Id = context.NextId("fill"), Targets = [context.Target(target)] }
        : new SetOutlineAction { Id = context.NextId("outline"), Targets = [context.Target(target)], Remove = true };

    private static CorelAction Export(Context context, string format, string path) => Lower(format) switch
    {
        "pdf" => new ExportPdfAction { Id = context.NextId("pdf"), FilePath = path },
        "png" => new ExportPngAction { Id = context.NextId("png"), FilePath = path },
        _ => new ExportSvgAction { Id = context.NextId("svg"), FilePath = path },
    };

    private static double Num(Match match, int group) =>
        double.Parse(match.Groups[group].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>Lower-cases with Turkish rules, so "SAĞA", "YUKARI" and "KIRMIZI" compare correctly.</summary>
    private static string Lower(string value) => value.Trim().ToLower(Turkish);

    private static bool IsOneOf(string value, params string[] options) => options.Contains(Lower(value), StringComparer.Ordinal);

    private static string Color(string value)
    {
        var trimmed = Unquote(value);
        if (ColorHex.IsValid(trimmed))
        {
            return trimmed.ToUpperInvariant();
        }

        return NamedColors.TryGetValue(Lower(trimmed), out var hex) || NamedColors.TryGetValue(trimmed.ToLowerInvariant(), out hex)
            ? hex
            : throw new FormatException(Msg.Format("Planner.UnknownColor", trimmed));
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1]
            : trimmed;
    }

    private static string Summarize(string request)
    {
        var single = request.ReplaceLineEndings(" ").Trim();
        return single.Length <= 60 ? single : single[..57] + "…";
    }

    [GeneratedRegex(@"[\r\n;]+|(?<=[.!?])\s+(?=\p{Lu})")]
    private static partial Regex CommandSeparator();

    /// <summary>
    /// A command pattern. It is compiled twice: once culture-invariant (so "ALIGN" matches "align") and
    /// once with Turkish casing (so "SEÇİLİ" matches "seçili" and "SIL" does not match "sil").
    /// </summary>
    private sealed class Rule
    {
        private readonly Regex _invariant;
        private readonly Regex _turkish;

        public Rule(string pattern, Func<Match, Context, IEnumerable<CorelAction>> build)
        {
            Build = build;
            _invariant = new Regex(pattern, Options);

            // A Regex captures the casing rules of the culture that is current when it is constructed.
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = Turkish;
                _turkish = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        public Func<Match, Context, IEnumerable<CorelAction>> Build { get; }

        public Match? Match(string command)
        {
            var match = _invariant.Match(command);
            if (match.Success)
            {
                return match;
            }

            match = _turkish.Match(command);
            return match.Success ? match : null;
        }
    }

    private sealed class Context(DocumentSnapshot? document)
    {
        private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
        private string? _lastCreated;

        public DocumentSnapshot? Document { get; } = document;

        public List<CorelAction> Actions { get; } = [];

        public string NextId(string prefix)
        {
            _counters[prefix] = _counters.GetValueOrDefault(prefix) + 1;
            return $"{prefix}{_counters[prefix]}";
        }

        public T Remember<T>(T action)
            where T : CorelAction
        {
            _lastCreated = action.Id;
            return action;
        }

        /// <summary>Maps the captured target words of either language to a target reference.</summary>
        public string Target(string token)
        {
            var trimmed = token.Trim();
            if (trimmed.StartsWith("shape_", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.ToLowerInvariant();
            }

            if (trimmed[0] is '"' or '\'')
            {
                return TargetRef.ForName(Unquote(trimmed));
            }

            if (Lower(trimmed) is "it" or "o" or "onu" or "bu" or "bunu")
            {
                return _lastCreated is null
                    ? throw new FormatException(Msg.Get("Planner.PronounWithoutObject"))
                    : TargetRef.ForAction(_lastCreated);
            }

            return TargetRef.Selection;
        }
    }
}
