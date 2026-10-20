using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LectorKaraoke
{
    // Configurar.exe: deja la carpeta lista y el lector arrancando con Windows. Se puede ejecutar las veces que haga falta.
    //   Configurar.exe                 instala o repara lo que falte, registra el arranque y reinicia el lector
    //   Configurar.exe /reiniciar      solo reinicia el lector (por ejemplo, tras cambiar config.ini a mano)
    //   Configurar.exe /desinstalar    quita el arranque automático y cierra el lector (no borra la carpeta)
    //   Configurar.exe /actualizar-voz actualiza edge-tts (si Microsoft cambia el acceso a las voces en línea)
    //   /sin-descargas  /silencioso
    internal static class Configurar
    {
        private sealed class Descarga
        {
            public string Url, Sha256;
            public long Tamano;
        }

        private const string EdgeTtsVersion = "7.3.1";

        private static readonly Descarga Python = new Descarga
        {
            Url = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
            Sha256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3", Tamano = 11133606
        };
        private static readonly Descarga Piper = new Descarga
        {
            Url = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            Sha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea", Tamano = 22477236
        };
        private static readonly Descarga VozOnnx = new Descarga
        {
            Url = "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_MX/ald/medium/es_MX-ald-medium.onnx",
            Sha256 = "019b3803293c93e34a206dd2e53a3889209a514e786fd7144f7b70196c579b63", Tamano = 63201294
        };
        private static readonly Descarga VozJson = new Descarga
        {
            Url = "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_MX/ald/medium/es_MX-ald-medium.onnx.json",
            Sha256 = "5a71498158e04afc8099bfd019c7e87c68eb9d042505a2b1a87e5c1ac2b1a61d", Tamano = 4878
        };
        private const string GetPip = "https://bootstrap.pypa.io/get-pip.py";

        private static string baseDir;
        private static bool silencioso;
        private static int errores;

        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 y 1.3
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Registro.Iniciar(baseDir, "configurar.log");
            Console.OutputEncoding = Encoding.UTF8;
            var a = args.Select(x => x.ToLowerInvariant().Replace('-', '/').TrimStart('/')).ToList();
            silencioso = a.Contains("silencioso");
            Console.WriteLine("Lector Karaoke - configuración");
            Console.WriteLine("Carpeta: " + baseDir);
            Console.WriteLine();
            try
            {
                if (a.Contains("desinstalar")) Desinstalar();
                else if (a.Contains("reiniciar")) ReiniciarLector();
                else if (a.Contains("actualizar/voz") || a.Contains("actualizar-voz")) ActualizarVoz();
                else Instalar(!a.Contains("sin/descargas") && !a.Contains("sin-descargas"));
            }
            catch (Exception e)
            {
                errores++;
                Paso("ERROR: " + e.Message);
                Registro.Error("Configurar", e);
            }
            Console.WriteLine();
            Console.WriteLine(errores == 0 ? "Listo." : "Terminó con " + errores + " problema(s). Revisa logs\\configurar.log.");
            if (!silencioso)
            {
                Console.WriteLine("Presiona una tecla para cerrar.");
                try { Console.ReadKey(true); } catch { }
            }
            return errores == 0 ? 0 : 1;
        }

        private static void Instalar(bool descargar)
        {
            string exe = Path.Combine(baseDir, "LectorKaraoke.exe");
            if (!File.Exists(exe)) throw new Exception("No encuentro LectorKaraoke.exe en esta carpeta.");
            foreach (var d in new[] { "logs", "tmp", "motor", "piper" }) Directory.CreateDirectory(Path.Combine(baseDir, d));

            string cfg = Path.Combine(baseDir, Configuracion.NombreArchivo);
            if (!File.Exists(cfg)) { File.WriteAllText(cfg, Configuracion.TextoPredeterminado, new UTF8Encoding(true)); Paso("config.ini creado con los valores predeterminados."); }
            else Paso("config.ini ya existe (se conserva).");

            if (descargar)
            {
                PrepararPython();
                PrepararPiper();
            }
            else Paso("Descargas omitidas (/sin-descargas).");

            Inicio.Registrar(exe);
            Paso("Arranque automático registrado: " + exe);
            ReiniciarLector();
        }

        // ---------- Python embebido + edge-tts (voz Jorge) ----------
        private static void PrepararPython()
        {
            string dir = Path.Combine(baseDir, "motor", "python");
            string py = Path.Combine(dir, "python.exe");
            if (!File.Exists(py))
            {
                Paso("Descargando Python embebido (11 MB)…");
                string zip = Bajar(Python, "python-embed.zip");
                if (zip == null) return;
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(zip, dir);
                File.Delete(zip);
                // Habilitar site-packages en la distribución embebida.
                var pth = Directory.GetFiles(dir, "python*._pth").FirstOrDefault();
                if (pth != null)
                {
                    var lineas = File.ReadAllLines(pth).Select(l => l.Trim() == "#import site" ? "import site" : l).ToList();
                    if (!lineas.Contains("Lib\\site-packages")) lineas.Insert(Math.Max(0, lineas.Count - 1), "Lib\\site-packages");
                    File.WriteAllLines(pth, lineas);
                }
                Paso("Python embebido listo.");
            }
            else Paso("Python embebido ya estaba.");

            if (Ejecutar(py, "-I -c \"import edge_tts;print(edge_tts.__version__)\"", 30000) == 0)
            {
                Paso("edge-tts ya estaba instalado.");
                return;
            }
            if (Ejecutar(py, "-I -m pip --version", 30000) != 0)
            {
                Paso("Instalando pip…");
                string gp = Path.Combine(baseDir, "tmp", "get-pip.py");
                if (!BajarSimple(GetPip, gp)) return;
                if (Ejecutar(py, "\"" + gp + "\" --no-warn-script-location --disable-pip-version-check", 300000) != 0) { Fallo("No se pudo instalar pip."); return; }
                File.Delete(gp);
            }
            Paso("Instalando edge-tts " + EdgeTtsVersion + "…");
            if (Ejecutar(py, "-I -m pip install --disable-pip-version-check --no-warn-script-location edge-tts==" + EdgeTtsVersion, 300000) != 0)
            { Fallo("No se pudo instalar edge-tts (¿sin internet?). El lector usará la voz local."); return; }
            Paso("edge-tts listo.");
        }

        // ---------- Piper (voz local sin internet) ----------
        private static void PrepararPiper()
        {
            string dir = Path.Combine(baseDir, "piper");
            if (!File.Exists(Path.Combine(dir, "piper.exe")))
            {
                Paso("Descargando Piper (22 MB)…");
                string zip = Bajar(Piper, "piper.zip");
                if (zip != null)
                {
                    string temp = Path.Combine(baseDir, "tmp", "piper-zip");
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    ZipFile.ExtractToDirectory(zip, temp);
                    string origen = Directory.Exists(Path.Combine(temp, "piper")) ? Path.Combine(temp, "piper") : temp;
                    CopiarDirectorio(origen, dir);
                    Directory.Delete(temp, true);
                    File.Delete(zip);
                    Paso("Piper listo.");
                }
            }
            else Paso("Piper ya estaba.");

            string voces = Path.Combine(dir, "voces");
            Directory.CreateDirectory(voces);
            string onnx = Path.Combine(voces, "es_MX-ald-medium.onnx");
            if (!File.Exists(onnx) || !HashOk(onnx, VozOnnx.Sha256))
            {
                Paso("Descargando la voz local es_MX-ald (63 MB)…");
                string f = Bajar(VozOnnx, "voz.onnx");
                if (f != null) { if (File.Exists(onnx)) File.Delete(onnx); File.Move(f, onnx); Paso("Voz local lista."); }
            }
            else Paso("Voz local ya estaba.");
            string json = onnx + ".json";
            if (!File.Exists(json))
            {
                string f = Bajar(VozJson, "voz.json");
                if (f != null) File.Move(f, json);
            }
        }

        // ---------- Utilidades ----------
        private static string Bajar(Descarga d, string nombre)
        {
            string destino = Path.Combine(baseDir, "tmp", nombre);
            for (int intento = 1; intento <= 3; intento++)
            {
                try
                {
                    if (File.Exists(destino)) File.Delete(destino);
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "LectorKaraoke-Configurar");
                        wc.DownloadFile(d.Url, destino);
                    }
                    if (HashOk(destino, d.Sha256)) return destino;
                    Paso("  La descarga no coincide con la huella SHA-256 esperada (intento " + intento + ").");
                }
                catch (Exception e) { Paso("  Falló la descarga (intento " + intento + "): " + e.Message); }
                Thread.Sleep(1500 * intento);
            }
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
            Fallo("No se pudo descargar " + d.Url);
            return null;
        }

        private static bool BajarSimple(string url, string destino)
        {
            try
            {
                using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "LectorKaraoke-Configurar"); wc.DownloadFile(url, destino); }
                return true;
            }
            catch (Exception e) { Fallo("No se pudo descargar " + url + ": " + e.Message); return false; }
        }

        private static bool HashOk(string f, string esperado)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(f))
            {
                var h = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                return h == esperado;
            }
        }

        private static void CopiarDirectorio(string origen, string destino)
        {
            Directory.CreateDirectory(destino);
            foreach (var f in Directory.GetFiles(origen)) File.Copy(f, Path.Combine(destino, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(origen)) CopiarDirectorio(d, Path.Combine(destino, Path.GetFileName(d)));
        }

        private static int Ejecutar(string exe, string args, int msMax)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(msMax)) { try { p.Kill(); } catch { } return -1; }
                Registro.Info(Path.GetFileName(exe) + " " + args + " -> " + p.ExitCode + " | " + (o.Result + " " + e.Result).Replace("\r", " ").Replace("\n", " ").Trim());
                return p.ExitCode;
            }
        }

        private static void ReiniciarLector()
        {
            string exe = Path.Combine(baseDir, "LectorKaraoke.exe");
            // UseShellExecute: el lector no hereda la consola de Configurar (si no, quien lea esa salida se queda esperando).
            using (Process.Start(new ProcessStartInfo(exe, "--reiniciar") { UseShellExecute = true, WorkingDirectory = baseDir, WindowStyle = ProcessWindowStyle.Hidden })) { }
            Thread.Sleep(1500);
            bool corre = Process.GetProcessesByName("LectorKaraoke").Any();
            if (corre) Paso("Lector reiniciado: icono en la bandeja, tecla según config.ini.");
            else Fallo("El lector no quedó corriendo. Revisa logs\\lector.log.");
        }

        private static void Desinstalar()
        {
            Inicio.Quitar();
            Paso("Arranque automático quitado.");
            string exe = Path.Combine(baseDir, "LectorKaraoke.exe");
            if (File.Exists(exe))
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, "--salir") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })) p.WaitForExit(8000);
            }
            Paso("Lector cerrado. La carpeta queda intacta; puedes borrarla si ya no la usas.");
        }

        private static void ActualizarVoz()
        {
            string py = Path.Combine(baseDir, "motor", "python", "python.exe");
            if (!File.Exists(py)) { Fallo("Falta Python embebido: ejecuta Configurar.exe sin opciones."); return; }
            Paso("Actualizando edge-tts…");
            if (Ejecutar(py, "-I -m pip install --upgrade --disable-pip-version-check --no-warn-script-location edge-tts", 300000) == 0) Paso("edge-tts actualizado.");
            else Fallo("No se pudo actualizar edge-tts.");
            ReiniciarLector();
        }

        private static void Paso(string m) { Console.WriteLine(m); Registro.Info(m); }
        private static void Fallo(string m) { errores++; Console.WriteLine("ERROR: " + m); Registro.Aviso(m); }
    }
}
