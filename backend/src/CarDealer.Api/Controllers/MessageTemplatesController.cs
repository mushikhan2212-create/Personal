using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.Abstractions;
using CarDealer.Application.Messaging;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarDealer.Api.Controllers;

public sealed record SaveTemplateRequest(string? Name, string? Body, int? SortOrder);

/// <summary>
/// The dealer's own message wording.
/// </summary>
/// <remarks>
/// Tenant-owned throughout: the query filter is flat equality, so a template belonging to
/// another dealer is not merely forbidden but invisible, and every lookup here goes through it.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/message-templates")]
public sealed class MessageTemplatesController : ControllerBase
{
    private const string WhatsApp = "whatsapp";

    private readonly CarDealerDbContext _db;
    private readonly ITenantContext _tenantContext;

    public MessageTemplatesController(CarDealerDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <summary>Lists this tenant's templates, in picker order.</summary>
    /// <remarks>
    /// Gated on <c>customers.manage</c> rather than the template-editing permission: anyone who
    /// may message a customer needs to read the templates in order to use one. Editing them is
    /// the higher bar.
    /// </remarks>
    [HttpGet]
    [HasPermission(Permissions.CustomersManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var templates = await _db.MessageTemplates
            .Where(t => t.Channel == WhatsApp)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new
            {
                Id = t.PublicId,
                t.Name,
                t.Body,
                t.SortOrder,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new
        {
            items = templates.Select(t => new
            {
                t.Id,
                t.Name,
                t.Body,
                t.SortOrder,

                // Computed rather than stored, so it can never disagree with the body somebody
                // just edited: a template naming any vehicle field cannot be rendered without
                // a car, and the picker hides it when there is none.
                needsVehicle = TemplateRenderer.NeedsVehicle(t.Body),

                // Surfaced so the editor can warn on it. The operator chose to allow the
                // listing link per-template; that choice is worth showing back to them every
                // time they open one, because the cost lands on a customer who follows it.
                revealsSource = t.Body.Contains("{ListingUrl", StringComparison.OrdinalIgnoreCase),
            }),

            // The catalogue the editor offers, so the list of legal placeholders lives in one
            // place rather than being retyped into the frontend and drifting from the renderer.
            placeholders = TemplateRenderer.Placeholders.Select(p => new
            {
                p.Name,
                p.Description,
                p.NeedsVehicle,
            }),
        });
    }

    /// <summary>Creates a template.</summary>
    [HttpPost]
    [HasPermission(Permissions.MessagingTemplatesManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] SaveTemplateRequest request, CancellationToken ct)
    {
        if (Invalid(request) is { } problem)
        {
            return BadRequest(problem);
        }

        var name = request.Name!.Trim();

        var clash = await _db.MessageTemplates
            .AnyAsync(t => t.Channel == WhatsApp && t.Name == name, ct)
            .ConfigureAwait(false);

        if (clash)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A template with that name already exists.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var template = new MessageTemplate
        {
            TenantId = _tenantContext.TenantId,
            Name = name,
            Channel = WhatsApp,
            Body = request.Body!.Trim(),
            SortOrder = request.SortOrder ?? 100,
        };

        _db.MessageTemplates.Add(template);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return CreatedAtAction(
            nameof(List), new { version = "1.0" }, new { Id = template.PublicId, template.Name });
    }

    /// <summary>Replaces a template's name, body and order.</summary>
    [HttpPut("{publicId:guid}")]
    [HasPermission(Permissions.MessagingTemplatesManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid publicId, [FromBody] SaveTemplateRequest request, CancellationToken ct)
    {
        if (Invalid(request) is { } problem)
        {
            return BadRequest(problem);
        }

        var template = await _db.MessageTemplates
            .FirstOrDefaultAsync(t => t.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (template is null)
        {
            return NotFound();
        }

        var name = request.Name!.Trim();

        var clash = await _db.MessageTemplates
            .AnyAsync(t => t.Channel == WhatsApp && t.Name == name && t.PublicId != publicId, ct)
            .ConfigureAwait(false);

        if (clash)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A template with that name already exists.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        template.Name = name;
        template.Body = request.Body!.Trim();
        template.SortOrder = request.SortOrder ?? template.SortOrder;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { Id = template.PublicId, template.Name });
    }

    /// <summary>Deletes a template.</summary>
    /// <remarks>
    /// A real delete, and it stays deleted. Nothing re-seeds an existing tenant's templates -
    /// the same rule the vehicle sources learned the hard way, for the same reason: a list
    /// somebody curates is theirs, and a platform that re-imposes its own on every restart is
    /// overruling them.
    /// </remarks>
    [HttpDelete("{publicId:guid}")]
    [HasPermission(Permissions.MessagingTemplatesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        var template = await _db.MessageTemplates
            .FirstOrDefaultAsync(t => t.PublicId == publicId, ct)
            .ConfigureAwait(false);

        if (template is null)
        {
            return NotFound();
        }

        _db.MessageTemplates.Remove(template);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Adds back any starter template whose name is not currently in use.</summary>
    /// <remarks>
    /// The recovery path for deleting one by accident, and the reason no automatic re-seed is
    /// needed. It adds only what is missing by name and never overwrites a template that is
    /// there, so pressing it twice does nothing the second time and it cannot discard wording
    /// somebody has edited.
    /// </remarks>
    [HttpPost("restore-starters")]
    [HasPermission(Permissions.MessagingTemplatesManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreStarters(CancellationToken ct)
    {
        var existing = await _db.MessageTemplates
            .Where(t => t.Channel == WhatsApp)
            .Select(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();

        foreach (var starter in StarterTemplates.All.Where(s => !have.Contains(s.Name)))
        {
            _db.MessageTemplates.Add(new MessageTemplate
            {
                TenantId = _tenantContext.TenantId,
                Name = starter.Name,
                Channel = WhatsApp,
                Body = starter.Body,
                SortOrder = starter.SortOrder,
            });

            added.Add(starter.Name);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Ok(new { restored = added });
    }

    /// <summary>
    /// The checks a template has to pass before it is stored.
    /// </summary>
    /// <remarks>
    /// The placeholder check is the one that earns its keep. An unrecognised name renders
    /// literally - braces and all - into a message a salesperson then sends, and the author has
    /// no way to see the typo because the editor shows them the template rather than the
    /// result. Rejecting it here is the last point at which anybody notices.
    /// </remarks>
    private static ProblemDetails? Invalid(SaveTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new ProblemDetails { Title = "A template needs a name.", Status = 400 };
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return new ProblemDetails { Title = "A template needs a body.", Status = 400 };
        }

        var unknown = TemplateRenderer.UnknownPlaceholders(request.Body);

        if (unknown.Count > 0)
        {
            return new ProblemDetails
            {
                Title = unknown.Count == 1
                    ? $"There is no placeholder called {{{unknown[0]}}}."
                    : "Some of those placeholders do not exist.",
                Detail = string.Join(", ", unknown.Select(u => $"{{{u}}}"))
                    + ". Available: "
                    + string.Join(", ", TemplateRenderer.Placeholders.Select(p => $"{{{p.Name}}}")),
                Status = 400,
            };
        }

        return null;
    }
}
