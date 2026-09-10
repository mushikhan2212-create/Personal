using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One call to an AI provider: what was asked, what it cost, and whether it was believed.
/// </summary>
/// <remarks>
/// <para>
/// SQL schema spec section AIRequests. It exists for three jobs, and the third is the one
/// people forget.
/// </para>
///
/// <para>
/// <b>Spend</b> - tokens and cost per call, so the bill is observable from the first day
/// rather than arriving at the end of the month as a surprise.
/// </para>
///
/// <para>
/// <b>Reuse</b> - <see cref="InputHash"/> covers the requirement, the candidate ids, the model
/// and the prompt version. An unchanged question does not get asked twice, which matters
/// because a salesperson refreshing a screen should not be charged for it.
/// </para>
///
/// <para>
/// <b>Rejections</b> - a call whose answer failed the guards is recorded as
/// <see cref="AIRequestStatus.Rejected"/> rather than discarded. It was paid for, and the rate
/// at which a provider's answers are thrown away is the single most useful number for deciding
/// whether to keep paying that provider. Deleting the evidence would make provider choice a
/// matter of opinion again.
/// </para>
/// </remarks>
public class AIRequest : Entity, ITenantScoped
{
    public long TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    /// <summary>Short provider name, e.g. "anthropic" or "groq".</summary>
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Which operation this was, e.g. "rank". Section 11 lists the others.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Fingerprint of the question, for reuse.
    /// </summary>
    /// <remarks>
    /// Covers the prompt version deliberately: a reworded prompt is a different question, and
    /// serving a stored answer to the old one would be answering something nobody asked.
    /// </remarks>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>
    /// What was sent, for auditing.
    /// </summary>
    /// <remarks>
    /// Open item O4 warns that this column can itself capture personal data - the payload gets
    /// scrubbed and the audit trail quietly keeps a copy. It cannot here: the only customer
    /// data that reaches a provider is <c>RequirementBrief</c>, which has no field for a name,
    /// a number or free text. That is a property of the type rather than of this comment.
    /// </remarks>
    public string? InputMetadataJson { get; set; }

    public string? OutputMetadataJson { get; set; }

    public string? TokenUsageJson { get; set; }

    /// <summary>Cost in USD where the provider reports one, else null.</summary>
    public decimal? Cost { get; set; }

    public AIRequestStatus Status { get; set; } = AIRequestStatus.Pending;

    /// <summary>Why it failed or was rejected, in words a person can act on.</summary>
    public string? FailureReason { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
