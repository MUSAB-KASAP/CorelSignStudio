using System.Globalization;
using System.Text.RegularExpressions;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Planning;

public sealed record PlanningRequest
{
    public required string UserRequest { get; init; }

    /// <summary>The current document, when one was inspected; lets planners resolve "the title", page centre, etc.</summary>
    public DocumentSnapshot? Document { get; init; }

    public IReadOnlyList<ReferenceInput> References { get; init; } = [];
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

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
/// Temporary, rule-based planner that understands a small fixed vocabulary (English plus a few
/// Turkish forms). It exists to exercise the whole pipeline before an AI planner is connected.
/// </summary>
public sealed partial class DeterministicCommandPlanner : ICommandPlanner
{
    private const string Number = @"(-?\d+(?:[.,]\d+)?)";
    private const string Size = Number + @"\s*(?:mm)?\s*[x×*]\s*" + Number;
    private const string Target = @"(shape_\d+|the\s+selection|selection|(?:the\s+)?selected\s+(?:objects?|shapes?)|""[^""]+""|'[^']+'|it)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly IReadOnlyDictionary<string, string> NamedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["white"] = "#FFFFFF", ["red"] = "#FF0000", ["green"] = "#00A651", ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00", ["orange"] = "#FF8000", ["gray"] = "#808080", ["grey"] = "#808080",
        ["siyah"] = "#000000", ["beyaz"] = "#FFFFFF", ["kırmızı"] = "#FF0000", ["yeşil"] = "#00A651", ["mavi"] = "#0000FF",
        ["sarı"] = "#FFFF00",
    };

    private static readonly IReadOnlyList<Rule> Rules = BuildRules();

    public string Name => "Deterministic test planner";

    /// <summary>Example commands shown in the UI.</summary>
    public static IReadOnlyList<string> Examples { get; } =
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
                var match = rule.Pattern.Match(command);
                if (!match.Success)
                {
                    continue;
                }

