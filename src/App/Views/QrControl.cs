using System.Windows;
using System.Windows.Media;

namespace EasyMouse.App.Views;

/// <summary>Draws a QR matrix as crisp squares (dark on white, as the design's code).</summary>
public sealed class QrControl : FrameworkElement
{
    public static readonly DependencyProperty MatrixProperty = DependencyProperty.Register(
        nameof(Matrix), typeof(bool[,]), typeof(QrControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool[,]? Matrix
    {
        get => (bool[,]?)GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = Matrix;
        if (m is null) return;
        int n = m.GetLength(0);
        double cell = Math.Min(ActualWidth, ActualHeight) / n;
        var dark = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16));
        dark.Freeze();
        // One geometry for all modules: no hairline seams between neighbours.
        var group = new StreamGeometry();
        using (var ctx = group.Open())
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!m[x, y]) continue;
                    ctx.BeginFigure(new Point(x * cell, y * cell), true, true);
                    ctx.LineTo(new Point((x + 1) * cell, y * cell), false, false);
                    ctx.LineTo(new Point((x + 1) * cell, (y + 1) * cell), false, false);
                    ctx.LineTo(new Point(x * cell, (y + 1) * cell), false, false);
                }
        }
        group.Freeze();
        dc.DrawGeometry(dark, null, group);
    }
}
