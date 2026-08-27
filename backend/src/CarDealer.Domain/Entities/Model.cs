namespace CarDealer.Domain.Entities;

/// <summary>Canonical model, scoped to a <see cref="Make"/>.</summary>
public class Model
{
    public int Id { get; set; }

    public int MakeId { get; set; }

    public Make Make { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
