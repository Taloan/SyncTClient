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
| ohne Inhalt | Platzhalter, nicht hydriert |
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
Platzhalter, Hydration, Dehydration, Index, Sequenz, Blockliste, Sync-Root,
Gegenstelle, Rückstand, Durchgang. Diese Begriffe sind gesetzt und werden nicht
durch eigene ersetzt.
