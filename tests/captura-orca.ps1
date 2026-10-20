# Lee en una terminal de prueba de Orca y guarda una captura de pantalla mientras se resalta una palabra.
param([string]$Espacio = "")
. (Join-Path $PSScriptRoot "integracion.ps1") -Caso ninguno | Out-Null
$orca = Join-Path $env:LOCALAPPDATA "Programs\orca\resources\bin\orca.exe"
$dir = Join-Path $Base "tmp_prueba"; New-Item -ItemType Directory -Force $dir | Out-Null
$py = Join-Path $dir "texto_orca.py"
Set-Content -Path $py -Encoding UTF8 -Value @'
import time
print("Primera linea de la prueba visual del karaoke en la terminal de Orca.")
print("Segunda linea: el resaltado debe caer exactamente sobre cada palabra.")
print("Tercera linea con numeros 25.000 y simbolos (entre parentesis).")
time.sleep(600)
'@
if (-not (EsperarInactividad 8 10)) { Write-Host "equipo ocupado"; exit 1 }
$j = & $orca terminal create --worktree "path:$Espacio" --title prueba-visual --command "python $py" --json
$j = ($j -join "`n") | ConvertFrom-Json
$h = $j.result.terminal.handle
& $orca terminal switch --terminal $h --json | Out-Null
Start-Sleep -Seconds 3
$ventOrca = (Get-Process Orca | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
[W]::Enfocar($ventOrca); Start-Sleep -Milliseconds 600
# Clic en el centro de la terminal visible más grande para darle el foco del teclado.
$raiz = [System.Windows.Automation.AutomationElement]::FromHandle($ventOrca)
$grupos = $raiz.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Group)))
$pant = $grupos | Where-Object { (" " + $_.Current.ClassName + " ") -match " xterm-screen " -and -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty } | Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
if ($pant) {
  $b = $pant.Current.BoundingRectangle
  [void][W]::SetCursorPos([int]($b.Left + $b.Width / 2), [int]($b.Top + $b.Height / 2))
  Start-Sleep -Milliseconds 150
  [W]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 300
}
[W]::Combo([byte[]](0x11, 0x10, 0x41)); Start-Sleep -Milliseconds 400
$m = Marca
[W]::Tecla(0x78)
$r = Esperar "^\S+\trects\tpalabra=[3-9]\|-?\d" $m 15
Start-Sleep -Milliseconds 40
if ($r) {
  $v = RectDe $r.Linea
  $x = $v[0] - 500; $y = $v[1] - 120
  $bmp = New-Object System.Drawing.Bitmap 1100, 300
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path $dir "orca-visual.png")); $g.Dispose(); $bmp.Dispose()
  Write-Host "rect: $($r.Linea)"
}
[void](Esperar "^\S+\tfin\t" $m 30)
& $orca terminal close --terminal $h --json | Out-Null
Write-Host "listo"
