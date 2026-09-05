<#
.SYNOPSIS
    Macht aus dem Veroeffentlichungsverzeichnis einen Installer und legt ihn
    als Freigabe auf GitHub ab.

.DESCRIPTION
    Der Ablauf von Hand war: in Visual Studio veroeffentlichen, das Verzeichnis
    zusammenpacken, einen Installer bauen, ihn hochladen, verlinken und die
    Versionsnummer nachziehen. Dieses Werkzeug macht alles ausser dem ersten
    Schritt.

    Vorausgesetzt wird:
      * Visual Studio hat nach BIN veroeffentlicht (Profil FolderProfile)
      * Inno Setup liegt auf dem Rechner
      * gh ist eingerichtet:  winget install GitHub.cli  und  gh auth login

    Der Quelltext geht mit. Das Original bleibt "origin" -- von GitHub wird
    nie gezogen, nur geschoben. Damit kann von dort nichts in den
    Arbeitsordner gelangen, gleich was jemand dort anstellt.

    Der Name des Verzeichnisses steht in tools\veroeffentlichung.json oder
    wird mit -Repo uebergeben.

.PARAMETER Fassung
    Die Fassung, etwa 1.2.0. Ohne Angabe wird die letzte Stelle der Fassung
    aus Directory.Build.props um eins erhoeht.

.PARAMETER Hinweise
    Was in der Freigabe steht. Ohne Angabe entstehen sie aus den Commits seit
    dem letzten Etikett.

.PARAMETER Repo
    Das Verzeichnis auf GitHub, etwa "dirkmertens/SyncTClient".

.PARAMETER Entwurf
    Die Freigabe wird als Entwurf angelegt und nicht veroeffentlicht.

.PARAMETER NurPaket
    Nur den Installer bauen, nichts hochladen. Fuer den Blick darauf, bevor
    etwas nach draussen geht.

.PARAMETER GitHubUeberschreiben
    Ersetzt den Verlauf auf GitHub durch den hiesigen, statt ihn
    fortzuschreiben. Noetig genau einmal: wenn das Verzeichnis dort
    angelegt wurde, bevor es diesen Quelltext gab, und die beiden
    Verlaeufe nichts gemeinsam haben. Was auf GitHub steht und hier
    fehlt, ist danach verloren.

.EXAMPLE
    .\tools\Veroeffentlichen.ps1
    .\tools\Veroeffentlichen.ps1 -Fassung 1.0.0 -Hinweise "Erste oeffentliche Fassung."
    .\tools\Veroeffentlichen.ps1 -NurPaket
#>

[CmdletBinding()]
param(
    [string] $Fassung,
    [string] $Hinweise,
    [string] $Repo,
    [switch] $Entwurf,
    [switch] $NurPaket,
    [switch] $GitHubUeberschreiben
)

$ErrorActionPreference = 'Stop'

$Wurzel      = Split-Path -Parent $PSScriptRoot
$BinVerz     = Join-Path $Wurzel 'BIN'
$DistVerz    = Join-Path $Wurzel 'dist'
$Skript      = Join-Path $Wurzel 'setup\SyncTClient.iss'
$PropsDatei  = Join-Path $Wurzel 'Directory.Build.props'
$ChangeDatei = Join-Path $Wurzel 'CHANGELOG.md'
$Einstellung = Join-Path $PSScriptRoot 'veroeffentlichung.json'

function Schritt($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Abbruch($text) { Write-Host "!!  $text" -ForegroundColor Red; exit 1 }

# Windows PowerShell macht aus jeder Zeile, die ein Programm nach stderr
# schreibt, einen Fehlersatz, sobald stderr umgeleitet wird. Bei
# $ErrorActionPreference = 'Stop' bricht das Werkzeug daran ab -- auch
# wenn das Programm nur "nicht gefunden" gemeldet hat und genau das die
# Antwort war, auf die es ankommt.
#
# Aufrufe, deren Fehlschlag vorgesehen ist, laufen deshalb hierueber.
# Zurueck kommt allein der Rueckgabewert.
function Stumm {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Befehl)

    $vorher = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Befehl[0] @($Befehl[1..($Befehl.Count - 1)]) 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $vorher }
}

# ---------------------------------------------------------------- Vorbedingungen

