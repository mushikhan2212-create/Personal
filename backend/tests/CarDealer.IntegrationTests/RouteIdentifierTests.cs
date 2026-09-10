using System.Reflection;
using CarDealer.Domain.Common;
using CarDealer.Domain.Entities;
using CarDealer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using CarDealer.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CarDealer.IntegrationTests;

/// <summary>
/// The shape of the identifiers this API puts in its URLs.
/// </summary>
/// <remarks>
/// <para>
/// Decision D17, closing open item O8. A record addressed at the <b>top level</b> of a route
/// carries a GUID, because the id in the URL is the only thing identifying it: a sequential key
/// there can be walked, and on a table shared by every tenant it also discloses how much of it
/// there is. A record reached through a <b>nested</b> route may keep its integer key, because
/// the parent's own GUID already gates access and cannot be guessed - the same shape as
/// <c>/repos/{owner}/{repo}/issues/{number}</c>.
/// </para>
///
/// <para>
/// This is a test rather than a note in a document because the failure it guards against is
/// silent and additive. Phase 1 added two integer-keyed top-level routes without anyone
/// noticing, against an open item that had flagged exactly that inconsistency. A convention
/// nothing enforces is a convention that decays one endpoint at a time.
/// </para>
/// </remarks>
public sealed class RouteIdentifierTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RouteIdentifierTests(ApiFactory factory) => _factory = factory;

    /// <summary>A route parameter and the constraint it was declared with, e.g. <c>id:long</c>.</summary>
    private static readonly Regex Parameter = new(@"\{(?<name>\w+)(?::(?<constraint>\w+))?\}");

    private static IEnumerable<(string Controller, string Method, string Template)> Routes()
    {
        var controllers = typeof(CustomersController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    if (attribute.Template is { } template)
                    {
                        yield return (controller.Name, method.Name, template);
                    }
                }
            }
        }
    }

    [Fact]
    public void A_top_level_route_never_takes_a_sequential_key()
    {
        // "Top level" means the template's first parameter - nothing unguessable precedes it.
        var offenders = new List<string>();

        foreach (var (controller, method, template) in Routes())
        {
            var parameters = Parameter.Matches(template);

            if (parameters.Count == 0)
            {
                continue;
            }

            var first = parameters[0];
            var constraint = first.Groups["constraint"].Value;

            if (constraint is "long" or "int")
            {
                offenders.Add($"{controller}.{method}: \"{template}\"");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These routes identify a record by its sequential key with nothing unguessable in "
            + "front of it, which makes them enumerable (decision D17). Give the entity a "
            + "PublicId, or nest the route under one:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void A_nested_integer_key_always_sits_behind_a_guid()
    {
        // The other half of the rule. An integer child key is fine, but only because something
        // unguessable comes first - so assert that rather than trusting it.
        var offenders = new List<string>();

        foreach (var (controller, method, template) in Routes())
        {
            var parameters = Parameter.Matches(template).ToList();

            for (var i = 1; i < parameters.Count; i++)
            {
                if (parameters[i].Groups["constraint"].Value is not ("long" or "int"))
                {
                    continue;
                }

                var precededByGuid = parameters
                    .Take(i)
                    .Any(p => p.Groups["constraint"].Value == "guid");

                if (!precededByGuid)
                {
                    offenders.Add($"{controller}.{method}: \"{template}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These routes take an integer key that is not gated by a GUID earlier in the "
            + "path, so the integer is doing the identifying on its own (decision D17):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_rule_is_actually_exercised_by_routes_of_both_shapes()
    {
        // Guards the two tests above against passing vacuously. If a refactor ever left no
        // parameterised routes at all, or no nested ones, they would go green while checking
        // nothing - and this is the assertion that notices.
        var templates = Routes().Select(r => r.Template).ToList();

        Assert.Contains(templates, t => t.Contains("{publicId:guid}", StringComparison.Ordinal));

        Assert.Contains(
            templates,
            t => Parameter.Matches(t).Count > 1
                && t.Contains(":guid", StringComparison.Ordinal)
                && t.Contains(":long", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_row_gets_its_identifier_on_either_save_path(bool asynchronous)
    {
        // Both overloads, because the first version of this wired the assignment into
        // SaveChangesAsync only. Nothing in the app calls the synchronous one today, which is
        // exactly why the gap would have sat there until something did - and the symptom is an
        // all-zeros GUID that the unique index rejects only when a second row arrives.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        var role = new Role
        {
            Name = $"Route id probe {Guid.NewGuid():N}"[..40],
            // A tenant-owned role, so IsSystemRole computes false and the row is disposable.
            TenantId = await AnyTenantIdAsync(),
        };

        db.Roles.Add(role);

        if (asynchronous)
        {
            await db.SaveChangesAsync();
        }
        else
        {
            db.SaveChanges();
        }

        Assert.NotEqual(Guid.Empty, role.PublicId);

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
    }

    private async Task<long> AnyTenantIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarDealerDbContext>();

        return await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).FirstAsync();
    }

    [Fact]
    public void Every_entity_behind_a_top_level_route_declares_the_marker()
    {
        // The interface is what the DbContext keys off, so an entity that gains a PublicId
        // property without it would silently go unassigned.
        var withPublicId = typeof(Vehicle).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetProperty("PublicId") is not null)
            .ToList();

        Assert.NotEmpty(withPublicId);

        var unmarked = withPublicId
            .Where(t => !typeof(IPubliclyAddressable).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            unmarked.Count == 0,
            "These entities carry a PublicId but do not implement IPubliclyAddressable, so "
            + "nothing fills it in on save (decision D17):\n  " + string.Join("\n  ", unmarked));
    }
}
