using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LectorKaraoke
{
    internal sealed class Audio
    {
        public string Archivo;
        public string Motor;
        public Trozo Trozo;
        public double DuracionMs;
    }

    internal interface IMotor
    {
        string Nombre { get; }
        bool Disponible(Configuracion cfg);
        Task<Audio> Sintetizar(Trozo t, Configuracion cfg, CancellationToken ct);
        void Liberar();
    }

    internal sealed class FalloMotor : Exception
    {
        public readonly string Tipo;
        public FalloMotor(string tipo, string msg) : base(msg) { Tipo = tipo; }
    }

    // ===================================================================================
    // Edge (Jorge y demás voces neuronales). Proceso Python embebido que vive mientras se usa.
    // ===================================================================================
    internal sealed class MotorEdge : IMotor
    {
        private readonly string baseDir, python, script, tmp;
        private Process proc;
        private Stream entrada;
        private readonly object candado = new object();
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pendientes = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private int siguienteId;
        private DateTime ultimoUso = DateTime.MinValue;
        private DateTime caidoHasta = DateTime.MinValue;
        private DateTime ultimaActualizacion = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> vocesFallidas = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private bool actualizando;
        private readonly System.Threading.Timer reloj;
        private int esperaMin = 5;
        public event Action<string> Aviso;

        public string Nombre { get { return "edge"; } }

        public MotorEdge(string baseDir)
        {
            this.baseDir = baseDir;
            python = Path.Combine(baseDir, "motor", "python", "python.exe");
            script = Path.Combine(baseDir, "motor", "lector_edge.py");
            tmp = Path.Combine(baseDir, "tmp");
            reloj = new System.Threading.Timer(RevisarInactividad, null, 30000, 30000);
        }

        public bool Disponible(Configuracion cfg)
        {
            esperaMin = cfg.EsperaMotorMin;
            return cfg.UsarInternet && File.Exists(python) && File.Exists(script) && DateTime.Now >= caidoHasta && !actualizando;
        }

        // Se llama al presionar la tecla: el proceso arranca mientras se captura el texto.
        public void Precalentar(Configuracion cfg)
        {
            if (!Disponible(cfg)) return;
            Task.Run(() => { try { Asegurar(); ultimoUso = DateTime.Now; } catch (Exception e) { Registro.Error("Precalentando edge", e); } });
        }

        private void Asegurar()
        {
            lock (candado)
            {
                if (proc != null && !proc.HasExited) return;
                var psi = new ProcessStartInfo(python, "-I -u \"" + script + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.Combine(baseDir, "motor"),
                    StandardOutputEncoding = Encoding.UTF8,
                };
                proc = Process.Start(psi);
                entrada = proc.StandardInput.BaseStream;
                var p = proc;
                var lector = new Thread(() => LeerSalida(p)) { IsBackground = true, Name = "edge-salida" };
                lector.Start();
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Registro.Aviso("edge stderr: " + Recortar(e.Data, 300)); };
                p.BeginErrorReadLine();
                Registro.Info("Motor edge iniciado (pid " + p.Id + ").");
            }
        }

        private void LeerSalida(Process p)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            try
            {
                string linea;
                while ((linea = p.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> d;
                    try { d = js.DeserializeObject(linea) as Dictionary<string, object>; }
                    catch { continue; }
                    if (d == null || !d.ContainsKey("id")) continue;
                    int id = Convert.ToInt32(d["id"], CultureInfo.InvariantCulture);
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    lock (candado)
                    {
                        if (!pendientes.TryGetValue(id, out tcs)) continue;
                        pendientes.Remove(id);
                    }
                    tcs.TrySetResult(d);
                }
            }
            catch (Exception e) { Registro.Error("Lectura del motor edge", e); }
            // El proceso terminó: fallan las peticiones pendientes. Si no lo cerró el lector, se marca como caído
            // un rato para no reintentar (con su demora de arranque) en cada trozo.
            lock (candado)
            {
                if (ReferenceEquals(proc, p))
                {
                    caidoHasta = DateTime.Now.AddSeconds(30);
                    Registro.Aviso("El motor edge terminó inesperadamente; se usa la voz local durante 30 s.");
                }
                foreach (var t in pendientes.Values) t.TrySetException(new FalloMotor("otro", "el motor edge terminó"));
                pendientes.Clear();
            }
        }

        private void Enviar(string json)
        {
            var b = Encoding.ASCII.GetBytes(json + "\n");
            lock (candado)
            {
                entrada.Write(b, 0, b.Length);
                entrada.Flush();
            }
        }

        public async Task<Audio> Sintetizar(Trozo t, Configuracion cfg, CancellationToken ct)
        {
            string voz = VozPara(t, cfg);
            DateTime hasta;
            lock (candado)
                if (vocesFallidas.TryGetValue(voz, out hasta) && DateTime.Now < hasta)
                    throw new FalloMotor("voz", "la voz " + voz + " no respondió hace poco");
            Asegurar();
            ultimoUso = DateTime.Now;
            int id = Interlocked.Increment(ref siguienteId);
            string archivo = Path.Combine(tmp, "e" + id + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp3");
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            lock (candado) pendientes[id] = tcs;
            string vel = (cfg.Velocidad >= 0 ? "+" : "") + cfg.Velocidad + "%";
            var sb = new StringBuilder();
            sb.Append("{\"accion\":\"hablar\",\"id\":").Append(id)
              .Append(",\"texto\":").Append(Json(t.Texto))
              .Append(",\"voz\":").Append(Json(voz))
              .Append(",\"velocidad\":").Append(Json(vel))
              .Append(",\"archivo\":").Append(Json(archivo)).Append("}");
            try { Enviar(sb.ToString()); }
            catch (Exception e)
            {
                lock (candado) pendientes.Remove(id);
                Liberar();
                throw new FalloMotor("otro", "no se pudo hablar con el motor edge: " + e.Message);
            }

            var espera = Task.Delay(TimeSpan.FromSeconds(14), ct);
            var listo = await Task.WhenAny(tcs.Task, espera).ConfigureAwait(false);
            if (listo != tcs.Task)
            {
                lock (candado) pendientes.Remove(id);
                try { Enviar("{\"accion\":\"cancelar\",\"id\":" + id + "}"); } catch { }
                Borrar(archivo);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                caidoHasta = DateTime.Now.AddSeconds(20);
                throw new FalloMotor("red", "el motor edge no respondió a tiempo");
            }
            var r = await tcs.Task.ConfigureAwait(false);
            ultimoUso = DateTime.Now;
            if (!(r.ContainsKey("ok") && r["ok"] is bool && (bool)r["ok"]))
            {
                string tipo = r.ContainsKey("tipo") ? Convert.ToString(r["tipo"]) : "otro";
                string err = r.ContainsKey("error") ? Convert.ToString(r["error"]) : "?";
                if (tipo == "red") caidoHasta = DateTime.Now.AddSeconds(60);
                if (tipo == "voz")
                {
                    // La voz no existe o está mal escrita: no se reintenta en cada trozo (las demás voces siguen).
                    lock (candado) vocesFallidas[voz] = DateTime.Now.AddMinutes(2);
                    var h = Aviso;
                    if (h != null) h("La voz " + voz + " no respondió (¿nombre mal escrito en config.ini?). Se usa la voz local.");
                }
                if (tipo == "bloqueo")
                {
                    caidoHasta = DateTime.Now.AddMinutes(10);
                    ActualizarEnSegundoPlano();
                }
                throw new FalloMotor(tipo, err);
            }
            ct.ThrowIfCancellationRequested();
            var marcas = new List<Marca>();
            var arr = r.ContainsKey("marcas") ? r["marcas"] as object[] : null;
            if (arr != null)
                foreach (var o in arr)
                {
                    var m = o as object[];
                    if (m == null || m.Length < 3) continue;
                    marcas.Add(new Marca(Convert.ToString(m[0]), Convert.ToDouble(m[1], CultureInfo.InvariantCulture), Convert.ToDouble(m[2], CultureInfo.InvariantCulture)));
                }
            double dur = marcas.Count > 0 ? marcas[marcas.Count - 1].InicioMs + marcas[marcas.Count - 1].DuracionMs + 300 : 1000;
            Tiempos.Alinear(t, marcas, dur);
            return new Audio { Archivo = archivo, Motor = "edge", Trozo = t, DuracionMs = dur };
        }

        internal static string VozPara(Trozo t, Configuracion cfg)
        {
            if (!string.IsNullOrEmpty(cfg.VozIngles) && Preparador.EsIngles(t.Texto)) return cfg.VozIngles;
            return cfg.VozEdge;
        }

        // Microsoft cambia a veces el acceso de edge-tts (error 403). Se intenta actualizar la librería una vez al día.
        private void ActualizarEnSegundoPlano()
        {
            if (actualizando || (DateTime.Now - ultimaActualizacion).TotalHours < 24) return;
            actualizando = true;
            ultimaActualizacion = DateTime.Now;
            var h = Aviso;
            if (h != null) h("Microsoft rechazó la voz en línea; se intenta actualizar edge-tts. Mientras tanto se usa la voz local.");
            Task.Run(() =>
            {
                try
                {
                    Liberar();
                    var psi = new ProcessStartInfo(python, "-I -m pip install --upgrade --disable-pip-version-check --no-warn-script-location edge-tts")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var p = Process.Start(psi))
                    {
                        string salida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(180000);
                        Registro.Info("Actualización de edge-tts terminó con código " + p.ExitCode + ": " + Recortar(salida.Replace("\r", " ").Replace("\n", " "), 400));
                        if (p.ExitCode == 0) caidoHasta = DateTime.MinValue;
                    }
                }
                catch (Exception e) { Registro.Error("Actualizando edge-tts", e); }
                finally { actualizando = false; }
            });
        }

        private void RevisarInactividad(object _)
        {
            try
            {
                bool ocupado;
                lock (candado) ocupado = pendientes.Count > 0;
                if (proc != null && !ocupado && (DateTime.Now - ultimoUso).TotalMinutes >= esperaMin) Liberar();
            }
            catch { }
        }

        public void Liberar()
        {
            Process p;
            lock (candado)
            {
                p = proc;
                proc = null;
                if (entrada != null) { try { entrada.Close(); } catch { } entrada = null; }
            }
            if (p == null) return;
            try
            {
                if (!p.WaitForExit(800)) p.Kill();
                Registro.Info("Motor edge cerrado.");
            }
            catch { }
            finally { p.Dispose(); }
        }

        internal static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        internal static void Borrar(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        internal static string Recortar(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "…"; }
    }

    // ===================================================================================
    // Piper: voz neuronal local, sin internet. No da marcas por palabra: se estiman.
    // ===================================================================================
    internal sealed class MotorPiper : IMotor
    {
        private readonly string exe, voces, tmp;
        public string Nombre { get { return "piper"; } }

        public MotorPiper(string baseDir)
        {
            exe = Path.Combine(baseDir, "piper", "piper.exe");
            voces = Path.Combine(baseDir, "piper", "voces");
            tmp = Path.Combine(baseDir, "tmp");
        }

        private string Modelo(Configuracion cfg) { return Path.Combine(voces, cfg.VozPiper + ".onnx"); }

        public bool Disponible(Configuracion cfg)
        {
            string m = Modelo(cfg);
            return File.Exists(exe) && File.Exists(m) && File.Exists(m + ".json");
        }

        public Task<Audio> Sintetizar(Trozo t, Configuracion cfg, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                string wav = Path.Combine(tmp, "p" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".wav");
                double escala = 1.0 / (1.0 + cfg.Velocidad / 100.0);
                var psi = new ProcessStartInfo(exe, "--model \"" + Modelo(cfg) + "\" --output_file \"" + wav + "\" --length_scale " +
                    escala.ToString("0.###", CultureInfo.InvariantCulture) + " --sentence_silence 0.15")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var b = Encoding.UTF8.GetBytes(MotorSapi.TextoLocal(t.Texto).Replace('\n', ' ').Replace('\r', ' ') + "\n");
                    p.StandardInput.BaseStream.Write(b, 0, b.Length);
                    p.StandardInput.Close();
                    var limite = DateTime.Now.AddSeconds(30);
                    while (!p.WaitForExit(50))
                    {
                        if (ct.IsCancellationRequested || DateTime.Now > limite)
                        {
                            try { p.Kill(); } catch { }
                            MotorEdge.Borrar(wav);
                            ct.ThrowIfCancellationRequested();
                            throw new FalloMotor("otro", "piper no terminó a tiempo");
                        }
                    }
                    if (p.ExitCode != 0 || !File.Exists(wav)) throw new FalloMotor("otro", "piper terminó con código " + p.ExitCode);
                }
                double total, ini, fin;
                Wav.Analizar(wav, out total, out ini, out fin);
                Tiempos.Estimar(t, ini, fin);
                return new Audio { Archivo = wav, Motor = "piper", Trozo = t, DuracionMs = total };
            }, ct);
        }

        public void Liberar() { }
    }

    // ===================================================================================
    // SAPI: voces de Windows. Marcas exactas por palabra (evento SpeakProgress).
    // ===================================================================================
    internal sealed class MotorSapi : IMotor
    {
        private readonly string tmp;
        public string Nombre { get { return "sapi"; } }
        public MotorSapi(string baseDir) { tmp = Path.Combine(baseDir, "tmp"); }

        public bool Disponible(Configuracion cfg) { return true; }

        public Task<Audio> Sintetizar(Trozo t, Configuracion cfg, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                string wav = Path.Combine(tmp, "s" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".wav");
                using (var s = new SpeechSynthesizer())
                {
                    ElegirVoz(s, cfg);
                    s.Rate = Math.Max(-10, Math.Min(10, (int)Math.Round(cfg.Velocidad / 10.0)));
                    s.SetOutputToWaveFile(wav);
                    foreach (var p in t.Palabras) { p.InicioMs = -1; p.FinMs = -1; }
                    s.SpeakProgress += (o, e) =>
                    {
                        int i = PalabraEnPos(t, e.CharacterPosition);
                        if (i >= 0 && t.Palabras[i].InicioMs < 0) t.Palabras[i].InicioMs = e.AudioPosition.TotalMilliseconds;
                    };
                    var prompt = s.SpeakAsync(TextoLocal(t.Texto));
                    while (!prompt.IsCompleted)
                    {
                        if (ct.IsCancellationRequested) { s.SpeakAsyncCancelAll(); break; }
                        Thread.Sleep(20);
                    }
                    s.SetOutputToNull();
                }
                ct.ThrowIfCancellationRequested();
                double total, ini, fin;
                Wav.Analizar(wav, out total, out ini, out fin);
                // SAPI informa posiciones algo desfasadas respecto al audio real en frases largas:
                // si la última palabra "empieza" después de que termina la voz, se reescala todo a la voz real.
                double maxInicio = 0;
                foreach (var p in t.Palabras) maxInicio = Math.Max(maxInicio, p.InicioMs);
                if (maxInicio > 0 && fin > 0 && maxInicio > fin * 0.97)
                {
                    double escala = (fin * 0.92) / maxInicio;
                    foreach (var p in t.Palabras) if (p.InicioMs >= 0) p.InicioMs *= escala;
                }
                Tiempos.Interpolar(t, total);
                return new Audio { Archivo = wav, Motor = "sapi", Trozo = t, DuracionMs = total };
            }, ct);
        }

        private static void ElegirVoz(SpeechSynthesizer s, Configuracion cfg)
        {
            var voces = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            VoiceInfo elegida = null;
            if (!string.IsNullOrEmpty(cfg.VozSapi))
                elegida = voces.FirstOrDefault(v => v.Name.IndexOf(cfg.VozSapi, StringComparison.OrdinalIgnoreCase) >= 0);
            if (elegida == null) elegida = voces.FirstOrDefault(v => v.Culture.Name.StartsWith("es-MX", StringComparison.OrdinalIgnoreCase));
            if (elegida == null) elegida = voces.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "es");
            if (elegida != null) s.SelectVoice(elegida.Name);
        }

        // La mini pausa "·" la entienden las voces de Edge; Piper y SAPI podrían leerla: se cambia por una coma
        // (mismo largo, así las posiciones de las palabras no cambian).
        internal static string TextoLocal(string texto) { return texto.Replace('·', ','); }

        private static int PalabraEnPos(Trozo t, int pos)
        {
            for (int i = t.Palabras.Count - 1; i >= 0; i--)
                if (t.Palabras[i].HablaInicio <= pos) return i;
            return -1;
        }

        public void Liberar() { }
    }

    // ===================================================================================
    // Elige el motor y cae al siguiente si falla.
    // ===================================================================================
    internal sealed class Motores
    {
        private readonly MotorEdge edge;
        private readonly MotorPiper piper;
        private readonly MotorSapi sapi;
        public event Action<string> Aviso;
        private string ultimoAviso = "";

        public Motores(string baseDir)
        {
            edge = new MotorEdge(baseDir);
            piper = new MotorPiper(baseDir);
            sapi = new MotorSapi(baseDir);
            edge.Aviso += m => Avisar(m);
        }

        private void Avisar(string m)
        {
            Registro.Aviso(m);
            if (m == ultimoAviso) return;
            ultimoAviso = m;
            var h = Aviso;
            if (h != null) h(m);
        }

        private IEnumerable<IMotor> Orden(Configuracion cfg)
        {
            if (cfg.Motor == "edge") return new IMotor[] { edge, piper, sapi };
            if (cfg.Motor == "piper") return new IMotor[] { piper, sapi };
            return new IMotor[] { sapi };
        }

        public async Task<Audio> Sintetizar(Trozo t, Configuracion cfg, CancellationToken ct)
        {
            Exception ultimo = null;
            foreach (var m in Orden(cfg))
            {
                if (!m.Disponible(cfg)) continue;
                try
                {
                    var sw = Stopwatch.StartNew();
                    var a = await m.Sintetizar(t, cfg, ct).ConfigureAwait(false);
                    Registro.Info("Trozo de " + t.Texto.Length + " car. sintetizado con " + m.Nombre + " en " + sw.ElapsedMilliseconds + " ms.");
                    if (m != Orden(cfg).First())
                        Avisar("Se está usando la voz " + m.Nombre + " porque la voz preferida no está disponible.");
                    return a;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    ultimo = e;
                    Registro.Error("Motor " + m.Nombre + " falló", e);
                }
            }
            throw new FalloMotor("otro", "ningún motor de voz pudo leer el texto" + (ultimo != null ? ": " + ultimo.Message : ""));
        }

        public void Precalentar(Configuracion cfg) { if (cfg.Motor == "edge") edge.Precalentar(cfg); }

        public void Liberar() { edge.Liberar(); }
    }

    // Lectura mínima de WAV PCM para saber duración y dónde empieza y termina la voz.
    internal static class Wav
    {
        public static void Analizar(string ruta, out double totalMs, out double vozInicioMs, out double vozFinMs)
        {
            totalMs = 0; vozInicioMs = 0; vozFinMs = 0;
            byte[] d = File.ReadAllBytes(ruta);
            int canales = 1, tasa = 22050, bits = 16, datos = -1, largo = 0;
            int i = 12;
            while (i + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, i, 4);
                int tam = BitConverter.ToInt32(d, i + 4);
                if (id == "fmt ")
                {
                    canales = BitConverter.ToInt16(d, i + 10);
                    tasa = BitConverter.ToInt32(d, i + 12);
                    bits = BitConverter.ToInt16(d, i + 22);
                }
                else if (id == "data") { datos = i + 8; largo = Math.Min(tam, d.Length - datos); break; }
                i += 8 + tam + (tam & 1);
            }
            if (datos < 0 || bits != 16) { totalMs = 1000; vozFinMs = 1000; return; }
            int muestras = largo / (2 * canales);
            totalMs = muestras * 1000.0 / tasa;
            int umbral = 900;
            int primero = -1, ultimo = -1;
            for (int k = 0; k < muestras; k++)
            {
                int v = Math.Abs((int)BitConverter.ToInt16(d, datos + k * 2 * canales));
                if (v > umbral) { if (primero < 0) primero = k; ultimo = k; }
            }
            if (primero < 0) { vozInicioMs = 0; vozFinMs = totalMs; return; }
            vozInicioMs = primero * 1000.0 / tasa;
            vozFinMs = ultimo * 1000.0 / tasa;
        }
    }
}
