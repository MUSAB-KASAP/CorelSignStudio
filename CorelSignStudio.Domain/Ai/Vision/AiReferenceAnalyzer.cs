using System.Text.RegularExpressions;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Ai.Vision;

public sealed record ReferenceAnalysisRequest
{
    public required ReferenceInput Reference { get; init; }

    /// <summary>What the user wrote; lets the analyzer apply requested changes and find a page number.</summary>
    public string UserRequest { get; init; } = "";

    /// <summary>1-based page for multi-page sources, when already known.</summary>
    public int? PageNumber { get; init; }

    /// <summary>True to analyse again even if an identical request was answered earlier in this session.</summary>
    public bool BypassCache { get; init; }
}

public sealed record ReferenceAnalysisResult
{
    public required AiPlanningStatus Status { get; init; }
    public ReferenceAnalysis? Analysis { get; init; }
    public string? ClarificationQuestion { get; init; }
    public required string UserMessage { get; init; }
    public AiPlanningDiagnostics Diagnostics { get; init; } = new();

    public bool IsReady => Status == AiPlanningStatus.Ready && Analysis is not null;
}

/// <summary>Understands a reference well enough to rebuild it: what is on it, where, and how each part should be recreated.</summary>
public interface IReferenceVisionAnalyzer
{
    Task<ReferenceAnalysisResult> AnalyzeAsync(ReferenceAnalysisRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reference → preview → vision model → validated <see cref="ReferenceAnalysis"/>.
/// Vector references (SVG, CDR, single-page PDF) are never sent to the model: their own geometry is better
/// than any redraw, so they are marked for reuse. Only the one reference passed in is ever uploaded, and
/// only when this method is called.
/// </summary>
public sealed partial class AiReferenceAnalyzer(
    Func<IAiClient?> clientFactory,
    IReferencePreviewRenderer previewRenderer,
    ReferenceAnalysisParser? parser = null,
    AiPlannerOptions? options = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IReferenceVisionAnalyzer
{
    private static readonly string SystemPrompt = ReferenceVisionPrompt.Build();
    private static readonly string ResponseSchema = ReferenceVisionPrompt.BuildResponseSchema();

    private readonly ReferenceAnalysisParser _parser = parser ?? new ReferenceAnalysisParser();
    private readonly AiPlannerOptions _options = options ?? new AiPlannerOptions();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<ReferenceAnalysisResult> AnalyzeAsync(ReferenceAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.Reference;
        var requestedPage = request.PageNumber ?? FindPageNumber(request.UserRequest);

        ReferencePreview? preview = null;
        if (previewRenderer.CanRender(reference))
        {
            try
            {
                preview = await previewRenderer.RenderAsync(reference, new ReferencePreviewOptions { PageNumber = requestedPage ?? 1 }, cancellationToken).ConfigureAwait(false);
            }
            catch (ReferencePreviewException exception)
            {
                return Failed(AiPlanningStatus.Invalid, exception.Message);
            }
        }

        if (reference.IsVector)
        {
            // SVG and CDR are always reused whole. Only a multi-page PDF needs a page to be chosen.
            if (reference.FileType != ReferenceFileType.Pdf)
            {
                return VectorReuse(reference, preview);
            }

            if (preview is { PageCount: > 1 } && requestedPage is null)
            {
                var question = Msg.Format("Vision.PageQuestion", preview.PageCount);
                return new ReferenceAnalysisResult { Status = AiPlanningStatus.NeedsClarification, ClarificationQuestion = question, UserMessage = question };
            }

            if (preview is null or { PageCount: <= 1 })
            {
                return VectorReuse(reference, preview);
            }

            // A chosen page of a multi-page PDF cannot be imported on its own, so it is analysed visually.
        }

        if (preview is null)
        {
            return Failed(AiPlanningStatus.Invalid, Msg.Format("Vision.PreviewFailed", reference.FileName));
        }

        var client = clientFactory();
        if (client is null)
        {
            return Failed(AiPlanningStatus.ProviderError, AiUserMessages.For(AiErrorKind.NotConfigured), AiErrorKind.NotConfigured);
        }

        if (!client.SupportsImages)
        {
            return Failed(AiPlanningStatus.ProviderError, Msg.Get("Vision.NotSupported"), AiErrorKind.BadRequest);
        }

        var image = new AiImage
        {
            FileName = reference.FileName,
            MimeType = preview.MimeType,
            Bytes = preview.Bytes,
            WidthPixels = preview.WidthPixels,
            HeightPixels = preview.HeightPixels,
        };
        var baseMessage = BuildUserMessage(request, preview);
        var userMessage = baseMessage;
        var attempts = 0;
        var repaired = false;
        AiResponse? response = null;
        ReferenceParseResult? parsed = null;

        while (attempts < Math.Max(1, _options.MaxAttempts))
        {
            attempts++;
            try
            {
                response = await client.CompleteAsync(
                    new AiRequest
                    {
                        SystemPrompt = SystemPrompt,
                        UserMessage = userMessage,
                        Images = [image],
                        JsonSchema = _options.UseJsonSchema ? ResponseSchema : null,
                        MaxOutputTokens = _options.MaxOutputTokens,
                        Timeout = _options.RequestTimeout,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiClientException exception)
            {
                if (exception.IsTransient && attempts < _options.MaxAttempts)
                {
                    await _delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new ReferenceAnalysisResult
                {
                    Status = AiPlanningStatus.ProviderError,
                    UserMessage = exception.UserMessage,
                    Diagnostics = Diagnostics(client, response, attempts) with { ErrorKind = exception.Kind, TechnicalError = exception.ToString() },
                };
            }

            parsed = _parser.Parse(response.Content);
            if (parsed.Status != AiPlanningStatus.Invalid || parsed.RepairHint is null || repaired || attempts >= _options.MaxAttempts)
            {
                break;
            }

            repaired = true;
            userMessage = baseMessage + "\n\n## Your previous answer was rejected\n\n" + parsed.RepairHint +
                          "\n\nLook at the image again and return a corrected JSON object.";
        }

        var diagnostics = Diagnostics(client, response, attempts);
        if (parsed is null)
        {
            return new ReferenceAnalysisResult { Status = AiPlanningStatus.Invalid, UserMessage = Msg.Get("Vision.Invalid.Malformed"), Diagnostics = diagnostics };
        }

        if (parsed.Status == AiPlanningStatus.NeedsClarification)
        {
            return new ReferenceAnalysisResult
            {
                Status = AiPlanningStatus.NeedsClarification,
                ClarificationQuestion = parsed.ClarificationQuestion,
                UserMessage = parsed.ClarificationQuestion!,
                Diagnostics = diagnostics,
            };
        }

        if (parsed.Status != AiPlanningStatus.Ready)
        {
            return new ReferenceAnalysisResult { Status = AiPlanningStatus.Invalid, UserMessage = parsed.UserError ?? Msg.Get("Vision.Invalid.Malformed"), Diagnostics = diagnostics };
        }

        var warnings = parsed.Warnings.ToList();
        if (reference.IsVector)
        {
            warnings.Add(Msg.Get("Vision.MultiPageRedraw"));
        }

        var analysis = new ReferenceAnalysis
        {
            ReferenceId = reference.Id,
            AnalyzerName = Msg.Get("Vision.Analyzer.Name"),
            FileType = reference.FileType,
            FileName = reference.FileName,
            FileSizeBytes = SafeLength(reference.FilePath),
            PageNumber = preview.PageNumber,
            PageCount = preview.PageCount,
            PhysicalSize = preview.PhysicalSize,
            PixelWidth = preview.OriginalWidthPixels > 0 ? preview.OriginalWidthPixels : preview.WidthPixels,
            PixelHeight = preview.OriginalHeightPixels > 0 ? preview.OriginalHeightPixels : preview.HeightPixels,
            AspectRatio = preview.HeightPixels > 0 ? (double)preview.WidthPixels / preview.HeightPixels : null,
            BackgroundColor = parsed.BackgroundColor,
            Elements = parsed.Elements,
            Colors = parsed.Elements.SelectMany(element => new[] { element.FillColor, element.OutlineColor }).Where(color => color is not null).Select(color => color!).Distinct().ToArray(),
            Fonts = parsed.Elements.Select(element => element.FontFamilyGuess).Where(font => font is not null).Select(font => font!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings,
            AppliedModifications = parsed.AppliedModifications,
            Summary = parsed.Summary,
            Confidence = parsed.Confidence,
        };

        return new ReferenceAnalysisResult
        {
            Status = AiPlanningStatus.Ready,
            Analysis = analysis,
            UserMessage = Msg.Format("Vision.Ready", analysis.Elements.Count),
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Finds "2. sayfa", "sayfa 2", "page 2" or "ikinci sayfa" in the request.</summary>
    public static int? FindPageNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PageNumberPattern().Match(text);
        if (match.Success)
        {
            var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return int.TryParse(digits, out var page) && page > 0 ? page : null;
        }

        // Turkish lower-casing first, so "İkinci" is read like "ikinci".
        var ordinal = PageOrdinalPattern().Match(text.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));
        return ordinal.Success
            ? ordinal.Groups[1].Value switch
            {
                "ilk" or "birinci" or "first" => 1,
                "ikinci" or "second" => 2,
                "üçüncü" or "third" => 3,
                "dördüncü" or "fourth" => 4,
                _ => (int?)null,
            }
            : null;
    }

    private static ReferenceAnalysisResult VectorReuse(ReferenceInput reference, ReferencePreview? preview)
    {
        var analysis = new ReferenceAnalysis
        {
            ReferenceId = reference.Id,
            AnalyzerName = Msg.Get("Reference.Analyzer.Name"),
            FileType = reference.FileType,
            FileName = reference.FileName,
            FileSizeBytes = SafeLength(reference.FilePath),
            PhysicalSize = preview?.PhysicalSize,
            PixelWidth = preview?.WidthPixels,
            PixelHeight = preview?.HeightPixels,
            AspectRatio = preview?.PhysicalSize?.AspectRatio ?? (preview is { HeightPixels: > 0 } ? (double)preview.WidthPixels / preview.HeightPixels : null),
            CanReuseVectorContent = true,
            Elements =
            [
                new ReferenceElement
                {
                    Id = ReferenceAnalysis.ElementId(1),
                    Kind = ReferenceElementKind.Group,
                    Label = reference.FileName,
                    Bounds = new NormalizedBounds(0, 0, 1, 1),
                    ZIndex = 1,
                    Strategy = ReconstructionStrategy.ReuseVector,
                },
            ],
            Notes = [Msg.Get("Vision.VectorReuse"), Msg.Get("Vision.Note.VectorNoAi")],
            Summary = Msg.Get("Vision.VectorReuse"),
            Confidence = 1,
        };
        return new ReferenceAnalysisResult { Status = AiPlanningStatus.Ready, Analysis = analysis, UserMessage = Msg.Get("Vision.VectorReuse") };
    }

    private static string BuildUserMessage(ReferenceAnalysisRequest request, ReferencePreview preview) =>
        $"Reference file: {request.Reference.FileName} (type {request.Reference.FileTypeLabel}, page {preview.PageNumber} of {preview.PageCount}, " +
        $"shown at {preview.WidthPixels} x {preview.HeightPixels} px).\n\n" +
        "## User request\n\n" + (string.IsNullOrWhiteSpace(request.UserRequest) ? "(none — describe the design as it is)" : request.UserRequest.Trim());

    private static ReferenceAnalysisResult Failed(AiPlanningStatus status, string message, AiErrorKind? kind = null) =>
        new() { Status = status, UserMessage = message, Diagnostics = new AiPlanningDiagnostics { ErrorKind = kind } };

    private static AiPlanningDiagnostics Diagnostics(IAiClient client, AiResponse? response, int attempts) => new()
    {
        ProviderId = response?.ProviderId ?? client.ProviderId,
        Model = response?.Model ?? client.Model,
        RequestId = response?.RequestId,
        Attempts = attempts,
        InputTokens = response?.InputTokens,
        OutputTokens = response?.OutputTokens,
        PlannerUsed = nameof(AiReferenceAnalyzer),
    };

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"(?:(\d+)\s*\.?\s*(?:sayfa|page)|(?:sayfa|page)\s*(\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberPattern();

    [GeneratedRegex(@"\b(ilk|birinci|ikinci|üçüncü|dördüncü|first|second|third|fourth)\s+(?:sayfa|page)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageOrdinalPattern();
}
