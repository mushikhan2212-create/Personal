using CarDealer.Infrastructure.Import;

namespace CarDealer.UnitTests;

/// <summary>
/// The CSV reader, against the shapes a real spreadsheet export actually produces.
/// </summary>
/// <remarks>
/// Each of these is a way that splitting on commas loses or corrupts a customer, which is the
/// reason the reader exists rather than a one-line String.Split.
/// </remarks>
public sealed class CsvReaderTests
{
    [Fact]
    public void Reads_plain_rows()
    {
        var rows = CsvReader.Parse("a,b,c\n1,2,3");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "2", "3"], rows[1]);
    }

    [Fact]
    public void A_quoted_field_may_contain_the_delimiter()
    {
        // The single most common way a naive parser shifts every later column by one.
        var rows = CsvReader.Parse("name,city\nImran,\"Karachi, Sindh\"");

        Assert.Equal(["Imran", "Karachi, Sindh"], rows[1]);
    }

    [Fact]
    public void A_quoted_field_may_contain_a_line_break()
    {
        // Alt+Enter inside a cell. Splitting on newlines turns one customer into two, the
        // second of them nonsense.
        var rows = CsvReader.Parse("name,notes\nImran,\"line one\nline two\"");

        Assert.Equal(2, rows.Count);
        Assert.Equal("line one\nline two", rows[1][1]);
    }

    [Fact]
    public void A_doubled_quote_is_one_literal_quote()
    {
        var rows = CsvReader.Parse("part\n\"5\"\" lift kit\"");

        Assert.Equal("5\" lift kit", rows[1][0]);
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_literal()
    {
        // What a spreadsheet writes when the cell was never quoted to begin with.
        var rows = CsvReader.Parse("part\n5\" lift kit");

        Assert.Equal("5\" lift kit", rows[1][0]);
    }

    [Theory]
    [InlineData("a,b\r\n1,2")]
    [InlineData("a,b\n1,2")]
    [InlineData("a,b\r1,2")]
    public void Every_line_ending_is_understood(string text)
    {
        var rows = CsvReader.Parse(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void The_last_line_needs_no_terminator()
    {
        // Dropping it would lose the final customer in every file that does not end in a
        // newline, which is most of them.
        var rows = CsvReader.Parse("a,b\n1,2");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void The_excel_byte_order_mark_is_not_part_of_the_first_header()
    {
        // Left in, "first name" arrives as an invisible-prefixed string that matches no column
        // - which reads as a missing column rather than as an encoding artefact.
        var rows = CsvReader.Parse("﻿first name,phone\nImran,123");

        Assert.Equal("first name", rows[0][0]);
    }

    [Fact]
    public void Blank_lines_are_not_rows()
    {
        // Trailing blank lines are everywhere in exports; each would otherwise be reported as
        // an invalid customer.
        var rows = CsvReader.Parse("a,b\n1,2\n\n\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void An_empty_field_survives_as_an_empty_string()
    {
        var rows = CsvReader.Parse("a,b,c\n1,,3");

        Assert.Equal(["1", "", "3"], rows[1]);
    }

    [Fact]
    public void A_semicolon_export_can_be_read()
    {
        var rows = CsvReader.Parse("a;b\n1;2", ';');

        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void Empty_input_is_no_rows_rather_than_one_empty_row()
    {
        Assert.Empty(CsvReader.Parse(""));
        Assert.Empty(CsvReader.Parse("   \n  "));
    }

    [Fact]
    public void An_unterminated_quote_yields_the_rest_of_the_file_rather_than_throwing()
    {
        // A file that is nearly right is the normal case. Reading it and letting the caller
        // reject the row beats refusing four hundred good rows over one stray quote.
        var rows = CsvReader.Parse("name\n\"unclosed, and on it goes");

        Assert.Equal(2, rows.Count);
        Assert.Equal("unclosed, and on it goes", rows[1][0]);
    }
}
