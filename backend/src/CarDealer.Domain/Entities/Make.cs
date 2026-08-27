using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// Canonical manufacturer. Source data arrives as TOYOTA, Toyota, toyota and トヨタ for the
/// same maker; this is what collapses them
/// (docs/spec/03-canonical-vehicle-model.md section 7).
/// </summary>
/// <remarks>
/// Reference data, not tenant-owned: the set of manufacturers is a fact about the world.
/// Int key rather than the bigint used for transactional tables - this table has hundreds of
/// rows, not millions.
/// </remarks>
public class Make
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CountryCode { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Model> Models { get; set; } = new List<Model>();
}
