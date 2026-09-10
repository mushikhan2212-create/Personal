using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// A reusable message a salesperson starts from when writing to a customer.
/// </summary>
/// <remarks>
/// <para>
/// Not a WhatsApp Business <c>MessageTemplate</c>, which is a Meta-approved artefact tied to the
/// Business API and a deferred table. This is the dealer's own wording, stored so it survives
/// being typed once, and it stays a <b>draft</b>: the composed text lands in an editable box and
/// nothing sends until a person presses send (decision D15, master prompt section 18).
/// </para>
///
/// <para>
/// Tenant-owned with the CRM's flat-equality filter, never the catalogue's null-is-global rule.
/// How a dealer talks to their customers is theirs, and a template carries their pricing habits,
/// their sign-off and often their margin thinking - a shared one would leak all three.
/// </para>
///
/// <para>
/// The body is free text carrying placeholders such as <c>{FirstName}</c> and <c>{Model}</c>,
/// resolved by <c>TemplateRenderer</c>. The set of legal placeholders is closed and validated on
/// save, because the failure otherwise is silent and lands in front of a customer: a template
/// saying <c>{Modle}</c> renders that literally into a message somebody sends.
/// </para>
/// </remarks>
public class MessageTemplate : AuditableEntity, ITenantScoped, IPubliclyAddressable
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Stable external identifier, so URLs never expose the sequential key (D17).</summary>
    public Guid PublicId { get; set; }

    /// <summary>What the salesperson picks it by, e.g. "Price quote".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The channel this is written for. Only "whatsapp" exists today.</summary>
    /// <remarks>
    /// Stored rather than assumed because the same dealer's email wording is not their WhatsApp
    /// wording - one carries a subject line and a signature block, the other is a phone message.
    /// A template offered for the wrong channel is worse than no template.
    /// </remarks>
    public string Channel { get; set; } = "whatsapp";

    /// <summary>The text, with placeholders.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Order in the picker. Lower first, then by name.</summary>
    public int SortOrder { get; set; }
}
