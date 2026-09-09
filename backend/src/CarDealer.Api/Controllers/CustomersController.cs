using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Search;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Import;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>
/// The tenant's customers, what each is looking for, and which stock fits.
/// </summary>
/// <remarks>
/// Every query here runs through the DbContext's tenant filter and none of them calls
/// <c>IgnoreQueryFilters</c>. That is a rule rather than an accident: several administrative
/// paths in this codebase legitimately bypass the filters, and a single bypass on a customer
/// query would read every tenant's book. If a future screen seems to need one, the screen is
/// wrong.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/customers")]
public sealed class CustomersController : ControllerBase
{
    /// <summary>
    /// The largest CSV one import will accept.
    /// </summary>
    /// <remarks>
    /// Well under the vehicle import's 64 MB, because the row limit binds first: 5,000
    /// customers of name, phone and email is a few hundred kilobytes, so a file past this is
    /// not a contact list.
    /// </remarks>
    private const long MaxImportBytes = 8L * 1024 * 1024;

    private readonly CarDealerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISearchProvider _search;

    public CustomersController(
        CarDealerDbContext db, ITenantContext tenant, ISearchProvider search)
    {
        _db = db;
        _tenant = tenant;
        _search = search;
    }

    // -------------------------------------------------------------------------------------
    // Customers
    // -------------------------------------------------------------------------------------

    /// <summary>Lists this tenant's customers, optionally narrowed by name, phone or email.</summary>
    [HttpGet]
    [HasPermission(Permissions.CustomersRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] CustomerStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var customers = _db.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            // One term across the three fields somebody actually searches a customer by. A
            // phone number is what a salesperson has in front of them when the phone rings.
            var term = q.Trim();
            customers = customers.Where(c =>
                (c.FirstName != null && c.FirstName.Contains(term))
                || (c.LastName != null && c.LastName.Contains(term))
                || (c.Phone != null && c.Phone.Contains(term))
                || (c.Email != null && c.Email.Contains(term)));
        }

        if (status is { } wanted)
        {
            customers = customers.Where(c => c.Status == wanted);
        }

        var total = await customers.CountAsync(ct).ConfigureAwait(false);
        var size = Math.Clamp(pageSize, 1, 100);

        var items = await customers
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Skip((Math.Max(1, page) - 1) * size)
            .Take(size)
            .Select(c => new
            {
                c.PublicId,
                c.FirstName,
                c.LastName,
                c.Phone,
                c.Email,
                c.CountryCode,
                c.City,
                Status = c.Status.ToString(),
                LeadSource = c.LeadSource.ToString(),
                OpenRequirements = c.Requirements.Count(r => r.Status == RequirementStatus.Open),
                c.UpdatedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new { items, totalCount = total, page = Math.Max(1, page), pageSize = size });
    }

