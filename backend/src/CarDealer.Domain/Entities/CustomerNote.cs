using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// Something a person chose to write down about a customer, on the day they wrote it.
/// </summary>
/// <remarks>
/// <para>
/// A log rather than the single <see cref="Customer.Notes"/> box this replaces, because in this
/// trade the date is half the content. "7,000 is his ceiling" means something different said
/// last week and said in March, and a customer who claims they were quoted 6,500 in August is
/// answerable only if the record kept its dates. One overwritten box cannot do that: either the
/// old text is destroyed or it grows into a wall nobody reads.
/// </para>
///
/// <para>
/// Not the activity timeline master prompt section 9 also asks for, and deliberately not.
/// A timeline is generated - message sent, requirement added, alert raised - and answers "what
/// happened". This is typed by a person and answers "what did they say". The two want different
/// storage and different screens, and building the timeline first would have buried the notes
/// somebody actually wrote inside a stream of events the system wrote.
/// </para>
///
/// <para>
/// As private as the customer it belongs to. <c>TenantId</c> is non-nullable with the flat
/// equality filter the rest of the CRM uses, never the catalogue's null-is-global rule, and
/// deleting a customer takes their notes with them - erasure has to reach everything derived
/// from the person (open item O3).
/// </para>
/// </remarks>
public class CustomerNote : Entity
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public long CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Who wrote it, or null for a note the platform moved here rather than a person typing it.
    /// </summary>
    /// <remarks>
    /// Null is the migrated case: the single notes box that preceded this table had no author,
    /// and inventing one would put words in somebody's mouth. The screen says "imported" rather
    /// than naming a user.
    /// </remarks>
    public long? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    /// <summary>
    /// When the text last changed, or null if it never has.
    /// </summary>
    /// <remarks>
    /// Shown, because a note edited after the fact is weaker evidence than one written on the
    /// day - which is the whole reason for keeping dates at all.
    /// </remarks>
    public DateTime? EditedAtUtc { get; set; }
}
