using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LectorKaraoke
{
    // Ventana por capas: transparente a clics, sin foco, fuera de Alt+Tab y de la barra de tareas.
    internal class CapaBase : Form
    {
        public CapaBase()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "LectorKaraoke.Capa";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Nativo.WS_EX_LAYERED | Nativo.WS_EX_TRANSPARENT | Nativo.WS_EX_TOOLWINDOW | Nativo.WS_EX_NOACTIVATE | Nativo.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        // Pinta un bitmap ARGB en la pantalla en (x, y) con transparencia por píxel.
        protected void Pintar(Bitmap bmp, int x, int y)
        {
            if (!IsHandleCreated) CreateHandle();
            IntPtr pantalla = Nativo.GetDC(IntPtr.Zero);
            IntPtr mem = Nativo.CreateCompatibleDC(pantalla);
            IntPtr hb = IntPtr.Zero, viejo = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0));
                viejo = Nativo.SelectObject(mem, hb);
                var tam = new Nativo.SIZE { cx = bmp.Width, cy = bmp.Height };
                var origen = new Nativo.POINT();
                var destino = new Nativo.POINT { X = x, Y = y };
                var mezcla = new Nativo.BLENDFUNCTION { BlendOp = Nativo.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Nativo.AC_SRC_ALPHA };
                Nativo.UpdateLayeredWindow(Handle, pantalla, ref destino, ref tam, mem, ref origen, 0, ref mezcla, Nativo.ULW_ALPHA);
                Nativo.SetWindowPos(Handle, Nativo.HWND_TOPMOST, 0, 0, 0, 0,
                    Nativo.SWP_NOMOVE | Nativo.SWP_NOSIZE | Nativo.SWP_NOACTIVATE | Nativo.SWP_SHOWWINDOW);
            }
            finally
            {
                if (viejo != IntPtr.Zero) Nativo.SelectObject(mem, viejo);
                if (hb != IntPtr.Zero) Nativo.DeleteObject(hb);
                Nativo.DeleteDC(mem);
                Nativo.ReleaseDC(IntPtr.Zero, pantalla);
            }
        }

        // Se muestran con UpdateLayeredWindow + SWP_SHOWWINDOW, que Windows Forms no registra en Visible:
        // por eso se ocultan también por la vía nativa.
        public void Ocultar()
        {
            if (!IsHandleCreated) return;
            ShowWindow(Handle, 0);
            if (Visible) Hide();
        }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    }

    // Resaltado de palabras y del elemento bajo el mouse.
    internal sealed class CapaResaltado : CapaBase
    {
        public void Dibujar(List<Rectangle> actual, List<Rectangle> leidos, Rectangle marco, Configuracion cfg)
        {
            var todos = new List<Rectangle>();
            todos.AddRange(actual);
            if (cfg.MarcarLeido) todos.AddRange(leidos);
            if (!marco.IsEmpty) todos.Add(marco);
            if (todos.Count == 0) { Ocultar(); return; }

            Rectangle u = todos[0];
            foreach (var r in todos) u = Rectangle.Union(u, r);
            int margen = 8 + cfg.GrosorSubrayado * 3;
            u.Inflate(margen, margen);
            // Un área absurda (texto disperso por varios monitores) se limita a la zona de la palabra actual.
            if ((long)u.Width * u.Height > 40000000L && actual.Count > 0)
            {
                u = actual[0];
                foreach (var r in actual) u = Rectangle.Union(u, r);
                u.Inflate(600, 300);
            }
            if (u.Width <= 0 || u.Height <= 0) { Ocultar(); return; }

            using (var bmp = new Bitmap(u.Width, u.Height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var c = cfg.ColorResaltado;
                if (cfg.MarcarLeido)
                    using (var b = new SolidBrush(Color.FromArgb(Alfa(cfg.OpacidadLeido), c)))
                        foreach (var r in leidos) Relleno(g, b, Mover(r, u), 3);
                using (var b = new SolidBrush(Color.FromArgb(Alfa(cfg.OpacidadActual), c)))
                    foreach (var r in actual)
                    {
                        var m = Mover(r, u);
                        m.Inflate(2, 1);
                        Relleno(g, b, m, 4);
                    }
                if (cfg.GrosorSubrayado > 0)
                    using (var b = new SolidBrush(Color.FromArgb(255, c)))
                        foreach (var r in actual)
                        {
                            var m = Mover(r, u);
                            int grosor = Grosor(cfg, r.Height);
                            g.FillRectangle(b, m.Left - 2, m.Bottom + 1, m.Width + 4, grosor);
                        }
                if (!marco.IsEmpty)
                {
                    var m = Mover(marco, u);
                    using (var b = new SolidBrush(Color.FromArgb(Alfa(Math.Max(10, cfg.OpacidadLeido)), c)))
                        Relleno(g, b, m, 6);
                    int grosor = Math.Max(3, Grosor(cfg, 24));
                    using (var p = new Pen(Color.FromArgb(255, c), grosor))
                    {
                        m.Inflate(grosor / 2 + 2, grosor / 2 + 2);
                        g.DrawRectangle(p, m);
                    }
                }
                Pintar(bmp, u.X, u.Y);
            }
        }

        private static int Grosor(Configuracion cfg, int alto)
        {
            // El grosor configurado es a 100 % de escala; crece con el tamaño del texto (DPI o zoom de la app).
            double escala = Math.Max(1.0, Math.Min(4.0, alto / 18.0));
            return Math.Max(1, (int)Math.Round(cfg.GrosorSubrayado * escala));
        }

        private static int Alfa(int pct) { return Math.Max(0, Math.Min(255, pct * 255 / 100)); }
        private static Rectangle Mover(Rectangle r, Rectangle u) { return new Rectangle(r.X - u.X, r.Y - u.Y, r.Width, r.Height); }

        private static void Relleno(Graphics g, Brush b, Rectangle r, int radio)
        {
            if (r.Width <= 2 * radio || r.Height <= 2 * radio) { g.FillRectangle(b, r); return; }
            using (var path = new GraphicsPath())
            {
                int d = radio * 2;
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }
    }

    // Franja tipo subtítulo para cuando la aplicación no dice dónde está cada palabra.
    internal sealed class CapaFranja : CapaBase
    {
        private Point ancla;
        private Trozo trozo;
        private List<RectangleF> cajas;
        private List<int> lineaDe;
        private Font fuente;
        private float altoLinea;
        private Rectangle monitor;
        private float escalaDpi = 1;

        public void Iniciar(Point puntero)
        {
            ancla = puntero;
            monitor = Screen.FromPoint(puntero).Bounds;
            escalaDpi = Dpi.Escala(puntero);
        }

        public void Mostrar(Trozo t, int actual, Configuracion cfg)
        {
            if (t == null || t.Palabras.Count == 0) { Ocultar(); return; }
            if (!ReferenceEquals(t, trozo) || fuente == null || Math.Abs(fuente.SizeInPoints - cfg.FranjaTamano * escalaDpi) > 0.1)
                Maquetar(t, cfg);

            int lineas = lineaDe.Count == 0 ? 1 : lineaDe[lineaDe.Count - 1] + 1;
            int lineaActual = actual >= 0 && actual < lineaDe.Count ? lineaDe[actual] : 0;
            const int visibles = 3;
            int primera = Math.Max(0, Math.Min(lineaActual - 1, lineas - visibles));
            int ultima = Math.Min(lineas - 1, primera + visibles - 1);

            float relleno = 14 * escalaDpi;
            float ancho = 0;
            for (int i = 0; i < cajas.Count; i++)
                if (lineaDe[i] >= primera && lineaDe[i] <= ultima) ancho = Math.Max(ancho, cajas[i].Right);
            int w = (int)Math.Ceiling(ancho + relleno * 2);
            int h = (int)Math.Ceiling((ultima - primera + 1) * altoLinea + relleno * 2);
            if (w < 40 || h < 20) { Ocultar(); return; }

            int x = ancla.X - w / 2;
            int y = ancla.Y + (int)(28 * escalaDpi);
            if (y + h > monitor.Bottom) y = ancla.Y - (int)(28 * escalaDpi) - h;
            x = Math.Max(monitor.Left + 4, Math.Min(x, monitor.Right - w - 4));
            y = Math.Max(monitor.Top + 4, Math.Min(y, monitor.Bottom - h - 4));

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var fondo = new SolidBrush(Color.FromArgb(232, 24, 24, 28)))
                using (var path = Redondeado(new RectangleF(0, 0, w - 1, h - 1), 12 * escalaDpi))
                    g.FillPath(fondo, path);
                var c = cfg.ColorResaltado;
                using (var blanco = new SolidBrush(Color.FromArgb(255, 240, 240, 240)))
                using (var leido = new SolidBrush(Color.FromArgb(255, Mezcla(c, Color.White, 0.55f))))
                using (var marca = new SolidBrush(Color.FromArgb(255, c)))
                using (var oscuro = new SolidBrush(Color.FromArgb(255, 20, 20, 20)))
                {
                    for (int i = 0; i < cajas.Count; i++)
                    {
                        if (lineaDe[i] < primera || lineaDe[i] > ultima) continue;
                        var r = cajas[i];
                        float dy = relleno + (lineaDe[i] - primera) * altoLinea;
                        var pos = new RectangleF(r.X + relleno, dy, r.Width, altoLinea);
                        Brush pincel = blanco;
                        if (i == actual)
                        {
                            g.FillRectangle(marca, pos.X - 3, pos.Y + 2, pos.Width + 6, pos.Height - 4);
                            pincel = oscuro;
                        }
                        else if (i < actual && cfg.MarcarLeido) pincel = leido;
                        g.DrawString(t.Palabras[i].Habla, fuente, pincel, pos.X, pos.Y, StringFormat.GenericTypographic);
                        if (i == actual && cfg.GrosorSubrayado > 0)
                            g.FillRectangle(marca, pos.X - 3, pos.Bottom - 1, pos.Width + 6, Math.Max(2, cfg.GrosorSubrayado * escalaDpi));
                    }
                }
                Pintar(bmp, x, y);
            }
        }

        private void Maquetar(Trozo t, Configuracion cfg)
        {
            trozo = t;
            if (fuente != null) fuente.Dispose();
            fuente = new Font("Segoe UI", cfg.FranjaTamano * escalaDpi, FontStyle.Regular, GraphicsUnit.Point);
            cajas = new List<RectangleF>();
            lineaDe = new List<int>();
            float maxAncho = monitor.Width * 0.7f;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float espacio = g.MeasureString(" ", fuente, PointF.Empty, StringFormat.GenericTypographic).Width + fuente.Size * 0.35f;
                altoLinea = fuente.GetHeight(g) * 1.25f;
                float x = 0;
                int linea = 0;
                foreach (var p in t.Palabras)
                {
                    float ancho = g.MeasureString(p.Habla, fuente, PointF.Empty, StringFormat.GenericTypographic).Width;
                    if (x > 0 && x + ancho > maxAncho) { linea++; x = 0; }
                    cajas.Add(new RectangleF(x, 0, ancho, altoLinea));
                    lineaDe.Add(linea);
                    x += ancho + espacio;
                }
            }
        }

        private static GraphicsPath Redondeado(RectangleF r, float radio)
        {
            var p = new GraphicsPath();
            float d = radio * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Mezcla(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public void Reiniciar() { trozo = null; }
    }

    internal static class Dpi
    {
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Nativo.POINT pt, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int tipo, out uint x, out uint y);

        public static float Escala(Point p)
        {
            try
            {
                var mon = MonitorFromPoint(new Nativo.POINT { X = p.X, Y = p.Y }, 2);
                uint x, y;
                if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) return x / 96f;
            }
            catch { }
            return 1f;
        }
    }
}
