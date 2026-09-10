namespace CarDealer.Domain.Common;

/// <summary>
/// An entity whose external identifier appears in an API route.
/// </summary>
/// <remarks>
/// <para>
/// Decision D17: a record addressed at the <b>top level</b> of a route carries a
/// <see cref="PublicId"/>, because the id in the URL is the only thing identifying it and a
/// sequential key there can be walked. A record reached through a nested route - a customer's
/// requirement or note - keeps its integer key, because the parent's own identifier already
/// gates access and cannot be guessed.
/// </para>
///
/// <para>
/// The interface exists so <c>CarDealerDbContext</c> can fill these in on save rather than
/// relying on every <c>new</c> to remember. A missed assignment is an all-zeros GUID, which the
/// unique index only rejects once a second row arrives.
/// </para>
/// </remarks>
public interface IPubliclyAddressable
{
    Guid PublicId { get; set; }
}
