using System.Text;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Storage;

/// <summary>
/// Reads batch rows from CSV. The first line holds the variable names; the delimiter (comma, semicolon
/// or tab) is detected from it, so files saved by Excel in any locale work. Excel (.xlsx) can be added
/// later as another reader that produces the same <see cref="BatchRow"/> list.
/// </summary>
public static class CsvBatchReader
{
    public static IReadOnlyList<BatchRow> ReadFile(string path)
    {
        // Detects a UTF-8/UTF-16 byte order mark and otherwise assumes UTF-8.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<BatchRow> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var firstLineEnd = csv.IndexOfAny(['\r', '\n']);
        var delimiter = DetectDelimiter(firstLineEnd < 0 ? csv : csv[..firstLineEnd]);
        var records = ParseRecords(csv, delimiter);
        if (records.Count == 0)
        {
            return [];
        }

        var headers = records[0].Select(RecipeBuilder.NormalizeNameOrEmpty).ToArray();
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
        for (var index = 1; index < records.Count; index++)
        {
            var record = records[index];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < headers.Length; column++)
            {
                if (headers[column].Length > 0)
                {
                    values[headers[column]] = column < record.Count ? record[column].Trim() : "";
                }
            }

            rows.Add(new BatchRow(rows.Count + 1, values));
        }

        return rows;
    }

    private static char DetectDelimiter(string headerLine)
    {
        var candidates = new[] { ';', ',', '\t' };
        return candidates.OrderByDescending(candidate => CountOutsideQuotes(headerLine, candidate)).First();
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                count++;
            }
        }

        return count;
    }

    private static List<List<string>> ParseRecords(string csv, char delimiter)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            if (fieldStarted || record.Count > 0)
            {
                EndField();
                records.Add(record);
                record = [];
            }
        }

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (character == delimiter)
            {
                EndField();
                fieldStarted = true; // A trailing delimiter means one more (empty) field.
            }
            else if (character is '\r' or '\n')
            {
                EndRecord();
            }
            else
            {
                field.Append(character);
                fieldStarted = true;
            }
        }

        EndRecord();
        return records;
    }
}