# Das Veroeffentlichungsverzeichnis. Ohne die drei Dateien ist es keines:
# die Anwendung, der Dienst und die Shell-Erweiterung.
Schritt 'Veroeffentlichungsverzeichnis pruefen'

foreach ($noetig in 'SyncTClient.exe', 'synctmount.dll', 'synctexplorer.dll') {
    $pfad = Join-Path $BinVerz $noetig
    if (-not (Test-Path -LiteralPath $pfad)) {
        Abbruch "$noetig fehlt in $BinVerz. In Visual Studio veroeffentlichen (Profil FolderProfile), dann erneut."
    }
}

# Ist das Verzeichnis aelter als der Quelltext, ist es der Stand von gestern.
# Das faellt sonst erst auf, wenn jemand die Freigabe herunterlaedt.
$juengsterQuelltext = Get-ChildItem (Join-Path $Wurzel 'src') -Recurse -File -Include *.cs, *.xaml -EA SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$anwendung = Get-Item (Join-Path $BinVerz 'SyncTClient.exe')

if ($juengsterQuelltext -and $juengsterQuelltext.LastWriteTime -gt $anwendung.LastWriteTime) {
    Write-Host ("!!  BIN ist aelter als der Quelltext: {0:dd.MM. HH:mm} gegen {1:dd.MM. HH:mm} ({2})." -f `
        $anwendung.LastWriteTime, $juengsterQuelltext.LastWriteTime, $juengsterQuelltext.Name) -ForegroundColor Yellow

    if ((Read-Host 'Trotzdem weiter? (j/N)') -ne 'j') { exit 1 }
}

# Inno Setup. Die Fassung 6 liegt unter Programme, die 7 beim Benutzer.
Schritt 'Inno Setup suchen'

$Iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $Iscc) { Abbruch 'ISCC.exe nicht gefunden. Inno Setup installieren: winget install JRSoftware.InnoSetup' }
Write-Host "    $Iscc"

# ---------------------------------------------------------------- Fassung

Schritt 'Fassung bestimmen'

# Ausdruecklich UTF-8, in beide Richtungen.
#
# Get-Content liest in Windows PowerShell ohne Angabe in der Kodierung
# des Systems. Die Datei ist UTF-8 ohne Vorzeichen; jeder Umlaut kam als
# zwei Zeichen zurueck und wurde beim Zurueckschreiben ein zweites Mal
# kodiert. Nach einem Lauf stand in der Datei "trAcgt" statt "traegt".
$ohneVorzeichen = New-Object System.Text.UTF8Encoding($false)
$props = [System.IO.File]::ReadAllText($PropsDatei, $ohneVorzeichen)
if ($props -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
    Abbruch "In $PropsDatei steht keine Fassung."
}

$bisher = $Matches[1]

if (-not $Fassung) {
    # Die letzte Stelle um eins weiter. Wer etwas anderes will, gibt es an.
    $teile = $bisher.Split('.')
    $Fassung = '{0}.{1}.{2}' -f $teile[0], $teile[1], ([int]$teile[2] + 1)
}

if ($Fassung -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { Abbruch "Ungueltige Fassung: $Fassung" }
Write-Host "    $bisher -> $Fassung"

$etikett = "v$Fassung"
# Ein Lauf, der weiter hinten abgebrochen ist -- beim Schieben nach GitHub
# etwa -- hat Fassung, Commit und Etikett schon abgelegt. Der zweite Lauf
# soll dort weitermachen und nicht an der eigenen Vorarbeit scheitern.
$schonFestgeschrieben = $false

if ((git tag --list $etikett)) {
    if ($bisher -ne $Fassung) {
        Abbruch "Das Etikett $etikett gibt es schon, in $PropsDatei steht aber $bisher. Mit -Fassung eine andere angeben."
    }

    if ((Stumm git merge-base --is-ancestor $etikett HEAD) -ne 0) {
        Abbruch "Das Etikett $etikett zeigt nicht in den aktuellen Zweig."
    }

    $schonFestgeschrieben = $true
    Write-Host "    $etikett ist schon festgeschrieben -- es wird nur noch geschoben."
}

# ---------------------------------------------------------------- Paket

Schritt 'Installer bauen'

New-Item -ItemType Directory -Force -Path $DistVerz | Out-Null

& $Iscc "/DFassung=$Fassung" "/DQuelle=$BinVerz" "/DZiel=$DistVerz" $Skript | Out-Null
if ($LASTEXITCODE -ne 0) { Abbruch "Inno Setup brach ab (Code $LASTEXITCODE)." }

$Paket = Join-Path $DistVerz "SyncTClient-$Fassung-setup.exe"
if (-not (Test-Path -LiteralPath $Paket)) { Abbruch "Der Installer wurde nicht erzeugt: $Paket" }

$pruefsumme = (Get-FileHash -LiteralPath $Paket -Algorithm SHA256).Hash.ToLower()
$groesse = '{0:N1} MB' -f ((Get-Item $Paket).Length / 1MB)

Write-Host "    $Paket  ($groesse)"
Write-Host "    SHA256 $pruefsumme"

if ($NurPaket) { Schritt 'Nur das Paket verlangt -- nichts hochgeladen.'; exit 0 }

# ---------------------------------------------------------------- GitHub

Schritt 'GitHub pruefen'

if (-not (Get-Command gh -EA SilentlyContinue)) {
    Abbruch 'gh nicht gefunden. Einrichten: winget install GitHub.cli   danach   gh auth login'
}

if (-not $Repo) {
    if (Test-Path -LiteralPath $Einstellung) {
        $Repo = (Get-Content -LiteralPath $Einstellung -Raw | ConvertFrom-Json).Repo
    }
}

if (-not $Repo) {
    Abbruch @"
Kein GitHub-Verzeichnis angegeben. Einmalig festlegen:

    '{ "Repo": "benutzer/SyncTClient" }' | Set-Content -Encoding utf8 "$Einstellung"

oder je Aufruf mit -Repo uebergeben.
"@
}

Write-Host "    $Repo"

# ---------------------------------------------------------------- Aenderungen

Schritt 'Aenderungen zusammenstellen'

# Von welchem Etikett bis wohin. Beim ersten Lauf gibt es das neue Etikett noch
# nicht, dann zaehlt HEAD; beim zweiten Lauf nach einem Abbruch steht es schon.
$bisRef = if ($schonFestgeschrieben) { $etikett } else { 'HEAD' }
$vonRef = if ($schonFestgeschrieben) { "$etikett^" } else { 'HEAD' }

# Vor dem ersten Etikett gibt es kein vorheriges; git meldet das nach stderr.
$vorher = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$letztes = git describe --tags --abbrev=0 $vonRef 2>$null | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { $letztes = $null }
$ErrorActionPreference = $vorher

$aenderungen = if ($letztes) { git log --pretty=format:'- %s' "$letztes..$bisRef" }
               else          { git log --pretty=format:'- %s' -20 $bisRef }
$aenderungen = ($aenderungen -join "`n")

if ($letztes) { Write-Host "    $letztes -> $etikett" }
Write-Host ("    {0} Eintraege" -f ($aenderungen -split "`n").Count)

# ---------------------------------------------------------------- Fassung festschreiben

Schritt 'Fassung festschreiben und etikettieren'

if ($schonFestgeschrieben) {
    Write-Host "    Fassung, Commit und Etikett liegen schon vor."
}
else {
    $neueProps = $props -replace '<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>', "<Version>$Fassung</Version>"
    [System.IO.File]::WriteAllText($PropsDatei, $neueProps, $ohneVorzeichen)

    # Der neue Abschnitt ganz oben, unter der Marke. Ohne Umweg ueber einen
    # regulaeren Ausdruck: in Commit-Betreffen steht "$" und "$1" durchaus,
    # und -replace wuerde das als Rueckverweis lesen.
    $changelog = [System.IO.File]::ReadAllText($ChangeDatei, $ohneVorzeichen)
    $marke = '<!-- Neue Fassungen fügt tools/Veroeffentlichen.ps1 unter dieser Zeile ein. -->'

    if (-not $changelog.Contains($marke)) { Abbruch "In $ChangeDatei fehlt die Marke fuer neue Fassungen." }

    $zeilenende = if ($changelog.Contains("`r`n")) { "`r`n" } else { "`n" }
    $abschnitt  = "## $Fassung -- $(Get-Date -Format 'yyyy-MM-dd')" +
                  $zeilenende + $zeilenende +
                  ($aenderungen -replace "`n", $zeilenende) + $zeilenende

    $changelog = $changelog.Replace($marke, $marke + $zeilenende + $zeilenende + $abschnitt)
    [System.IO.File]::WriteAllText($ChangeDatei, $changelog, $ohneVorzeichen)

    git add $PropsDatei $ChangeDatei
    git commit -m "Fassung $Fassung" | Out-Null
    git tag -a $etikett -m "SyncTClient $Fassung"
}

# Zuerst auf die eigene Gegenstelle. Sie ist das Original.
git push
git push origin $etikett

# Und dann derselbe Stand nach GitHub.
#
# Eine eigene Gegenstelle mit eigenem Namen, damit "git push" ohne Zusatz
# weiterhin nur an "origin" geht. Gezogen wird von GitHub nie: was dort
# jemand anstellt -- eine Abspaltung, eine Aenderungsanfrage -- bleibt dort
# und kommt nie in diesen Arbeitsordner.
Schritt 'Quelltext nach GitHub schieben'

if (-not (git remote | Where-Object { $_ -eq 'github' })) {
    git remote add github "https://github.com/$Repo.git"
    Write-Host "    Gegenstelle 'github' angelegt: $Repo"
}

$zweig = (git rev-parse --abbrev-ref HEAD).Trim()

if ($GitHubUeberschreiben) { git push --force github $zweig }
else                       { git push         github $zweig }

if ($LASTEXITCODE -ne 0) {
    Abbruch @"
Der Quelltext liess sich nicht nach GitHub schieben.

Auf GitHub liegt ein Verlauf, der hier nicht vorkommt -- meist, weil das
Verzeichnis dort mit README und Lizenz angelegt wurde. Von GitHub wird
grundsaetzlich nicht gezogen: nichts von dort soll je in diesen
Arbeitsordner gelangen.

Steht der Inhalt von dort hier ohnehin schon, dann ersetzt

    .\tools\Veroeffentlichen.ps1 -Fassung $Fassung -GitHubUeberschreiben

den Verlauf auf GitHub durch diesen. Was dort steht und hier fehlt, ist
danach verloren.
"@
}

if ($GitHubUeberschreiben) { git push --force github $etikett }
else                       { git push         github $etikett }
if ($LASTEXITCODE -ne 0) { Abbruch 'Das Etikett liess sich nicht nach GitHub schieben.' }

# ---------------------------------------------------------------- Freigabe

Schritt 'Freigabe anlegen'

# gh legt keine zweite Freigabe unter demselben Etikett an. Das vorher zu
# sagen ist verstaendlicher als der Fehler, der sonst kommt.
if ((Stumm gh release view $etikett --repo $Repo) -eq 0) {
    Abbruch "Auf GitHub gibt es die Freigabe $etikett schon. Loeschen mit: gh release delete $etikett --repo $Repo"
}

# Derselbe Text, der im Changelog steht. Zwei Quellen fuer dieselbe Liste
# waeren zwei Gelegenheiten, auseinanderzulaufen.
if (-not $Hinweise) { $Hinweise = $aenderungen }

$text = @"
$Hinweise

---

**Installer:** ``SyncTClient-$Fassung-setup.exe`` ($groesse)
**SHA256:** ``$pruefsumme``

Die Installation braucht keine Administratorrechte. Die Einbindung in den
Explorer -- Vorschaubilder und Kontextmenü -- trägt das Programm beim ersten
Start selbst ein.
"@

$textDatei = Join-Path $env:TEMP "synct-freigabe-$Fassung.md"
# Ohne Vorzeichen. Set-Content -Encoding utf8 setzt in Windows
# PowerShell eines, und gh reicht es unveraendert weiter: in der
# Freigabe stand es dann als sichtbares Zeichen vor der ersten Zeile.
[System.IO.File]::WriteAllText($textDatei, $text, $ohneVorzeichen)

$argumente = @(
    'release', 'create', $etikett, $Paket,
    '--repo', $Repo,
    '--title', "SyncTClient $Fassung",
    '--notes-file', $textDatei
)

if ($Entwurf) { $argumente += '--draft' }

gh @argumente
if ($LASTEXITCODE -ne 0) { Abbruch "gh brach ab (Code $LASTEXITCODE)." }

Remove-Item -LiteralPath $textDatei -EA SilentlyContinue

Schritt "Fertig: SyncTClient $Fassung"
