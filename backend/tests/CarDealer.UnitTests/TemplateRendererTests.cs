using CarDealer.Application.Messaging;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;

namespace CarDealer.UnitTests;

/// <summary>
/// How a stored template becomes the text somebody sends.
/// </summary>
/// <remarks>
/// The rules worth pinning down are the two that decide what a customer sees when the catalogue
/// is missing a field, which on this trade's data is often: a placeholder with nothing behind it
/// takes its line away, unless the author supplied a fallback.
/// </remarks>
public sealed class TemplateRendererTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_placeholder_is_replaced_by_its_value()
    {
        var text = TemplateRenderer.Render(
            "Hi {FirstName}, about the {Model}.",
            Values(("FirstName", "Imran"), ("Model", "Corolla")));

        Assert.Equal("Hi Imran, about the Corolla.", text);
    }

    [Fact]
    public void An_empty_placeholder_takes_its_line_with_it()
    {
        // The rule that matters most. "Year:" with nothing after it reads as though the dealer
        // forgot to finish the message, rather than as data the exporter never sent.
        var text = TemplateRenderer.Render(
            "• Year: {Year}\n• Colour: {Colour}",
            Values(("Year", "2016"), ("Colour", null)));

        Assert.Equal("• Year: 2016", text);
    }

    [Fact]
    public void A_whitespace_value_counts_as_empty()
    {
        // Import data arrives padded. A colour of "   " is absence wearing a costume.
        var text = TemplateRenderer.Render(
            "• Colour: {Colour}\n• Year: {Year}",
            Values(("Colour", "  "), ("Year", "2016")));

        Assert.Equal("• Year: 2016", text);
    }

    [Fact]
    public void A_fallback_keeps_the_line()
    {
        // The greeting is the line that must never be dropped: a message opening mid-sentence
        // because a customer has no first name recorded is worse than "Hi there".
        var text = TemplateRenderer.Render("Hi {FirstName|there},", Values(("FirstName", null)));

        Assert.Equal("Hi there,", text);
    }

    [Fact]
    public void A_fallback_is_ignored_when_there_is_a_real_value()
    {
        var text = TemplateRenderer.Render("Hi {FirstName|there},", Values(("FirstName", "Imran")));

        Assert.Equal("Hi Imran,", text);
    }

    [Fact]
    public void An_empty_fallback_keeps_the_line_and_writes_nothing()
    {
        // "{Variant|}" is an author saying "leave a blank here but keep the line", which is a
        // different instruction from "{Variant}" and has to stay distinguishable from it.
        var text = TemplateRenderer.Render(
            "Toyota Corolla {Variant|}", Values(("Variant", null)));

        Assert.Equal("Toyota Corolla", text);
    }

    [Fact]
    public void A_line_goes_if_any_of_its_placeholders_is_empty()
    {
        var text = TemplateRenderer.Render(
            "• {Fuel} · {Transmission}\n• Year: {Year}",
            Values(("Fuel", "Petrol"), ("Transmission", null), ("Year", "2016")));

        Assert.Equal("• Year: 2016", text);
    }

    [Fact]
    public void Dropping_a_line_does_not_leave_a_hole_where_it_was()
    {
        // A bullet removed from the middle of a list is invisible. The only bullet between two
        // blank lines, removed, would otherwise leave a double gap that reads as unfinished.
        var text = TemplateRenderer.Render(
            "Hi there,\n\n• Colour: {Colour}\n\nRegards", Values(("Colour", null)));

        Assert.Equal("Hi there,\n\nRegards", text);
    }

    [Fact]
    public void Blank_lines_between_paragraphs_survive()
    {
        var text = TemplateRenderer.Render(
            "Hi Imran,\n\nHere it is.\n\nRegards", Values());

        Assert.Equal("Hi Imran,\n\nHere it is.\n\nRegards", text);
    }

    [Fact]
    public void Windows_line_endings_do_not_leave_stray_carriage_returns()
    {
        // A template pasted from Word or Notepad arrives with \r\n. Left alone, every line of
        // the WhatsApp message ends in an invisible character.
        var text = TemplateRenderer.Render("Hi Imran,\r\n\r\nRegards", Values());

        Assert.DoesNotContain('\r', text);
        Assert.Equal("Hi Imran,\n\nRegards", text);
    }

    [Fact]
    public void Placeholder_names_are_matched_regardless_of_case()
    {
        var text = TemplateRenderer.Render("{firstname} {FIRSTNAME}", Values(("FirstName", "Imran")));

        Assert.Equal("Imran Imran", text);
    }

    [Fact]
    public void An_unknown_placeholder_is_reported_by_name()
    {
        // It would otherwise render literally - braces and all - into a message somebody sends,
        // and the author has no way to see the typo before the customer does.
        var unknown = TemplateRenderer.UnknownPlaceholders("Hi {FirstName}, the {Modle} is here.");

        Assert.Equal(["Modle"], unknown);
    }

    [Fact]
    public void Every_unknown_placeholder_is_reported_at_once()
    {
        // Somebody fixing a template wants the whole list, not the first mistake and another
        // round trip.
        var unknown = TemplateRenderer.UnknownPlaceholders("{Modle} {Yeer} {Make}");

        Assert.Equal(2, unknown.Count);
        Assert.Contains("Modle", unknown);
        Assert.Contains("Yeer", unknown);
    }

    [Fact]
    public void Every_starter_template_is_valid_and_renders()
    {
        // The starters ship to every new tenant, so a typo in one of them is a typo in
        // everybody's first experience of the feature.
        Assert.NotEmpty(StarterTemplates.All);

        foreach (var starter in StarterTemplates.All)
        {
            Assert.Empty(TemplateRenderer.UnknownPlaceholders(starter.Body));

            // With nothing resolvable at all, a starter must still produce something rather
            // than collapsing to an empty string - the fallbacks are what guarantee that.
            var bare = TemplateRenderer.Render(starter.Body, Values());

            Assert.False(
                string.IsNullOrWhiteSpace(bare),
                $"Starter '{starter.Name}' renders to nothing when no field is known.");
        }
    }

    [Fact]
    public void A_template_naming_a_vehicle_field_is_marked_as_needing_one()
    {
        Assert.True(TemplateRenderer.NeedsVehicle("About the {Model}."));
        Assert.False(TemplateRenderer.NeedsVehicle("Hi {FirstName}, are you free?"));
    }

    [Fact]
    public void The_price_is_the_dealers_own_and_never_the_exporters()
    {
        // The single most consequential line in this feature. The catalogue rows carry what the
        // *exporter* asks; this dealer brokers other exporters' stock and adds a margin. If
        // {Price} ever resolved from a listing, the message would quote the dealer's own buying
        // price to the person they are quoting to.
        var vehicle = new Vehicle { Make = "Toyota", Model = "Corolla" };

        vehicle.Listings.Add(new VehicleListing
        {
            Price = 5_720m,
            PriceBaseCurrency = 5_720m,
            BaseCurrencyCode = "USD",
            CurrencyCode = "USD",
            IsActive = true,
        });

        var values = TemplateFields.For(
            new Customer { FirstName = "Imran" },
            vehicle,
            new TenantVehicle { TenantPrice = 7_200m, TenantCurrencyCode = "USD" },
            "Nihon Motors");

        var text = TemplateRenderer.Render("Price: {Price}", values);

        Assert.Equal("Price: USD 7,200", text);
        Assert.DoesNotContain("5,720", text);
    }

    [Fact]
    public void With_no_retail_price_set_the_price_line_disappears_rather_than_falling_back()
    {
        // The failure this forbids is the quiet one: a quote template that renders the source
        // listing's price because nothing else was available. A missing line gets noticed; a
        // plausible wrong number does not.
        var vehicle = new Vehicle { Make = "Toyota", Model = "Corolla" };

        vehicle.Listings.Add(new VehicleListing
        {
            Price = 5_720m,
            PriceBaseCurrency = 5_720m,
            BaseCurrencyCode = "USD",
            CurrencyCode = "USD",
            IsActive = true,
        });

        var values = TemplateFields.For(
            new Customer { FirstName = "Imran" }, vehicle, overlay: null, "Nihon Motors");

        var text = TemplateRenderer.Render("Hi {FirstName},\nPrice: {Price}", values);

        Assert.Equal("Hi Imran,", text);
        Assert.DoesNotContain("5,720", text);
        Assert.DoesNotContain("Price", text);
    }

    [Fact]
    public void Specifications_reach_the_customer_as_words_not_enum_names()
    {
        // The one place an enum name must never surface. Left to ToString these read
        // "ContinuouslyVariable" and "RightHandDrive".
        var values = TemplateFields.For(
            new Customer { FirstName = "Imran" },
            new Vehicle
            {
                Make = "Toyota",
                Transmission = Transmission.ContinuouslyVariable,
                SteeringSide = SteeringSide.RightHandDrive,
            },
            overlay: null,
            "Nihon Motors");

        var text = TemplateRenderer.Render("{Transmission} · {Steering}", values);

        Assert.DoesNotContain("ContinuouslyVariable", text);
        Assert.DoesNotContain("RightHandDrive", text);

        // The trade's own abbreviations, which is what SpecWords is for - a Japanese-export
        // buyer reads "RHD" more fluently than "Right-hand drive", and it is shorter in a
        // message they open on a phone.
        Assert.Equal("CVT · RHD", text);
    }

    [Fact]
    public void An_unknown_specification_drops_its_line_rather_than_saying_Unknown()
    {
        var values = TemplateFields.For(
            new Customer { FirstName = "Imran" },
            new Vehicle { Make = "Toyota", FuelType = FuelType.Unknown },
            overlay: null,
            "Nihon Motors");

        var text = TemplateRenderer.Render("Hi {FirstName},\n• Fuel: {Fuel}", values);

        Assert.Equal("Hi Imran,", text);
    }
}
