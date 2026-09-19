# Änderungen

Die Einträge sind von Hand geschrieben. Sie nennen, was sich für den Anwender
geändert hat, nicht die Betreffe der Commits — wer den Verlauf im einzelnen
sehen will, findet ihn in `git log`.

*Entries are written by hand and are in German. See `git log` for the
commit-by-commit history.*

<!-- Neue Fassungen kommen von Hand unter diese Zeile, vor die vorige. -->

## 0.9.6 — in Arbeit

**Lightroom**

- Einträge der Gegenstelle, die bei geöffnetem Katalog zurückstanden, wurden
  aus der Schlange genommen und nach dem Schließen nie wieder angesehen: 799
  Einträge standen eine Stunde nach dem Ende von Lightroom noch als
  Rückstand. Sie bleiben jetzt in der Schlange und werden im nächsten Takt
  erneut geprüft.
- Ein geöffneter Katalog wird nicht mehr angefasst. Solange neben `X.lrcat`
  die Sperrdatei `X.lrcat.lock` liegt, wird keine Datei des Verbunds —
  `X.lrcat`, `X.lrcat-data`, `X Helper.lrdata`, `X Previews.lrdata`,
  `X Smart Previews.lrdata`, `X Sync.lrdata` — angekündigt, übernommen oder
  gelöscht. Unabhängig vom Smart-Datenbankmodus. Anlass: 79 Dateien aus
  `PRI-v14.lrcat-data` während einer Lightroom-Sitzung durch eine eingehende
  Änderung entfernt, danach Reparaturmeldung und Absturz.

**Abgleich**

- Schließt eine Gegenstelle Dateien per Muster aus oder wählt sie ab, kündigt
  sie die Einträge als ungültig an (0 B, ohne Blöcke). Solche Einträge
  standen hier in der Tabelle wie jeder andere und gewannen bei neuerer
  Version: fünf GPX-Dateien, die die Rossibox eben aus AppData ausgeschlossen
  hatte, standen als Rückstand "hier 56 KB statt 0 B", und 15.561 solcher
  Einträge lagen im Index. Syncthing lässt einen ungültigen Eintrag nie
  gegen eine gültige Fassung gewinnen und holt ihn nie. Jetzt gilt er als
  "nicht angekündigt": der Eintrag der Gegenstelle geht fort, die eigene
  Datei bleibt; gespeicherte Altlasten werden beim Öffnen einmalig entfernt.
  Die Sequenznummer der Gegenstelle bleibt dabei erhalten, damit sie beim
  Verbinden an der richtigen Stelle fortsetzt.
- Führen mehrere Gegenstellen eine Datei in verschiedenen Fassungen, wurde
  der Rückstand an der größten Größe und jüngsten Zeit über alle gemessen —
  einer Fassung, die es nicht gibt. Acht Fotos, die hier und auf der
  Rossibox neu vorlagen und auf dem getrennten DIRK-PC noch alt, standen so
  dauerhaft als "hier 21989814 statt 22001291 Bytes" im Rückstand. Gemessen
  wird jetzt an der geltenden Fassung.
- Eine Datei, die ein anderes Programm geöffnet hat, wurde bei jedem Versuch
  vollständig übertragen und erst beim Ersetzen abgewiesen — bei "notizi
  pr.db" 12,6 MB je Minute über das Relay. Jetzt wird vor dem Übertragen
  geprüft.
- Eine Änderung neben einer Löschung gewinnt; eine Löschung wartet, bis eine
  hier noch nicht angekündigte Änderung hinaus ist. Vorher sah die Löschung
  der Gegenstelle neuer aus als eine eben geschriebene Datei.
- Eine Löschung braucht einen Zeugen: den Durchgang, der die Datei zuletzt
  noch vorfand, oder den Beobachter. Einträge, die hier nie mit Inhalt lagen,
  werden nicht mehr als gelöscht gemeldet.
- Die Konfliktkopie entsteht durch Kopieren; die eigene Datei bleibt an ihrem
  Platz, bis die Fassung der Gegenstelle sie ersetzt. Vorher fehlte der Name
  zwischenzeitlich, und das ging als Löschung hinaus.
- Nach einem gewonnenen Konflikt geht die Datei mit neuer Version hinaus.
  Vorher blieb die Ankündigung aus, und die Gegenstelle hielt ihre Fassung
  für die gültige.
- Änderungen, die beim Beenden noch nicht angekündigt waren, werden beim
  nächsten Start aufgegriffen.
- Eine von der Gegenstelle übernommene Datei bekommt deren Änderungszeit;
  vorher meldete jeder Durchgang sie als geändert und hashte sie neu.
- Was hier geändert und noch nicht angekündigt ist, steht in der Zeile und
  im Protokoll — statt „abgeglichen".

**Verbindungen**

