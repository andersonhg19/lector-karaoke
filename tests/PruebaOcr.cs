using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using LectorKaraoke;

// Prueba sin pantalla del ubicador de terminal: una captura real de la terminal de Orca y el texto copiado.
// Comprueba que cada palabra cae dentro de la rejilla, en orden de lectura, y que un texto ausente no se ubica.
// Datos en tests\datos (locales): orca-captura.png y orca-copiado.txt. Rejilla de la captura: X=-1579, Y=47.
internal static class PruebaOcr
{
    private static int ok, fallos;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datos");
        string png = Path.Combine(dir, "orca-captura.png"), txt = Path.Combine(dir, "orca-copiado.txt");
        if (!File.Exists(png) || !File.Exists(txt)) { Console.WriteLine("OCR: sin datos locales (tests\\datos), se omite."); return 0; }

        Cierto("motor OCR de Windows en español disponible", OcrWindows.Motor() != null);
        string texto = File.ReadAllText(txt);
        using (var bmp = new Bitmap(png))
        {
            var rejilla = new Rectangle(-1579, 47, bmp.Width, bmp.Height);
            var sw = Stopwatch.StartNew();
            var ocr = OcrWindows.Reconocer(bmp).GetAwaiter().GetResult();
            var u = UbicadorTerminal.Construir(texto, rejilla, bmp, ocr);
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine("    " + u.Diagnostico + " en " + ms + " ms");
            Cierto("tiempo de ubicación < 1500 ms (" + ms + ")", ms < 1500);
            Cierto("anclas suficientes (" + u.Anclas + ")", u.Anclas >= 20);
            Cierto("error medio de anclas < 4 px (" + u.ErrorMedioPx.ToString("0.0") + ")", u.ErrorMedioPx >= 0 && u.ErrorMedioPx < 4);

            // Cada palabra del texto: rectángulo dentro de la rejilla y en orden de lectura.
            var cfg = new Configuracion();
            var palabras = Preparador.Preparar(texto, cfg).Trozos.SelectMany(t => t.Palabras).ToList();
            int con = 0, dentro = 0, enOrden = 0;
            Rectangle previo = Rectangle.Empty;
            foreach (var p in palabras)
            {
                var rs = u.Rects(p.OrigInicio, p.OrigLargo);
                if (rs.Count == 0) continue;
                con++;
                if (rs.All(r => rejilla.Contains(r))) dentro++;
                var r0 = rs[0];
                if (previo.IsEmpty || r0.Y > previo.Y + 2 || (Math.Abs(r0.Y - previo.Y) <= 2 && r0.X >= previo.X)) enOrden++;
                previo = r0;
            }
            Cierto("palabras ubicadas (" + con + " de " + palabras.Count + ")", con >= palabras.Count * 0.9);
            Cierto("todas dentro de la rejilla", dentro == con);
            Cierto("en orden de lectura (" + enOrden + " de " + con + ")", enOrden >= con - 1);

            // Un texto que no está en pantalla no se ubica (mejor franja que resaltar mal).
            var otro = UbicadorTerminal.Construir("Este texto no aparece en ninguna parte de la captura de prueba, nada que ver.", rejilla, bmp, ocr);
            Cierto("texto ausente: no confiable (" + otro.Diagnostico + ")", otro.Anclas < 3);
        }
        Console.WriteLine();
        Console.WriteLine("OCR: " + ok + " ok, " + fallos + " fallos");
        return fallos;
    }

    private static void Cierto(string n, bool c) { if (c) { ok++; Console.WriteLine("  OK    " + n); } else { fallos++; Console.WriteLine("  FALLA " + n); } }
}
