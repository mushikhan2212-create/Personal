using System.Text;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Infrastructure.Import;

/// <summary>
/// Turns a spreadsheet export into customers.
/// </summary>
/// <remarks>
/// The shape of this deliberately mirrors the vehicle import: dry run first, per-row failures
/// rather than an all-or-nothing rejection, and counts that say what happened. A dealer's
/// contact list arrives as a messy export with a header nobody agreed on, and an importer that
/// answers "400 Bad Request" is one the dealer works around by not using it.
///
/// The one substantive policy decision is what to do about a customer who is already here.
/// This <b>skips</b> them and says so, rather than overwriting. An import is usually a
/// re-import - the same sheet, one month on - and overwriting would silently discard the notes,
/// status and assignment a salesperson has edited since. Skipping loses nothing: the row is
/// reported, and the operator can open the customer and change it by hand. Overwriting cannot
/// be undone.
///
/// Every read and write goes through the tenant-filtered <see cref="CarDealerDbContext"/>, so
/// "already here" means already here <i>for this tenant</i>. There is no IgnoreQueryFilters in
/// this file and there must not be: a duplicate check that saw other tenants would leak the
/// existence of their customers through the skip count alone.
/// </remarks>
public sealed class CustomerCsvImportService
{
    private readonly CarDealerDbContext _db;
    private readonly ITenantContext _tenant;

