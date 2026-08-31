# Erzeugt aus den PNG-Vorlagen die Symboldateien des Programms:
#
#   SyncTClient.ico   das Programmsymbol (stc2.png, ohne Plakette)
#   Status-*.ico      je ein Symbol fuer die Zustaende im Infobereich
#
# Alle Groessen werden unkomprimiert abgelegt (BITMAPINFOHEADER, Bilddaten von
# unten nach oben, danach die AND-Maske). PNG-komprimierte Eintraege verstehen
# zwar Explorer und Taskleiste, aber GDI+ nicht -- und genau ueber GDI+ holt
# sich das Zeichen im Infobereich sein Bild. Mit PNG-Eintraegen stuende dort
# Farbmuell.
#
# Jede Vorlage wird auf ihren eigenen Inhalt zugeschnitten. Ein gemeinsamer
# Ausschnitt waere naheliegend, damit das Symbol beim Zustandswechsel nicht
# springt -- er ist hier aber nicht moeglich: die Vorlagen haben verschiedene
# Leinwandgroessen (1254x1254 und 1312x1199), dieselben Bildpunkte liegen
# darin also nicht an derselben Stelle. Die Seitenverhaeltnisse der Motive
# liegen mit 1,02 bis 1,07 nah genug beieinander, dass kein Sprung auffaellt.

