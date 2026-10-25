using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LectorKaraoke
{
    internal enum Estado { Reposo, Capturando, Hablando }

    // Orquesta tecla → captura → voz → resaltado. Todo corre en el hilo de la interfaz salvo UIA y la síntesis.
    internal sealed class Lector : IDisposable
    {
        private readonly string baseDir, tmp;
        private Configuracion cfg;
        private HiloUia uia = new HiloUia();
        private readonly Motores motores;
        private readonly Reproductor rep = new Reproductor();
        private readonly CapaResaltado capa = new CapaResaltado();
        private readonly CapaFranja franja = new CapaFranja();
        private Estado estado = Estado.Reposo;
        private int generacion;
        private CancellationTokenSource cts;
        private DateTime ultimaPulsacion = DateTime.MinValue;
        private readonly bool prueba;
        public event Action<string> Aviso;
        public Estado EstadoActual { get { return estado; } }

        public Lector(string baseDir, Configuracion cfg, bool prueba)
        {
            this.baseDir = baseDir;
            this.cfg = cfg;
            this.prueba = prueba;
            tmp = Path.Combine(baseDir, "tmp");
            Directory.CreateDirectory(tmp);
            LimpiarTmp();
            motores = new Motores(baseDir);
            motores.Aviso += m => { var h = Aviso; if (h != null) h(m); };
            motores.Precalentar(cfg);
            capa.CreateControl();
            franja.CreateControl();
        }

        public void CambiarConfiguracion(Configuracion nueva) { cfg = nueva; }

        public void AlPresionar()
        {
            var ahora = DateTime.Now;
            if ((ahora - ultimaPulsacion).TotalMilliseconds < 250) return;   // antirrebote
            ultimaPulsacion = ahora;
            if (estado != Estado.Reposo) { Detener("tecla"); return; }
            var nop = Iniciar();
        }

        private async Task Iniciar()
        {
            estado = Estado.Capturando;
            int g = ++generacion;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var sw = Stopwatch.StartNew();
            motores.Precalentar(cfg);
            Captura cap;
            try
            {
                var propias = new[] { capa.Handle, franja.Handle };
                var cfgLocal = cfg;
                cap = await EnUia(() => Captor.Capturar(cfgLocal, propias), 8000, "captura");
            }
            catch (Exception e)
            {
                Registro.Error("Captura", e);
                Terminar(g);
                Pitido();
                return;
            }
            if (g != generacion) return;
            Registro.Info("Captura: fuente=" + cap.Fuente + " proceso=" + cap.Proceso + " largo=" + cap.Texto.Length +
                          (cap.Motivo.Length > 0 ? " motivo=" + cap.Motivo : "") + " en " + sw.ElapsedMilliseconds + " ms.");
            Prueba("captura", "fuente=" + cap.Fuente + "|proceso=" + cap.Proceso + "|texto=" + cap.Texto);
            if (cfg.RegistrarTexto) Registro.Info("Texto: " + cap.Texto.Replace("\r", "\\r").Replace("\n", "\\n"));

            if (cap.Fuente == Fuente.Nada) { Terminar(g); Pitido(); return; }
            var prep = Preparador.Preparar(cap.Texto, cfg);
            if (prep.Secretos > 0) Registro.Info("Se ocultaron " + prep.Secretos + " valores sensibles.");
            if (prep.Truncado) Registro.Aviso("Selección recortada a " + cfg.MaxCaracteres + " caracteres.");
            Prueba("preparado", "trozos=" + prep.Trozos.Count + "|secretos=" + prep.Secretos + "|habla=" + string.Join(" / ", prep.Trozos.Select(t => t.Texto)));
            if (prep.Trozos.Count == 0) { Terminar(g); Pitido(); return; }

            estado = Estado.Hablando;
            terminal = null;
            if (cap.Fuente == Fuente.Copia && !cap.Rejilla.IsEmpty) terminal = UbicarTerminal(cap);
            try { await Hablar(g, cap, prep, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (g == generacion)
                {
                    Registro.Error("Lectura", e);
                    var h = Aviso;
                    if (h != null) h("No se pudo leer: " + e.Message);
                    Pitido();
                }
            }
            finally { Terminar(g); }
        }

        private async Task Hablar(int g, Captura cap, Preparado prep, CancellationToken ct)
        {
            var trozos = prep.Trozos;
            if (cap.Fuente == Fuente.Mouse && !cap.Zona.IsEmpty)
                capa.Dibujar(new List<Rectangle>(), new List<Rectangle>(), cap.Zona, cfg);
            if (cap.Fuente == Fuente.Copia && cfg.Franja) franja.Iniciar(cap.Puntero);

            var cfgLocal = cfg;
            // Con Edge se piden varios trozos a la vez (el servidor tarda más que lo que dura un trozo corto);
            // con los motores locales, uno por delante para no saturar la CPU.
            int adelante = cfgLocal.Motor == "edge" && cfgLocal.UsarInternet ? 3 : 1;
            var tareas = new Task<Audio>[trozos.Count];
            int usados = 0;
            try
            {
                for (int k = 0; k < trozos.Count && k <= adelante - 1; k++) tareas[k] = motores.Sintetizar(trozos[k], cfgLocal, ct);
                for (int i = 0; i < trozos.Count; i++)
                {
                    if (tareas[i] == null) tareas[i] = motores.Sintetizar(trozos[i], cfgLocal, ct);
                    Audio a = await tareas[i];
                    usados = i + 1;
                    if (g != generacion) { MotorEdge.Borrar(a.Archivo); return; }
                    int siguienteK = i + adelante;
                    if (siguienteK < trozos.Count && tareas[siguienteK] == null) tareas[siguienteK] = motores.Sintetizar(trozos[siguienteK], cfgLocal, ct);
                    Prueba("audio", "trozo=" + i + "|motor=" + a.Motor + "|tiempos=" + string.Join(",", a.Trozo.Palabras.Select(p => (int)p.InicioMs)));
                    double inicioVoz = a.Trozo.Palabras.Count > 0 ? Math.Max(0, a.Trozo.Palabras[0].InicioMs - 40) : 0;
                    if (!rep.Abrir(a.Archivo, cfg.Volumen) || !rep.Reproducir(inicioVoz))
                    {
                        MotorEdge.Borrar(a.Archivo);
                        throw new Exception("no se pudo reproducir el audio (" + rep.UltimoError + ")");
                    }
                    await Seguir(g, cap, a.Trozo, ct);
                    rep.Cerrar();
                    MotorEdge.Borrar(a.Archivo);
                    if (g != generacion) return;
                }
            }
            finally
            {
                // Al detener, los trozos ya pedidos por adelantado terminan después: se borran sus audios.
                for (int k = usados; k < tareas.Length; k++)
                    if (tareas[k] != null)
                    {
                        var nop = tareas[k].ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion && t.Result != null) MotorEdge.Borrar(t.Result.Archivo); });
                    }
            }
        }

        private Task<UbicadorTerminal> terminal;

        // Captura la rejilla de la terminal enseguida (antes de que cambie) y ubica las palabras con el OCR de Windows,
        // en paralelo con la síntesis de la voz. Si no es confiable, se sigue con la franja.
        private Task<UbicadorTerminal> UbicarTerminal(Captura cap)
        {
            var rejilla = cap.Rejilla;
            var texto = cap.Texto;
            var tarea = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                using (var bmp = OcrWindows.Capturar(rejilla, false))
                {
                    var ocr = OcrWindows.Reconocer(bmp).GetAwaiter().GetResult();
                    var u = UbicadorTerminal.Construir(texto, rejilla, bmp, ocr);
                    Registro.Info("Ubicación en terminal: " + u.Diagnostico + " en " + sw.ElapsedMilliseconds + " ms.");
                    Prueba("terminal", u.Diagnostico + "|ms=" + sw.ElapsedMilliseconds);
                    return u.Anclas >= 3 ? u : null;
                }
            });
            tarea.ContinueWith(t => Registro.Error("Ubicación en terminal", t.Exception != null ? t.Exception.GetBaseException() : null),
                TaskContinuationOptions.OnlyOnFaulted);
            return tarea;
        }

        private UbicadorTerminal TerminalLista()
        {
            var t = terminal;
            return t != null && t.Status == TaskStatus.RanToCompletion ? t.Result : null;
        }

        // Mientras suena el trozo, mueve el resaltado a la palabra que se está diciendo.
        private async Task Seguir(int g, Captura cap, Trozo t, CancellationToken ct)
        {
            int ultima = -2;
            var leidos = new List<Rectangle>();
            var actuales = new List<Rectangle>();
            bool pidiendo = false;
            var inicio = DateTime.Now;
            // Fin de la voz: la última palabra termina; el silencio final del audio no se espera.
            double finVoz = 0;
            foreach (var w in t.Palabras) finVoz = Math.Max(finVoz, w.FinMs);
            while (g == generacion)
            {
                await Task.Delay(30);
                if (g != generacion) return;
                double pos = rep.Posicion();
                int idx = Tiempos.PalabraEn(t, pos);
                if (idx != ultima && idx >= 0)
                {
                    ultima = idx;
                    if (cap.Fuente == Fuente.Accesibilidad && !pidiendo)
                    {
                        if (VentanaUtil(cap.Ventana))
                        {
                            pidiendo = true;
                            var p = t.Palabras[idx];
                            int indice = idx;
                            var ubic = cap.Ubicador;
                            var tarea = EnUia(() => ubic.Rects(p.OrigInicio, p.OrigLargo), 2500, "posición de palabra");
                            var nop = tarea.ContinueWith(r =>
                            {
                                pidiendo = false;
                                if (g != generacion || r.IsFaulted) return;
                                leidos.AddRange(actuales);
                                actuales = AjustarAlto(r.Result, p.OrigLargo);
                                Prueba("rects", "palabra=" + indice + "|" + string.Join(";", actuales.Select(x => x.X + "," + x.Y + "," + x.Width + "," + x.Height)));
                                if (actuales.Count == 0 && leidos.Count == 0) { capa.Ocultar(); return; }
                                capa.Dibujar(actuales, leidos, Rectangle.Empty, cfg);
                            }, TaskScheduler.FromCurrentSynchronizationContext());
                        }
                        else capa.Ocultar();
                    }
                    else if (cap.Fuente == Fuente.Copia && TerminalLista() != null)
                    {
                        // Resaltado sobre el texto de la terminal: cálculo puro, sin consultas lentas.
                        franja.Ocultar();
                        var p = t.Palabras[idx];
                        leidos.AddRange(actuales);
                        actuales = TerminalLista().Rects(p.OrigInicio, p.OrigLargo);
                        Prueba("rects", "palabra=" + idx + "|" + string.Join(";", actuales.Select(x => x.X + "," + x.Y + "," + x.Width + "," + x.Height)));
                        if (VentanaUtil(cap.Ventana) && (actuales.Count > 0 || leidos.Count > 0)) capa.Dibujar(actuales, leidos, Rectangle.Empty, cfg);
                        else capa.Ocultar();
                    }
                    else if (cap.Fuente == Fuente.Copia && cfg.Franja)
                    {
                        franja.Mostrar(t, idx, cfg);
                    }
                }
                if (!rep.Sonando() && (DateTime.Now - inicio).TotalMilliseconds > 200) break;
                if (finVoz > 0 && pos >= finVoz + 60) break;
            }
            if (cap.Fuente == Fuente.Accesibilidad || cap.Fuente == Fuente.Copia) capa.Ocultar();
        }

        // Ejecuta en el hilo UIA con tiempo límite. Si una aplicación colgada bloquea la consulta, ese hilo se descarta
        // y se crea otro, para que el lector siga respondiendo a la tecla.
        private async Task<T> EnUia<T>(Func<T> f, int ms, string que)
        {
            var hilo = uia;
            var t = hilo.Ejecutar(f);
            if (await Task.WhenAny(t, Task.Delay(ms)) == t) return await t;
            Registro.Aviso("La consulta de accesibilidad (" + que + ") tardó más de " + ms + " ms: se descarta y se usa un hilo nuevo.");
            if (ReferenceEquals(uia, hilo))
            {
                uia = new HiloUia();
                hilo.Dispose();
            }
            throw new TimeoutException(que);
        }

        // Algunas aplicaciones devuelven el rectángulo de la línea (con interlineado) y no el de las letras. Se estima
        // el alto de las letras por el ancho medio de cada carácter y se recorta desde abajo, donde está el texto.
        internal static List<Rectangle> AjustarAlto(List<Rectangle> rs, int caracteres)
        {
            if (rs.Count == 0 || caracteres <= 0) return rs;
            int ancho = 0;
            foreach (var r in rs) ancho += r.Width;
            double porCaracter = (double)ancho / caracteres;
            int alto = (int)Math.Round(porCaracter * 2.6);
            if (alto < 8) return rs;
            var res = new List<Rectangle>(rs.Count);
            foreach (var r in rs)
            {
                if (r.Height > alto * 1.25) res.Add(new Rectangle(r.X, r.Bottom - alto, r.Width, alto));
                else res.Add(r);
            }
            return res;
        }

        private static bool VentanaUtil(IntPtr h)
        {
            return h != IntPtr.Zero && Nativo.IsWindow(h) && Nativo.IsWindowVisible(h) && !Nativo.IsIconic(h);
        }

        public void Detener(string motivo)
        {
            if (estado == Estado.Reposo) return;
            Registro.Info("Lectura detenida (" + motivo + ").");
            Prueba("detenido", motivo);
            generacion++;
            if (cts != null) cts.Cancel();
            rep.Cerrar();
            capa.Ocultar();
            franja.Ocultar();
            franja.Reiniciar();
            estado = Estado.Reposo;
        }

        private void Terminar(int g)
        {
            if (g != generacion) return;
            rep.Cerrar();
            capa.Ocultar();
            franja.Ocultar();
            franja.Reiniciar();
            estado = Estado.Reposo;
            Prueba("fin", "");
            ProgramarRecorte(g);
        }

        // Tras leer, si sigue en reposo 15 s, se libera la memoria que dejó la lectura (imágenes, consultas UIA).
        private async void ProgramarRecorte(int g)
        {
            await Task.Delay(15000);
            if (g != generacion || estado != Estado.Reposo) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proceso, IntPtr min, IntPtr max);

        private static void Pitido() { Nativo.MessageBeep(0x00000030); }

        private void LimpiarTmp()
        {
            try { foreach (var f in Directory.GetFiles(tmp)) MotorEdge.Borrar(f); } catch { }
        }

        private void Prueba(string evento, string detalle)
        {
            if (!prueba) return;
            try
            {
                File.AppendAllText(Path.Combine(baseDir, "logs", "prueba.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + evento + "\t" + detalle.Replace("\r", "\\r").Replace("\n", "\\n") + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        public void Dispose()
        {
            Detener("salida");
            motores.Liberar();
            uia.Dispose();
            rep.Dispose();
            capa.Dispose();
            franja.Dispose();
            LimpiarTmp();
        }
    }
}
