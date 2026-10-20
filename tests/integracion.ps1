# Pruebas de integración reales de Lector Karaoke (Windows 11).
# Requiere el lector corriendo con --prueba. Uso: powershell -File tests\integracion.ps1 -Caso notepad
# -Espacio: carpeta de un espacio de trabajo de Orca donde crear la terminal de prueba (solo para el caso "orca").
param([string]$Caso = "todos", [string]$Espacio = "")
$ErrorActionPreference = "Stop"
$Base = Split-Path -Parent $PSScriptRoot
$LogPrueba = Join-Path $Base "logs\prueba.log"

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  public static void Tecla(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void Combo(byte[] vks) { foreach (var v in vks) keybd_event(v, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, UIntPtr.Zero); }
  [StructLayout(LayoutKind.Sequential)] public struct LII { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LII p);
  public static uint Inactivo() { var l = new LII(); l.cbSize = 8; GetLastInputInfo(ref l); return (uint)Environment.TickCount - l.dwTime; }
  public static void Enfocar(IntPtr h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); ShowWindow(h, 9); SetForegroundWindow(h); }
}
"@
[void][W]::SetProcessDpiAwarenessContext([IntPtr](-4))

$script:ok = 0; $script:fallos = 0
function Cierto($nombre, $cond) { if ($cond) { $script:ok++; Write-Host "  OK    $nombre" } else { $script:fallos++; Write-Host "  FALLA $nombre" -ForegroundColor Red } }

function Eventos { if (Test-Path $LogPrueba) { Get-Content $LogPrueba -Encoding UTF8 } else { @() } }
function Esperar($patron, $desde, $seg = 25) {
  $limite = (Get-Date).AddSeconds($seg)
  while ((Get-Date) -lt $limite) {
    $l = @(Eventos)
    for ($i = $desde; $i -lt $l.Count; $i++) { if ($l[$i] -match $patron) { return @{ Linea = $l[$i]; Indice = $i } } }
    Start-Sleep -Milliseconds 60
  }
  return $null
}
function Marca { return @(Eventos).Count }

# Espera a que el usuario no toque teclado ni mouse durante $seg segundos (máximo $max minutos).
function EsperarInactividad($seg = 12, $max = 20) {
  $limite = (Get-Date).AddMinutes($max)
  while ((Get-Date) -lt $limite) {
    if ([W]::Inactivo() -ge $seg * 1000) { return $true }
    Start-Sleep -Seconds 2
  }
  return $false
}

# Cuenta píxeles parecidos al color de subrayado (naranja puro) dentro del rectángulo.
function PixelesNaranja($x, $y, $w, $h) {
  if ($w -le 0 -or $h -le 0) { return 0 }
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $n = 0
  for ($i = 0; $i -lt $w; $i += 2) { for ($j = 0; $j -lt $h; $j += 1) {
    $c = $bmp.GetPixel($i, $j)
    if ($c.R -gt 230 -and $c.G -gt 110 -and $c.G -lt 175 -and $c.B -lt 60) { $n++ }
  } }
  $g.Dispose(); $bmp.Dispose()
  return $n
}

function VentanaDe($proceso, $titulo, $seg = 20) {
  $limite = (Get-Date).AddSeconds($seg)
  while ((Get-Date) -lt $limite) {
    $p = Get-Process $proceso -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$titulo*" } | Select-Object -First 1
    if ($p) { return $p.MainWindowHandle }
    Start-Sleep -Milliseconds 200
  }
  return [IntPtr]::Zero
}

function SeleccionarTodoUia($hwnd) {
  $raiz = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
  $c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsTextPatternAvailableProperty, $true)
  $c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  $doc = $raiz.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
  if (-not $doc) { $doc = $raiz.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c1) }
  if (-not $doc) { return $false }
  try { $doc.SetFocus() } catch { }
  $tp = $doc.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
  $tp.DocumentRange.Select()
  return $true
}

