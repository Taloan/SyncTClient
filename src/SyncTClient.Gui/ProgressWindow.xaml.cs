using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Zeigt, wie weit ein Ordner beim Einlesen ist.
/// </summary>
/// <remarks>
/// Der Index eines grossen Ordners braucht Zeit, und danach das Rechnen der
/// Blocklisten noch einmal. Bisher stand davon nur ein Satz in der Statuszeile
/// am unteren Rand -- an einer Stelle, an der niemand hinsieht, waehrend er
/// auf ein Fenster wartet, das nicht kommt.
///
/// Das Fenster gehoert dem Aufrufer: er zeigt es, haengt es mit
/// <see cref="Verfolge"/> an den Ordner und schliesst es, wenn er fertig ist.
/// </remarks>
public partial class ProgressWindow : Window
{
    private ShareHost? _host;

    /// <summary>
    /// Fragt den Stand ab, statt auf Meldungen zu warten.
    /// </summary>
    /// <remarks>
    /// Der Ordner meldet seine Phase aus fremden Faeden. Ein Takt in der
    /// Oberflaeche kommt ohne Marshalling aus und zeigt zwischen zwei
    /// Meldungen dasselbe an wie danach.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ProgressWindow(string name)
    {
        InitializeComponent();

        TitleText.Text = name;
        PhaseText.Text = App.S("S.Work.Asking");
        Bar.IsIndeterminate = true;

        _takt.Tick += (_, _) => Zeigen();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();
    }

    /// <summary>Ab jetzt steht der Stand dieses Ordners im Fenster.</summary>
    public void Verfolge(ShareHost host) => _host = host;

    private void Zeigen()
    {
        if (_host is not { } host) return;

        PhaseText.Text = host.Phase switch
        {
            SyncPhase.Index => App.S("R.PhaseIndex"),
            SyncPhase.Platzhalter => App.S("R.PhasePlaceholders"),
            SyncPhase.Cache => App.S("R.PhaseCache"),
            SyncPhase.Inhalte => App.S("R.PhaseContent"),
            SyncPhase.Abgleich => App.S("R.PhaseSyncing"),
            _ => App.S("S.Work.Asking")
        };

        // Ohne bekannten Umfang ein laufender Balken. Eine Null waere eine
        // Aussage ueber den Stand, und die haben wir nicht.
        if (host.PhaseTotal <= 0)
        {
            Bar.IsIndeterminate = true;
            CountText.Text = host.PhaseDone > 0 ? Format.Count(host.PhaseDone) : "";
            return;
        }

        Bar.IsIndeterminate = false;
        Bar.Value = Math.Clamp(100.0 * host.PhaseDone / host.PhaseTotal, 0, 100);
        CountText.Text = App.S("S.Work.Of", Format.Count(host.PhaseDone), Format.Count(host.PhaseTotal));
    }
}
