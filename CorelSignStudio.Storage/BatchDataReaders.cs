using ClosedXML.Excel;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Recipes;

namespace CorelSignStudio.Storage;

/// <summary>CSV as a batch data source (see <see cref="CsvBatchReader"/> for the format rules).</summary>
public sealed class CsvBatchDataReader : IBatchDataReader
{
    public bool CanRead(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".txt";

    public IReadOnlyList<string> GetSheetNames(string path) => [];

    public IReadOnlyList<BatchRow> Read(string path, string? sheetName = null) => CsvBatchReader.ReadFile(path);
}

/// <summary>
/// Excel (.xlsx) as a batch data source, read with ClosedXML (MIT) — no Excel installation and no COM.
/// The first used row holds the variable names; cells are taken as displayed text, so dates, numbers and
/// leading zeros formatted in Excel arrive the way the user sees them. The file is opened read-only and
/// may be open in Excel at the same time.
/// </summary>
public sealed class ExcelBatchDataReader : IBatchDataReader
{
    public bool CanRead(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";

    public IReadOnlyList<string> GetSheetNames(string path)
    {
        using var workbook = Open(path);
        return workbook.Worksheets.Select(sheet => sheet.Name).ToArray();
    }

    public IReadOnlyList<BatchRow> Read(string path, string? sheetName = null)
    {
        using var workbook = Open(path);
        IXLWorksheet sheet;
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            sheet = workbook.Worksheets.First();
        }
        else if (!workbook.Worksheets.TryGetWorksheet(sheetName, out sheet!))
        {
            throw new FormatException(Msg.Format("Batch.SheetNotFound", sheetName, string.Join(", ", workbook.Worksheets.Select(candidate => candidate.Name))));
        }

        var range = sheet.RangeUsed();
        if (range is null)
        {
            return [];
        }

        var headerRow = range.FirstRow();
        var headers = headerRow.Cells().Select(cell => RecipeBuilder.NormalizeNameOrEmpty(cell.GetFormattedString())).ToArray();
        if (headers.All(string.IsNullOrEmpty))
        {
            throw new FormatException(Msg.Get("Csv.HeaderRequired"));
        }

        var duplicate = headers.Where(header => header.Length > 0).GroupBy(header => header).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException(Msg.Format("Csv.DuplicateColumn", duplicate.Key));
        }

        var rows = new List<BatchRow>();
        foreach (var row in range.Rows().Skip(1))
        {
            var cells = Enumerable.Range(1, headers.Length).Select(column => row.Cell(column).GetFormattedString().Trim()).ToArray();
            if (cells.All(string.IsNullOrEmpty))
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < headers.Length; column++)
            {
                if (headers[column].Length > 0)
                {
                    values[headers[column]] = cells[column];
                }
            }

            rows.Add(new BatchRow(rows.Count + 1, values));
        }

        return rows;
    }

    private static XLWorkbook Open(string path)
    {
        try
        {
            // FileShare.ReadWrite lets the workbook stay open in Excel while it is read here.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new XLWorkbook(stream);
        }
        catch (Exception exception) when (exception is not IOException and not UnauthorizedAccessException and not OutOfMemoryException)
        {
            throw new FormatException(Msg.Format("Batch.ExcelUnreadable", Path.GetFileName(path)), exception);
        }
    }
}

/// <summary>Picks the reader for a data file by its extension.</summary>
public static class BatchDataReaders
{
    public static IReadOnlyList<IBatchDataReader> All { get; } = [new CsvBatchDataReader(), new ExcelBatchDataReader()];

    public static IBatchDataReader For(string path) =>
        All.FirstOrDefault(reader => reader.CanRead(path))
        ?? throw new FormatException(Msg.Format("Batch.UnsupportedFile", Path.GetExtension(path)));
}
