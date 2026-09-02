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
        VehicleImportDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<VehicleImportDocument>(content, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The uploaded file is not valid JSON: {ex.Message} "
                + "See docs/spec/08-import-format.md for the expected shape.", ex);
        }

        if (document is null)
        {
            throw new InvalidOperationException("The uploaded file was empty.");
        }

        // A document naming a different source than the endpoint targets is a mistake worth
        // catching: importing one exporter's stock under another's attributes every car to the
        // wrong place, and attribution is a POC acceptance criterion.
        if (!string.IsNullOrWhiteSpace(document.SourceCode)
            && !string.Equals(document.SourceCode, sourceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This document declares sourceCode '{document.SourceCode}' but was posted to "
                + $"'{sourceCode}'. Import it to the source it belongs to, or remove the "
                + "sourceCode field to accept the endpoint's.");
        }

        var records = document.Vehicles
            .Select((vehicle, index) => new RawVehicleRecord
            {
                // Index as a fallback so a record missing its id still gets a distinct
                // identifier to be reported under, rather than colliding with every other one.
                ExternalId = string.IsNullOrWhiteSpace(vehicle.ExternalId)
                    ? $"row-{index + 1}"
                    : vehicle.ExternalId,
                SourceCode = sourceCode,
                RetrievedAtUtc = document.CapturedAtUtc ?? DateTime.UtcNow,

                // Re-serialised per record so each row carries its own payload, which is what
                // makes re-normalizing a stored record possible later without the file.
                RawPayload = JsonSerializer.Serialize(vehicle, Json),
            })
            .ToList();

        return new JsonFileVehicleProvider(sourceCode, records);
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
