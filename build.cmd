@echo off
REM Compila Lector Karaoke con el compilador de C# que trae Windows (.NET Framework 4.8). No hace falta instalar nada.
setlocal
cd /d "%~dp0"
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
set CSC=%NET%\csc.exe
if not exist "%CSC%" (
  echo No se encontro csc.exe de .NET Framework 4.8.
  exit /b 1
)
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%NET%\System.Web.Extensions.dll" /r:"%GAC%\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /r:"%NET%\WPF\WindowsBase.dll"
set WM=%WINDIR%\System32\WinMetadata
set WINRT=/r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Graphics.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Globalization.winmd" /r:"%WM%\Windows.Security.winmd" /r:"%NET%\System.Runtime.dll" /r:"%NET%\System.Threading.Tasks.dll" /r:"%NET%\System.Runtime.WindowsRuntime.dll"
set COMUN=src\Nativo.cs src\Registro.cs src\Configuracion.cs src\Inicio.cs

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:LectorKaraoke.exe /win32manifest:src\app.manifest %REFS% %WINRT% %COMUN% src\UbicadorTerminal.cs src\Preparador.cs src\Tiempos.cs src\Motores.cs src\Reproductor.cs src\Captura.cs src\Capas.cs src\Lector.cs src\Programa.cs
if errorlevel 1 exit /b 1

"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:Configurar.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll %COMUN% src\Configurar.cs
if errorlevel 1 exit /b 1

if exist tests\Pruebas.cs (
  "%CSC%" /nologo /target:exe /platform:anycpu /out:tests\Pruebas.exe %REFS% %COMUN% src\Preparador.cs src\Tiempos.cs src\Motores.cs tests\Pruebas.cs
  if errorlevel 1 exit /b 1
)
if exist tests\PruebaMotores.cs (
  "%CSC%" /nologo /target:exe /platform:anycpu /out:tests\PruebaMotores.exe %REFS% %COMUN% src\Preparador.cs src\Tiempos.cs src\Motores.cs src\Reproductor.cs tests\PruebaMotores.cs
  if errorlevel 1 exit /b 1
)
if exist tests\PruebaOcr.cs (
  "%CSC%" /nologo /target:exe /platform:anycpu /win32manifest:src\app.manifest /out:tests\PruebaOcr.exe %REFS% %WINRT% %COMUN% src\Preparador.cs src\UbicadorTerminal.cs tests\PruebaOcr.cs
  if errorlevel 1 exit /b 1
)
echo Compilacion correcta.
