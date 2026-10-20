using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LectorKaraoke
{
    internal static class Programa
    {
        public const string NombreMutex = "Local\\LectorKaraoke.Instancia";
        public const string EventoSalir = "Local\\LectorKaraoke.Salir";
        public const string EventoLeer = "Local\\LectorKaraoke.Leer";

        [STAThread]
        private static int Main(string[] args)
        {
            try { Nativo.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // por monitor v2 (además del manifiesto)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Registro.Iniciar(baseDir, "lector.log");
            bool reiniciar = args.Any(a => a.Equals("--reiniciar", StringComparison.OrdinalIgnoreCase));
            bool salir = args.Any(a => a.Equals("--salir", StringComparison.OrdinalIgnoreCase));
            bool prueba = args.Any(a => a.Equals("--prueba", StringComparison.OrdinalIgnoreCase));

            if (salir || reiniciar)
            {
                bool cerrado = CerrarInstancia();
                if (salir) return cerrado ? 0 : 1;
            }

            bool nueva;
            using (var mutex = new Mutex(true, NombreMutex, out nueva))
            {
                if (!nueva)
                {
                    Registro.Info("Ya hay un lector abierto; esta instancia se cierra.");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Registro.Error("Excepción no controlada (interfaz)", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Registro.Error("Excepción no controlada", e.ExceptionObject as Exception);
                Registro.Info("Lector iniciado (" + Application.ExecutablePath + ")" + (prueba ? " en modo prueba" : "") + ".");
                Inicio.Reparar(Application.ExecutablePath);
                using (var app = new Aplicacion(baseDir, prueba)) Application.Run(app);
                Registro.Info("Lector cerrado.");
            }
            return 0;
        }

        // Pide a la instancia abierta que se cierre; si no responde en 5 s, se termina el proceso.
        private static bool CerrarInstancia()
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(EventoSalir, out ev)) { ev.Set(); ev.Dispose(); }
            }
            catch { }
            var limite = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < limite)
            {
                bool libre;
                using (var m = new Mutex(false, NombreMutex, out libre)) { }
                if (!OtrosProcesos().Any()) return true;
                Thread.Sleep(100);
            }
            foreach (var p in OtrosProcesos())
            {
                try { p.Kill(); p.WaitForExit(2000); Registro.Aviso("Se forzó el cierre del lector anterior (pid " + p.Id + ")."); } catch { }
            }
            return !OtrosProcesos().Any();
        }

        private static Process[] OtrosProcesos()
        {
            int yo = Process.GetCurrentProcess().Id;
            return Process.GetProcessesByName("LectorKaraoke").Where(p => p.Id != yo).ToArray();
        }
    }

    // Contexto de la aplicación: bandeja, tecla global, configuración en caliente.
    internal sealed class Aplicacion : ApplicationContext
    {
        private readonly string baseDir, rutaConfig;
        private Configuracion cfg;
        private readonly Lector lector;
        private readonly VentanaTecla tecla;
        private readonly NotifyIcon icono;
        private readonly FileSystemWatcher vigia;
        private readonly System.Windows.Forms.Timer recarga;
        private readonly Control invocador;
        private readonly EventWaitHandle evSalir, evLeer;
        private readonly Thread hiloEventos;
        private volatile bool cerrando;

        public Aplicacion(string baseDir, bool prueba)
        {
            this.baseDir = baseDir;
            rutaConfig = Path.Combine(baseDir, Configuracion.NombreArchivo);
            invocador = new Control();
            invocador.CreateControl();
            cfg = Configuracion.Cargar(rutaConfig);
            Registro.Info("Configuración: " + cfg.Firma());

            icono = new NotifyIcon { Icon = Icono.Crear(cfg.ColorResaltado), Text = "Lector Karaoke (" + cfg.Tecla + ")", Visible = true };
            icono.ContextMenuStrip = Menu();
            icono.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) lector.AlPresionar(); };

            lector = new Lector(baseDir, cfg, prueba);
            lector.Aviso += m => Globo(m, ToolTipIcon.Info);

            tecla = new VentanaTecla();
            tecla.Presionada += () => lector.AlPresionar();
            RegistrarTecla();
            MostrarProblemas();

            recarga = new System.Windows.Forms.Timer { Interval = 400 };
            recarga.Tick += (s, e) => { recarga.Stop(); Recargar(); };
            vigia = new FileSystemWatcher(baseDir, Configuracion.NombreArchivo)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                SynchronizingObject = invocador
            };
            FileSystemEventHandler cambio = (s, e) => { recarga.Stop(); recarga.Start(); };
            vigia.Changed += cambio;
            vigia.Created += cambio;
            vigia.Renamed += (s, e) => { recarga.Stop(); recarga.Start(); };
            vigia.EnableRaisingEvents = true;

            evSalir = new EventWaitHandle(false, EventResetMode.AutoReset, Programa.EventoSalir);
            evLeer = new EventWaitHandle(false, EventResetMode.AutoReset, Programa.EventoLeer);
            hiloEventos = new Thread(EsperarEventos) { IsBackground = true, Name = "eventos" };
            hiloEventos.Start();
        }

        private void EsperarEventos()
        {
            var handles = new WaitHandle[] { evSalir, evLeer };
            while (!cerrando)
            {
                int i;
                try { i = WaitHandle.WaitAny(handles); } catch { return; }
                if (cerrando) return;
                try
                {
                    if (i == 0) invocador.BeginInvoke(new Action(ExitThread));
                    else invocador.BeginInvoke(new Action(() => lector.AlPresionar()));
                }
                catch { return; }
            }
        }

        private uint teclaActiva;
        private string nombreTeclaActiva = "";

        private void RegistrarTecla()
        {
            string error;
            if (tecla.Registrar(cfg.TeclaVk, out error))
            {
                teclaActiva = cfg.TeclaVk;
                nombreTeclaActiva = cfg.Tecla;
                Registro.Info("Tecla registrada: " + cfg.Tecla + ".");
                return;
            }
            Registro.Aviso("No se pudo registrar la tecla " + cfg.Tecla + ": " + error);
            // Si había una tecla funcionando, se conserva para no quedarse sin lector.
            string anterior;
            if (teclaActiva != 0 && teclaActiva != cfg.TeclaVk && tecla.Registrar(teclaActiva, out anterior))
            {
                Globo("La tecla " + cfg.Tecla + " la está usando otro programa; se mantiene " + nombreTeclaActiva + ".", ToolTipIcon.Warning);
                Registro.Info("Se mantiene la tecla " + nombreTeclaActiva + ".");
            }
            else Globo("La tecla " + cfg.Tecla + " la está usando otro programa. Cambia 'tecla' en config.ini.", ToolTipIcon.Warning);
        }

        private void Recargar()
        {
            var nueva = Configuracion.Cargar(rutaConfig);
            bool cambioTecla = nueva.TeclaVk != cfg.TeclaVk;
            cfg = nueva;
            lector.CambiarConfiguracion(cfg);
            if (cambioTecla) RegistrarTecla();
            var viejo = icono.Icon;
            icono.Icon = Icono.Crear(cfg.ColorResaltado);
            if (viejo != null) viejo.Dispose();
            icono.Text = "Lector Karaoke (" + cfg.Tecla + ")";
            Registro.Info("Configuración recargada: " + cfg.Firma());
            if (!MostrarProblemas()) Globo("Configuración aplicada.", ToolTipIcon.Info);
        }

        private bool MostrarProblemas()
        {
            if (cfg.Problemas.Count == 0) return false;
            foreach (var p in cfg.Problemas) Registro.Aviso("config.ini: " + p);
            Globo("config.ini: " + cfg.Problemas[0] + (cfg.Problemas.Count > 1 ? " (y " + (cfg.Problemas.Count - 1) + " más, ver logs)" : ""), ToolTipIcon.Warning);
            return true;
        }

        private void Globo(string texto, ToolTipIcon tipo)
        {
            if (invocador.InvokeRequired) { invocador.BeginInvoke(new Action(() => Globo(texto, tipo))); return; }
            try { icono.ShowBalloonTip(5000, "Lector Karaoke", texto.Length > 250 ? texto.Substring(0, 250) : texto, tipo); } catch { }
        }

        private ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Leer o detener", null, (s, e) => lector.AlPresionar());
            m.Items.Add("Abrir config.ini", null, (s, e) => Abrir(rutaConfig));
            m.Items.Add("Abrir carpeta del lector", null, (s, e) => Abrir(baseDir));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Reiniciar", null, (s, e) => Reiniciar());
            m.Items.Add("Salir", null, (s, e) => ExitThread());
            return m;
        }

        private static void Abrir(string ruta)
        {
            try { Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true }); }
            catch (Exception e) { Registro.Error("Abriendo " + ruta, e); }
        }

        private void Reiniciar()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--reiniciar") { UseShellExecute = false }); }
            catch (Exception e) { Registro.Error("Reiniciando", e); }
        }

        protected override void ExitThreadCore()
        {
            cerrando = true;
            try { evSalir.Set(); } catch { }
            vigia.EnableRaisingEvents = false;
            tecla.Dispose();
            lector.Dispose();
            icono.Visible = false;
            icono.Dispose();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                vigia.Dispose();
                recarga.Dispose();
                invocador.Dispose();
                evSalir.Dispose();
                evLeer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Ventana invisible que recibe la tecla global.
    internal sealed class VentanaTecla : NativeWindow, IDisposable
    {
        private const int Id = 0x4C4B;
        private bool registrada;
        public event Action Presionada;

        public VentanaTecla() { CreateHandle(new CreateParams { Caption = "LectorKaraoke.Tecla" }); }

        public bool Registrar(uint vk, out string error)
        {
            error = "";
            if (registrada) { Nativo.UnregisterHotKey(Handle, Id); registrada = false; }
            if (Nativo.RegisterHotKey(Handle, Id, Nativo.MOD_NOREPEAT, vk)) { registrada = true; return true; }
            error = "código " + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Nativo.WM_HOTKEY && m.WParam.ToInt32() == Id)
            {
                var h = Presionada;
                if (h != null) h();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registrada) Nativo.UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }

    internal static class Icono
    {
        public static Icon Crear(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("L", f, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                DestruirIcono(h);
                return ic;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);
        private static void DestruirIcono(IntPtr h) { DestroyIcon(h); }
    }
}
