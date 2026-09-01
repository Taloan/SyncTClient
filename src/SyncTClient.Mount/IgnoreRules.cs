using System.Text;
using System.Text.RegularExpressions;

namespace SyncTClient.Mount;

/// <summary>
/// Muster, die einen Namen ganz aus dem Abgleich nehmen.
/// </summary>
/// <remarks>
/// Der Auswahlbaum und die Muster beantworten zwei verschiedene Fragen. Der
/// Baum sagt, welche <em>Zweige</em> auf diesem Geraet liegen sollen; was er
/// abwaehlt, wird trotzdem uebertragen und erst danach hier entfernt. Ein
/// Muster sagt, welche <em>Art</em> von Datei ueberhaupt nicht dazugehoert:
/// sie wird nicht angekuendigt, nicht geholt und nicht angelegt.
///
/// Der Baum kann "diesen Ordner nicht" sagen. Er kann nicht "nie *.tmp"
/// sagen, und schon gar nicht fuer Dateien, die es heute noch nicht gibt.
///
/// Anders als Syncthing steht die Liste nicht als ".stignore" im
/// Freigabeordner, sondern in der Konfiguration. Eine Datei im Ordner waere
/// selbst wieder ein Gegenstand des Abgleichs und braeuchte eine
/// Sonderregel, die sie davon ausnimmt -- und danach eine zweite fuer den
/// Fall, dass beide Seiten sie aendern.
///
/// Ebenso fehlt hier das Praefix "(?d)". Es heisst bei Syncthing "diese
/// Datei darf geloescht werden, wenn sie sonst das Entfernen eines Ordners
/// verhindert". Das ist eine Regel, die man einmal setzt und zwei Jahre
/// spaeter nicht mehr versteht, wenn Dateien verschwinden.
///
/// <para>Die Schreibweise:</para>
/// <list type="bullet">
///   <item><c>*</c> steht fuer beliebig viele Zeichen innerhalb eines
///   Namens, aber nicht ueber einen Verzeichnistrenner hinweg.</item>
///   <item><c>**</c> steht fuer beliebig viele Zeichen einschliesslich der
///   Trenner, also ueber mehrere Ebenen.</item>
///   <item><c>?</c> steht fuer genau ein Zeichen ausser dem Trenner.</item>
///   <item><c>!</c> am Anfang kehrt die Zeile um: was sie trifft, bleibt
///   dabei.</item>
///   <item><c>//</c> am Anfang ist ein Kommentar.</item>
/// </list>
///
/// Ein Muster ohne Trenner meint den Namen selbst, gleich auf welcher Ebene
/// er liegt: "Thumbs.db" trifft die Datei im Hauptverzeichnis wie die im
/// siebten Unterverzeichnis. Ein Muster mit Trenner wird gegen den ganzen
/// Pfad ab der Wurzel der Freigabe gelesen.
///
/// Trifft ein Muster ein Verzeichnis, ist alles darunter mitgemeint.
///
/// Es entscheidet das <em>erste</em> Muster, das zutrifft. Eine Ausnahme
/// gehoert deshalb ueber die Zeile, von der sie ausnimmt -- steht sie
/// darunter, kommt sie nie zum Zug. Syncthing haelt es genauso.
///
/// Verglichen wird ohne Ruecksicht auf Gross- und Kleinschreibung. Auf
/// diesem System gibt es "Thumbs.db" und "thumbs.db" nicht nebeneinander.
/// </remarks>
public sealed class IgnoreRules
{
    /// <summary>Keine Muster. Trifft nichts.</summary>
    public static readonly IgnoreRules Leer = new([]);

    private readonly (Regex Muster, bool Ausnahme)[] _regeln;

    private IgnoreRules((Regex, bool)[] regeln) => _regeln = regeln;

    /// <summary>Ob ueberhaupt etwas zu pruefen ist.</summary>
    public bool Any => _regeln.Length > 0;

    /// <summary>Uebersetzt die Zeilen. Unlesbare werden uebergangen.</summary>
    /// <remarks>
    /// Eine Zeile, die sich nicht uebersetzen laesst, darf den Abgleich nicht
    /// aufhalten. Sie trifft dann nichts -- das ist die harmlose Richtung:
    /// eine Datei zu viel im Abgleich ist ein Aergernis, eine Datei zu wenig
    /// ist ein Datenverlust.
    /// </remarks>
    public static IgnoreRules Parse(IEnumerable<string>? zeilen)
    {
        if (zeilen is null) return Leer;

        var regeln = new List<(Regex, bool)>();

        foreach (var roh in zeilen)
        {
            var zeile = roh?.Trim();
            if (string.IsNullOrEmpty(zeile)) continue;
            if (zeile.StartsWith("//", StringComparison.Ordinal)) continue;

            var ausnahme = zeile[0] == '!';
            if (ausnahme) zeile = zeile[1..].Trim();

            // Ein fuehrender Trenner bindet an die Wurzel. Er ist dafuer
            // nicht noetig -- das tut jeder Trenner im Muster --, aber
            // gewohnt, und ohne diesen Schnitt wuerde er als leerer erster
            // Namensteil gelesen und traefe nie etwas.
            zeile = zeile.Replace('\\', '/').TrimStart('/');
            if (zeile.Length == 0) continue;

            try { regeln.Add((new Regex(Uebersetzen(zeile), Optionen), ausnahme)); }
            catch (ArgumentException) { }
        }

        return regeln.Count == 0 ? Leer : new IgnoreRules([.. regeln]);
    }

    private const RegexOptions Optionen =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    /// <summary>
    /// Gehoert dieser Name zu dem, was ausgenommen ist?
    /// </summary>
    /// <param name="relativePath">Pfad ab der Wurzel der Freigabe, mit / als Trenner.</param>
    public bool Matches(string relativePath)
    {
        if (_regeln.Length == 0 || string.IsNullOrEmpty(relativePath)) return false;

        var name = relativePath.Replace('\\', '/').Trim('/');

        foreach (var (muster, ausnahme) in _regeln)
            if (muster.IsMatch(name))
                return !ausnahme;

        return false;
    }

    /// <summary>Aus einem Muster wird ein regulaerer Ausdruck.</summary>
    /// <remarks>
    /// Zwei Klammern rahmen ihn ein. Vorn steht, auf welcher Ebene das Muster
    /// greifen darf: ohne Trenner auf jeder, mit Trenner nur ab der Wurzel.
    /// Hinten steht, dass ein getroffenes Verzeichnis alles unter sich
    /// mitnimmt.
    /// </remarks>
    private static string Uebersetzen(string muster)
    {
        var vorn = muster.Contains('/') ? "^" : "(?:^|.*/)";

        var kern = new StringBuilder();

        for (var i = 0; i < muster.Length;)
        {
            var c = muster[i];

            if (c == '*')
            {
                if (i + 1 < muster.Length && muster[i + 1] == '*')
                {
                    // "**/" darf auch gar keine Ebene meinen: "a/**/b" trifft
                    // "a/b". Ohne diesen Fall braeuchte es zwei Zeilen fuer
                    // dieselbe Aussage.
                    if (i + 2 < muster.Length && muster[i + 2] == '/')
                    {
                        kern.Append("(?:.*/)?");
                        i += 3;
                    }
                    else
                    {
                        kern.Append(".*");
                        i += 2;
                    }

                    continue;
                }

                kern.Append("[^/]*");
                i++;
                continue;
            }

            if (c == '?')
            {
                kern.Append("[^/]");
                i++;
                continue;
            }

            kern.Append(Regex.Escape(c.ToString()));
            i++;
        }

        return vorn + kern + "(?:/.*)?$";
    }
}
