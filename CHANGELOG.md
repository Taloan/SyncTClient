# Änderungen

Die Einträge ab 0.9.2 entstehen beim Veröffentlichen aus den Commit-Betreffen
und sind deshalb deutsch.

*Entries from 0.9.2 onwards are generated from commit subjects at release time
and are therefore in German.*

<!-- Neue Fassungen fügt tools/Veroeffentlichen.ps1 unter dieser Zeile ein. -->

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