param(
    [string] $Verzeichnis = "$PSScriptRoot\..\src\SyncTClient.Gui",
    [int[]]  $Groessen = @(16, 20, 24, 32, 48, 64, 128, 256),
    [double] $Rand = 0.04
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$zustaende = [ordered]@{
    'Status-Ok'      = 'stc2-ok.png'
    'Status-Work'    = 'stc2-work.png'
    'Status-Pause'   = 'stc2-pause.png'
    'Status-Unknown' = 'stc2-unknows.png'
    'Status-Error'   = 'stc2-error.png'
}

# --- Hilfsmittel -----------------------------------------------------------

function Open-Vorlage {
    <# Laedt eine Vorlage und sorgt dafuer, dass sie einen Alphakanal hat. #>
    param([string] $Pfad)

    $bild = New-Object System.Drawing.Bitmap ([System.IO.Path]::GetFullPath($Pfad))
    if ($bild.PixelFormat -eq [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) { return $bild }

    # Write-Host und nicht Write-Output: was eine Funktion ausgibt, ist ihr
    # Rueckgabewert. Ein Hinweis auf der Pipeline haenge sich an das Bild an,
    # und der Aufrufer bekaeme ein Array statt einer Bitmap.
    Write-Host ("  Hinweis: {0} hat keinen Alphakanal -- Hintergrund wird freigestellt." -f (Split-Path $Pfad -Leaf))
    $frei = Remove-Hintergrund $bild
    $bild.Dispose()
    return $frei
}

function Get-Rahmen {
    <# Der sichtbare Bereich eines Bildes. #>
    param([System.Drawing.Bitmap] $Bild)

    $minX = $Bild.Width; $maxX = 0; $minY = $Bild.Height; $maxY = 0
    for ($y = 0; $y -lt $Bild.Height; $y += 2) {
        for ($x = 0; $x -lt $Bild.Width; $x += 2) {
            if ($Bild.GetPixel($x, $y).A -gt 24) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    return [PSCustomObject]@{ Links = $minX; Rechts = $maxX; Oben = $minY; Unten = $maxY }
}

function Remove-Hintergrund {
    <#
      Notbehelf fuer Vorlagen ohne Alphakanal.

      Nicht einfach "alles Weisse durchsichtig": das Motiv enthaelt selbst
      Weiss -- die Buchstaben, den Haken, den Ring um die Plakette. Statt
      dessen wird vom Rand her geflutet; durchsichtig wird nur, was hell und
      vom Rand aus erreichbar ist.

      Die Kanten bleiben dabei hell umsaeumt, denn sie waren gegen Weiss
      geglaettet. Sauberer ist es, die Vorlage mit Alphakanal zu exportieren.
    #>
    param([System.Drawing.Bitmap] $Bild)

    $b = $Bild.Width; $h = $Bild.Height
    $ziel = New-Object System.Drawing.Bitmap($b, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($ziel)
    $g.DrawImage($Bild, 0, 0, $b, $h)
    $g.Dispose()

    $bereich = New-Object System.Drawing.Rectangle(0, 0, $b, $h)
    $roh = $ziel.LockBits($bereich, [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $laenge = $roh.Stride * $h
        [byte[]] $punkte = New-Object byte[] $laenge
        [System.Runtime.InteropServices.Marshal]::Copy($roh.Scan0, $punkte, 0, $laenge)

        [bool[]] $erledigt = New-Object bool[] ($b * $h)
        $stapel = New-Object System.Collections.Generic.Stack[int]

        for ($x = 0; $x -lt $b; $x++) { $stapel.Push($x); $stapel.Push(($h - 1) * $b + $x) }
        for ($y = 0; $y -lt $h; $y++) { $stapel.Push($y * $b); $stapel.Push($y * $b + $b - 1) }

        while ($stapel.Count -gt 0) {
            $i = $stapel.Pop()
            if ($erledigt[$i]) { continue }
            $erledigt[$i] = $true

            $x = $i % $b; $y = [int]($i / $b)
            $o = $y * $roh.Stride + $x * 4

            # BGRA. Nur helle Punkte gelten als Hintergrund.
            if ($punkte[$o] -lt 232 -or $punkte[$o+1] -lt 232 -or $punkte[$o+2] -lt 232) { continue }

            $punkte[$o+3] = 0

            if ($x -gt 0)      { $stapel.Push($i - 1) }
            if ($x -lt $b - 1) { $stapel.Push($i + 1) }
            if ($y -gt 0)      { $stapel.Push($i - $b) }
            if ($y -lt $h - 1) { $stapel.Push($i + $b) }
        }

        [System.Runtime.InteropServices.Marshal]::Copy($punkte, 0, $roh.Scan0, $laenge)
    }
    finally {
        $ziel.UnlockBits($roh)
    }

    return $ziel
}

function New-DibEintrag {
    <# Ein unkomprimierter Eintrag, wie ihn GDI+ lesen kann. #>
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
    $schreiber.Write([int32] 0); $schreiber.Write([int32] 0)
    $schreiber.Write([uint32] 0); $schreiber.Write([uint32] 0)

    $bereich = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $roh = $Bild.LockBits($bereich, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        [byte[]] $zeile = New-Object byte[] ($s * 4)
        for ($y = $s - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($roh.Scan0, $y * $roh.Stride), $zeile, 0, $s * 4)
            $schreiber.Write($zeile, 0, $zeile.Length)
        }
    }
    finally {
        $Bild.UnlockBits($roh)
    }

    # AND-Maske, Zeilen auf vier Byte aufgefuellt. Durchgehend null: die
    # Durchsichtigkeit steckt bereits im Alphakanal.
    [byte[]] $maske = New-Object byte[] (([int][Math]::Ceiling($s / 32.0)) * 4 * $s)
    $schreiber.Write($maske, 0, $maske.Length)
    $schreiber.Flush()

    return ,[byte[]] $strom.ToArray()
}

function Write-Symboldatei {
    <# Schneidet die Vorlage auf den Rahmen zu und schreibt die .ico-Datei. #>
    param(
        [string] $Vorlage,
        [string] $Ziel,
        $Rahmen
    )

    $quelle = Open-Vorlage $Vorlage

    $breite = $Rahmen.Rechts - $Rahmen.Links + 1
    $hoehe  = $Rahmen.Unten - $Rahmen.Oben + 1

    # Auf ein Quadrat legen, mittig, mit etwas Luft. Ein Symbol, das die
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
        $Rahmen.Links, $Rahmen.Oben, $breite, $hoehe, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $quelle.Dispose()

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

        $daten[$s] = New-DibEintrag $bild
        $bild.Dispose()
    }
    $motiv.Dispose()

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

            $schreiber.Write([byte] $kennung); $schreiber.Write([byte] $kennung)
            $schreiber.Write([byte] 0); $schreiber.Write([byte] 0)
            $schreiber.Write([uint16] 1); $schreiber.Write([uint16] 32)
            $schreiber.Write([uint32] $inhalt.Length); $schreiber.Write([uint32] $versatz)
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

    Write-Output ("  {0,-20} {1,8:N0} Bytes" -f (Split-Path $ausgabe -Leaf), (Get-Item $ausgabe).Length)
}

# --- Programmsymbol --------------------------------------------------------

$stamm = [System.IO.Path]::GetFullPath($Verzeichnis)

Write-Output 'Programmsymbol:'
$grund = Open-Vorlage "$stamm\stc2.png"
$eigen = Get-Rahmen $grund
$grund.Dispose()
Write-Symboldatei "$stamm\stc2.png" "$stamm\SyncTClient.ico" $eigen

# --- Zustandssymbole, alle im selben Ausschnitt -----------------------------

Write-Output 'Zustaende:'

# Jede Vorlage bekommt ihren eigenen Ausschnitt. Ein gemeinsamer waere
# falsch: die Vorlagen haben unterschiedliche Leinwandgroessen, dieselben
# Bildpunkte liegen darin also nicht an derselben Stelle. Die Seitenverhael-
# tnisse der Motive liegen nah beieinander (1,02 bis 1,07), das Symbol
# springt beim Zustandswechsel daher nicht sichtbar.
foreach ($name in $zustaende.Keys) {
    $pfad = "$stamm\$($zustaende[$name])"
    $bild = Open-Vorlage $pfad
    $r = Get-Rahmen $bild
    $bild.Dispose()

    Write-Symboldatei $pfad "$stamm\$name.ico" $r
}

# --- Nachsehen, ob sich alles lesen laesst ---------------------------------

Write-Output 'Gegenprobe:'
foreach ($name in @('SyncTClient') + @($zustaende.Keys)) {
    $probe = New-Object System.Drawing.Icon("$stamm\$name.ico")
    Write-Output ("  {0,-20} {1}x{2}" -f $name, $probe.Width, $probe.Height)
    $probe.Dispose()
}
