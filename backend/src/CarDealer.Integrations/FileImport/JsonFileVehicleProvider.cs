using System.Text.Json;
using CarDealer.Application.VehicleSources;
using CarDealer.Domain.Enums;

namespace CarDealer.Integrations.FileImport;

/// <summary>
/// Serves an already-read import document as pages, so an import runs through the ordinary
/// sync pipeline instead of beside it.
/// </summary>
/// <remarks>
/// Constructed per import around the uploaded bytes rather than registered in the container:
/// there is no ambient "the file", and a provider that had to be reconfigured between requests
/// would be shared mutable state in a scoped service.
///
/// Nothing here is import-specific from the pipeline's point of view. Paging, deduplication,
/// upsert, per-item failure handling and job accounting are the same code that runs for an API
/// sync, which is the whole reason to implement this interface rather than write a second
/// importer.
/// </remarks>
public sealed class JsonFileVehicleProvider : IVehicleSourceSyncProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<RawVehicleRecord> _records;

    private JsonFileVehicleProvider(string sourceCode, IReadOnlyList<RawVehicleRecord> records)
    {
        SourceCode = sourceCode;
        _records = records;
    }

    public string SourceCode { get; }

    public VehicleSourceProviderType ProviderType => VehicleSourceProviderType.DealerJson;

    /// <summary>Total records the document offered, before any filtering.</summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// Reads a document and wraps it. Throws on JSON that cannot be parsed at all.
    /// </summary>
    /// <remarks>
    /// A whole-file failure and a bad record are different things and are treated differently:
    /// unreadable JSON stops the import before a job is even started, while one malformed
    /// vehicle inside readable JSON fails only itself and is reported as a SyncJobItem. Losing
    /// that distinction would mean one bad row could discard an otherwise good file.
    /// </remarks>
    public static JsonFileVehicleProvider Read(Stream content, string sourceCode)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The uploaded file is not valid JSON: {ex.Message} "
                + "See docs/spec/08-import-format.md for the expected shape.", ex);
        }

        using (parsed)
        {
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "The file must be an object with a 'vehicles' array, not a bare array. "
                    + "See docs/spec/08-import-format.md.");
            }

            var declaredCode = DocumentString(root, "sourceCode", "source_code");

            // A document naming a different source than the endpoint targets is a mistake worth
            // catching: importing one exporter's stock under another's attributes every car to
            // the wrong place, and attribution is a POC acceptance criterion.
            if (!string.IsNullOrWhiteSpace(declaredCode)
                && !string.Equals(declaredCode, sourceCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"This document declares sourceCode '{declaredCode}' but was posted to "
                    + $"'{sourceCode}'. Import it to the source it belongs to, or remove the "
                    + "sourceCode field to accept the endpoint's.");
            }

            if (!TryFind(root, ["vehicles", "records", "items"], out var vehicles)
                || vehicles.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The file has no 'vehicles' array. See docs/spec/08-import-format.md.");
            }

            // When the document says when it was captured, that is when every record in it was
            // last seen. Producers rarely stamp each row, and rejecting a whole file for a
            // field the document already answers would be pedantry - but "now" is never
            // substituted, because that would assert a confirmation nobody made.
            var capturedAt = DocumentDate(root, "capturedAtUtc", "captured_at_utc", "capturedAt")
                ?? DateTime.UtcNow;

            var records = vehicles.EnumerateArray()
                .Select((vehicle, index) => new RawVehicleRecord
                {
                    // Index as a fallback so a record missing its id still gets a distinct
                    // identifier to be reported under, rather than colliding with every other.
                    ExternalId = ExternalIdOf(vehicle) ?? $"row-{index + 1}",
                    SourceCode = sourceCode,
                    RetrievedAtUtc = capturedAt,

                    // Stored verbatim, so re-normalizing a record later needs no file.
                    RawPayload = vehicle.GetRawText(),
                })
                .ToList();

            return new JsonFileVehicleProvider(sourceCode, records);
        }
    }

    private static bool TryFind(JsonElement root, string[] names, out JsonElement value)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? DocumentString(JsonElement root, params string[] names)
        => TryFind(root, names, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? DocumentDate(JsonElement root, params string[] names)
        => DateTime.TryParse(
            DocumentString(root, names),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    /// <summary>The record's own id, under any of the names producers use for it.</summary>
    /// <remarks>
    /// Returns null for anything that is not an object, rather than throwing. A bare string in
    /// the vehicles array is one bad record, not a bad file, and failing here would reject the
    /// whole upload - losing every good row alongside the one malformed one. It is carried
    /// through instead, and fails on its own during normalization where it is counted.
    /// </remarks>
    private static string? ExternalIdOf(JsonElement vehicle)
    {
        if (vehicle.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "externalId", "stock_id", "stockId", "id", "listing_id", "listingId" })
        {
            if (vehicle.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return null;
    }

    public Task<VehicleSourcePage> FetchPageAsync(
        VehicleSourceQuery query, CancellationToken ct = default)
    {
        var pageSize = Math.Max(1, query.PageSize);
        var skip = (Math.Max(1, query.Page) - 1) * pageSize;

        var page = _records.Skip(skip).Take(pageSize).ToList();
        var totalPages = (int)Math.Ceiling(_records.Count / (double)pageSize);

        return Task.FromResult(new VehicleSourcePage
        {
            Records = page,
            TotalCount = _records.Count,
            Page = query.Page,
            TotalPages = totalPages,
            HasNextPage = skip + page.Count < _records.Count,

            // Nothing was fetched over a network, and reporting a made-up duration would
            // corrupt the POC's response-time measurement.
            Elapsed = TimeSpan.Zero,
        });
    }
}