                try
                {
                    context.Actions.AddRange(rule.Build(match, context));
                    handled = true;
                }
                catch (FormatException exception)
                {
                    unrecognized.Add($"{command} ({exception.Message})");
                    handled = true;
                }

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
                Message = unrecognized.Count == 0
                    ? "Type what CorelDRAW should do."
                    : "None of the commands were understood by the built-in test planner.",
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
            Metadata = new Dictionary<string, string> { ["planner"] = Name },
        };

        return new PlanningResult
        {
            Plan = plan,
            UnrecognizedCommands = unrecognized,
            Message = unrecognized.Count == 0
                ? $"{plan.Actions.Count} operation(s) prepared."
                : $"{plan.Actions.Count} operation(s) prepared; {unrecognized.Count} command(s) were not understood.",
        };
    }

    public static IReadOnlyList<string> SplitCommands(string request) =>
        CommandSeparator().Split(request)
            .Select(part => part.Trim().TrimEnd('.', '!').Trim())
            .Where(part => part.Length > 0)
            .ToArray();

    private static List<Rule> BuildRules() =>
    [
        // --- Document -------------------------------------------------------------------------
        new($@"^(?:create|make|new)\s+(?:a\s+)?(?:new\s+)?{Size}\s*(?:mm)?\s+(?:document|page|design|file)$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:create|make|new)\s+(?:a\s+)?(?:new\s+)?(?:document|page|design|file)\s+(?:of\s+|with\s+size\s+|sized\s+)?{Size}\s*(?:mm)?$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^{Size}\s*(?:mm)?\s+(?:belge|doküman|dosya|sayfa|tasarım)\s+(?:oluştur|aç|yap)$", (m, c) =>
            [new CreateDocumentAction { Id = c.NextId("doc"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:make|set|resize)\s+(?:this\s+|the\s+)?(?:design|page|document)(?:\s+size)?(?:\s+to)?\s+{Size}\s*(?:mm)?$", (m, c) =>
            [new SetPageSizeAction { Id = c.NextId("page"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),
        new($@"^(?:bu\s+)?(?:tasarımı|sayfayı|belgeyi)\s+{Size}\s*(?:mm)?\s+yap$", (m, c) =>
            [new SetPageSizeAction { Id = c.NextId("page"), WidthMm = Num(m, 1), HeightMm = Num(m, 2) }]),

        // --- Create ---------------------------------------------------------------------------
        new(@"^(?:add|create|write|insert)\s+(?:the\s+)?text\s+(.+?)\s+(?:in|at|to)\s+(?:the\s+)?(?:page\s+)?(?:center|centre|middle)(?:\s+of\s+the\s+page)?$", (m, c) =>
            CenteredText(c, Unquote(m.Groups[1].Value))),
        new(@"^(?:ortaya|merkeze)\s+(.+?)\s+(?:yaz|metni\s+ekle|ekle)$", (m, c) => CenteredText(c, Unquote(m.Groups[1].Value))),
        new($@"^(?:add|create|write|insert)\s+(?:the\s+)?text\s+(.+?)\s+at\s+{Number}\s*(?:mm)?\s*[,; ]\s*{Number}\s*(?:mm)?$", (m, c) =>
            [c.Remember(new CreateTextAction { Id = c.NextId("text"), Text = Unquote(m.Groups[1].Value), XMm = Num(m, 2), YMm = Num(m, 3) })]),
        new($@"^(?:add|create|draw|insert)\s+(?:a\s+|an\s+)?(rectangle|ellipse|circle)\s+(?:of\s+)?{Size}\s*(?:mm)?(?:\s+at\s+{Number}\s*(?:mm)?\s*[,; ]\s*{Number}\s*(?:mm)?)?$", (m, c) =>
        {
            var (x, y) = m.Groups[4].Success ? (Num(m, 4), Num(m, 5)) : (0d, 0d);
            CreateShapeAction shape = m.Groups[1].Value.Equals("rectangle", StringComparison.OrdinalIgnoreCase)
                ? new CreateRectangleAction { Id = c.NextId("rect"), XMm = x, YMm = y, WidthMm = Num(m, 2), HeightMm = Num(m, 3) }
                : new CreateEllipseAction { Id = c.NextId("ellipse"), XMm = x, YMm = y, WidthMm = Num(m, 2), HeightMm = Num(m, 3) };
            c.Remember(shape);
            return m.Groups[4].Success
                ? new CorelAction[] { shape }
                : new CorelAction[] { shape, new AlignAction { Id = c.NextId("align"), Targets = [TargetRef.ForAction(shape.Id)], Horizontal = HorizontalAlign.Center, Vertical = VerticalAlign.Center } };
        }),
        new(@"^(?:add|create|draw|insert|make)\s+(?:a\s+)?table\s+(?:with|of)\s+(\d+)\s+columns?\s+(?:and|by|x)\s+(\d+)\s+rows?(\s+and\s+(?:center|centre)\s+(?:all\s+)?(?:the\s+)?text)?$", (m, c) =>
            Table(c, Int(m, 1), Int(m, 2), m.Groups[3].Success)),
        new(@"^(?:add|create|draw|insert|make)\s+(?:a\s+)?table\s+(?:with|of)\s+(\d+)\s+rows?\s+(?:and|by|x)\s+(\d+)\s+columns?(\s+and\s+(?:center|centre)\s+(?:all\s+)?(?:the\s+)?text)?$", (m, c) =>
            Table(c, Int(m, 2), Int(m, 1), m.Groups[3].Success)),
        new(@"^(\d+)\s+sütun(?:lu)?\s+(?:ve\s+)?(\d+)\s+satır(?:lı)?\s+(?:bir\s+)?tablo\s+(?:oluştur|ekle|yap)$", (m, c) =>
            Table(c, Int(m, 1), Int(m, 2), false)),
        new(@"^import\s+(?:the\s+)?(?:file\s+)?(.+)$", (m, c) =>
            [c.Remember(new ImportFileAction { Id = c.NextId("import"), FilePath = Unquote(m.Groups[1].Value) })]),

        // --- Transform ------------------------------------------------------------------------
        new($@"^(?:move|shift|nudge)\s+{Target}\s+(?:by\s+)?{Number}\s*(?:mm)?\s+(?:to\s+the\s+)?(right|left|up|upwards?|down|downwards?)$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 2), m.Groups[3].Value)]),
        new($@"^(?:move|shift|nudge)\s+{Target}\s+(right|left|up|upwards?|down|downwards?)\s+(?:by\s+)?{Number}\s*(?:mm)?$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 3), m.Groups[2].Value)]),
        new($@"^{Target}(?:'?[ıiuü]|'?y[ıiuü]|'?n[ıiuü])?\s+{Number}\s*(?:mm)?\s+(sağa|sola|yukarı|aşağı)\s+(?:taşı|kaydır|al)$", (m, c) =>
            [Move(c, m.Groups[1].Value, Num(m, 2), m.Groups[3].Value)]),
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
        new($@"^{Target}\s+metnini\s+(.+?)\s+yap$", (m, c) =>
            [new SetTextAction { Id = c.NextId("settext"), Targets = [c.Target(m.Groups[1].Value)], Text = Unquote(m.Groups[2].Value) }]),
        new($@"^(?:change|set)\s+(?:the\s+)?font\s+of\s+{Target}\s+to\s+(.+?)(?:\s+{Number}\s*pt)?$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontFamily = Unquote(m.Groups[2].Value), FontSizePt = m.Groups[3].Success ? Num(m, 3) : null }]),
        new($@"^(?:change|set)\s+(?:the\s+)?font\s+size\s+of\s+{Target}\s+to\s+{Number}\s*(?:pt)?$", (m, c) =>
            [new SetFontAction { Id = c.NextId("font"), Targets = [c.Target(m.Groups[1].Value)], FontSizePt = Num(m, 2) }]),
        new($@"^(?:set|change)\s+(?:the\s+)?(?:fill|colou?r)\s+of\s+{Target}\s+to\s+(\S+)$", (m, c) =>
            [new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^(?:fill|colou?r)\s+{Target}\s+(?:with\s+)?(\S+)$", (m, c) =>
            [new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value) }]),
        new($@"^remove\s+(?:the\s+)?(fill|outline)\s+(?:of|from)\s+{Target}$", (m, c) =>
            [m.Groups[1].Value.Equals("fill", StringComparison.OrdinalIgnoreCase)
                ? (CorelAction)new SetFillAction { Id = c.NextId("fill"), Targets = [c.Target(m.Groups[2].Value)] }
                : new SetOutlineAction { Id = c.NextId("outline"), Targets = [c.Target(m.Groups[2].Value)], Remove = true }]),
        new($@"^(?:set|change)\s+(?:the\s+)?outline\s+of\s+{Target}\s+to\s+(\S+)(?:\s+{Number}\s*mm)?$", (m, c) =>
            [new SetOutlineAction { Id = c.NextId("outline"), Targets = [c.Target(m.Groups[1].Value)], Color = Color(m.Groups[2].Value), WidthMm = m.Groups[3].Success ? Num(m, 3) : null }]),

        // --- Arrange --------------------------------------------------------------------------
        new($@"^(?:center|centre)\s+{Target}(?:\s+(?:on|in)\s+(?:the\s+)?page)?$", (m, c) =>
            [new AlignAction { Id = c.NextId("align"), Targets = [c.Target(m.Groups[1].Value)], Horizontal = HorizontalAlign.Center, Vertical = VerticalAlign.Center }]),
        new($@"^align\s+{Target}\s+(?:to\s+(?:the\s+)?)?(left|center|centre|right|top|middle|bottom)(?:\s+(?:on|of|in)\s+(?:the\s+)?page)?$", (m, c) =>
            [Align(c, m.Groups[1].Value, m.Groups[2].Value)]),
        new($@"^distribute\s+{Target}\s+(horizontally|vertically)$", (m, c) =>
            [new DistributeAction { Id = c.NextId("distribute"), Targets = [c.Target(m.Groups[1].Value)], Direction = m.Groups[2].Value.StartsWith('h') || m.Groups[2].Value.StartsWith('H') ? DistributeDirection.Horizontal : DistributeDirection.Vertical }]),
        new($@"^(?:duplicate|copy)\s+{Target}(?:\s+{Number}\s+times)?$", (m, c) =>
            [c.Remember(new DuplicateAction { Id = c.NextId("duplicate"), Targets = [c.Target(m.Groups[1].Value)], OffsetXMm = 10, OffsetYMm = 10, Count = m.Groups[2].Success ? (int)Num(m, 2) : 1 })]),
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
        {
            var path = Unquote(m.Groups[2].Value);
            return [m.Groups[1].Value.ToLowerInvariant() switch
            {
                "pdf" => (CorelAction)new ExportPdfAction { Id = c.NextId("pdf"), FilePath = path },
                "png" => new ExportPngAction { Id = c.NextId("png"), FilePath = path },
                _ => new ExportSvgAction { Id = c.NextId("svg"), FilePath = path },
            }];
        }),
    ];

    private static IEnumerable<CorelAction> CenteredText(Context context, string text)
    {
        var create = new CreateTextAction { Id = context.NextId("text"), Text = text, FontFamily = "Arial", FontSizePt = 48 };
        context.Remember(create);
        return
        [
            create,
            new AlignAction
            {
                Id = context.NextId("align"),
                Targets = [TargetRef.ForAction(create.Id)],
                Horizontal = HorizontalAlign.Center,
                Vertical = VerticalAlign.Center,
            },
        ];
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
        var (dx, dy) = direction.ToLowerInvariant() switch
        {
            "right" or "sağa" => (distance, 0d),
            "left" or "sola" => (-distance, 0d),
            "up" or "upward" or "upwards" or "yukarı" => (0d, -distance),
            _ => (0d, distance),
        };
        return new MoveAction { Id = context.NextId("move"), Targets = [context.Target(target)], DeltaXMm = dx, DeltaYMm = dy };
    }

    private static AlignAction Align(Context context, string target, string edge)
    {
        var (horizontal, vertical) = edge.ToLowerInvariant() switch
        {
            "left" => (HorizontalAlign.Left, VerticalAlign.None),
            "right" => (HorizontalAlign.Right, VerticalAlign.None),
            "center" or "centre" => (HorizontalAlign.Center, VerticalAlign.None),
            "top" => (HorizontalAlign.None, VerticalAlign.Top),
            "bottom" => (HorizontalAlign.None, VerticalAlign.Bottom),
            _ => (HorizontalAlign.None, VerticalAlign.Center),
        };
        return new AlignAction { Id = context.NextId("align"), Targets = [context.Target(target)], Horizontal = horizontal, Vertical = vertical };
    }

    private static double Num(Match match, int group) =>
        double.Parse(match.Groups[group].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string Color(string value)
    {
        var trimmed = Unquote(value);
        if (ColorHex.IsValid(trimmed))
        {
            return trimmed.ToUpperInvariant();
        }

        return NamedColors.TryGetValue(trimmed, out var hex)
            ? hex
            : throw new FormatException($"unknown colour '{trimmed}'; use a name such as red or #RRGGBB");
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

    private sealed class Rule(string pattern, Func<Match, Context, IEnumerable<CorelAction>> build)
    {
        public Regex Pattern { get; } = new(pattern, Options);

        public Func<Match, Context, IEnumerable<CorelAction>> Build { get; } = build;
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

        public string Target(string token)
        {
            var trimmed = token.Trim();
            if (trimmed.StartsWith("shape_", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.ToLowerInvariant();
            }

            if (trimmed.Equals("it", StringComparison.OrdinalIgnoreCase))
            {
                return _lastCreated is null
                    ? throw new FormatException("'it' does not refer to anything created earlier in this request")
                    : TargetRef.ForAction(_lastCreated);
            }

            if (trimmed[0] is '"' or '\'')
            {
                return TargetRef.ForName(Unquote(trimmed));
            }

            return TargetRef.Selection;
        }
    }
}
