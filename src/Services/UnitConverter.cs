using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CloCloWidget;

// Parses a selected "<number> <unit>" phrase (e.g. "5 km") and converts it
// entirely locally, via a shared base unit per category (so any unit in a
// category converts to any other, not just one fixed pair) — no network
// call, no currency support. An earlier version added currency conversion
// via a live exchange-rate API, but that's been dropped: it was the one
// part of this feature that needed a network round trip, and stripping it
// keeps this simple, instant, and fully offline instead.
public static class UnitConverter
{
    public record UnitDef(string Key, string Label, Func<double, double> ToBase, Func<double, double> FromBase)
    {
        public override string ToString() => Label;
    }

    // Base units: km (length), kg (weight), °C (temperature) — arbitrary
    // choices, any unit converts to any other in its category via this
    // shared intermediate rather than needing an entry per pair.
    public static readonly Dictionary<string, UnitDef[]> Categories = new()
    {
        ["length"] = new[]
        {
            new UnitDef("km", "km", v => v, v => v),
            new UnitDef("mi", "mi", v => v * 1.60934, v => v * 0.621371),
            new UnitDef("m", "m", v => v / 1000, v => v * 1000),
            new UnitDef("ft", "ft", v => v * 0.0003048, v => v * 3280.84),
        },
        ["weight"] = new[]
        {
            new UnitDef("kg", "kg", v => v, v => v),
            new UnitDef("lb", "lb", v => v * 0.453592, v => v * 2.20462),
        },
        ["temperature"] = new[]
        {
            new UnitDef("c", "°C", v => v, v => v),
            new UnitDef("f", "°F", v => (v - 32) * 5 / 9, v => v * 9 / 5 + 32),
            new UnitDef("k", "K", v => v - 273.15, v => v + 273.15),
        },
    };

    private static readonly Regex Pattern = new(@"^\s*(-?\d+(?:\.\d+)?)\s*°?\s*([a-zA-Z]+)\s*$", RegexOptions.Compiled);

    // Aliases accepted when parsing free-text selections, mapped to a
    // canonical (category, key) pair — separate from Categories' own keys
    // since a selection might say "kilometers" where the category table
    // only needs to know "km".
    private static readonly (string[] Aliases, string Category, string Key)[] UnitAliases =
    {
        (new[] { "km", "kilometer", "kilometers", "kilometre", "kilometres" }, "length", "km"),
        (new[] { "mi", "mile", "miles" }, "length", "mi"),
        (new[] { "m", "meter", "meters", "metre", "metres" }, "length", "m"),
        (new[] { "ft", "foot", "feet" }, "length", "ft"),
        (new[] { "kg", "kilogram", "kilograms", "kilo", "kilos" }, "weight", "kg"),
        (new[] { "lb", "lbs", "pound", "pounds" }, "weight", "lb"),
        (new[] { "c", "celsius" }, "temperature", "c"),
        (new[] { "f", "fahrenheit" }, "temperature", "f"),
        (new[] { "k", "kelvin" }, "temperature", "k"),
    };

    public record ParsedSelection(double Value, string Category, string UnitKey);

    public static (ParsedSelection? Parsed, string? Error) ParseSelection(string text)
    {
        var match = Pattern.Match(text.Trim());
        if (!match.Success)
        {
            return (null, "Select just a number and a unit, like \"5 km\" or \"98.6 F\".");
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unitText = match.Groups[2].Value;

        var alias = UnitAliases.FirstOrDefault(u => u.Aliases.Contains(unitText, StringComparer.OrdinalIgnoreCase));
        if (alias.Aliases != null)
        {
            return (new ParsedSelection(value, alias.Category, alias.Key), null);
        }

        return (null, $"\"{unitText}\" isn't a unit I recognize.");
    }

    // A real type, not a ValueTuple — WPF's ComboBox binds to this via
    // reflection (DisplayMemberPath, SelectedItem casts), and a named
    // tuple's element names ("Category", "Unit") are compile-time-only
    // sugar over the real runtime properties Item1/Item2, which reflection
    // can't see under the names the C# compiler shows. Confirmed this
    // broke the display (ComboBox.Text came back empty) before switching
    // to this.
    public record CategoryUnit(string Category, UnitDef Unit)
    {
        public override string ToString() => Unit.Label;
    }

    // Every unit across every category, flattened — the From dropdown picks
    // from this whole list; the To dropdown is then scoped to whichever
    // category From turns out to belong to (see SiblingUnits).
    public static IEnumerable<CategoryUnit> AllUnits =>
        Categories.SelectMany(kv => kv.Value.Select(u => new CategoryUnit(kv.Key, u)));

    public static UnitDef[] SiblingUnits(string category) => Categories[category];

    public static double Convert(UnitDef from, UnitDef to, double value) => to.FromBase(from.ToBase(value));

    public static string FormatNumber(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