# ------------------------------------------------------------------------------------------------
function Caso-Notepad {
  if (-not (EsperarInactividad)) { Write-Host "  (el equipo no quedó libre; caso omitido)"; return }
  Write-Host "== Bloc de notas: selección por accesibilidad, karaoke y secreto oculto"
  $dir = Join-Path $Base "tmp_prueba"; New-Item -ItemType Directory -Force $dir | Out-Null
  $nombreNotas = "prueba-lector-" + (Get-Random); $f = Join-Path $dir "$nombreNotas.txt"
  Set-Content -Path $f -Encoding UTF8 -Value "Primera frase de prueba para el lector karaoke. Luego viene getUserById, el texto `"entre comillas`" (opcional) a/b #1 y la clave password=Secreta123 sin decir su valor."
  $p = Start-Process notepad.exe -ArgumentList "`"$f`"" -PassThru
  $h = VentanaDe "notepad" $nombreNotas 15
  Cierto "se abrió el Bloc de notas" ($h -ne [IntPtr]::Zero)
  if ($h -eq [IntPtr]::Zero) { return }
  Start-Sleep -Milliseconds 800
  [W]::Enfocar($h); Start-Sleep -Milliseconds 400
  Cierto "texto seleccionado por UIA" (SeleccionarTodoUia $h)
  Start-Sleep -Milliseconds 300
  $m = Marca
  [W]::Tecla(0x78)   # F9
  $cap = Esperar "^\S+\tcaptura\t" $m 8
  Cierto "captura registrada" ($cap -ne $null)
  if ($cap) {
    Cierto "fuente = Accesibilidad" ($cap.Linea -match "fuente=Accesibilidad")
    Cierto "texto capturado completo" ($cap.Linea -match "Primera frase" -and $cap.Linea -match "sin decir su valor")
  }
  $prep = Esperar "^\S+\tpreparado\t" $m 5
  Cierto "secreto no se envía a la voz" ($prep -and $prep.Linea -notmatch "Secreta123" -and $prep.Linea -match "valor oculto")
  Cierto "identificador separado" ($prep -and $prep.Linea -match "get User By Id")
  Cierto "símbolos nombrados" ($prep -and $prep.Linea -match "abre comillas entre comillas cierra comillas" -and $prep.Linea -match "abre paréntesis opcional cierra paréntesis" -and $prep.Linea -match "a barra b numeral 1")
  $aud = Esperar "^\S+\taudio\t" $m 15
  Cierto "audio sintetizado con edge (Jorge)" ($aud -and $aud.Linea -match "motor=edge")
  $r1 = Esperar "^\S+	rects	palabra=\d+\|-?\d" $m 10
  Cierto "rectángulos de palabra calculados" ($r1 -ne $null)
  if ($r1) {
    $partes = ($r1.Linea -split "\|")[1] -split ";" | Select-Object -First 1
    $v = $partes -split ","
    Start-Sleep -Milliseconds 120
    $n = PixelesNaranja ([int]$v[0] - 6) ([int]$v[1]) ([int]$v[2] + 12) ([int]$v[3] + 30)
    Cierto "resaltado naranja visible sobre la palabra ($n px)" ($n -gt 5)
  }
  $r3 = Esperar "^\S+\trects\tpalabra=([3-9]|\d\d)\|" $m 15
  Cierto "el resaltado avanza palabra por palabra" ($r3 -ne $null)
  $fin = Esperar "^\S+\tfin\t" $m 40
  Cierto "la lectura termina sola" ($fin -ne $null)
  Start-Sleep -Milliseconds 300
  if ($r1) { $n2 = PixelesNaranja ([int]$v[0] - 6) ([int]$v[1]) ([int]$v[2] + 12) ([int]$v[3] + 30); Cierto "la capa desaparece al terminar ($n2 px)" ($n2 -lt 3) }

  # Detener con la misma tecla.
  [W]::Enfocar($h); Start-Sleep -Milliseconds 300
  [void](SeleccionarTodoUia $h); Start-Sleep -Milliseconds 200
  $m = Marca
  [W]::Tecla(0x78)
  $aud = Esperar "^\S+\taudio\t" $m 15
  Start-Sleep -Milliseconds 700
  $t0 = Get-Date
  [W]::Tecla(0x78)
  $det = Esperar "^\S+\tdetenido\ttecla" $m 3
  Cierto "F9 durante la lectura la detiene" ($det -ne $null)
  Start-Sleep -Milliseconds 400
  $tras = @(Eventos)[($m)..(@(Eventos).Count - 1)] -join "`n"
  Cierto "no sigue leyendo tras detener" (-not ((Esperar "^\S+\taudio\ttrozo=1" ($det.Indice) 3) -ne $null))
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$nombreNotas*" } | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------------------------------------
function Caso-Orca {
  if (-not $Espacio) { Write-Host "== Orca: omitido (pasa -Espacio <carpeta de un espacio de trabajo de Orca>)"; return }
  if (-not (EsperarInactividad)) { Write-Host "  (el equipo no quedó libre; caso omitido)"; return }
  Write-Host "== Terminal de Orca: copia segura (sin ^C), franja y portapapeles restaurado"
  $orca = Join-Path $env:LOCALAPPDATA "Programs\orca\resources\bin\orca.exe"
  $dir = Join-Path $Base "tmp_prueba"; New-Item -ItemType Directory -Force $dir | Out-Null
  $py = Join-Path $dir "teclas.py"; $reg = Join-Path $dir "teclas.log"
  Remove-Item $reg -ErrorAction SilentlyContinue
  Set-Content -Path $py -Encoding UTF8 -Value @'
import msvcrt, sys, time
log = open(sys.argv[1], "a", encoding="utf-8")
log.write("INICIO\n"); log.flush()
print("LINEA DE PRUEBA PARA ORCA: el lector debe leer este texto.", flush=True)
try:
    while True:
        while msvcrt.kbhit():
            ch = msvcrt.getwch(); log.write(repr(ch) + "\n"); log.flush()
        time.sleep(0.02)
except KeyboardInterrupt:
    log.write("SIGINT\n"); log.flush()
'@
  Set-Clipboard -Value "contenido-original-123"
  $j = & $orca terminal create --worktree "path:$Espacio" --title prueba-lector --command "python $py $reg" --json
  ($j -join "`n") | Set-Content (Join-Path $dir "create.json") -Encoding UTF8
  $j = ($j -join "`n") | ConvertFrom-Json
  $h = $j.result.terminal.handle
  if (-not $h) { $h = $j.result.handle }
  Cierto "terminal de prueba creada ($h)" ($h -ne $null)
  if ($h) { & $orca terminal switch --terminal $h --json | Out-Null }
  Start-Sleep -Seconds 3
  $ventOrca = (Get-Process Orca -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
  if ($ventOrca) { [W]::Enfocar($ventOrca); Start-Sleep -Milliseconds 600 }
  $fg = [W]::GetForegroundWindow(); $pidFg = 0
  $proc = (Get-Process | Where-Object { $_.MainWindowHandle -eq $fg } | Select-Object -First 1)
  Cierto "Orca en primer plano" ($proc -and $proc.ProcessName -like "*orca*")
  Cierto "el script de prueba arrancó" ((Test-Path $reg) -and ((Get-Content $reg) -contains "INICIO"))

  # 1) Sin selección: el lector intenta Ctrl+Shift+C; no debe llegar ninguna interrupción al proceso.
  $m = Marca
  [W]::Tecla(0x78)
  $cap = Esperar "^\S+\tcaptura\t" $m 6
  Cierto "captura sin selección no usa la copia" ($cap -and $cap.Linea -notmatch "fuente=Copia")
  Start-Sleep -Milliseconds 600
  if (@(Eventos)[-1] -notmatch "\tfin\t") { [W]::Tecla(0x78); Start-Sleep -Milliseconds 400 }
  $teclas = Get-Content $reg
  Cierto "el proceso NO recibió ^C ni SIGINT" (-not ($teclas -contains "SIGINT") -and -not ($teclas -match "x03"))

  # 2) Con todo seleccionado (Ctrl+Shift+A de Orca): copia, lee y deja el portapapeles como estaba.
  [W]::Combo([byte[]](0x11, 0x10, 0x41)); Start-Sleep -Milliseconds 400
  $m = Marca
  [W]::Tecla(0x78)
  $cap = Esperar "^\S+\tcaptura\t" $m 8
  Cierto "captura por copia en Orca" ($cap -and $cap.Linea -match "fuente=Copia" -and $cap.Linea -match "proceso=orca")
  Cierto "texto de la terminal capturado" ($cap -and $cap.Linea -match "LINEA DE PRUEBA PARA ORCA")
  Start-Sleep -Milliseconds 300
  Cierto "portapapeles restaurado" ((Get-Clipboard -Raw).Trim() -eq "contenido-original-123")
  $ter = Esperar "^\S+\tterminal\t" $m 10
  Cierto "ubicación por OCR en la terminal ($(if ($ter) { ($ter.Linea -split "`t")[2] }))" ($ter -and $ter.Linea -match "anclas=([3-9]|\d\d+)/")
  $rt = Esperar "^\S+\trects\tpalabra=[1-9]\d*\|-?\d" $m 15
  Cierto "rectángulos de palabra en la terminal de Orca" ($rt -ne $null)
  if ($rt) {
    $v = RectDe $rt.Linea
    Start-Sleep -Milliseconds 60
    # A velocidad alta una palabra corta dura ~80 ms: se revisa toda la fila de la palabra.
    $n = PixelesNaranja ($v[0] - 500) $v[1] 1100 ($v[3] + 12)
    Cierto "resaltado naranja sobre la fila de la palabra en Orca ($n px)" ($n -gt 5)
    $bmp = New-Object System.Drawing.Bitmap 1000, 240
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($v[0] - 450, $v[1] - 100, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Base "tmp_prueba\orca-integracion.png")); $g.Dispose(); $bmp.Dispose()
  }
  $fin = Esperar "^\S+\tfin\t" $m 30
  if (-not $fin) { [W]::Tecla(0x78) }
  $teclas = Get-Content $reg
  Cierto "tras todo, el proceso sigue sin ^C" (-not ($teclas -contains "SIGINT") -and -not ($teclas -match "x03"))
  & $orca terminal close --terminal $h --json | Out-Null
}

# ------------------------------------------------------------------------------------------------
function AbrirNotas($nombre, $texto) {
  $dir = Join-Path $Base "tmp_prueba"; New-Item -ItemType Directory -Force $dir | Out-Null
  $f = Join-Path $dir "$nombre.txt"
  Set-Content -Path $f -Encoding UTF8 -Value $texto
  $p = Start-Process notepad.exe -ArgumentList "`"$f`"" -PassThru
  $h = VentanaDe "notepad" $nombre 8
  if ($h -eq [IntPtr]::Zero) {
    # El Bloc de notas restauró su sesión y dejó activa otra pestaña: se selecciona la del archivo de prueba.
    $w = (Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1)
    if ($w) {
      $raizN = [System.Windows.Automation.AutomationElement]::FromHandle($w.MainWindowHandle)
      $cTab = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
      $tab = $raizN.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cTab) | Where-Object { $_.Current.Name -like "*$nombre*" } | Select-Object -First 1
      if ($tab) { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500; $h = VentanaDe "notepad" $nombre 5 }
    }
  }
  if ($h -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 800; [W]::Enfocar($h); Start-Sleep -Milliseconds 400 }
  return $h
}
function CerrarNotas($nombre) {
  Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$nombre*" } | Stop-Process -Force -ErrorAction SilentlyContinue
}
function SinPixelesEn($r) { return (PixelesNaranja ($r[0] - 6) $r[1] ($r[2] + 12) ($r[3] + 30)) -lt 3 }
function RectDe($linea) { return @((($linea -split "\|")[1] -split ";" | Select-Object -First 1) -split "," | ForEach-Object { [int]$_ }) }

function Caso-Mouse {
  Write-Host "== Modo mouse: elemento bajo el puntero, sin selección"
  if (-not (EsperarInactividad)) { Write-Host "  (el equipo no quedó libre; caso omitido)"; return }
  $nm = "prueba-mouse-" + (Get-Random); $h = AbrirNotas $nm "texto cualquiera"
  if ($h -eq [IntPtr]::Zero) { Cierto "se abrió el Bloc de notas" $false; return }
  # Quitar la selección: rango vacío al inicio.
  $raiz = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  $cDoc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  $doc = $raiz.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cDoc)
  $tp = $doc.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
  $r = $tp.DocumentRange.Clone(); $r.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $r, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start); $r.Select()
  # Un botón con nombre.
  $cBtn = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $btn = $raiz.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cBtn) | Where-Object { $_.Current.Name -and -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty } | Select-Object -First 1
  Cierto "hay un botón con nombre ($($btn.Current.Name))" ($btn -ne $null)
  if ($btn) {
    $b = $btn.Current.BoundingRectangle
    [void][W]::SetCursorPos([int]($b.Left + $b.Width / 2), [int]($b.Top + $b.Height / 2))
    Start-Sleep -Milliseconds 1500
    $m = Marca
    [W]::Tecla(0x78)
    $cap = Esperar "^\S+\tcaptura\t" $m 8
    Cierto "captura en modo mouse" ($cap -and $cap.Linea -match "fuente=Mouse")
    Cierto "lee el nombre del botón" ($cap -and $cap.Linea.Contains($btn.Current.Name))
    $aud = Esperar "^\S+\taudio\t" $m 10
    Start-Sleep -Milliseconds 300
    $n = PixelesNaranja ([int]$b.Left - 12) ([int]$b.Top - 12) ([int]$b.Width + 24) ([int]$b.Height + 24)
    Cierto "marco naranja alrededor del botón ($n px)" ($n -gt 10)
    [void](Esperar "^\S+\tfin\t" $m 15)
  }
  CerrarNotas $nm
}

