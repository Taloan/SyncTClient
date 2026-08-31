# Erzeugt SyncTClient.ico aus einer PNG-Vorlage mit durchsichtigem Grund.
#
# Warum ein Skript und kein einmaliger Handgriff: das Symbol muss sich
# nachvollziehbar neu erzeugen lassen, wenn die Vorlage sich aendert.
#
# Alle Groessen werden unkomprimiert abgelegt (BITMAPINFOHEADER, Bilddaten von
# unten nach oben, danach die AND-Maske). PNG-komprimierte Eintraege verstehen
# zwar Explorer und Taskleiste, aber GDI+ nicht -- und genau ueber GDI+ holt
# sich das Zeichen im Infobereich sein Bild aus der Programmdatei. Mit
# PNG-Eintraegen stuende dort Farbmuell.

param(
    [string] $Vorlage = "$PSScriptRoot\..\src\SyncTClient.Gui\stc2.png",
    [string] $Ziel    = "$PSScriptRoot\..\src\SyncTClient.Gui\SyncTClient.ico",
    [int[]]  $Groessen = @(16, 24, 32, 48, 64, 128, 256),
    [double] $Rand    = 0.04
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

# --- Vorlage laden und auf ihren sichtbaren Teil zuschneiden ----------------

$quelle = New-Object System.Drawing.Bitmap ([System.IO.Path]::GetFullPath($Vorlage))

$minX = $quelle.Width; $maxX = 0; $minY = $quelle.Height; $maxY = 0
for ($y = 0; $y -lt $quelle.Height; $y += 2) {
    for ($x = 0; $x -lt $quelle.Width; $x += 2) {
        if ($quelle.GetPixel($x, $y).A -gt 24) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

$breite = $maxX - $minX + 1
$hoehe  = $maxY - $minY + 1

# Auf ein Quadrat legen, mittig, mit etwas Luft am Rand. Ein Symbol, das die
# Flaeche ganz ausfuellt, stoesst in Listen an seine Nachbarn.
$seite = [int]([Math]::Max($breite, $hoehe) * (1 + $Rand))
$motiv = New-Object System.Drawing.Bitmap($seite, $seite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($motiv)
$g.Clear([System.Drawing.Color]::Transparent)
$g.InterpolationMode = 'HighQualityBicubic'
$g.PixelOffsetMode = 'HighQuality'
$g.DrawImage(
    $quelle,
    (New-Object System.Drawing.Rectangle([int](($seite - $breite) / 2), [int](($seite - $hoehe) / 2), $breite, $hoehe)),
    $minX, $minY, $breite, $hoehe, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$quelle.Dispose()

# --- Je Groesse einen unkomprimierten Eintrag ------------------------------

function Neu-DibEintrag {
    param([System.Drawing.Bitmap] $Bild)

    $s = $Bild.Width
    $strom = New-Object System.IO.MemoryStream
    $schreiber = New-Object System.IO.BinaryWriter($strom)

    # BITMAPINFOHEADER. Die doppelte Hoehe ist Vorschrift: Bild und Maske
    # stehen untereinander in derselben Flaeche.
    $schreiber.Write([uint32] 40)
    $schreiber.Write([int32] $s)
    $schreiber.Write([int32] ($s * 2))
    $schreiber.Write([uint16] 1)
    $schreiber.Write([uint16] 32)
    $schreiber.Write([uint32] 0)
    $schreiber.Write([uint32] ($s * $s * 4))
    $schreiber.Write([int32] 0)
    $schreiber.Write([int32] 0)
    $schreiber.Write([uint32] 0)
    $schreiber.Write([uint32] 0)

    $bereich = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $daten = $Bild.LockBits($bereich, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        [byte[]] $zeile = New-Object byte[] ($s * 4)
        for ($y = $s - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($daten.Scan0, $y * $daten.Stride), $zeile, 0, $s * 4)
            $schreiber.Write($zeile, 0, $zeile.Length)
        }
    }
    finally {
        $Bild.UnlockBits($daten)
    }

    # AND-Maske, Zeilen auf vier Byte aufgefuellt. Durchgehend null: die
    # Durchsichtigkeit steckt bereits im Alphakanal.
    [byte[]] $maske = New-Object byte[] (([int][Math]::Ceiling($s / 32.0)) * 4 * $s)
    $schreiber.Write($maske, 0, $maske.Length)
    $schreiber.Flush()

    return ,[byte[]] $strom.ToArray()
}

$daten = @{}
foreach ($s in $Groessen) {
    $bild = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($bild)
    $gg.Clear([System.Drawing.Color]::Transparent)
    $gg.InterpolationMode = 'HighQualityBicubic'
    $gg.PixelOffsetMode = 'HighQuality'
    $gg.SmoothingMode = 'AntiAlias'
    $gg.DrawImage($motiv, 0, 0, $s, $s)
    $gg.Dispose()

    $daten[$s] = Neu-DibEintrag $bild
    $bild.Dispose()
}
$motiv.Dispose()

# --- Datei zusammensetzen --------------------------------------------------

$ausgabe = [System.IO.Path]::GetFullPath($Ziel)
$datei = [System.IO.File]::Create($ausgabe)
try {
    $schreiber = New-Object System.IO.BinaryWriter($datei)

    $schreiber.Write([uint16] 0)
    $schreiber.Write([uint16] 1)
    $schreiber.Write([uint16] $Groessen.Count)

    $versatz = 6 + 16 * $Groessen.Count
    foreach ($s in $Groessen) {
        [byte[]] $inhalt = $daten[$s]
        # 256 wird als 0 eingetragen; ein Byte fasst 256 nicht.
        $kennung = if ($s -ge 256) { 0 } else { $s }

        $schreiber.Write([byte] $kennung)
        $schreiber.Write([byte] $kennung)
        $schreiber.Write([byte] 0)
        $schreiber.Write([byte] 0)
        $schreiber.Write([uint16] 1)
        $schreiber.Write([uint16] 32)
        $schreiber.Write([uint32] $inhalt.Length)
        $schreiber.Write([uint32] $versatz)
        $versatz += $inhalt.Length
    }

    foreach ($s in $Groessen) {
        [byte[]] $inhalt = $daten[$s]
        $schreiber.Write($inhalt, 0, $inhalt.Length)
    }

    $schreiber.Flush()
}
finally {
    $datei.Dispose()
}

$grosse = (Get-Item $ausgabe).Length
Write-Output ("{0}: {1:N0} Bytes, {2} Groessen" -f (Split-Path $ausgabe -Leaf), $grosse, $Groessen.Count)

# --- Nachsehen, ob es auch gelesen werden kann -----------------------------

$probe = New-Object System.Drawing.Icon($ausgabe)
Write-Output ("gelesen: {0}x{1}" -f $probe.Width, $probe.Height)
$probe.Dispose()
