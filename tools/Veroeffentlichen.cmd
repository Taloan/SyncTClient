@echo off
rem ---------------------------------------------------------------------------
rem  Veroeffentlichen: Installer bauen und als Freigabe auf GitHub ablegen.
rem
rem  Vorher in Visual Studio veroeffentlichen (Profil FolderProfile) -- diese
rem  Datei baut nur, was in BIN liegt.
rem
rem  Doppelklick genuegt. Ohne Angabe zaehlt die letzte Stelle der Fassung um
rem  eins weiter; alles, was hier zusaetzlich angegeben wird, geht an das
rem  Skript durch:
rem
rem      Veroeffentlichen.cmd -NurPaket
rem      Veroeffentlichen.cmd -Fassung 1.0.0 -Hinweise "Erste Fassung."
rem      Veroeffentlichen.cmd -Entwurf
rem
rem  Warum der Umweg ueber diese Datei: Windows verknuepft .ps1 nicht mit
rem  PowerShell, sondern mit dem Editor -- ein Doppelklick auf das Skript
rem  wuerde es nur anzeigen. Und die Ausfuehrungsrichtlinie steht auf diesem
rem  Rechner auf Restricted, weshalb -ExecutionPolicy Bypass dazugehoert.
rem ---------------------------------------------------------------------------

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Veroeffentlichen.ps1" %*
set FEHLER=%ERRORLEVEL%

rem Bei einem Doppelklick schliesst sich das Fenster sofort und niemand liest,
rem was passiert ist. Aus einem offenen Fenster gestartet, stoert ein Halt --
rem deshalb nur im ersten Fall. Die Befehlszeile von cmd nennt beim Doppelklick
rem den Namen dieser Datei, sonst nicht.
echo %cmdcmdline% | find /i "%~nx0" >nul
if not errorlevel 1 (
    echo.
    pause
)

exit /b %FEHLER%