    /// <summary>One customer, with every requirement they are shopping.</summary>
    [HttpGet("{publicId:guid}")]
    [HasPermission(Permissions.CustomersRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var customer = await _db.Customers
            .AsNoTracking()
            .Where(c => c.PublicId == publicId)
            .Select(c => new
            {
                c.PublicId,
                c.FirstName,
                c.LastName,
                c.Phone,
                c.Email,
                c.CountryCode,
                c.City,
                c.PreferredLanguage,
                Status = c.Status.ToString(),
                LeadSource = c.LeadSource.ToString(),
                c.Notes,
                c.AssignedUserId,
                c.CreatedAtUtc,
                c.UpdatedAtUtc,
                Requirements = c.Requirements
                    .OrderByDescending(r => r.UpdatedAtUtc)
                    .Select(r => Describe(r))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return customer is null ? CustomerNotFound(publicId) : Ok(customer);
    }

    [HttpPost]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CustomerRequest request, CancellationToken ct)
    {
        if (Blank(request.FirstName) is null && Blank(request.LastName) is null
            && Blank(request.Phone) is null && Blank(request.Email) is null)
        {
            // A record with no name and no way to reach them is not a customer, and once saved
            // it is indistinguishable from every other empty row.
            return BadRequest(new ProblemDetails
            {
                Title = "A customer needs a name or a way to contact them.",
                Detail = "Supply at least one of firstName, lastName, phone or email.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var customer = new Customer
        {
            TenantId = _tenant.TenantId,
            PublicId = Guid.NewGuid(),
        };

        Apply(customer, request);

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Created($"/api/v1/customers/{customer.PublicId}", new { customer.PublicId });
    }

    /// <summary>
    /// Creates customers in bulk from a spreadsheet export.
    /// </summary>
    /// <remarks>
    /// Use <c>dryRun=true</c> first. It reports exactly what a real import would do - how many
    /// customers would be created, which rows are already here, and what is wrong with the rest
    /// - and writes nothing. On a file of four hundred contacts that is the difference between
    /// finding a mis-mapped column now and finding it afterwards.
    ///
    /// A row naming someone this tenant already has is <b>skipped, not overwritten</b>. An
    /// import is usually a re-import of the same sheet, and overwriting would discard the notes
    /// and status a salesperson has edited since. Skipped rows are listed by name, so
    /// reconciling them by hand is possible; an overwrite would not be.
    ///
    /// This is the fastest way to put a large amount of personal data into the platform, which
    /// makes it the endpoint that matters most for
    /// <see href="../../../docs/spec/05-open-items.md">O3</see>. It holds nothing the manual
    /// form does not, and the file itself is parsed and discarded rather than stored - unlike a
    /// vehicle import, which keeps the payload for re-runs. A spreadsheet of customers is not
    /// something to leave lying in blob storage while the retention policy is still unwritten.
    /// </remarks>
    [HttpPost("import")]
    [HasPermission(Permissions.CustomersManage)]
    [RequestSizeLimit(MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Import(
        IFormFile file,
        [FromServices] CustomerCsvImportService importer,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "No file was uploaded.",
                Detail = "Post the CSV as multipart/form-data under the field name 'file'.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        string csv;

        // detectEncodingFromByteOrderMarks handles the UTF-16 a spreadsheet occasionally
        // writes; the reader strips the UTF-8 mark itself, which this does not remove.
        using (var reader = new StreamReader(file.OpenReadStream(), detectEncodingFromByteOrderMarks: true))
        {
            csv = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var result = await importer.ImportAsync(csv, dryRun, ct).ConfigureAwait(false);

        if (result.RejectedReason is { } reason)
        {
            // The file could not be read as a customer list at all - as distinct from a file
            // whose individual rows have problems, which is a 200 with those rows listed.
            return BadRequest(new ProblemDetails
            {
                Title = "That file could not be imported.",
                Detail = reason,
                Status = StatusCodes.Status400BadRequest,
            });
        }

        return Ok(new
        {
            dryRun = result.DryRun,
            totalRows = result.TotalRows,
            created = result.Created,
            duplicates = result.Duplicates,
            invalid = result.Invalid,
            sample = result.Sample,
            problems = result.Problems.Select(p => new { p.Row, p.Label, p.Message }),
            unreportedProblems = result.UnreportedProblems,
        });
    }

    [HttpPut("{publicId:guid}")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid publicId, [FromBody] CustomerRequest request, CancellationToken ct)
    {
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return CustomerNotFound(publicId);
        }

        Apply(customer, request);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { customer.PublicId });
    }

    /// <summary>
    /// Deletes a customer and everything attached to them.
    /// </summary>
    /// <remarks>
    /// A real delete, not a flag. This is the platform's first personal data, and open item O3
    /// leaves retention and erasure policy unanswered - so the code makes the eventual answer
    /// possible rather than pre-empting it. A soft delete would make "we deleted it" untrue on
    /// the day someone asks.
    /// </remarks>
    [HttpDelete("{publicId:guid}")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        var customer = await _db.Customers
            .Include(c => c.Requirements)
            .FirstOrDefaultAsync(c => c.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return CustomerNotFound(publicId);
        }

        var requirements = customer.Requirements.Count;

        _db.Customers.Remove(customer);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { deleted = publicId, requirementsDeleted = requirements });
    }

    // -------------------------------------------------------------------------------------
    // Requirements
    // -------------------------------------------------------------------------------------

    [HttpPost("{publicId:guid}/requirements")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddRequirement(
        Guid publicId, [FromBody] RequirementRequest request, CancellationToken ct)
    {
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return CustomerNotFound(publicId);
        }

        var requirement = new CustomerRequirement
        {
            TenantId = _tenant.TenantId,
            CustomerId = customer.Id,
        };

        Apply(requirement, request);

        _db.CustomerRequirements.Add(requirement);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Created(
            $"/api/v1/customers/{publicId}/requirements/{requirement.Id}",
            new { requirement.Id });
    }

    [HttpPut("{publicId:guid}/requirements/{requirementId:long}")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRequirement(
        Guid publicId, long requirementId, [FromBody] RequirementRequest request,
        CancellationToken ct)
    {
        var requirement = await FindRequirementAsync(publicId, requirementId, ct)
            .ConfigureAwait(false);

        if (requirement is null)
        {
            return RequirementNotFound(requirementId);
        }

        Apply(requirement, request);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { requirement.Id });
    }

    [HttpDelete("{publicId:guid}/requirements/{requirementId:long}")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRequirement(
        Guid publicId, long requirementId, CancellationToken ct)
    {
        var requirement = await FindRequirementAsync(publicId, requirementId, ct)
            .ConfigureAwait(false);

        if (requirement is null)
        {
            return RequirementNotFound(requirementId);
        }

        _db.CustomerRequirements.Remove(requirement);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { deleted = requirementId });
    }

    /// <summary>
    /// The stock that fits this requirement.
    /// </summary>
    /// <remarks>
    /// A requirement is a saved search, so this builds a <see cref="VehicleSearchQuery"/> and
    /// runs it through the same provider the vehicle screen uses. Matching therefore inherits
    /// tenant scoping, the caller's muted sources and the grouping-by-car behaviour, and the
    /// response is the same shape the search endpoint returns so the frontend renders it with
    /// the table it already has.
    ///
    /// Nothing is persisted. Schema section VehicleRecommendations exists and stays empty until
    /// Phase 2: it carries a score, reasons and an AI request id, and writing rows into it now
    /// would be filling in Phase 2's table with a filter's output and calling it a
    /// recommendation.
    /// </remarks>
    [HttpGet("{publicId:guid}/requirements/{requirementId:long}/matches")]
    [HasPermission(Permissions.CustomersRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Matches(
        Guid publicId,
        long requirementId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var requirement = await FindRequirementAsync(publicId, requirementId, ct)
            .ConfigureAwait(false);

        if (requirement is null)
        {
            return RequirementNotFound(requirementId);
        }

        var result = await _search
            .SearchAsync(ToQuery(requirement, page, pageSize), ct)
            .ConfigureAwait(false);

        return Ok(new
        {
            requirementId,
            matchedOn = Explain(requirement),
            result.TotalCount,
            result.Page,
            result.PageSize,

            // Same derivation VehiclesController uses, so the frontend's existing table and
            // pager consume this response without a second shape to learn.
            TotalPages = result.PageSize == 0
                ? 0
                : (int)Math.Ceiling(result.TotalCount / (double)result.PageSize),
            ElapsedMilliseconds = (int)result.Elapsed.TotalMilliseconds,

            // Mapped through the same projection the vehicle search uses, not returned raw.
            // The raw hit names its year ModelYear; the API contract calls it year, and a
            // second spelling would mean the frontend needs a second table to render matches.
            Items = result.Hits.Select(VehicleSummary.From).ToList(),
        });
    }

    /// <summary>
    /// Turns a requirement into a catalog query.
    /// </summary>
    /// <remarks>
    /// The budget maps onto the base-currency range, which is the only price comparable across
    /// currencies (decision D6). A listing whose currency has no pinned rate is excluded from a
    /// budgeted requirement rather than converted at a guess - so a requirement with a budget
    /// can legitimately return fewer cars than one without, and that is the honest answer
    /// rather than a fault.
    /// </remarks>
    private static VehicleSearchQuery ToQuery(CustomerRequirement r, int page, int pageSize)
        => new()
        {
            Make = r.Make,
            Model = r.Model,
            BodyType = r.BodyType,

            // Variant is not a structured filter on the catalog side, so it goes through free
            // text, where it matches make, model or variant. Better a slightly wide match than
            // silently dropping what the customer asked for.
            Text = r.Variant,

            MinYear = r.MinYear,
            MaxYear = r.MaxYear,
            MinMileage = r.MinMileage,
            MaxMileage = r.MaxMileage,
            Transmission = r.Transmission,
            FuelType = r.FuelType,
            MinPriceBase = r.MinPrice,
            MaxPriceBase = r.MaxPrice,
            Page = page,
            PageSize = pageSize,
            Sort = VehicleSearchSort.PriceAscending,
        };

    /// <summary>
    /// Which of the requirement's fields actually narrowed the search.
    /// </summary>
    /// <remarks>
    /// Returned so a salesperson looking at a disappointing result can see what was applied,
    /// rather than assuming the catalog is empty. It is also the first thing to check when a
    /// match looks wrong.
    /// </remarks>
    private static string[] Explain(CustomerRequirement r)
    {
        var applied = new List<string>();

        if (!string.IsNullOrWhiteSpace(r.Make)) applied.Add($"make {r.Make}");
        if (!string.IsNullOrWhiteSpace(r.Model)) applied.Add($"model {r.Model}");
        if (!string.IsNullOrWhiteSpace(r.Variant)) applied.Add($"variant {r.Variant}");
        if (!string.IsNullOrWhiteSpace(r.BodyType)) applied.Add($"body {r.BodyType}");
        if (r.MinYear is { } minY) applied.Add($"year from {minY}");
        if (r.MaxYear is { } maxY) applied.Add($"year to {maxY}");
        if (r.MinMileage is { } minM) applied.Add($"mileage from {minM:N0}");
        if (r.MaxMileage is { } maxM) applied.Add($"mileage to {maxM:N0}");
        if (r.Transmission is { } t) applied.Add($"transmission {t}");
        if (r.FuelType is { } f) applied.Add($"fuel {f}");
        if (r.MinPrice is { } minP) applied.Add($"price from {minP:N0}");
        if (r.MaxPrice is { } maxP) applied.Add($"price to {maxP:N0}");

        // Recorded on the requirement but never applied - see the remarks on the entity, and
        // open item O10. Said out loud so nobody assumes it filtered.
        if (!string.IsNullOrWhiteSpace(r.DestinationCountryCode))
        {
            applied.Add($"destination {r.DestinationCountryCode} (recorded, not filtered)");
        }

        return [.. applied];
    }

    // -------------------------------------------------------------------------------------

    private Task<CustomerRequirement?> FindRequirementAsync(
        Guid publicId, long requirementId, CancellationToken ct)
        => _db.CustomerRequirements
            .Where(r => r.Id == requirementId && r.Customer.PublicId == publicId)
            .FirstOrDefaultAsync(ct);

    private static void Apply(Customer customer, CustomerRequest request)
    {
        customer.FirstName = Blank(request.FirstName);
        customer.LastName = Blank(request.LastName);
        customer.Phone = Blank(request.Phone);
        customer.Email = Blank(request.Email);
        customer.CountryCode = Blank(request.CountryCode)?.ToUpperInvariant();
        customer.City = Blank(request.City);
        customer.PreferredLanguage = Blank(request.PreferredLanguage);
        customer.Notes = Blank(request.Notes);
        customer.AssignedUserId = request.AssignedUserId;

        if (request.Status is { } status) customer.Status = status;
        if (request.LeadSource is { } source) customer.LeadSource = source;
    }

    private static void Apply(CustomerRequirement requirement, RequirementRequest request)
    {
        requirement.Name = Blank(request.Name);
        requirement.Make = Blank(request.Make);
        requirement.Model = Blank(request.Model);
        requirement.Variant = Blank(request.Variant);
        requirement.BodyType = Blank(request.BodyType);
        requirement.ExteriorColor = Blank(request.ExteriorColor);
        requirement.MinYear = request.MinYear;
        requirement.MaxYear = request.MaxYear;
        requirement.MinMileage = request.MinMileage;
        requirement.MaxMileage = request.MaxMileage;
        requirement.Transmission = request.Transmission;
        requirement.FuelType = request.FuelType;
        requirement.MinPrice = request.MinPrice;
        requirement.MaxPrice = request.MaxPrice;
        requirement.CurrencyCode = Blank(request.CurrencyCode)?.ToUpperInvariant();
        requirement.DestinationCountryCode = Blank(request.DestinationCountryCode)?.ToUpperInvariant();
        requirement.DestinationCity = Blank(request.DestinationCity);
        requirement.RawRequirementText = Blank(request.RawRequirementText);

        if (request.Status is { } status) requirement.Status = status;
    }

    private static object Describe(CustomerRequirement r) => new
    {
        r.Id,
        r.Name,
        r.Make,
        r.Model,
        r.Variant,
        r.BodyType,
        r.ExteriorColor,
        r.MinYear,
        r.MaxYear,
        r.MinMileage,
        r.MaxMileage,
        Transmission = r.Transmission != null ? r.Transmission.ToString() : null,
        FuelType = r.FuelType != null ? r.FuelType.ToString() : null,
        r.MinPrice,
        r.MaxPrice,
        r.CurrencyCode,
        r.DestinationCountryCode,
        r.DestinationCity,
        r.RawRequirementText,
        Status = r.Status.ToString(),
        r.UpdatedAtUtc,
    };

    /// <summary>Empty and whitespace both mean "not supplied", and are stored as null.</summary>
    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private NotFoundObjectResult CustomerNotFound(Guid publicId) => NotFound(new ProblemDetails
    {
        Title = $"No customer with id '{publicId}'.",
        Status = StatusCodes.Status404NotFound,
    });

    private NotFoundObjectResult RequirementNotFound(long id) => NotFound(new ProblemDetails
    {
        Title = $"No requirement with id {id} for this customer.",
        Status = StatusCodes.Status404NotFound,
    });
}

/// <summary>Fields a customer can be created or updated with (master prompt section 9).</summary>
public sealed record CustomerRequest
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public string? PreferredLanguage { get; init; }
    public CustomerStatus? Status { get; init; }
    public LeadSource? LeadSource { get; init; }
    public long? AssignedUserId { get; init; }
    public string? Notes { get; init; }
}

public sealed record RequirementRequest
{
    public string? Name { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Variant { get; init; }
    public string? BodyType { get; init; }
    public string? ExteriorColor { get; init; }
    public int? MinYear { get; init; }
    public int? MaxYear { get; init; }
    public int? MinMileage { get; init; }
    public int? MaxMileage { get; init; }
    public Transmission? Transmission { get; init; }
    public FuelType? FuelType { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public string? CurrencyCode { get; init; }
    public string? DestinationCountryCode { get; init; }
    public string? DestinationCity { get; init; }
    public string? RawRequirementText { get; init; }
    public RequirementStatus? Status { get; init; }
}
