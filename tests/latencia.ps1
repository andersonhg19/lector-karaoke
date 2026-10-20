# Mide F9 -> inicio del audio con el lector real, para varias voces. Requiere el lector con --prueba.
param([string[]]$Voces = @("fr-FR-RemyMultilingualNeural", "es-MX-JorgeNeural"), [int]$Repeticiones = 4)
. (Join-Path $PSScriptRoot "integracion.ps1") -Caso ninguno | Out-Null
$cfg = Join-Path $Base "config.ini"
$original = Get-Content $cfg -Raw -Encoding UTF8
$frases = @("Revisa el pedido de la sucursal norte antes del cierre del mes y confirma el inventario.",
            "El servidor de pruebas quedó listo, ahora falta validar los reportes con el equipo.",
            "Mañana a primera hora enviamos la factura corregida al cliente con el nuevo descuento.",
            "La migración terminó sin errores pero hay que revisar los permisos de los usuarios.")
foreach ($voz in $Voces) {
  ($original -replace "(?m)^voz_edge = .*$", "voz_edge = $voz") | Set-Content $cfg -Encoding UTF8 -NoNewline
  Start-Sleep -Seconds 2
  $tiempos = @()
  for ($i = 0; $i -lt $Repeticiones; $i++) {
    if (-not (EsperarInactividad 8 10)) { break }
    $txt = $frases[$i % $frases.Count] + " Prueba " + (Get-Random) + "."
    $n = "prueba-lat-" + (Get-Random)
    $h = AbrirNotas $n $txt
    [void](SeleccionarTodoUia $h); Start-Sleep -Milliseconds 300
    $m = Marca; $t0 = Get-Date
    [W]::Tecla(0x78)
    $a = Esperar "^\S+\taudio\ttrozo=0" $m 15
    if ($a) { $tiempos += [int]((Get-Date) - $t0).TotalMilliseconds }
    [void](Esperar "^\S+\tfin\t" $m 20)
    CerrarNotas $n
  }
  $orden = $tiempos | Sort-Object
  Write-Host ("{0,-32} F9 -> voz (ms): {1}   mediana {2}" -f $voz, ($orden -join ", "), $orden[[int]($orden.Count / 2)])
}
$original | Set-Content $cfg -Encoding UTF8 -NoNewline
