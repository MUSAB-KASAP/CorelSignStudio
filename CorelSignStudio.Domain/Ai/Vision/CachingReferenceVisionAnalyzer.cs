using System.Security.Cryptography;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Ai.Vision;

/// <summary>
/// Avoids sending the same unchanged reference to the provider twice in one session. The key is the
/// file's content hash, the page and the request's content (sizes ignored, since a size does not change
/// what is on the reference). Only successful analyses are kept, only in memory, and never any key or
/// image: the cache holds the structured result, not the picture.
/// </summary>
public sealed class CachingReferenceVisionAnalyzer(IReferenceVisionAnalyzer inner, int capacity = 16) : IReferenceVisionAnalyzer
{
    private readonly object _gate = new();
    private readonly LinkedList<(string Key, ReferenceAnalysisResult Result)> _entries = new();

    /// <summary>How many requests were answered from memory.</summary>
    public int Hits { get; private set; }

    public async Task<ReferenceAnalysisResult> AnalyzeAsync(ReferenceAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.BypassCache ? null : KeyFor(request);
        if (key is not null)
        {
            lock (_gate)
            {
                var node = _entries.First;
                while (node is not null && node.Value.Key != key)
                {
                    node = node.Next;
                }

                if (node is not null)
                {
                    _entries.Remove(node);
                    _entries.AddFirst(node);
                    Hits++;

                    // The reference object may be a new instance of the same file; hand back its id.
                    return node.Value.Result with { Analysis = node.Value.Result.Analysis! with { ReferenceId = request.Reference.Id } };
                }
            }
        }

        var result = await inner.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        var storeKey = key ?? KeyFor(request);
        if (result.IsReady && storeKey is not null)
        {
            lock (_gate)
            {
                var existing = _entries.First;
                while (existing is not null && existing.Value.Key != storeKey)
                {
                    existing = existing.Next;
                }

                if (existing is not null)
                {
                    _entries.Remove(existing);
                }

                _entries.AddFirst((storeKey, result));
                while (_entries.Count > Math.Max(1, capacity))
                {
                    _entries.RemoveLast();
                }
            }
        }

        return result;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static string? KeyFor(ReferenceAnalysisRequest request)
    {
        try
        {
            using var stream = File.OpenRead(request.Reference.FilePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var page = request.PageNumber ?? AiReferenceAnalyzer.FindPageNumber(request.UserRequest) ?? 0;
            var content = DimensionParser.RemoveSizes(request.UserRequest).ToLowerInvariant();
            return $"{hash}|{page}|{content}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null; // an unreadable file is simply not cached; the analyzer reports the problem
        }
    }
}
