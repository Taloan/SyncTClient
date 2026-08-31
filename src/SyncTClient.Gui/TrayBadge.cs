using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;

namespace SyncTClient.Gui;

/// <summary>Was das Zeichen im Infobereich gerade meldet.</summary>
public enum TrayStatus
{
    Getrennt,
    Synchronisiert,
    Pausiert,
    Erledigt,
    Fehler
}

/// <summary>
/// Setzt das Zeichen im Infobereich aus Programmsymbol und Zustandsplakette
/// zusammen.
/// </summary>
/// <remarks>
/// Nextcloud legt für jeden Zustand eine eigene Symboldatei bei. Das ist
/// unnötig: fünf Zustände mal drei Bildschirmauflösungen wären fünfzehn
/// Dateien, die bei jeder Änderung am Programmsymbol neu entstehen müssten.
/// Hier wird die Plakette gezeichnet, und ein neuer Zustand kostet ein paar
/// Zeilen statt einer neuen Datei.
///
/// Gezeichnet wird in vierfacher Größe und danach verkleinert. Ein Kreis mit
/// weißem Zeichen darin misst bei 16 Pixeln keine zehn Punkte; direkt in
/// dieser Größe gezeichnet wäre er eine Treppe.
///
/// Die fertigen Symbole bleiben liegen. Sie tragen ein Betriebssystem-Handle,
/// das freigegeben werden muss, und das Zeichen wechselt im Sekundentakt
/// seinen Zustand — jedes Mal neu zu zeichnen hieße, im Sekundentakt Handles
/// zu erzeugen und wieder einzusammeln.
/// </remarks>
public sealed class TrayBadge : IDisposable
{
    private readonly Dictionary<(TrayStatus Status, int Size), Icon> _fertig = [];
    private readonly List<IntPtr> _handles = [];

    /// <summary>Das Zeichen für einen Zustand, in der gewünschten Kantenlänge.</summary>
    public Icon For(TrayStatus status, int size)
    {
        if (_fertig.TryGetValue((status, size), out var vorhanden)) return vorhanden;

        var icon = Compose(status, size);
        _fertig[(status, size)] = icon;
        return icon;
    }

    private Icon Compose(TrayStatus status, int size)
    {
        var arbeit = size * 4;

        using var flaeche = new Bitmap(arbeit, arbeit, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flaeche))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Das Programmsymbol bekommt nicht die ganze Flaeche. Es rueckt
            // nach links oben und macht rechts unten Platz -- sonst muesste
            // die Plakette entweder klein bleiben oder das Symbol verdecken.
            var eigen = (int)(arbeit * 0.84f);
            using (var basis = Basis(arbeit))
            {
                if (basis is not null) g.DrawImage(basis, 0, 0, eigen, eigen);
            }

