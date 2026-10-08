namespace CorelSignStudio.Domain.Batch;

/// <summary>
/// A source of batch rows: the first row holds the variable names, every following row is one job.
/// Implementations exist for CSV and Excel (.xlsx); both produce the same <see cref="BatchRow"/> list.
/// </summary>
public interface IBatchDataReader
{
    bool CanRead(string path);

    /// <summary>Worksheet names, in order. A format without sheets returns an empty list.</summary>
    IReadOnlyList<string> GetSheetNames(string path);

    /// <param name="path">Data file.</param>
    /// <param name="sheetName">Worksheet to read; the first one when omitted.</param>
    IReadOnlyList<BatchRow> Read(string path, string? sheetName = null);
}
