using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Messaging;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Messaging;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

/// <summary>
/// Composing a message to a customer, and getting it to them.
/// </summary>
/// <remarks>
/// Today the configured provider prepares a WhatsApp click-to-chat link that a salesperson taps
/// and sends from their own phone. When Meta Business verification comes through, a provider
/// that sends directly replaces it and this controller does not change: the response already
/// distinguishes "prepared, waiting for you" from "sent", so the screens do not have to be
/// rewritten to learn the difference.
///
/// <para>
/// The customer lookup runs through the DbContext's tenant filter with no
/// <c>IgnoreQueryFilters</c>, so composing a message to another tenant's customer is a 404 -
/// which also means this endpoint cannot be used to read a phone number out of somebody else's
/// book.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/messaging")]
public sealed class MessagingController : ControllerBase
{
    /// <summary>
    /// How many photos the compose screen offers.
    /// </summary>
    /// <remarks>
    /// A real listing carries sixty-odd. Nobody attaches sixty photos to a WhatsApp message,
    /// and offering them all turns the drawer into the gallery it is not.
    /// </remarks>
    private const int MaxPhotosOffered = 10;

    private readonly CarDealerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IMessagingProvider _provider;

    public MessagingController(
        CarDealerDbContext db, ITenantContext tenant, IMessagingProvider provider)
    {
        _db = db;
        _tenant = tenant;
        _provider = provider;
    }

    /// <summary>
    /// Prepares a message to a customer, optionally about a particular car.
    /// </summary>
    /// <remarks>
    /// Called again with an edited <c>body</c> each time the salesperson changes the text, so
    /// the link handed back always matches what is on screen. Cheap by design: no state, and
    /// the only query is the customer and the car.
    ///
    /// A number that cannot be resolved comes back as <c>canSend: false</c> with a reason, not
    /// as an error. "This customer's number has no country code" is something the screen has to
    /// explain and somebody has to fix in the customer record - it is not an exception.
    /// </remarks>
    [HttpPost("whatsapp/draft")]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Draft(
        [FromBody] MessageDraftRequest request, CancellationToken ct)
    {
        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PublicId == request.CustomerPublicId, ct)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"No customer with id '{request.CustomerPublicId}'.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        var tenantName = await _db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Id == _tenant.TenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;

        Vehicle? vehicle = null;
        var photos = Array.Empty<string>();

        if (request.VehiclePublicId is { } vehiclePublicId)
        {
            vehicle = await _db.Vehicles
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.PublicId == vehiclePublicId, ct)
                .ConfigureAwait(false);

            if (vehicle is null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = $"No vehicle with id '{vehiclePublicId}'.",
                    Status = StatusCodes.Status404NotFound,
                });
            }

            // Returned so the compose screen can show them and offer them for download. They
            // are not put in the message: a click-to-chat link carries text only, and an image
            // URL would name the exporter exactly as the listing link did.
            photos = await _db.VehicleImages
                .AsNoTracking()
                .Where(i => i.VehicleId == vehicle.Id)
                .OrderBy(i => i.SortOrder)
                .Take(MaxPhotosOffered)
                .Select(i => i.ImageUrl)
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
        }

        var body = !string.IsNullOrWhiteSpace(request.Body)
            ? request.Body
            : vehicle is null
                ? MessageComposer.ForCustomer(customer, tenantName)
                : MessageComposer.ForVehicle(customer, vehicle, tenantName);

        var dispatch = await _provider
            .DispatchAsync(new MessageDraft(customer.Phone, customer.CountryCode, body), ct)
            .ConfigureAwait(false);

        return Ok(new
        {
            channel = _provider.Channel,

            // What the provider can do, so the screen describes the action honestly rather
            // than promising "sent" when it means "opens WhatsApp on your phone".
            canSendDirectly = _provider.Capabilities.CanSendDirectly,
            canReceive = _provider.Capabilities.CanReceive,

            to = customer.Phone,
            normalizedPhone = dispatch.NormalizedPhone,
            body,
            canSend = dispatch.Kind != DispatchKind.Failed,
            handoffUrl = dispatch.HandoffUrl,
            reason = dispatch.Reason,

            // For the salesperson to attach in WhatsApp. Indexed rather than sent as URLs to
            // download from, so the download route reads the address out of our own database
            // instead of accepting one from the caller.
            photos = photos.Select((url, index) => new
            {
                index,
                url,
                downloadUrl = request.VehiclePublicId is null
                    ? null
                    : $"/api/v1/vehicles/{request.VehiclePublicId}/photos/{index}",
            }),
        });
    }
}

public sealed record MessageDraftRequest
{
    public required Guid CustomerPublicId { get; init; }

    /// <summary>The car to write about. Omit for a message with no vehicle attached.</summary>
    public Guid? VehiclePublicId { get; init; }

    /// <summary>
    /// The message as edited by the salesperson. When absent, one is composed for them.
    /// </summary>
    public string? Body { get; init; }
}
