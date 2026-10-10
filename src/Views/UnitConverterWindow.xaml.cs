using System.Globalization;
using System.Linq;
using System.Windows;

namespace CloCloWidget;

// A classic From/To converter — type a number, pick the From unit, pick
// the To unit, see one result. Replaces an earlier design that showed
// every other unit in the category at once; this is more direct when you
// already know exactly which two units you care about.
public partial class UnitConverterWindow : Window
{
    // parsed is optional — a text selection like "5 km" pre-fills the
    // number and From unit as a convenience, but the window is just as
    // usable with nothing selected at all, defaulting to 1 km -> mi.
    public UnitConverterWindow(UnitConverter.ParsedSelection? parsed)
    {
        InitializeComponent();

        var defaultCategory = parsed?.Category ?? "length";
        var defaultFromKey = parsed?.UnitKey ?? "km";
        ValueBox.Text = UnitConverter.FormatNumber(parsed?.Value ?? 1);

        FromCombo.ItemsSource = UnitConverter.AllUnits.ToList();
        FromCombo.SelectedItem = UnitConverter.AllUnits.First(u => u.Category == defaultCategory && u.Unit.Key == defaultFromKey);

        FromCombo.SelectionChanged += (_, _) => RebuildToOptions();
        ToCombo.SelectionChanged += (_, _) => Recalculate();
        ValueBox.TextChanged += (_, _) => Recalculate();

        RebuildToOptions();
    }

    // Scopes To's choices to whichever category From currently belongs to
    // (converting km to kg makes no sense), excluding From itself so
    // picking a pair always means an actual conversion.
    private void RebuildToOptions()
    {
        if (FromCombo.SelectedItem is not UnitConverter.CategoryUnit from) return;

        var siblings = UnitConverter.SiblingUnits(from.Category).Where(u => u.Key != from.Unit.Key).ToList();
        ToCombo.ItemsSource = siblings;
        ToCombo.SelectedItem = siblings.FirstOrDefault();
        Recalculate();
    }

    private void Recalculate()
    {
        ResultBox.Text = "";
        if (FromCombo.SelectedItem is not UnitConverter.CategoryUnit fromUnit) return;
        if (ToCombo.SelectedItem is not UnitConverter.UnitDef toUnit) return;
        if (!double.TryParse(ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;

        var result = UnitConverter.Convert(fromUnit.Unit, toUnit, value);
        ResultBox.Text = UnitConverter.FormatNumber(result);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        CopyButton.Content = SafeClipboard.TrySetText(ResultBox.Text) ? "Copied!" : "Clipboard busy";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