- Ein Index oder Nachtrag konnte vor unserer Ordnerliste hinausgehen, wenn
  ein schon laufender Ordner auf die Liste der Gegenstelle sofort antwortete
  und die eigene noch hinter dem Öffnen der Indexdatenbanken stand.
  Syncthing schließt darauf die Verbindung — am 19.09. viermal in einer
  Minute, und alle Ordner standen auf "gestoppt". Index und Nachtrag warten
  jetzt, bis die Ordnerliste durch ist.
- Die Ordnerliste der Gegenstelle wurde gelesen, bevor die eigenen Ordner
  eingetragen waren; alle noch fehlenden galten als "nicht übernommen" —
  sieben Angebote für längst eingerichtete Ordner und eine Anfrage in der
  Warteliste. Gelesen wird jetzt erst nach dem Eintragen.
- Der Grund, den die Gegenstelle beim Schließen nennt, steht jetzt im
  Protokoll; bisher hieß es nur "hat die Verbindung beendet".
- Endete die Verbindung während der Ankündigung, stand "Object reference not
  set to an instance of an object" statt eines Abbruchs.
- Eine Blockanfrage nach zwei Minuten Ruhe galt sofort als unbeantwortet;
  jede Übertragung nach einer Pause scheiterte beim ersten Versuch.
- Endete die Leitung während der Ankündigung, blieb die Gegenstelle auf
  „verbindet" stehen: keine Wiederanwahl, jede eingehende Verbindung
  abgewiesen.
- Wählen beide Seiten einander gleichzeitig an, weicht die eigene Anwahl der
  eingehenden Verbindung.
- Fragt die Gegenstelle nach einer Datei, die hier als vorhanden angekündigt
  war, aber nicht liegt, wird die Ankündigung berichtigt statt minütlich
  abgelehnt.

**Oberfläche**

- In den Einstellungen standen Speichern und Abbrechen am Ende des
  Rollbereichs — wer nicht hinunterrollte, sah sie nicht, dachte nicht ans
  Speichern und schloss mit X; zwei gesetzte Haken waren beim nächsten
  Öffnen wieder fort. Die Knöpfe stehen jetzt fest unter dem Rollbereich,
  wie im Gerätedialog, und bei ungespeicherten Änderungen wird beim
  Schließen gefragt. Scheitert der Autostart-Eintrag, gehen die übrigen
  Einstellungen trotzdem durch, und der Fehler wird gemeldet.
- Eingehende Anfragen — neue Gegenstellen, angebotene Ordner — stehen im
  Reiter „Anfragen" und werden dort angenommen oder abgelehnt; kein Dialog
  mehr.
- Version, Build-Commit und Bauzeitpunkt stehen in der Titelzeile.


## 0.9.5 — in Arbeit

**Verbindungen**

- Ein Relay, der auf allen Schnittstellen horcht, trägt in seine Einladung
  die unbestimmte Adresse ein (`::ffff:0.0.0.0`). Der Aufbau dorthin
  scheiterte mit "die angeforderte Adresse ist in diesem Kontext ungültig",
  bei jedem Versuch, und das Telefon blieb unerreichbar. Eine unbestimmte
  Adresse heißt jetzt wie eine leere: der Relay selbst.

**Start**

- Zwei Ordner dürfen gleichzeitig anlaufen; die Plätze bekamen die, deren
  Index zuerst da war — und Lightroom mit 67 000 Einträgen hielt einen davon
  drei Minuten, während sieben kleine Ordner warteten. Jetzt läuft
  aufsteigend nach Indexgröße an: die kleinen sind nach Sekunden fertig, der
  große braucht dieselbe Zeit wie vorher.
- Die Sequenznummern der eigenen Ankündigungen sprangen zwischen zwei
  Nachrichten zurück. Syncthing nimmt das an, meldet es aber als Formfehler.
  Jetzt liegt alles, was hinausgeht, über allem, was je hinausging.

**Immer lokal**

- Auch "immer lokal" legte für jede neue oder geänderte Fassung der
  Gegenstelle einen Platzhalter an: die vorhandene Datei wurde entfernt, ein
  leerer Platzhalter trat an ihre Stelle, der Inhalt kam später — oder
  nicht. Über ein Relay, das die Verbindung verlor, blieben so 47 Fotos als
  leere Platzhalter zurück; Lightroom meldete sie beim Import als
  beschädigt, und die Fassung, die vorher dort lag, war fort. Jetzt wie bei
  Syncthing: die Fassung der Gegenstelle wird vollständig in eine Nebendatei
  übertragen und erst dann an die Stelle der alten gesetzt. Bis dahin bleibt
  die alte Datei unversehrt, und eine neue erscheint erst, wenn sie ganz da
  ist. Platzhalter gibt es nur noch bei "bei Bedarf".
- Vor dem Ersetzen wird geprüft, ob die Datei inzwischen hier geschrieben
  wurde. Dann wird sie nicht überschrieben, sondern angekündigt, und der
  Vergleich der Fassungen entscheidet beim nächsten Mal.