            Plakette(g, status, arbeit);
        }

        using var klein = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(klein))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(flaeche, 0, 0, size, size);
        }

        // Icon.FromHandle gibt das Handle nicht selbst zurück. Es wird
        // aufbewahrt und beim Aufräumen zerstört.
        var handle = klein.GetHicon();
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Das Programmsymbol in der Arbeitsgröße.
    /// </summary>
    /// <remarks>
    /// Aus der eingebetteten Symboldatei statt aus der Programmdatei: nur so
    /// lässt sich die passende Größe auswählen, statt eine 32er-Fassung
    /// hochzurechnen.
    /// </remarks>
    private static Bitmap? Basis(int size)
    {
        try
        {
            var quelle = Application.GetResourceStream(
                new Uri("SyncTClient.ico", UriKind.Relative))?.Stream;

            if (quelle is null) return null;

            using var strom = quelle;
            using var icon = new Icon(strom, new System.Drawing.Size(size, size));
            return icon.ToBitmap();
        }
        catch (Exception)
        {
            // Ohne Programmsymbol bleibt die Plakette allein. Das ist immer
            // noch besser als gar kein Zeichen im Infobereich.
            return null;
        }
    }

    private static void Plakette(Graphics g, TrayStatus status, int flaeche)
    {
        var (grund, zeichen) = Farben(status);

        // Rechts unten, buendig mit dem Rand. Gross genug, dass das Zeichen
        // darin bei 16 Punkten noch Form hat: bei kleinerer Plakette blieben
        // dafuer keine fuenf Punkte uebrig.
        // Buendig in der rechten unteren Ecke, so weit aussen wie moeglich.
        // Weiter ginge nur ueber den Rand hinaus, und dort wuerde der Kreis
        // abgeschnitten.
        var durchmesser = flaeche * 0.62f;
        var feld = new RectangleF(
            flaeche - durchmesser, flaeche - durchmesser, durchmesser, durchmesser);

        // Dunkler Ring statt hellem. Der helle trennte die Plakette zwar vom
        // Symbol, verschwand aber auf einer hellen Taskleiste; der dunkle
        // traegt auf beiden.
        using (var ring = new Pen(Color.FromArgb(225, 26, 30, 34), flaeche * 0.05f))
        {
            using var fuellung = new SolidBrush(grund);
            g.FillEllipse(fuellung, feld);
            g.DrawEllipse(ring, feld);
        }

        Zeichen(g, status, feld, zeichen);
    }

    private static (Color Grund, Color Zeichen) Farben(TrayStatus status) => status switch
    {
        TrayStatus.Synchronisiert => (Color.FromArgb(255, 46, 158, 79), Color.White),
        TrayStatus.Erledigt => (Color.FromArgb(255, 46, 158, 79), Color.White),
        TrayStatus.Pausiert => (Color.FromArgb(255, 224, 138, 20), Color.White),
        TrayStatus.Fehler => (Color.FromArgb(255, 200, 52, 44), Color.White),
        _ => (Color.FromArgb(255, 128, 132, 138), Color.White)
    };

    private static void Zeichen(Graphics g, TrayStatus status, RectangleF feld, Color farbe)
    {
        // Alles in Anteilen des Plakettenfelds, damit jede Größe stimmt.
        float X(float t) => feld.Left + feld.Width * t;
        float Y(float t) => feld.Top + feld.Height * t;

        using var stift = new Pen(farbe, feld.Width * 0.15f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var pinsel = new SolidBrush(farbe);

        switch (status)
        {
            case TrayStatus.Erledigt:
                g.DrawLines(stift,
                [
                    new PointF(X(0.26f), Y(0.52f)),
                    new PointF(X(0.44f), Y(0.70f)),
                    new PointF(X(0.75f), Y(0.32f))
                ]);
                break;

            case TrayStatus.Synchronisiert:
                g.FillPolygon(pinsel,
                [
                    new PointF(X(0.38f), Y(0.26f)),
                    new PointF(X(0.38f), Y(0.74f)),
                    new PointF(X(0.76f), Y(0.50f))
                ]);
                break;

            case TrayStatus.Pausiert:
                g.FillRectangle(pinsel, X(0.32f), Y(0.28f), feld.Width * 0.13f, feld.Height * 0.44f);
                g.FillRectangle(pinsel, X(0.55f), Y(0.28f), feld.Width * 0.13f, feld.Height * 0.44f);
                break;

            case TrayStatus.Fehler:
                g.FillRectangle(pinsel, X(0.43f), Y(0.24f), feld.Width * 0.14f, feld.Height * 0.34f);
                g.FillEllipse(pinsel, X(0.43f), Y(0.64f), feld.Width * 0.14f, feld.Height * 0.14f);
                break;

            default:
                // Getrennt: ein Fragezeichen aus Bogen und Punkt. Als Schrift
                // gesetzt bliebe bei 16 Pixeln nur ein Fleck übrig; gezeichnet
                // behält es seine Form.
                using (var bogen = new Pen(farbe, feld.Width * 0.14f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(bogen, X(0.30f), Y(0.20f), feld.Width * 0.40f, feld.Height * 0.34f, 170, 250);
                    g.DrawLine(bogen, X(0.50f), Y(0.44f), X(0.50f), Y(0.56f));
                }
                g.FillEllipse(pinsel, X(0.43f), Y(0.66f), feld.Width * 0.14f, feld.Height * 0.14f);
                break;
        }
    }

    public void Dispose()
    {
        foreach (var icon in _fertig.Values) icon.Dispose();
        _fertig.Clear();

        foreach (var handle in _handles) DestroyIcon(handle);
        _handles.Clear();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
