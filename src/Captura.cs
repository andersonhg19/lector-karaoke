using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace LectorKaraoke
{
    internal enum Fuente { Nada, Accesibilidad, Copia, Mouse }

    internal sealed class Captura
    {
        public Fuente Fuente = Fuente.Nada;
        public string Texto = "";
        public string Proceso = "";
        public IntPtr Ventana;
        public Rectangle Zona;              // modo mouse: rectángulo del elemento o tooltip
        public Point Puntero;
        public UbicadorUia Ubicador;        // solo con Fuente.Accesibilidad
        public Rectangle Rejilla;           // copia en una terminal con ubicación por OCR (Orca): zona de la rejilla
        public string Motivo = "";
    }

    // Todas las llamadas de UI Automation en un hilo MTA dedicado (recomendado por Microsoft y evita bloquear la interfaz).
    internal sealed class HiloUia : IDisposable
    {
        private readonly BlockingCollection<Action> cola = new BlockingCollection<Action>();
        private readonly Thread hilo;

        public HiloUia()
        {
            hilo = new Thread(() => { foreach (var a in cola.GetConsumingEnumerable()) { try { a(); } catch { } } })
            { IsBackground = true, Name = "uia" };
            hilo.SetApartmentState(ApartmentState.MTA);
            hilo.Start();
        }

        public Task<T> Ejecutar<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>();
            cola.Add(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }

        public void Dispose() { cola.CompleteAdding(); }
    }

    // Traduce posiciones del texto seleccionado a rectángulos de pantalla de cada palabra.
    internal sealed class UbicadorUia
    {
        private readonly TextPatternRange[] rangos;
        private readonly int[] desde;      // posición de cada rango en el texto unido
        private readonly string texto;
        private readonly TextPatternRange[] cursor;
        private readonly int[] cursorPos;
        public bool Desplazar;      // seguir_lectura: llevar la vista a la palabra si está fuera

        public UbicadorUia(TextPatternRange[] rangos, int[] desde, string texto)
        {
            this.rangos = rangos;
            this.desde = desde;
            this.texto = texto;
            cursor = new TextPatternRange[rangos.Length];
            cursorPos = new int[rangos.Length];
        }

        // Se llama en el hilo UIA.
        public List<Rectangle> Rects(int origInicio, int largo)
        {
            var res = new List<Rectangle>();
            int r = 0;
            while (r + 1 < rangos.Length && desde[r + 1] <= origInicio) r++;
            int local = origInicio - desde[r];
            string esperado = texto.Substring(origInicio, Math.Min(largo, texto.Length - origInicio)).Trim();

            TextPatternRange rr = Construir(r, local, largo);
            if (rr == null) return res;
            if (!Igual(rr, esperado))
            {
                // Algunos programas cuentan distinto los saltos de línea: se busca la palabra cerca.
                TextPatternRange hallado = null;
                for (int delta = 1; delta <= 6 && hallado == null; delta++)
                {
                    foreach (int d in new[] { -delta, delta })
                    {
                        if (local + d < 0) continue;
                        var c = Construir(r, local + d, largo);
                        if (c != null && Igual(c, esperado)) { hallado = c; break; }
                    }
                }
                if (hallado != null) rr = hallado;
            }
            System.Windows.Rect[] rs;
            try { rs = rr.GetBoundingRectangles(); }
            catch { return res; }
            if (Desplazar && (rs == null || rs.Length == 0))
            {
                try { rr.ScrollIntoView(false); rs = rr.GetBoundingRectangles(); }
                catch { }
            }
            if (rs == null) return res;
            foreach (var x in rs)
            {
                if (x.IsEmpty || x.Width < 1 || x.Height < 1) continue;
                res.Add(new Rectangle((int)Math.Round(x.Left), (int)Math.Round(x.Top), (int)Math.Round(x.Width), (int)Math.Round(x.Height)));
            }
            return res;
        }

        private TextPatternRange Construir(int r, int local, int largo)
        {
            try
            {
                // Cursor incremental: mover desde la última palabra evita recorrer el texto desde el inicio cada vez.
                if (cursor[r] == null || local < cursorPos[r])
                {
                    cursor[r] = rangos[r].Clone();
                    cursor[r].MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor[r], TextPatternRangeEndpoint.Start);
                    cursorPos[r] = 0;
                }
                int avance = local - cursorPos[r];
                if (avance > 0)
                {
                    int movido = cursor[r].Move(TextUnit.Character, avance);
                    cursorPos[r] += movido;
                }
                var w = cursor[r].Clone();
                w.MoveEndpointByRange(TextPatternRangeEndpoint.End, w, TextPatternRangeEndpoint.Start);
                w.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, largo);
                return w;
            }
            catch { return null; }
        }

        private static bool Igual(TextPatternRange rr, string esperado)
        {
            try { return rr.GetText(esperado.Length + 8).Trim() == esperado; }
            catch { return false; }
        }
    }

    internal static class Captor
    {
        // Se ejecuta en el hilo UIA.
        public static Captura Capturar(Configuracion cfg, IntPtr[] propias)
        {
            var c = new Captura();
            Nativo.POINT pt;
            Nativo.GetCursorPos(out pt);
            c.Puntero = new Point(pt.X, pt.Y);

            IntPtr fg = Nativo.GetForegroundWindow();
            uint pid;
            Nativo.GetWindowThreadProcessId(fg, out pid);
            c.Ventana = fg;
            c.Proceso = Nativo.NombreProceso(pid);
            bool esPropia = propias.Contains(fg) || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            bool elevada = !esPropia && pid != 0 && Nativo.ProcesoElevado(pid) && !Nativo.ProcesoActualElevado();

            if (!esPropia && !elevada)
            {
                if (SeleccionUia(c, cfg)) return c;
                if (c.Motivo == "contraseña") return c;
                if (SeleccionCopia(c, cfg))
                {
                    // Terminales sin accesibilidad de texto: se guarda la rejilla para ubicar las palabras con OCR.
                    if (cfg.OcrTerminal && cfg.OcrProgramas.Contains(c.Proceso))
                    {
                        c.Rejilla = UbicadorTerminal.RejillaDesdeFoco();
                        if (c.Rejilla.IsEmpty) c.Rejilla = UbicadorTerminal.RejillaEnVentana(fg);
                    }
                    return c;
                }
            }
            if (elevada) c.Motivo = "ventana de administrador";
            BajoMouse(c, pt);
            return c;
        }

        // ---------- 1. Accesibilidad (UI Automation) ----------
        private static bool SeleccionUia(Captura c, Configuracion cfg)
        {
            AutomationElement foco;
            try { foco = AutomationElement.FocusedElement; }
            catch { return false; }
            if (foco == null) return false;
            try
            {
                if (foco.Current.IsPassword) { c.Motivo = "contraseña"; return false; }
            }
            catch { }
            var el = foco;
            for (int nivel = 0; el != null && nivel < 4; nivel++)
            {
                object pat;
                try
                {
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                    {
                        var tp = (TextPattern)pat;
                        TextPatternRange[] sel;
                        try { sel = tp.GetSelection(); }
                        catch { sel = null; }
                        if (sel != null && sel.Length > 0)
                        {
                            var sb = new StringBuilder();
                            var desde = new List<int>();
                            var rangos = new List<TextPatternRange>();
                            foreach (var r in sel)
                            {
                                string t = r.GetText(cfg.MaxCaracteres + 1);
                                if (string.IsNullOrEmpty(t)) continue;
                                if (sb.Length > 0) sb.Append('\n');
                                desde.Add(sb.Length);
                                rangos.Add(r);
                                sb.Append(t);
                                if (sb.Length > cfg.MaxCaracteres) break;
                            }
                            if (sb.ToString().Trim().Length > 0)
                            {
                                c.Fuente = Fuente.Accesibilidad;
                                c.Texto = sb.ToString();
                                c.Ubicador = new UbicadorUia(rangos.ToArray(), desde.ToArray(), c.Texto) { Desplazar = cfg.SeguirLectura };
                                return true;
                            }
                        }
                        return false;   // el elemento con texto existe pero no hay selección
                    }
                }
                catch { }
                try { el = TreeWalker.ControlViewWalker.GetParent(el); }
                catch { el = null; }
                if (el == AutomationElement.RootElement) break;
            }
            return false;
        }

        // ---------- 2. Copia controlada ----------
        private static bool SeleccionCopia(Captura c, Configuracion cfg)
        {
            if (c.Proceso.Length == 0 || cfg.SinCopia.Contains(c.Proceso)) return false;
            string atajo;
            if (!cfg.AtajosCopia.TryGetValue(c.Proceso, out atajo)) atajo = cfg.AtajoCopiaPredeterminado;
            ushort[] vks;
            if (!Configuracion.TeclasDeAtajo(atajo, out vks)) return false;

            // Esperar a que suelte la tecla del lector para no mezclarla con el atajo.
            var limite = DateTime.Now.AddMilliseconds(600);
            while (DateTime.Now < limite && TeclaAbajo(cfg.TeclaVk)) Thread.Sleep(15);
            foreach (int m in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if (TeclaAbajo((uint)m)) { c.Motivo = "modificador presionado"; return false; }

            string texto = Portapapeles.CopiarSeleccion(vks, c.Ventana);
            if (string.IsNullOrEmpty(texto) || texto.Trim().Length == 0) return false;
            if (texto.Length > cfg.MaxCaracteres) texto = texto.Substring(0, cfg.MaxCaracteres);
            c.Fuente = Fuente.Copia;
            c.Texto = texto;
            return true;
        }

        private static bool TeclaAbajo(uint vk) { return (Nativo.GetAsyncKeyState((int)vk) & 0x8000) != 0; }

        // ---------- 3. Bajo el mouse ----------
        private static void BajoMouse(Captura c, Nativo.POINT pt)
        {
            if (BuscarTooltip(c, pt)) return;
            AutomationElement el = null;
            try { el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); }
            catch { }
            // En apps XAML o Electron la consulta por coordenadas a veces devuelve la ventana o un panel entero:
            // se busca dentro el elemento más pequeño que contenga el puntero y tenga nombre.
            el = Refinar(el, pt);
            for (int nivel = 0; el != null && nivel < 3; nivel++)
            {
                string texto = TextoDe(el);
                if (texto.Length > 0)
                {
                    c.Fuente = Fuente.Mouse;
                    c.Texto = texto;
                    c.Zona = Rect(el);
                    return;
                }
                try { el = TreeWalker.ControlViewWalker.GetParent(el); } catch { el = null; }
                if (el == AutomationElement.RootElement) break;
            }
            if (c.Motivo.Length == 0) c.Motivo = "nada que leer bajo el puntero";
        }

        private static AutomationElement Refinar(AutomationElement el, Nativo.POINT pt)
        {
            if (el == null) return null;
            try
            {
                var tipo = el.Current.ControlType;
                if (tipo != ControlType.Window && tipo != ControlType.Pane && tipo != ControlType.Custom && tipo != ControlType.Group) return el;
                var hijos = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                AutomationElement mejor = null;
                double area = double.MaxValue;
                int revisados = 0;
                foreach (AutomationElement h in hijos)
                {
                    if (++revisados > 3000) break;
                    var c = h.Current;
                    var r = c.BoundingRectangle;
                    if (r.IsEmpty || c.IsOffscreen || !r.Contains(pt.X, pt.Y)) continue;
                    if (string.IsNullOrEmpty(c.Name) && string.IsNullOrEmpty(c.HelpText)) continue;
                    double a = r.Width * r.Height;
                    if (a < area) { area = a; mejor = h; }
                }
                return mejor ?? el;
            }
            catch { return el; }
        }

        private static string TextoDe(AutomationElement el)
        {
            try
            {
                var cur = el.Current;
                if (cur.IsPassword) return "";
                string nombre = (cur.Name ?? "").Trim();
                string ayuda = (cur.HelpText ?? "").Trim();
                if (nombre.Length == 0)
                {
                    object pat;
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                    {
                        string v = ((ValuePattern)pat).Current.Value ?? "";
                        if (v.Length <= 500) nombre = v.Trim();
                    }
                }
                if (nombre.Length > 2000) nombre = nombre.Substring(0, 2000);
                if (ayuda.Length > 0 && !ayuda.Equals(nombre, StringComparison.OrdinalIgnoreCase) && nombre.IndexOf(ayuda, StringComparison.OrdinalIgnoreCase) < 0)
                    return nombre.Length > 0 ? nombre + ". " + ayuda : ayuda;
                return nombre;
            }
            catch { return ""; }
        }

        private static bool BuscarTooltip(Captura c, Nativo.POINT pt)
        {
            try
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip);
                var lista = AutomationElement.RootElement.FindAll(TreeScope.Children, cond);
                AutomationElement mejor = null;
                double mejorDist = double.MaxValue;
                foreach (AutomationElement t in lista)
                {
                    var r = t.Current.BoundingRectangle;
                    if (r.IsEmpty || t.Current.IsOffscreen) continue;
                    double dx = Math.Max(0, Math.Max(r.Left - pt.X, pt.X - r.Right));
                    double dy = Math.Max(0, Math.Max(r.Top - pt.Y, pt.Y - r.Bottom));
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 400 && dist < mejorDist) { mejor = t; mejorDist = dist; }
                }
                if (mejor == null) return false;
                string texto = TextoDe(mejor);
                if (texto.Length == 0)
                {
                    // Algunos tooltips guardan el texto en un hijo.
                    foreach (AutomationElement h in mejor.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        texto = TextoDe(h);
                        if (texto.Length > 0) break;
                    }
                }
                if (texto.Length == 0) return false;
                c.Fuente = Fuente.Mouse;
                c.Texto = texto;
                c.Zona = Rect(mejor);
                c.Motivo = "tooltip";
                return true;
            }
            catch { return false; }
        }

        private static Rectangle Rect(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty) return Rectangle.Empty;
                return new Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height);
            }
            catch { return Rectangle.Empty; }
        }
    }

    // Copia la selección con un atajo y deja el portapapeles exactamente como estaba.
    internal static class Portapapeles
    {
        public static string CopiarSeleccion(ushort[] atajo, IntPtr ventana)
        {
            string resultado = null;
            var hilo = new Thread(() => { resultado = CopiarProtegido(atajo, ventana); }) { IsBackground = true, Name = "portapapeles" };
            hilo.SetApartmentState(ApartmentState.STA);
            hilo.Start();
            if (!hilo.Join(4000)) Registro.Aviso("La copia de la selección tardó demasiado.");
            return resultado;
        }

        // Un fallo nativo de otra app en el portapapeles no debe cerrar el lector.
        [HandleProcessCorruptedStateExceptions]
        private static string CopiarProtegido(ushort[] atajo, IntPtr ventana)
        {
            try { return CopiarSta(atajo, ventana); }
            catch (Exception e) { Registro.Error("Copiando la selección", e); return null; }
        }

        private static string CopiarSta(ushort[] atajo, IntPtr ventana)
        {
            var guardado = Guardar();
            uint seq0 = Nativo.GetClipboardSequenceNumber();
            if (Nativo.GetForegroundWindow() != ventana) return null;   // el usuario cambió de ventana
            Enviar(atajo);
            string texto = null;
            // Las apps copian en 20-150 ms; si en 300 ms no cambió el portapapeles es que no había selección.
            var limite = DateTime.Now.AddMilliseconds(300);
            while (DateTime.Now < limite)
            {
                Thread.Sleep(10);
                if (Nativo.GetClipboardSequenceNumber() != seq0)
                {
                    Thread.Sleep(30);   // dar tiempo a que termine de escribir todos los formatos
                    texto = LeerTexto();
                    break;
                }
            }
            if (Nativo.GetClipboardSequenceNumber() != seq0) Restaurar(guardado);
            return texto;
        }

        private static void Enviar(ushort[] vks)
        {
            var lista = new List<Nativo.INPUT>();
            foreach (var vk in vks) lista.Add(Nativo.Tecla(vk, false));
            for (int i = vks.Length - 1; i >= 0; i--) lista.Add(Nativo.Tecla(vks[i], true));
            Nativo.SendInput((uint)lista.Count, lista.ToArray(), Marshal.SizeOf(typeof(Nativo.INPUT)));
        }

        private static string LeerTexto()
        {
            for (int i = 0; i < 6; i++)
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null; }
                catch (ExternalException) { Thread.Sleep(40); }
            }
            return null;
        }

        // Formatos de identificador (imagen, metarchivo) y de objetos OLE incrustados (Excel, Word):
        // al volver a ponerlos con OleFlushClipboard el proceso muere (violación de acceso en ole32).
        // Windows recrea Bitmap a partir de DeviceIndependentBitmap, que sí se guarda.
        private static readonly HashSet<string> NoRestaurables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bitmap", "MetaFilePict", "EnhancedMetafile", "Palette",
            "Embed Source", "Embedded Object", "Link Source", "Link Source Descriptor",
            "Object Descriptor", "Link", "ObjectLink", "OwnerLink", "Native"
        };

        private static List<KeyValuePair<string, object>> Guardar()
        {
            var lista = new List<KeyValuePair<string, object>>();
            for (int intento = 0; intento < 5; intento++)
            {
                try
                {
                    lista.Clear();
                    var d = Clipboard.GetDataObject();
                    if (d == null) return lista;
                    foreach (var f in d.GetFormats(false))
                    {
                        if (NoRestaurables.Contains(f)) continue;
                        try
                        {
                            object o = d.GetData(f, false);
                            var ms = o as MemoryStream;
                            if (ms != null) o = ms.ToArray();      // copiar: el stream original muere con el portapapeles
                            else if (o is Stream) continue;
                            if (o != null) lista.Add(new KeyValuePair<string, object>(f, o));
                        }
                        catch { }
                    }
                    return lista;
                }
                catch (ExternalException) { Thread.Sleep(40); }
            }
            return lista;
        }

        private static void Restaurar(List<KeyValuePair<string, object>> guardado)
        {
            for (int intento = 0; intento < 6; intento++)
            {
                try
                {
                    if (guardado.Count == 0) { Clipboard.Clear(); return; }
                    var d = new DataObject();
                    foreach (var kv in guardado)
                    {
                        object o = kv.Value;
                        var b = o as byte[];
                        d.SetData(kv.Key, false, b != null ? new MemoryStream(b) : o);
                    }
                    // Que el historial (Win+V) y el portapapeles en la nube no registren la restauración.
                    d.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new MemoryStream(new byte[4]));
                    d.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(BitConverter.GetBytes(0)));
                    d.SetData("CanUploadToCloudClipboard", false, new MemoryStream(BitConverter.GetBytes(0)));
                    Clipboard.SetDataObject(d, true, 3, 50);
                    return;
                }
                catch (ExternalException) { Thread.Sleep(50); }
                catch (Exception e) { Registro.Error("Restaurando el portapapeles", e); return; }
            }
            Registro.Aviso("No se pudo restaurar el portapapeles (estaba bloqueado por otro programa).");
        }
    }
}