- Nach dem ersten Herunterladen stand die Zeile auf "in sync", auch wenn
  Dateien fehlten ("45 von 47 geholt, 2 fehlen weiterhin", 7409 gegen 7407).
  Jetzt misst der nächste Durchgang den Rückstand; fertig ist erst, was er
  bestätigt.
- Ein gescheiterter Abruf wurde erst beim nächsten Durchgang über den Ordner
  erneut versucht. Jetzt bleibt er vorgemerkt und wird nach einer Minute
  wiederholt.

**Übertragung**

- Die Frist für eine Blockanfrage (zwei Minuten) galt je Anfrage. Die
  Gegenstelle beantwortet der Reihe nach; standen 45 Dateien in der
  Schlange, kam die Antwort auf die letzte über das Relay erst nach drei
  Minuten, und zwei Dateien blieben mit "keine Antwort in 120 s" liegen,
  während 500 MB ankamen. Jetzt gilt die Frist für die Leitung: sie läuft
  ab, wenn zwei Minuten lang gar keine Antwort mehr hereinkommt.

**Fehlerdatei**

- Die Fehlerdatei füllte sich mit Aufruflisten, die keinen Fehler des
  Programms beschrieben: 163-mal "The connection timed out from inactivity"
  von QUIC-Verbindungsversuchen, die nach 15 Sekunden aufgegeben waren, dazu
  abgebrochene TLS-Lesevorgänge vom Relay. Eine aufgegebene Aufgabe lief
  weiter, scheiterte später, und niemand nahm das Ergebnis entgegen. Jetzt
  bekommt jede befristete Aufgabe einen Nachläufer, der es entgegennimmt —
  und eine doch noch zustande gekommene QUIC-Verbindung schließt, statt sie
  offen liegenzulassen.

**Eigener Index**

- Bei jeder neuen Verbindung ging der ganze eigene Bestand hinaus, in einer
  einzigen Nachricht — bei PRI mit einer Million Blöcken rund 40 MB. Über
  einen Relay, der alle paar Minuten die Verbindung verlor, kam sie nie an
  ihr Ende: die Rossibox empfing die Ankündigung nicht, zeigte den Ordner
  "aktuell", und 66 neue Dateien standen hier stundenlang auf "wartet auf
  die Gegenstelle"; nach jedem Abriss begann dasselbe von vorn. Jetzt gilt,
  was die Gegenstelle in ihrer Ordnerliste über uns sagt: kennt sie unseren
  Index unter seiner Kennung bis Sequenz n, bekommt sie nur, was darüber
  liegt — wie bei Syncthing. Ein vollständiger Index geht in Stapeln hinaus,
  und ein Abriss mittendrin kostet nichts: beim nächsten Mal geht es dort
  weiter, wo die Gegenstelle stehen geblieben ist.
- Der Index geht in einem eigenen Lauf hinaus, nicht im Hintergrundlauf
  des Ordners: der übernimmt derweil Eingehendes und stößt Übertragungen
  an, statt minutenlang hinter dem Senden zu stehen.
- Kam die Ordnerliste der Gegenstelle erst nach der Wartefrist von fünf
  Sekunden — über das Relay des Telefons regelmäßig —, blieb die
  Entscheidung aus, und der Ordner ging beim nächsten Stapel vollständig
  hinaus. Jetzt fällt sie, sobald die Liste da ist.

**Tabelle**

- Eine eingerichtete Freigabe ohne Verbindung zeigte in der Pfadspalte einen
  Strich, als wäre kein Pfad festgelegt. Jetzt steht der Pfad aus der
  Konfiguration da, auch wenn die Gegenstelle nicht verbunden ist.
- "nicht verbunden" stand auch bei Ordnern, die eine Gegenstelle nur
  anbietet und die hier nicht übernommen sind — GPS Tracks von der
  Rossibox, die verbunden war. Die heißen jetzt "angeboten"; "nicht
  verbunden" bleibt den eingerichteten Freigaben ohne Verbindung.

**Protokoll**

- Ein geschlossener Übernahmedialog stand als "OperationCanceledException"
  mit Aufrufweg im Protokoll, als wäre etwas gescheitert. Ein Abbruch ist
  kein Fehler und wird nicht mehr so gemeldet.
- "setze bei Sequenz n fort" kam bei jeder erneuten Ankündigung für alle
  Ordner noch einmal — ein geöffneter und gleich wieder geschlossener
  Übernahmedialog kostete achtzehn Zeilen. Jetzt nur noch beim Verbinden.
- "Response musste n ms warten, weil über die Verbindung gerade etwas
  anderes lief" stand alle fünf Sekunden im Protokoll, solange die Rossibox
  Blöcke abrief: jede Blockantwort wartete auf ihre Vorgänger. Das ist die
  ausgelastete Leitung, kein Stau, und die Zeile "Verbindung: n
  Blockantworten gesendet" sagt es in einer. Gemeldet wird nur noch, was
  hinter etwas anderem steht oder von der Gegenstelle langsam abgenommen
  wird.
