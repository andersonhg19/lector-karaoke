# Abre el Bloc de notas en un monitor, lee con F9 y guarda una captura de pantalla para revisar el resaltado a ojo.
param([int]$Monitor = 0)
. (Join-Path $PSScriptRoot "integracion.ps1") -Caso ninguno | Out-Null
$s = [System.Windows.Forms.Screen]::AllScreens[$Monitor]
$nd = "prueba-cap-" + (Get-Random)
$h = AbrirNotas $nd "Prueba visual del resaltado en el monitor $($s.DeviceName) con varias palabras."
$b = $s.WorkingArea
Add-Type -Namespace X -Name Pos -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);' -ErrorAction SilentlyContinue
[void][X.Pos]::MoveWindow($h, $b.Left + 60, $b.Top + 60, 1000, 400, $true)
Start-Sleep -Milliseconds 700
[W]::Enfocar($h); Start-Sleep -Milliseconds 300
[void](SeleccionarTodoUia $h); Start-Sleep -Milliseconds 300
$m = Marca
[W]::Tecla(0x78)
$r = Esperar "^\S+\trects\tpalabra=3\|" $m 12
Start-Sleep -Milliseconds 150
$bmp = New-Object System.Drawing.Bitmap 1100, 500
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
$salida = Join-Path $Base "tmp_prueba\captura-monitor-$Monitor.png"
$bmp.Save($salida); $g.Dispose(); $bmp.Dispose()
Write-Host "rects: $($r.Linea)"
Write-Host "monitor: $($s.DeviceName) $($s.Bounds) area $b"
Write-Host "captura: $salida"
[void](Esperar "^\S+\tfin\t" $m 20)
CerrarNotas $nd
