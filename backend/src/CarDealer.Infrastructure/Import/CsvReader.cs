using System.Text;

namespace CarDealer.Infrastructure.Import;

/// <summary>
/// Reads delimited text into rows of fields, following RFC 4180.
/// </summary>
/// <remarks>
/// Written rather than taken from a package, because the alternative is a dependency for
/// roughly eighty lines and this is the whole of what the product needs: read a spreadsheet
/// export. It is deliberately not a general CSV library - no type conversion, no attribute
/// mapping, no streaming of enormous files.
///
/// What it does handle is the part people get wrong by splitting on commas, and every one of
/// these is a real spreadsheet export rather than a hypothetical:
///
/// <list type="bullet">
/// <item>a quoted field containing the delimiter - <c>"Karachi, Sindh"</c></item>
/// <item>a quoted field containing a line break - an address typed with Alt+Enter</item>
/// <item>a doubled quote standing for one literal quote - <c>"5"" lift"</c></item>
/// <item>CRLF, LF, and a file whose last line has no terminator at all</item>
/// <item>the UTF-8 byte order mark Excel writes, which otherwise becomes part of the first
/// header and makes that column silently unmatchable</item>
/// </list>
///
/// Anything malformed is read as literally as possible rather than throwing. A file that is
/// nearly right is the normal case, and a parser that refuses the whole thing over one stray
/// quote is less useful than one that hands back a row the caller can report as invalid.
/// </remarks>
public static class CsvReader
{
    /// <summary>
    /// Splits CSV text into rows. A row is a list of fields, in file order.
    /// </summary>
    /// <param name="text">The whole file. Empty or whitespace-only input yields no rows.</param>
    /// <param name="delimiter">
    /// The field separator. Comma by default; a semicolon export from a European locale is the
    /// reason this is a parameter.
    /// </param>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter = ',')
    {
        var rows = new List<IReadOnlyList<string>>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        // Excel writes a BOM on UTF-8 CSV. Left in place it becomes part of the first header
        // cell, so "first name" arrives as "﻿first name" and never matches anything - a
        // failure that looks like a missing column rather than an encoding artefact.
        if (text[0] == '﻿')
        {
            text = text[1..];
        }

        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                // A doubled quote inside a quoted field is one literal quote. A single one ends
                // the field.
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    // Only opens a quoted section at the start of a field. Mid-field it is a
                    // literal, which is what a spreadsheet writes for 5" lift when it has not
                    // quoted the cell.
                    if (field.Length == 0) inQuotes = true;
                    else field.Append(c);
                    break;

                case '\r':
                    // Swallowed; the '\n' that follows ends the row. A lone CR - classic Mac
                    // line endings - ends it here instead.
                    if (i + 1 >= text.Length || text[i + 1] != '\n')
                    {
                        EndRow(rows, row, field);
                    }

                    break;

                case '\n':
                    EndRow(rows, row, field);
                    break;

                default:
                    if (c == delimiter)
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                    }

                    break;
            }
        }

        // The last line of a file often has no terminator, and dropping it would silently lose
        // a customer.
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static void EndRow(
        List<IReadOnlyList<string>> rows, List<string> row, StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();

        // A blank line is not a row of one empty field - spreadsheet exports are full of
        // trailing ones, and each would otherwise be reported as an invalid customer.
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
        }

        row.Clear();
    }
}