- Das Protokoll wird zusätzlich als Datei geschrieben: `protokoll.log`
  neben der Konfiguration, je Start eine neue; die vorige bleibt als
  `protokoll.1.log` stehen.
- Eine Löschung wurde immer als Datei angekündigt, auch für ein Verzeichnis.
  Syncthing verwirft das ("encountered directory when trying to remove
  file/symlink"), und der Ordner stand bei der Rossibox dauerhaft auf "nicht
  synchronisiert" — SyncAll wegen `.sync`, BackgroundSwitcher wegen
  `Current`. Jetzt trägt die Löschung den Typ des Eintrags, den sie löscht.

**Mehrere Gegenstellen an einem Ordner**

- Eine aus der Freigabe genommene Gegenstelle verlor nur ihre Verbindung;
  ihre Ankündigungen blieben im Index und zählten weiter — im Rückstand, in
  der Spalte der Gegenstelle, bei "vollständig lokal" als Dateien, die zu
  holen sind, und in "1 von 2" samt Knotendialog. Gemessen an einem Ordner,
  bei dem die Rossibox seit Tagen abgewählt war: 83 Einträge von ihr, das
  Telefon führte vier. Jetzt gilt nur, was von einer beteiligten
  Gegenstelle stammt; beim Abwählen und beim Öffnen wird verworfen, was
  nicht dazugehört.


## 0.9.4 — 2026-09-11

Diese Fassung macht den Client außerhalb des eigenen Netzes brauchbar und
schließt drei Lücken, durch die Gegenstellen einen falschen Stand von uns
bekamen — einmal mit gelöschten Dateien, die zurückkamen.

**Verbindungen außerhalb des eigenen Netzes**

- Neu: Verbindungen über einen Relay, in beide Richtungen. Der Schalter in
  den Einstellungen stand schon da, dahinter lag aber nichts — eine
  Gegenstelle, die nur über einen Relay zu erreichen war, wurde übergangen.
  Aus einem fremden Netz blieb von fünf Adressen der Gegenstelle damit keine
  brauchbare übrig. Jetzt wird ein Relay genommen, sobald kein direkter Weg
  zustande kommt, und der Client meldet sich selbst an einem öffentlichen
  Relay an, damit Gegenstellen ihn darüber erreichen. Die Adresse steht in
  der Erkennung.
- Neu: QUIC (UDP 22000), ausgehend und eingehend. Auch dieser Schalter war
  bisher ohne Wirkung.
- Die Adressen einer Gegenstelle wurden nacheinander versucht, jede mit
  zehn Sekunden Frist. Bei neunzehn Adressen — Telefon mit mehreren Netzen
  — dauerte das Minuten, und der Relay kam als letzter dran. Jetzt werden
  alle direkten Adressen gleichzeitig versucht, danach alle Relays.
- Eine Gegenstelle, bei der ein Versuch mit einem Fehler endete, wurde nie
  wieder versucht; nur "getrennt" kam in den Wiederverbinder. Beide Zustände
  werden jetzt wieder aufgenommen, je Gegenstelle mit wachsendem Abstand
  von fünfzehn Sekunden bis fünf Minuten. Ein Netzwechsel setzt die Abstände
  zurück.
- Eine Verbindung galt nach drei Minuten ohne Empfang als tot. Syncthing
  setzt fünf Minuten an, und ein Telefon im Hintergrund schweigt länger als
  drei: die Verbindung zum Telefon riss alle fünf bis sechs Minuten ab, und
  das Protokoll schrieb es der Gegenstelle zu. Jetzt fünf Minuten, die
  Leitung wird dann sofort geschlossen, und die Zeile nennt den Grund.
- Führt die Gegenstelle nach einem Abriss noch die vorige Verbindung, gilt
  eine neue für sie zunächst als Zweitleitung: sie schickt eine Ordnerliste
  ohne Ordner. Die wurde wörtlich genommen — "bietet nichts mehr an" für
  jede Freigabe, und eine eigene Ordnerliste ohne Kenntnis, worauf die
  Gegenstelle ihren ganzen Index von vorn schickte. Eine Zweitleitung wird
  jetzt als solche erkannt, und ohne Ordnerliste gilt, was von der
  Gegenstelle gespeichert ist. In diesem Zustand wird außerdem alle dreißig
  Sekunden nachgefasst statt mit wachsendem Abstand.
- Der Wiederverbinder nahm sich eine Gegenstelle nach der anderen vor und
  wartete dabei, bis deren Freigaben angelaufen waren — über einen Relay
  zehn Minuten. So lange blieb jede andere Gegenstelle getrennt. Jetzt
  laufen die Versuche nebeneinander.
- Drei Verzeichnisse wurden bei jedem Verbinden als Konflikt gemeldet, weil
  der eigene Eintrag nach "die Version der Gegenstelle gilt" seine alte
  Fassung behielt. Er übernimmt sie jetzt.
- Solange der Index einer Freigabe noch eintrifft, stehen in den Spalten
  rechts Striche statt Nullen. Eine Null war dort keine Zahl, sondern eine
  falsche Aussage.

**Mehrere Gegenstellen an einem Ordner**

- Ein Ordner mit zwei Gegenstellen sprach nur mit der ersten. Die zweite
  bekam weder Index noch Anfragen, obwohl sie verbunden war.
- Was von einer Gegenstelle kam, erfuhr die andere nie. Sind zwei
  Gegenstellen untereinander nicht verbunden — pausiert, ausgeschaltet —,
  laufen ihre Dateien nur über uns, und wir gaben sie nicht weiter. Jetzt
  wird jede übernommene Datei mit ihrer Fassung an die übrigen Gegenstellen
  angekündigt, und dasselbe gilt für Löschungen.
- Die Einstellungen einer Freigabe und "Ordner öffnen" waren ohne
  Verbindung anklickbar und taten nichts. Beide arbeiten jetzt auf der
  Konfiguration, auch wenn kein Ordner läuft.
- Die Ansicht "Nur verbundene" zeigt jetzt die Freigaben mit mindestens
  einer Gegenstelle, unabhängig davon, ob die gerade erreichbar ist; "Nur
  nicht verbundene" die ohne Gegenstelle. Bisher hing die Ansicht an der
  Leitung: riss sie ab, war die Liste leer.

**Was die Gegenstelle von uns weiß**

- Dateien, die von einer Gegenstelle übernommen wurden, standen in keiner
  eigenen Ankündigung — über alle Freigaben hinweg rund 160 GB. Für die
  Gegenstellen sah der Client damit aus wie ein Gerät, das fast nichts hat:
  das Telefon zeigte ihn mit einem Prozent an. Diese Einträge werden jetzt
  nachgetragen, stapelweise zweitausend je Durchgang und erst, nachdem die
  Blockliste nachgerechnet ist und zum Eintrag passt. Bei großen Freigaben
  dauert das nach der Aktualisierung eine Weile; im Protokoll steht, wie
  viele noch folgen.
- Ein abgewählter Zweig zählte hier nicht als Rückstand, bei der Gegenstelle
  aber schon: sie sah Dateien, die sie führt und wir nicht, und zeigte uns
  dauerhaft mit 99 Prozent. Abgewähltes wird der Gegenstelle jetzt als
  "wird hier nicht liegen" gemeldet, so wie Syncthing es vorsieht.
- Eine Freigabe wurde nie fertig, obwohl nichts zu übertragen war. Eine
  Datei, deren Zeit sich verschoben hatte, deren Inhalt aber gleich war,
  zählte als offen; Begleitdateien von Datenbanken zählten in der
  eingehenden Richtung weiter; und eine Datenbank, die niemand mehr
  öffnete, blieb auf Dauer liegen, weil ihr Journal Inhalt hatte. Eine
  Datenbank gilt jetzt als beschäftigt, wenn sich in den letzten dreißig
  Sekunden etwas am Satz aus Datei und Journal bewegt hat — nicht, weil das
  Journal gefüllt ist. Das Journal wird nicht eingearbeitet: fremde Dateien
  werden nicht verändert.
- Eine Löschung ging sofort hinaus. Syncthing wartet sechzig Sekunden, weil
  viele Vorgänge für einen Augenblick wie eine Löschung aussehen. Jetzt
  ebenfalls sechzig Sekunden; taucht der Name in der Frist wieder auf,
  entfällt sie.

**Löschungen kamen zurück**

- Wurden Dateien aus einem Ordner herausgenommen, während ihn zwei
  Gegenstellen führen, kamen sie als Platzhalter zurück und wurden neu
  heruntergeladen. Die geltende Fassung eines Namens wurde nach "vorhanden
  vor gelöscht" gewählt, ohne den Versionsvektor zu vergleichen: sobald die
  eine Gegenstelle die Löschung bestätigt hatte und die andere noch nicht,
  gewann deren ältere Ankündigung. Und die eigene Löschung wurde beim
  Abgleich gar nicht erst verglichen. Es gilt jetzt, was den neuesten
  Versionsvektor trägt — bei Gleichstand nach denselben Regeln wie
  Syncthing —, und die eigene Löschung zählt dabei mit.
- Die Meldungen des Dateisystems kommen nicht immer in der Reihenfolge, in
  der die Dinge geschahen; eine Änderungsmeldung nach der Löschmeldung hob
  die vorgemerkte Löschung auf. Sie tut das nur noch, wenn die Datei auch
  dasteht.
- Zwischen dem Löschen einer Datei und ihrem Vermerk beim Beobachter liegen
  Millisekunden, und in diesem Fenster legte der Abgleich dieselbe Datei aus
  der Ankündigung der Gegenstelle neu an, oder eine laufende Übertragung
  schrieb sie fertig — und das galt dann als "wieder da". Von 60 von Hand
  gelöschten Dateien kamen so 32 binnen einer Sekunde zurück. Was der Client
  nach einer vorgemerkten Löschung selbst angelegt hat, nimmt er jetzt wieder
  fort; die Löschung bleibt.

**Durchgang**

- Namen, die die Bewertung mit Frist zurückgestellt hatte — eine offene
  Datenbank, eine Datei, an der noch geschrieben wird —, merkte der
  Durchgang jedes Mal neu vor und meldete sie als "neu oder geändert", über
  Stunden. Sie behalten jetzt ihre Frist.


## 0.9.3 — 2026-09-08

Diese Fassung räumt das Zusammenspiel mit Programmen auf, die auf dieselben
Dateien zugreifen, während der Abgleich läuft. Zwei der Fehler konnten Daten
kosten.

**Konfliktkopien**

- Eine lokal geänderte Datei wurde zur Konfliktkopie, sobald ein Index der
  Gegenstelle eintraf — unabhängig davon, was darin stand. Beim Verbinden
  schickt eine Gegenstelle ihren ganzen Index, also auch das, was sie von uns
  hat: der Widerhall der eigenen Ankündigung genügte. Die eben geschriebene
  Fassung wurde zur Seite gelegt und durch die ältere der Gegenstelle
  ersetzt. In einem Browserprofil sind so an einem Tag 198 Konfliktkopien
  entstanden, und die Konfigurationsdatei eines anderen Programms wurde
  mehrfach unbrauchbar. Jetzt wird die zuletzt angekündigte Version gegen die
  eingehende gehalten: kennt die Gegenstelle nichts Zusätzliches, wartet ihre
  Fassung, bis unsere Änderung angekündigt ist. Ein echter Konflikt bleibt
  ein Konflikt.
- Eine Datei, die wegen der Ruhefrist zurückgestellt wurde, griff erst der
  nächste Durchgang über den Ordner wieder auf — und der läuft stündlich. Das
  Fenster, in dem eine Änderung hier steht und nach außen noch nicht gesagt
  ist, war damit nicht zehn Sekunden lang, sondern bis zu einer Stunde. Jeder
  zurückgestellte Name trägt jetzt seine eigene Frist; fällig gewordene
  kommen im Leerlauf alle fünf Sekunden zurück in die Bewertung.

**Dateien, an denen ein anderes Programm arbeitet**

- Der Durchgang berechnete die Prüfsummen einer geänderten Datei sofort, also
  genau in dem Augenblick, in dem sie geschrieben wurde. Angekündigt wurde
  damit ein Zwischenstand, den es nie gegeben hat. Jetzt müssen zehn Sekunden
  ohne Änderung vergangen sein.
- Manche Programme arbeiten eine Datei in Stufen ab — erst das Ergebnis, das
  der Anwender sofort sehen soll, danach die Aufnahmedaten, danach die
  Bewertung. Mit einer festen Ruhefrist ging jede Stufe einzeln hinaus:
  gemessen an einem Fotoordner 667 MB für einen Bestand, der einmal hätte
  übertragen werden müssen. Die Ruhefrist gilt jetzt je Datei und wächst mit
  jeder Ankündigung, die kurz auf die vorige folgt — zehn Sekunden, zwanzig,
  vierzig, achtzig, höchstens zwei Minuten. Vier Minuten ohne Änderung setzen
  sie zurück, und die erste Ankündigung einer neuen Datei bleibt bei zehn
  Sekunden.
- Drei Lesewege öffneten fremde Dateien, ohne sie zum Schreiben freizugeben,
  und sperrten damit das Programm aus, dem die Datei gehört: das Berechnen
  beim Ankündigen, der Prüfdurchgang und der Zugriff, der eine Übertragung
  anstößt. Alle Lesewege teilen jetzt.

**Datenbanken**

- Neu: Smart-Datenbankmodus, einstellbar unter Speichermanagement, Vorgabe
  an. Eine SQLite-Datenbank ist ein Satz aus Datei und Journal, nicht eine
  Datei. Im WAL-Modus steht der neueste Stand gerade nicht in der `.db`:
  bestätigte Transaktionen liegen im Journal, bis ein Checkpoint sie
  einarbeitet. Wer die `.db` allein kopiert, überträgt einen veralteten
  Anfang, ohne dass irgendwo ein Fehler auftaucht. Übertragen wird deshalb
  nur, wenn das Journal leer ist — und das in beide Richtungen, denn sonst
  hätte die Gegenstelle stets die jüngere Fassung, und jede ihrer Änderungen
  legte die lokale Datei als Konfliktkopie zur Seite.
- Die Begleitdateien `-shm`, `-wal` und `-journal` werden weder angekündigt
  noch entgegengenommen. Sie sind nur zusammen mit genau dieser `.db` im
  selben Augenblick sinnvoll, und über zwei Dateien hinweg atomar zu
  übertragen kann das Protokoll nicht.

**Protokoll und Rückstand**

- Ein Programm, das seine Datenbank im Sekundentakt öffnet und schließt, ließ
  ihre Begleitdateien fortwährend entstehen und vergehen. Das Protokoll bekam
  davon rund sechzig Zeilen je Sekunde, über Minuten hinweg. Begleitdateien
  werden jetzt gar nicht erst vermerkt, und jede Meldung fällt je Name nur
  noch einmal.
- Dieselben Dateien standen dauerhaft im Rückstand, mit dem Grund "noch nicht
  angekündigt" — eine Übertragung, die nie kommt. Sie zählen jetzt nicht mehr
  als Rückstand, werden aber mit eigener Zahl genannt.
- Fordert eine Gegenstelle eine Datei an, deren Inhalt nicht mehr zur
  Ankündigung passt, wird jeder Block abgelehnt — bei fünfundzwanzig Megabyte
  über zweihundert Stück, und jeder stand als eigene Zeile im Protokoll.
  Jetzt steht die erste Ablehnung je Datei und Grund dort, und am Ende die
  Zahl.
- Der Grund dieser Ablehnung war außerdem irreführend. Er sprach von den
  Bytes; richtig ist, dass unsere Ankündigung veraltet ist. Die Datei wird
  bei einer solchen Ablehnung jetzt sofort zur erneuten Bewertung vorgemerkt,
  statt bis zum nächsten Durchgang zu warten und die Ablehnungen zu
  wiederholen.
- Eine zurückgestellte Datei nennt im Rückstand ihren Grund und die
  verbleibende Zeit. Ein aufgeschobener Name soll nicht aussehen wie ein
  hängender.

**Auslieferung**

- Der Installer trug eine Fassung zu wenig in sich: übersetzt wurde mit der
  alten Zahl, benannt mit der neuen. Für die Prüfung auf eine neue Fassung
  war das eine Schleife ohne Ende — ein installiertes 0.9.2 meldete sich als
  0.9.1, hielt 0.9.2 für neuer, installierte es und meldete weiter 0.9.1.
  Maßgeblich ist jetzt, was in der übersetzten Anwendung steht.
- Die Bildschirmfotos in den README tragen Breite und Höhe, damit die Seite
  beim Laden nicht springt.


## 0.9.2 — 2026-09-05

**Prüfung auf eine neue Fassung**

- Neu, einstellbar unter "Start und Fenster": nie, bei jedem Programmstart,
  wöchentlich oder monatlich. Abgefragt wird eine einzige Angabe — welche
  Freigabe auf GitHub die neueste ist. Liegt eine neuere vor, erscheint ein
  Hinweis über der Werkzeugleiste mit einem Verweis auf die Seite, auf der
  sie liegt.
- Heruntergeladen oder ausgeführt wird ausdrücklich nichts. Das Programm
  trägt keine Signatur und könnte deshalb gar nicht prüfen, ob eine geladene
  Datei vom Urheber stammt.
- Ein Fehlschlag bleibt stumm: kein Netz, GitHub nicht erreichbar — danach
  hat niemand gefragt, also kommt auch keine Meldung. Weggeklickt gilt für
  die genannte Fassung, eine noch neuere meldet sich wieder.

**Oberfläche**

- Der Kopf der Einstellungen nennt Symbol, Name, Fassung,
  Erstellungszeitpunkt und die Adresse des Quelltextes. Vorher stand die
  Fassung nur in den Eigenschaften der Datei.
- Die Grenzen je Datenträger stehen jetzt im Fenster der
  Platzhalter-Verwaltung, das ohnehin auf ein Laufwerk bezogen ist, statt als
  Liste über alle Laufwerke in den Einstellungen.
- Der Abschnitt zu den Datenträgern blieb zeitweise ganz leer: ein Fehlschlag
  beim Ermitteln der Verdrängungskandidaten riss die ganze Aufzählung mit. Er
  gilt jetzt je Zwischenspeicher und kostet nur dessen Kandidaten.
- Das Fenster der Platzhalter-Verwaltung rechnete beim Öffnen und stand dabei
  still. Beide Berechnungen laufen jetzt im Hintergrund.
- Der Knopf "Erweiterung neu erzeugen" ist entfernt. Er rief den Übersetzer
  auf einer Projektdatei auf, die es in einer Installation nicht gibt.

**Installer**

- Der Installationsort ist jetzt eine Wahl: für alle Benutzer nach
  `C:\Program Files` mit Administratorrechten, oder wie bisher nur für den
  angemeldeten Benutzer. Beides trägt, weil die Daten unter
  `%LOCALAPPDATA%\SyncTClient` liegen und nicht beim Programm.
- Vor dem ersten Schreibzugriff stehen die Nutzungsbedingungen und müssen
  bestätigt werden — auf Deutsch oder Englisch, je nach gewählter Sprache.
- Das Programmsymbol steht auf der ersten und letzten Seite des Assistenten
  und klein auf allen dazwischen.

**Dokumentation**

- README.md ist jetzt englisch, README.de.md deutsch. Beide verweisen
  aufeinander und sind Abschnitt für Abschnitt gleich gegliedert.
- Was Microsoft Defender SmartScreen beim Start des Installers meldet und
  warum, steht jetzt in beiden README: es gibt kein Signaturzertifikat, und
  SmartScreen urteilt zusätzlich nach Bekanntheit, die eine neue Software
  nicht haben kann. Der Weg weiter und der Vergleich der Prüfsumme stehen
  dabei.
- Der Abschnitt zur Platzhalter-Schwelle nennt jetzt, was gezählt wird und
  welche Bedingungen zusätzlich gelten, bevor Inhalt verworfen wird. Das ist
  der eine Vorgang hier, bei dem Daten verloren gehen können.
- Zwei Bildschirmfotos, das Programmsymbol neben der Überschrift und der
  Haftungsausschluss ganz oben.
- Aus dem Quelltext sind Angaben entfernt, die dort nichts zu suchen haben:
  Adresse und Geräte-ID einer fremden Gegenstelle in den Startprofilen, die
  Adresse des hiesigen Netzes in beiden README.

**Werkzeuge**

- Scheitert das Veröffentlichen, weil ein Dateimanager die
  Explorer-Erweiterung hält, steht jetzt eine Meldung da, die sagt, was zu
  tun ist. Visual Studio meldete bisher "Die Ursache des Fehlers konnte nicht
  ermittelt werden", während sie zehn Zeilen weiter oben stand.
- `tools\Veroeffentlichen.cmd` lässt sich anklicken. Windows verknüpft `.ps1`
  nicht mit PowerShell, und die Ausführungsrichtlinie steht auf Restricted.
- Diese Datei gibt es seit dieser Fassung.


## 0.9.1 — 2026-09-05

Erste veröffentlichte Fassung.

**Übertragung**

- Block Exchange Protocol in C#: Rahmung, Hello, Geräte-ID, Index, blockweiser
  Abruf, LZ4. Gegenstelle ist ein unverändertes Syncthing v2
- Eigene TLS-Schicht, weil Windows Ed25519 nicht beherrscht
- Beide Richtungen: Freigaben annehmen und eigene Ordner anbieten. Index und
  IndexUpdate gehen hinaus, eingehende Verbindungen werden angenommen
- Erkennung im eigenen Netz und über Erkennungsserver; Gegenstellen mit
  dynamischer Adresse werden gefunden
- Eine Verbindung je Gegenstelle für alle ihre Ordner, mit Wiederaufnahme nach
  einem Abriss
- Index in SQLite mit Wiederaufnahme: beim Neustart kommen nur Änderungen
- Konflikte nach Syncthings Muster, mit Gerätenamen statt Kurzkennung
- Ersetzte und gelöschte Fassungen unter `.stversions`, Aufbewahrung
  einstellbar, wahlweise über den Papierkorb
- Ausschlussmuster je Freigabe

**Platzhalter**

- Platzhalter im Explorer über die Cloud Filter API; der Inhalt wird
  übertragen, wenn jemand die Datei öffnet
- Überlagerungssymbole über den Anheft-Zustand
- Ein Modus je Datei und je Ordner, im Index geführt: Platzhalter oder immer
  lokal, mit Vererbung nach unten
- Cache-Limit je Datenträger, Verdrängung nach letztem Zugriff
- Verdrängung nur gegen Beweis: eine Kopie wird erst freigegeben, wenn genügend
  Gegenstellen sie vollständig im Index führen
- Lokal geänderte Dateien werden nicht verdrängt
- Der stündliche Durchgang gleicht die Anheft-Merkmale im Dateisystem mit der
  Datenbank ab

**Im Dateimanager**

- Kontextmenü mit vier Einträgen: immer behalten, Speicherplatz freigeben,
  Ordner ausblenden, als Freigabe anbieten. Sie zeigen an, was gerade gilt, und
  gelten für eine Mehrfachauswahl
- Vorschaubilder auf Zuruf: der Client überträgt den Kopf der Datei — einen
  Block von 128 KiB — und schneidet die eingebettete EXIF-Vorschau heraus. Der
  Platzhalter bleibt dabei stehen

**Oberfläche**

- Freigaben verwalten, angebotene Ordner übernehmen, Bindungen lösen,
  Teilbaum-Auswahl, Ansichtsfilter
- Platzhalter-Verwaltung je Datenträger als Baum, über alle Freigaben hinweg
- Übertragungen mit Fortschritt, Durchsatzdiagramm, Rückstand in beide
  Richtungen
- Protokollfenster, Symbol im Infobereich mit Zustandsplakette
- Deutsch und Englisch, helles und dunkles Thema
- Tagessicherung der Konfiguration samt Gerätezertifikat

**Auslieferung**

- Installer ohne Administratorrechte, mit Nutzungsbedingungen, die bestätigt
  werden müssen
