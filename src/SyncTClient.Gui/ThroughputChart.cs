using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Zeichnet den Durchsatz der gewaehlten Zeitspanne.
/// </summary>
/// <remarks>
/// Selbst gezeichnet statt mit einer Diagrammbibliothek: gebraucht werden
/// zwei Reihen ueber einer gemeinsamen Zeitachse. Dafuer waere jede Bibliothek
/// mehr Abhaengigkeit als Nutzen.
///
/// Die Hochachse skaliert selbst und rundet dabei nach oben. Eine feste
/// Obergrenze waere bei einer schnellen Leitung nutzlos, und bei einer
/// langsamen bliebe die Kurve ein flacher Strich.
/// </remarks>
public sealed class ThroughputChart : FrameworkElement
{
    private static readonly Brush EmpfangenErsatz = Neu(Color.FromRgb(0x1E, 0x88, 0xE5));
    private static readonly Brush GesendetErsatz = Neu(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly Brush RasterErsatz = Neu(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush SchriftErsatz = Neu(Color.FromRgb(0x88, 0x88, 0x88));

    // Beim Zeichnen aus dem Farbschema geholt. Ein fest hellgraues Raster
    // waere auf dunklem Grund viel zu hell.
    private Brush EmpfangenFuellung => Transparent(Farbe("Ein", EmpfangenErsatz), 60);
    private Pen EmpfangenLinie => NeuStift(Farbe("Ein", EmpfangenErsatz), 1.6);
    private Pen GesendetLinie => NeuStift(Farbe("Aus", GesendetErsatz), 1.4);
    private Pen Raster => NeuStift(Farbe("RahmenFein", RasterErsatz), 1);
    private Brush Schrift => Farbe("Gedaempft", SchriftErsatz);

    private Brush Farbe(string key, Brush ersatz) => TryFindResource(key) as Brush ?? ersatz;

    /// <summary>Dieselbe Farbe, nur durchscheinend. Fuer die Flaeche unter der Kurve.</summary>
    private static Brush Transparent(Brush brush, byte alpha)
    {
        if (brush is not SolidColorBrush solid) return brush;

        var color = solid.Color;
        return Neu(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private ThroughputPoint[] _points = [];

    /// <summary>
    /// Die Obergrenze der Hochachse, nachlaufend geglaettet.
    /// </summary>
    /// <remarks>
    /// Sie richtet sich nach der hoechsten Saeule. Ohne Glaettung sprang das
    /// ganze Bild in dem Moment, in dem eine Spitze aus dem Fenster lief:
    /// dieselben Daten standen ploetzlich doppelt so hoch da. Nach oben wird
    /// deshalb sofort nachgezogen -- eine Spitze soll nicht abgeschnitten
    /// werden --, nach unten nur langsam.
    /// </remarks>
    private double _obergrenze;

    public void Show(ThroughputPoint[] points)
    {
        _points = points;

        var spitze = 0.0;
        foreach (var p in points) spitze = Math.Max(spitze, Math.Max(p.Read, p.Written));

        var ziel = Rundung(Math.Max(spitze * 1.15, 1024));
        _obergrenze = ziel >= _obergrenze ? ziel : Math.Max(ziel, _obergrenze * 0.92);

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 8 || h <= 8) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        const double links = 52;
        var flaeche = new Rect(links, 4, Math.Max(1, w - links - 4), Math.Max(1, h - 20));

        // Mindestens 1 KB/s. Sonst erscheint bei geringem Durchsatz das
        // Rauschen als Zickzack.
        var obergrenze = Math.Max(_obergrenze, 1024);

        for (var i = 0; i <= 4; i++)
        {
            var y = flaeche.Top + flaeche.Height * i / 4.0;
            dc.DrawLine(Raster, new Point(flaeche.Left, y), new Point(flaeche.Right, y));

            var wert = obergrenze * (4 - i) / 4.0;
            dc.DrawText(Text(Format.Rate(wert)), new Point(2, y - 7));
        }

        if (_points.Length < 2) return;

        dc.DrawGeometry(EmpfangenFuellung, EmpfangenLinie, Kurve(p => p.Read, flaeche, obergrenze, true));
        dc.DrawGeometry(null, GesendetLinie, Kurve(p => p.Written, flaeche, obergrenze, false));
    }

    private Geometry Kurve(Func<ThroughputPoint, double> auswahl, Rect flaeche, double obergrenze, bool gefuellt)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var schritt = flaeche.Width / (_points.Length - 1);
            var start = Punkt(0, auswahl(_points[0]), flaeche, obergrenze, schritt);

            g.BeginFigure(gefuellt ? new Point(start.X, flaeche.Bottom) : start, gefuellt, false);
            if (gefuellt) g.LineTo(start, true, false);

            for (var i = 1; i < _points.Length; i++)
                g.LineTo(Punkt(i, auswahl(_points[i]), flaeche, obergrenze, schritt), true, false);

            if (gefuellt) g.LineTo(new Point(flaeche.Right, flaeche.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point Punkt(int index, double wert, Rect flaeche, double obergrenze, double schritt)
        => new(flaeche.Left + index * schritt,
               flaeche.Bottom - flaeche.Height * Math.Clamp(wert / obergrenze, 0, 1));

    /// <summary>Rundet auf das 1-, 1,25-, 1,5- oder 2-fache einer Zweierpotenz auf.</summary>
    private static double Rundung(double wert)
    {
        var basis = Math.Pow(2, Math.Floor(Math.Log2(wert)));
        foreach (var faktor in new[] { 1.0, 1.25, 1.5, 2.0 })
            if (basis * faktor >= wert) return basis * faktor;

        return basis * 2;
    }

    private FormattedText Text(string text) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 10, Schrift, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Neu(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen NeuStift(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