function Caso-Terminal {
  Write-Host "== Windows Terminal: selección por accesibilidad"
  if (-not (EsperarInactividad)) { Write-Host "  (el equipo no quedó libre; caso omitido)"; return }
  Start-Process wt.exe -ArgumentList "-w new --title PRUEBALECTOR --suppressApplicationTitle cmd /k echo HOLA DESDE WINDOWS TERMINAL, PRUEBA DEL LECTOR KARAOKE"
  $h = VentanaDe "WindowsTerminal" "PRUEBALECTOR" 15
  Cierto "se abrió Windows Terminal" ($h -ne [IntPtr]::Zero)
  if ($h -eq [IntPtr]::Zero) { return }
  Start-Sleep -Seconds 2
  [W]::Enfocar($h); Start-Sleep -Milliseconds 500
  $raiz = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  $c1 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsTextPatternAvailableProperty, $true)
  $term = $raiz.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c1)
  Cierto "la terminal expone texto por accesibilidad" ($term -ne $null)
  if ($term) {
    [W]::Combo([byte[]](0x11, 0x10, 0x41)); Start-Sleep -Milliseconds 400
    $tp = $term.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $sel = $false
    try { $sel = ($tp.GetSelection() | ForEach-Object { $_.GetText(500) }) -join "" -match "PRUEBA DEL LECTOR" } catch { }
    Start-Sleep -Milliseconds 300
    $m = Marca
    [W]::Tecla(0x78)
    $cap = Esperar "^\S+\tcaptura\t" $m 8
    Cierto "captura por accesibilidad en Windows Terminal" ($cap -and $cap.Linea -match "fuente=Accesibilidad" -and $cap.Linea -match "proceso=windowsterminal")
    Cierto "texto de la terminal correcto" ($cap -and $cap.Linea -match "PRUEBA DEL LECTOR")
    $r1 = Esperar "^\S+	rects	palabra=\d+\|-?\d" $m 12
    Cierto "posición de palabras en la terminal" ($r1 -ne $null)
    if ($r1) {
      $v = RectDe $r1.Linea
      Start-Sleep -Milliseconds 100
      $n = PixelesNaranja ($v[0] - 6) $v[1] ($v[2] + 12) ($v[3] + 30)
      Cierto "resaltado naranja sobre la terminal ($n px)" ($n -gt 5)
    }
    [void](Esperar "^\S+\tfin\t" $m 30)
  }
  Add-Type -Namespace X -Name Msg -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);' -ErrorAction SilentlyContinue
  [void][X.Msg]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
}

