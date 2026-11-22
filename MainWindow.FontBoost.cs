using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RescueWhale;

// Text boost: when the window is smaller than the layout needs, everything is scaled down to fit. On its own that
// makes the text shrink just as much as everything else, so text is enlarged inside the layout to make up for part
// of it. The boxes around it get more crowded instead, and long labels may be cut off at the smallest sizes.
public partial class MainWindow
{
    // each element's own font size before any boost, so boosting is always worked out from the original
    static readonly DependencyProperty BaseFontSizeProperty =
        DependencyProperty.RegisterAttached("BaseFontSize", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));

    static readonly DependencyProperty BaseWidthProperty =
        DependencyProperty.RegisterAttached("BaseWidth", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));

    double _fontBoost = 1;

    /// <summary>
    /// How much to enlarge text for a given layout scale: about two thirds of the shrink is made up (in proportion), up to 50%,
    /// in steps of 5% so a window being dragged doesn't redo the work at every pixel.
    /// </summary>
    static double BoostFor(double scale)
    {
        if (scale >= 0.98) return 1;
        double b = Math.Min(1.5, Math.Pow(1 / scale, 0.65));
        return Math.Round(b * 20) / 20;
    }

    /// <summary>Sets every piece of text in the window to its original size times the boost.</summary>
    void ApplyFontBoost(double boost)
    {
        _fontBoost = boost;
        BoostTree(ScaleHost, boost);
    }

    void BoostTree(DependencyObject node, double boost)
    {
        // ASCII art keeps its size: the logo and the battery drawing depend on exact character spacing
        // and the top strip (tabs and window buttons) keeps its size, since its row has no room to grow
        if (node == Logo || node == LogoBuoy || node == BatteryIcon || node == Strip) return;

        DependencyProperty? fontSize = node switch
        {
            TextBlock => TextBlock.FontSizeProperty,
            Control => Control.FontSizeProperty,
            _ => null,
        };
        if (fontSize != null)
        {
            var source = DependencyPropertyHelper.GetValueSource(node, fontSize).BaseValueSource;
            // text that just inherits its size follows its parent; only sizes set on the element itself (directly or
            // by a style) are boosted, so nothing gets enlarged twice
            if (source is not (BaseValueSource.Inherited or BaseValueSource.Default))
            {
                double original = (double)node.GetValue(BaseFontSizeProperty);
                if (original == 0)
                {
                    original = (double)node.GetValue(fontSize);
                    node.SetValue(BaseFontSizeProperty, original);
                }
                double target = original * boost;
                if (Math.Abs((double)node.GetValue(fontSize) - target) > 0.01) node.SetValue(fontSize, target);
            }
        }

        // a label with a fixed width (the column of setting names) widens with its text, so the text isn't cut off
        if (node is TextBlock label && DependencyPropertyHelper.GetValueSource(label, FrameworkElement.WidthProperty).BaseValueSource == BaseValueSource.Local)
        {
            double originalWidth = (double)label.GetValue(BaseWidthProperty);
            if (originalWidth == 0 && !double.IsNaN(label.Width))
            {
                originalWidth = label.Width;
                label.SetValue(BaseWidthProperty, originalWidth);
            }
            if (originalWidth > 0) label.Width = originalWidth * boost;
        }

        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) BoostTree(VisualTreeHelper.GetChild(node, i), boost);
    }
}
