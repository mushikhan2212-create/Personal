using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>One record within a sync run, so a single failure is traceable to its listing.</summary>
public class SyncJobItem : Entity
{
    public long SyncJobId { get; set; }

    public SyncJob SyncJob { get; set; } = null!;

    public string? ExternalListingId { get; set; }

    public SyncJobItemStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
}
