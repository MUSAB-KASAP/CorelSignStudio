using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using CorelSignStudio.Domain.Ai;

namespace CorelSignStudio.AI;

/// <summary>
/// <see cref="IAiClient"/> for the Claude API, through the official Anthropic C# SDK. This is the only
/// class that knows the provider; everything else works against the Domain abstraction.
/// </summary>
public sealed class AnthropicAiClient : IAiClient
{
    public const string Provider = "anthropic";
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AnthropicClient _client;

    /// <param name="apiKey">Secret key. It is handed to the SDK and never logged or stored by this class.</param>
    /// <param name="model">Model id; defaults to <see cref="DefaultModel"/>.</param>
    public AnthropicAiClient(string apiKey, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiClientException(AiErrorKind.NotConfigured, "No Anthropic API key is configured.");
        }

        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

        // Retries are owned by AiCommandPlanner (small and bounded), so the SDK must not add its own.
        _client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 0 };
    }

    public string ProviderId => Provider;

    public string Model { get; }

    public bool SupportsImages => true;

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await SendAsync(request, request.JsonSchema, cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicBadRequestException) when (request.JsonSchema is not null)
        {
            // The schema is a convenience, not the safety mechanism: every answer is re-validated by
            // AiPlanParser. If the provider rejects the schema itself, ask again in plain JSON mode.
            return await SendAsync(request, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Images first, then the text — the order the API recommends for image understanding.</summary>
    private static MessageParamContent BuildContent(AiRequest request)
    {
        if (request.Images.Count == 0)
        {
            return request.UserMessage;
        }

        var blocks = new List<ContentBlockParam>();
        foreach (var image in request.Images)
        {
            blocks.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = Convert.ToBase64String(image.Bytes),
                    MediaType = image.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? MediaType.ImageJpeg : MediaType.ImagePng,
                },
            });
        }

        blocks.Add(new TextBlockParam { Text = request.UserMessage });
        return blocks;
    }

    private async Task<AiResponse> SendAsync(AiRequest request, string? jsonSchema, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            var parameters = new MessageCreateParams
            {
                Model = Model,
                MaxTokens = request.MaxOutputTokens,

                // The instructions are identical for every request, so let the provider cache them.
                System = new List<TextBlockParam>
                {
                    new() { Text = request.SystemPrompt, CacheControl = new CacheControlEphemeral() },
                },
                Messages = [new() { Role = Role.User, Content = BuildContent(request) }],
                OutputConfig = jsonSchema is null
                    ? null
                    : new OutputConfig
                    {
                        Format = new JsonOutputFormat
                        {
                            Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema)!,
                        },
                    },
            };

            var message = await _client.Messages.Create(parameters, cancellationToken: timeout.Token).ConfigureAwait(false);

            // Always check why generation stopped before trusting the content.
            var stopReason = message.StopReason?.ToString() ?? "";
            if (stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiClientException(AiErrorKind.Refused, "The model declined the request (stop_reason: refusal).");
            }

            var text = string.Concat(message.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new AiClientException(AiErrorKind.Unknown, $"The model returned no text (stop_reason: {stopReason}).");
            }

            return new AiResponse
            {
                Content = text,
                ProviderId = Provider,
                Model = Model,
                RequestId = message.ID,
                InputTokens = message.Usage.InputTokens,
                OutputTokens = message.Usage.OutputTokens,
            };
        }
        catch (AnthropicBadRequestException) when (jsonSchema is not null)
        {
            throw; // handled by CompleteAsync: retry once without the schema
        }
        catch (AiClientException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiClientException(AiErrorKind.Timeout, $"No answer within {request.Timeout.TotalSeconds:0} s.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw AnthropicErrorMapper.Map(exception);
        }
    }
}

/// <summary>Classifies SDK exceptions, most specific first, so retryable and permanent failures stay distinguishable.</summary>
public static class AnthropicErrorMapper
{
    public static AiClientException Map(Exception exception) => exception switch
    {
        AiClientException already => already,
        AnthropicUnauthorizedException => new(AiErrorKind.InvalidApiKey, "401: the API key was rejected.", exception),
        AnthropicForbiddenException => new(AiErrorKind.InvalidApiKey, "403: the API key is not permitted to do this.", exception),
        AnthropicNotFoundException => new(AiErrorKind.ModelNotFound, "404: the model or endpoint was not found.", exception),
        AnthropicRateLimitException => new(AiErrorKind.RateLimited, "429: rate limited.", exception),
        AnthropicBadRequestException => new(AiErrorKind.BadRequest, "400: " + exception.Message, exception),
        AnthropicUnprocessableEntityException => new(AiErrorKind.BadRequest, "422: " + exception.Message, exception),
        Anthropic5xxException => new(AiErrorKind.ServiceUnavailable, "5xx: " + exception.Message, exception),
        AnthropicIOException => new(AiErrorKind.Network, "Network error: " + exception.Message, exception),
        HttpRequestException => new(AiErrorKind.Network, "Network error: " + exception.Message, exception),
        TimeoutException => new(AiErrorKind.Timeout, exception.Message, exception),
        AnthropicApiException => new(AiErrorKind.Unknown, exception.Message, exception),
        _ => new(AiErrorKind.Unknown, exception.Message, exception),
    };
}
