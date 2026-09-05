# Regeln für die Arbeit an SyncTClient

## Sprache: ausschließlich Fachbegriffe

SyncTClient ist ein Programm zur Dateiübertragung. Geschrieben wird deshalb
ausschließlich mit den Begriffen der **Dateiübertragung, der Netzwerktechnik
und der Computertechnik**.

Das gilt für alles: Protokollzeilen, Texte in der Oberfläche, Fehlermeldungen,
Quelltextkommentare, Commit-Nachrichten und die Unterhaltung über das Programm.

Keine Umschreibungen aus der Alltagssprache, keine Metaphern, keine Bildsprache,
kein Vokabular aus fremden Themengebieten. Ein Zustand, den das Programm
technisch genau kennt, wird auch technisch genau benannt.

| nicht | sondern |
|---|---|
| wird geholt, wird weggebracht | Übertragung läuft, wird übertragen |
| ohne Inhalt | Platzhalter |
| die Datei ist unterwegs, liegt auf der Leitung | Übertragung läuft |
| die Bytes liegen bei der Gegenstelle | der Inhalt ist nicht übertragen |
| aufräumen, wegräumen | löschen, verwerfen, freigeben |
| die Gegenstelle redet noch | die Gegenstelle sendet noch Indexdaten |
| der Balken hängt fest | die Phase wird nicht zurückgesetzt |

Der Grund ist kein Geschmack: eine Umschreibung beschreibt einen anderen
Zustand als den gemeinten. „Ohne Inhalt" stand einmal für einen Platzhalter und
las sich als „die Datei ist da, aber leer" — das Gegenteil dessen, was gemeint
war, und bei einer 11-MB-Datei klang es nach Datenverlust.

Wer einen Zustand benennt, prüft vorher, wie das Programm ihn intern nennt:
Platzhalter, Index, Sequenz, Blockliste, Sync-Root, Gegenstelle, Rückstand,
Durchgang. Diese Begriffe sind gesetzt und werden nicht durch eigene ersetzt.

## Das Protokoll ist für Anwender

Das Protokollfenster liest der Anwender, nicht der Entwickler. Es enthält
deshalb keine Bezeichner aus Programmierschnittstellen.

`Hydration` und `Dehydration` heißen die Vorgänge in der Cloud Filter API von
Windows (`CfHydratePlaceholder`, `CF_HYDRATION_POLICY`). Im Quelltext, in
Kommentaren und in Bezeichnern bleiben sie stehen — dort sind sie die Namen der
Schnittstelle und dürfen nicht übersetzt werden. Im Protokoll und in der
Oberfläche stehen sie nicht:

| Schnittstelle | Protokoll |
|---|---|
| Hydration | Inhalt angefordert |
| Hydration fehlgeschlagen | Inhalt konnte nicht bereitgestellt werden |
| dehydriert | Platzhalter |

Dasselbe gilt für jeden weiteren Namen aus einer Schnittstelle: `CfExecute`,
`CF_CALLBACK_TYPE_FETCH_DATA`, Statuswerte wie `0x80070020`. Im Protokoll steht,
was geschehen ist, nicht wie die Funktion heißt, die es gemeldet hat. Ein
Fehlercode darf dabeistehen, wenn er beim Nachsehen hilft — aber nie allein.

## Fertig heißt fertig

Eine Aufgabe wird zu Ende gebaut. Keine Rückfrage „soll ich weitermachen?"
mitten in einer Sache, die erkennbar noch nicht fertig ist, und keine
Aufforderung, zwischendurch etwas auszuprobieren.

Gefragt wird nur, wenn zwei Wege zu verschiedenen Programmen führen und die
Entscheidung nicht aus der Aufgabe hervorgeht. Alles andere wird entschieden,
gebaut, übersetzt und am Ende in einem Stück berichtet — mit den getroffenen
Annahmen, falls welche nötig waren.

Der Grund: wer die Aufgabe gestellt hat, geht vom Rechner weg und erwartet ein
Ergebnis. Eine Rückfrage nach halbem Weg kostet die ganze Zeit dazwischen, und
gelesen wird sie erst Stunden später.