function Caso-Adversarial {
  Write-Host "== Adversarial: pulsaciones rápidas, ventana cerrada a mitad, texto largo, monitor con otra escala"
  if (-not (EsperarInactividad)) { Write-Host "  (el equipo no quedó libre; caso omitido)"; return }
  $largo = (1..400 | ForEach-Object { "Oracion numero $_ del texto largo de prueba, con varias palabras para leer." }) -join " "
  $na = "prueba-adv-" + (Get-Random); $h = AbrirNotas $na $largo
  if ($h -eq [IntPtr]::Zero) { Cierto "se abrió el Bloc de notas" $false; return }
  [void](SeleccionarTodoUia $h); Start-Sleep -Milliseconds 300

  # 1) Texto largo: empieza rápido.
  $m = Marca; $t0 = Get-Date
  [W]::Tecla(0x78)
  $aud = Esperar "^\S+\taudio\t" $m 10
  $ms = ((Get-Date) - $t0).TotalMilliseconds
  Cierto "texto largo (~30.000 car.): empieza a hablar en $([int]$ms) ms (< 4000)" ($aud -and $ms -lt 4000)
  $prep = Esperar "^\S+\tpreparado\ttrozos=(\d+)" $m 2
  Cierto "texto largo dividido en muchos trozos" ($prep -and [int]([regex]::Match($prep.Linea, "trozos=(\d+)").Groups[1].Value) -gt 20)

  # 2) Pulsaciones rápidas: la segunda dentro de 250 ms se ignora, la tercera detiene.
  Start-Sleep -Milliseconds 800
  [W]::Tecla(0x78); Start-Sleep -Milliseconds 60; [W]::Tecla(0x78)
  Start-Sleep -Milliseconds 500
  $det = @(Eventos | Where-Object { $_ -match "\tdetenido\t" }).Count
  $nuevas = @(Eventos)[$m..(@(Eventos).Count - 1)] | Where-Object { $_ -match "\tcaptura\t" }
  Cierto "doble pulsación rápida: se detiene una sola vez y no relanza" (@($nuevas).Count -eq 1)
  Cierto "el lector sigue vivo" ((Get-Process LectorKaraoke -ErrorAction SilentlyContinue) -ne $null)

  # 3) Cerrar la ventana a mitad de lectura: el resaltado desaparece.
  [W]::Enfocar($h); Start-Sleep -Milliseconds 300
  [void](SeleccionarTodoUia $h); Start-Sleep -Milliseconds 300
  $m = Marca
  [W]::Tecla(0x78)
  $r1 = Esperar "^\S+	rects	palabra=\d+\|-?\d" $m 10
  if ($r1) {
    $v = RectDe $r1.Linea
    CerrarNotas $na
    Start-Sleep -Milliseconds 900
    Cierto "ventana cerrada: no queda naranja en pantalla" (SinPixelesEn $v)
    Cierto "el lector sigue vivo tras cerrar la ventana" ((Get-Process LectorKaraoke -ErrorAction SilentlyContinue) -ne $null)
  } else { Cierto "rects antes de cerrar" $false }
  # Detener solo si sigue leyendo (una F9 de más iniciaría otra lectura y desordenaría el caso siguiente).
  if (@(Eventos)[-1] -notmatch "	(fin|detenido)	") { [W]::Tecla(0x78) }
  [void](Esperar "^\S+	(fin|detenido)	" (Marca) 5)
  Start-Sleep -Milliseconds 500

  # 4) Monitor con otra escala: mover el Bloc de notas a cada monitor y comprobar el resaltado.
  $pantallas = [System.Windows.Forms.Screen]::AllScreens
  foreach ($s in $pantallas) {
    if (-not (EsperarInactividad 8 5)) { Write-Host "  (equipo ocupado; monitor omitido)"; continue }
    $nd = "prueba-dpi-" + (Get-Random); $h2 = AbrirNotas $nd "Prueba de escala en el monitor $($s.DeviceName) con resaltado exacto."
    if ($h2 -eq [IntPtr]::Zero) { Cierto "monitor $($s.DeviceName): se abrió el Bloc de notas" $false; continue }
    $b = $s.WorkingArea
    Add-Type -Namespace X -Name Pos -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);' -ErrorAction SilentlyContinue
    [void][X.Pos]::MoveWindow($h2, $b.Left + 60, $b.Top + 60, [Math]::Min(1000, $b.Width - 120), 500, $true)
    Start-Sleep -Milliseconds 700
    [W]::Enfocar($h2); Start-Sleep -Milliseconds 300
    [void](SeleccionarTodoUia $h2); Start-Sleep -Milliseconds 300
    $m = Marca
    [W]::Tecla(0x78)
    $r = Esperar "^\S+	rects	palabra=\d+\|-?\d" $m 12
    if ($r) {
      $v = RectDe $r.Linea
      Start-Sleep -Milliseconds 80
      $n = PixelesNaranja ($v[0] - 6) $v[1] ($v[2] + 12) ($v[3] + 30)
      $dentro = $v[0] -ge $b.Left -and $v[0] -lt $b.Right
      Cierto "monitor $($s.DeviceName) ($($s.Bounds.Width)x$($s.Bounds.Height)): palabra dentro del monitor y naranja visible ($n px)" ($dentro -and $n -gt 5)
    } else { Cierto "monitor $($s.DeviceName): rects" $false }
    [void](Esperar "^\S+\tfin\t" $m 20)
    CerrarNotas $nd
  }
}

$ErrorActionPreference = "Continue"
foreach ($c in @("notepad", "mouse", "terminal", "adversarial", "orca")) {
  if ($Caso -ne "todos" -and $Caso -ne $c) { continue }
  try { & ("Caso-" + $c) } catch { Cierto "caso $c sin excepciones: $($_.Exception.Message)" $false }
}
if ($Caso -ne "ninguno") {
  Write-Host ""
  Write-Host "INTEGRACION: $($script:ok) ok, $($script:fallos) fallos"
  exit $script:fallos
}