    public CustomerCsvImportService(CarDealerDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>
    /// The most rows one request will accept.
    /// </summary>
    /// <remarks>
    /// Not a judgement about how many customers a dealer may have - it is that this runs
    /// synchronously inside one HTTP request, and a file large enough to time out gives no
    /// result at all rather than a partial one. Past this the answer is a background job, which
    /// is a different feature.
    /// </remarks>
    public const int MaxRows = 5_000;

    /// <summary>How many problem rows are described individually before the rest are counted.</summary>
    private const int MaxReportedProblems = 200;

    /// <summary>
    /// Column headings this understands, in the forms spreadsheets actually use.
    /// </summary>
    /// <remarks>
    /// Matched after lower-casing and removing everything that is not a letter or digit, so
    /// "First Name", "first_name" and "FIRSTNAME" are one key. Being generous here is most of
    /// what makes the feature usable: the alternative is telling a dealer to rename their
    /// columns before the software will look at their file.
    /// </remarks>
    private static readonly Dictionary<string, string> HeaderAliases = new()
    {
        ["firstname"] = "firstName",
        ["first"] = "firstName",
        ["givenname"] = "firstName",
        ["forename"] = "firstName",
        ["lastname"] = "lastName",
        ["last"] = "lastName",
        ["surname"] = "lastName",
        ["familyname"] = "lastName",
        ["name"] = "fullName",
        ["fullname"] = "fullName",
        ["customername"] = "fullName",
        ["phone"] = "phone",
        ["phonenumber"] = "phone",
        ["mobile"] = "phone",
        ["mobilenumber"] = "phone",
        ["cell"] = "phone",
        ["whatsapp"] = "phone",
        ["contact"] = "phone",
        ["contactnumber"] = "phone",
        ["email"] = "email",
        ["emailaddress"] = "email",
        ["mail"] = "email",
        ["city"] = "city",
        ["town"] = "city",
        ["country"] = "countryCode",
        ["countrycode"] = "countryCode",
        ["countryiso"] = "countryCode",
        ["status"] = "status",
        ["customerstatus"] = "status",
        ["leadsource"] = "leadSource",
        ["source"] = "leadSource",
        ["camefrom"] = "leadSource",
        ["language"] = "preferredLanguage",
        ["preferredlanguage"] = "preferredLanguage",
        ["notes"] = "notes",
        ["note"] = "notes",
        ["comment"] = "notes",
        ["comments"] = "notes",
        ["remarks"] = "notes",
    };

    public async Task<CustomerImportResult> ImportAsync(
        string csv, bool dryRun, CancellationToken ct = default)
    {
        var rows = CsvReader.Parse(csv, DetectDelimiter(csv));

        if (rows.Count == 0)
        {
            return CustomerImportResult.Rejected("The file is empty.");
        }

        var columns = MapHeaders(rows[0]);

        if (columns.Count == 0)
        {
            return CustomerImportResult.Rejected(
                "None of the columns in the first row were recognised. The first row must be a "
                + "header. Recognised headings include first name, last name, phone, email, "
                + "city, country, status, source and notes.");
        }

        var dataRows = rows.Skip(1).ToList();

        if (dataRows.Count > MaxRows)
        {
            return CustomerImportResult.Rejected(
                $"The file has {dataRows.Count:N0} rows and the limit for one import is "
                + $"{MaxRows:N0}. Split it and import the parts.");
        }

        // Every existing phone and email for this tenant, so a duplicate is found without a
        // query per row. Two columns over a dealer's book is a small read; N queries is not.
        var existing = await _db.Customers
            .AsNoTracking()
            .Select(c => new { c.Phone, c.Email })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var knownPhones = existing
            .Select(c => NormalizePhone(c.Phone))
            .Where(p => p is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var knownEmails = existing
            .Select(c => NormalizeEmail(c.Email))
            .Where(e => e is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var result = new CustomerImportResult { DryRun = dryRun, TotalRows = dataRows.Count };
        var toAdd = new List<Customer>();

        for (var i = 0; i < dataRows.Count; i++)
        {
            // 1-based and counted in customers, not file lines: a quoted line break inside a
            // notes cell makes the two differ, and the operator is looking at a spreadsheet
            // where row 1 is the first customer.
            var rowNumber = i + 1;
            var parsed = ParseRow(dataRows[i], columns);

            if (parsed.Error is { } error)
            {
                result.Invalid++;
                result.AddProblem(rowNumber, parsed.Label, error, MaxReportedProblems);
                continue;
            }

            var customer = parsed.Customer!;
            var phone = NormalizePhone(customer.Phone);
            var email = NormalizeEmail(customer.Email);

            // Against the tenant's book, and against rows already accepted from this file - a
            // sheet with the same person on it twice is as common as one that overlaps the
            // database.
            if (phone is not null && !knownPhones.Add(phone))
            {
                result.Duplicates++;
                result.AddProblem(
                    rowNumber, parsed.Label,
                    $"Skipped: a customer with phone {customer.Phone} is already here.",
                    MaxReportedProblems);
                continue;
            }

            if (email is not null && !knownEmails.Add(email))
            {
                result.Duplicates++;
                result.AddProblem(
                    rowNumber, parsed.Label,
                    $"Skipped: a customer with email {customer.Email} is already here.",
                    MaxReportedProblems);
                continue;
            }

            customer.TenantId = _tenant.TenantId;
            customer.PublicId = Guid.NewGuid();
            toAdd.Add(customer);
            result.Created++;

            if (result.Sample.Count < 5)
            {
                result.Sample.Add(parsed.Label);
            }
        }

        if (!dryRun && toAdd.Count > 0)
        {
            _db.Customers.AddRange(toAdd);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Semicolons if the header row has more of them than commas.
    /// </summary>
    /// <remarks>
    /// Excel in a European locale writes semicolon-delimited CSV, and such a file read as
    /// comma-delimited parses as one enormous column - which surfaces as "no columns
    /// recognised", blaming the headings for a delimiter problem.
    /// </remarks>
    private static char DetectDelimiter(string csv)
    {
        var firstLine = csv.Split('\n', 2)[0];

        return firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
    }

    private static Dictionary<int, string> MapHeaders(IReadOnlyList<string> header)
    {
        var columns = new Dictionary<int, string>();

        for (var i = 0; i < header.Count; i++)
        {
            var key = new string([.. header[i].ToLowerInvariant().Where(char.IsLetterOrDigit)]);

            // First wins. A sheet with both "Phone" and "Mobile" would otherwise have the
            // second silently overwrite the first, and the operator would never know which
            // column was read.
            if (HeaderAliases.TryGetValue(key, out var mapped) && !columns.ContainsValue(mapped))
            {
                columns[i] = mapped;
            }
        }

        return columns;
    }

    private static ParsedRow ParseRow(IReadOnlyList<string> row, Dictionary<int, string> columns)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (index, name) in columns)
        {
            if (index < row.Count && !string.IsNullOrWhiteSpace(row[index]))
            {
                values[name] = row[index].Trim();
            }
        }

        var firstName = Get(values, "firstName");
        var lastName = Get(values, "lastName");

        // A single "Name" column is how most contact exports look. Split on the first space:
        // wrong for some names, and better than putting the whole thing in a field labelled
        // "first name" - the parts stay recoverable either way.
        if (firstName is null && lastName is null && Get(values, "fullName") is { } full)
        {
            var space = full.IndexOf(' ');
            firstName = space < 0 ? full : full[..space];
            lastName = space < 0 ? null : full[(space + 1)..].Trim();
        }

        var phone = Get(values, "phone");
        var email = Get(values, "email");
        var label = Label(firstName, lastName, phone, email);

        if (firstName is null && lastName is null && phone is null && email is null)
        {
            return ParsedRow.Invalid(
                label, "No name and no way to contact them — every recognised column is empty.");
        }

        if (email is not null && !LooksLikeEmail(email))
        {
            // Rejected rather than dropped. A malformed address in the email column is a
            // problem in the sheet, and importing the row without it would quietly lose the
            // only contact detail some of these rows have.
            return ParsedRow.Invalid(label, $"'{email}' is not an email address.");
        }

        var countryCode = Get(values, "countryCode");

        if (countryCode is not null && countryCode.Length != 2)
        {
            return ParsedRow.Invalid(
                label,
                $"Country '{countryCode}' must be a two-letter code such as PK, KE or JP.");
        }

        if (Get(values, "status") is { } statusText
            && !Enum.TryParse<CustomerStatus>(statusText, ignoreCase: true, out _))
        {
            return ParsedRow.Invalid(
                label,
                $"Status '{statusText}' is not one of {string.Join(", ", Enum.GetNames<CustomerStatus>())}.");
        }

        if (Get(values, "leadSource") is { } sourceText
            && !Enum.TryParse<LeadSource>(sourceText, ignoreCase: true, out _))
        {
            return ParsedRow.Invalid(
                label,
                $"Source '{sourceText}' is not one of {string.Join(", ", Enum.GetNames<LeadSource>())}.");
        }

        var customer = new Customer
        {
            FirstName = Truncate(firstName, 128),
            LastName = Truncate(lastName, 128),
            Phone = Truncate(phone, 32),
            Email = Truncate(email, 256),
            City = Truncate(Get(values, "city"), 128),
            CountryCode = countryCode?.ToUpperInvariant(),
            PreferredLanguage = Truncate(Get(values, "preferredLanguage"), 16),
            Notes = Truncate(Get(values, "notes"), 4000),
            Status = Get(values, "status") is { } s
                ? Enum.Parse<CustomerStatus>(s, ignoreCase: true)
                : CustomerStatus.Lead,
            LeadSource = Get(values, "leadSource") is { } l
                ? Enum.Parse<LeadSource>(l, ignoreCase: true)
                : LeadSource.Unknown,
        };

        return ParsedRow.Valid(customer, label);
    }

    private static string? Get(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Trimmed to the column's width rather than allowed to fail the whole insert.
    /// </summary>
    /// <remarks>
    /// Only the free-text fields reach this. A 200-character note truncated to 4,000 loses
    /// nothing anyone will notice; the same row rejected loses a customer.
    /// </remarks>
    private static string? Truncate(string? value, int max)
        => value is null ? null : (value.Length <= max ? value : value[..max]);

    /// <summary>Enough of a check to catch a name or a phone number typed into the email column.</summary>
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');

        return at > 0
            && at < value.Length - 1
            && value.IndexOf('@', at + 1) < 0
            && value.IndexOf('.', at) > at + 1
            && !value.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// Digits only, keeping a leading plus.
    /// </summary>
    /// <remarks>
    /// So that "+92 300 1234567", "+92-300-1234567" and "+923001234567" are recognised as one
    /// number. Deliberately not a full E.164 parse: "03001234567" and "+923001234567" are the
    /// same person and this will not know it, because deciding that needs a country and
    /// guessing one wrongly merges two different customers. A missed duplicate is a row the
    /// operator can delete; a wrong merge is data loss.
    /// </remarks>
    private static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new StringBuilder();

        if (value.TrimStart().StartsWith('+')) digits.Append('+');

        foreach (var c in value.Where(char.IsDigit)) digits.Append(c);

        return digits.Length <= (digits[0] == '+' ? 1 : 0) ? null : digits.ToString();
    }

    private static string? NormalizeEmail(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>How a row is named in the report, so a problem points at a person.</summary>
    private static string Label(string? first, string? last, string? phone, string? email)
    {
        var name = string.Join(' ', new[] { first, last }.Where(p => p is not null));

        return name.Length > 0 ? name : (phone ?? email ?? "(empty row)");
    }

    private sealed record ParsedRow(Customer? Customer, string Label, string? Error)
    {
        public static ParsedRow Valid(Customer customer, string label) => new(customer, label, null);

        public static ParsedRow Invalid(string label, string error) => new(null, label, error);
    }
}

/// <summary>What an import did, or - on a dry run - what it would have done.</summary>
public sealed class CustomerImportResult
{
    public bool DryRun { get; init; }

    public int TotalRows { get; init; }

    /// <summary>Rows that would become new customers.</summary>
    public int Created { get; set; }

    /// <summary>Rows skipped because that person is already here.</summary>
    public int Duplicates { get; set; }

    /// <summary>Rows that could not be read as a customer.</summary>
    public int Invalid { get; set; }

    /// <summary>Set only when the file was rejected outright, with the reason.</summary>
    public string? RejectedReason { get; init; }

    /// <summary>A few of the names that would be added, so a dry run is checkable at a glance.</summary>
    public List<string> Sample { get; } = [];

    public List<ImportProblem> Problems { get; } = [];

    /// <summary>Problems beyond the reporting cap, counted rather than listed.</summary>
    public int UnreportedProblems { get; private set; }

    public static CustomerImportResult Rejected(string reason)
        => new() { RejectedReason = reason };

    public void AddProblem(int row, string label, string message, int cap)
    {
        if (Problems.Count < cap)
        {
            Problems.Add(new ImportProblem(row, label, message));
        }
        else
        {
            UnreportedProblems++;
        }
    }
}

/// <param name="Row">1-based position among the file's customers, not its text lines.</param>
public sealed record ImportProblem(int Row, string Label, string Message);
