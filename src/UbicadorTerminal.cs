// Ubicador de palabras para terminales xterm.js (Orca) que no exponen TextPattern.
// Idea: el texto copiado (Ctrl+Shift+C) da el CONTENIDO exacto; UI Automation da el rectángulo de la rejilla
// (div "xterm-screen"); el OCR de Windows da ANCLAS (algunas palabras con su caja). Como la fuente es monoespaciada,
// con unas pocas anclas por línea se calcula la celda (fila, columna) de cada carácter y de ahí su rectángulo.
// C# 5 / .NET Framework 4.8 / csc. Sin dependencias fuera de Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace LectorKaraoke
{
    internal sealed class UbicadorTerminal
    {
        public Rectangle Rejilla;          // xterm-screen en píxeles físicos
        public double Cw, Ch;              // ancho y alto de celda (px físicos)
        public int Cols, Filas;
        public int Anclas, PalabrasOcr, CorrespondenciasTexto;
        public double ErrorMedioPx;        // diferencia media entre la caja OCR y la celda calculada (anclas)
        public string Diagnostico = "";
        public long MsCaptura, MsOcr, MsAlinear;
        private string texto;
        private int[] fila, col;           // celda de cada carácter del texto (-1 = desconocida)
        private bool[] anclado;            // el carácter pertenece a una palabra reconocida por OCR
        public int[] FilaDe { get { return fila; } }

        // ---------------- Localizar la rejilla con UI Automation ----------------

        // Desde el elemento con foco (el textarea "Terminal input" de xterm) sube hasta "xterm-screen".
        public static Rectangle RejillaDesdeFoco()
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                for (int i = 0; el != null && i < 4; i++)
                {
                    string cls = el.Current.ClassName ?? "";
                    if (TieneClase(cls, "xterm-screen")) return Rect(el);
                    if (TieneClase(cls, "xterm")) break;
                    el = TreeWalker.RawViewWalker.GetParent(el);
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        // Busca en la ventana de Orca la terminal con la clase "focus" (o la más grande visible).
        public static Rectangle RejillaEnVentana(IntPtr hwnd)
        {
            try
            {
                var raiz = AutomationElement.FromHandle(hwnd);
                var todos = raiz.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
                Rectangle mejor = Rectangle.Empty; bool mejorFoco = false;
                foreach (AutomationElement t in todos)
                {
                    string cls = t.Current.ClassName ?? "";
                    if (!TieneClase(cls, "xterm-screen")) continue;
                    var r = Rect(t);
                    if (r.Width < 50 || r.Height < 20) continue;
                    bool foco = false;
                    try
                    {
                        var p = TreeWalker.RawViewWalker.GetParent(t);
                        var pc = p == null ? "" : (p.Current.ClassName ?? "");
                        if (!TieneClase(pc, "xterm") && p != null) { p = TreeWalker.RawViewWalker.GetParent(p); pc = p == null ? "" : (p.Current.ClassName ?? ""); }
                        foco = TieneClase(pc, "focus");
                    }
                    catch { }
                    if ((foco && !mejorFoco) || (foco == mejorFoco && (long)r.Width * r.Height > (long)mejor.Width * mejor.Height))
                    { mejor = r; mejorFoco = foco; }
                }
                return mejor;
            }
            catch { return Rectangle.Empty; }
        }

        private static bool TieneClase(string clases, string c)
        {
            foreach (var x in clases.Split(' ')) if (x == c) return true;
            return false;
        }

        private static Rectangle Rect(AutomationElement el)
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty) return Rectangle.Empty;
            return new Rectangle((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        }

        // ---------------- Construcción ----------------

        private sealed class Corrida   // secuencia de letras o dígitos
        {
            public string Norm;
            public int Inicio, Largo;       // texto: posición en el texto copiado
            public float X0, X1, Y0, Y1;    // OCR: caja estimada de la corrida (px relativos a la captura)
            public int Grupo;               // OCR: fila visual (agrupación por altura)
        }

        public static UbicadorTerminal Construir(string texto, Rectangle rejilla, Bitmap captura, Windows.Media.Ocr.OcrResult ocr)
        {
            var sw = Stopwatch.StartNew();
            var u = new UbicadorTerminal { texto = texto, Rejilla = rejilla };
            int n = texto.Length;
            u.fila = Enumerable.Repeat(-1, n).ToArray();
            u.col = Enumerable.Repeat(-1, n).ToArray();
            u.anclado = new bool[n];

            // 1) Corridas del texto copiado + columna lógica (en celdas) de cada carácter dentro de su línea.
            var linea = new int[n]; var k = new int[n];
            int ln = 0, kk = 0;
            for (int i = 0; i < n; i++)
            {
                char c = texto[i];
                linea[i] = ln; k[i] = kk;
                if (c == '\n') { ln++; kk = 0; continue; }
                if (c == '\r') continue;
                if (char.IsLowSurrogate(c)) continue;
                kk += AnchoCelda(texto, i);
            }
            var A = CorridasTexto(texto);

            // 2) Corridas del OCR en orden de lectura (por filas visuales).
            var B = new List<Corrida>();
            var palabras = new List<Tuple<string, RectangleF>>();
            foreach (var l in ocr.Lines)
                foreach (var w in l.Words)
                {
                    var br = w.BoundingRect;
                    palabras.Add(Tuple.Create(w.Text, new RectangleF((float)br.X, (float)br.Y, (float)br.Width, (float)br.Height)));
                }
            u.PalabrasOcr = palabras.Count;
            if (palabras.Count == 0) { u.Diagnostico = "OCR sin palabras"; return u; }
            float altoMed = Mediana(palabras.Select(p => p.Item2.Height).ToList());
            palabras.Sort((a, b) => (a.Item2.Y + a.Item2.Height / 2).CompareTo(b.Item2.Y + b.Item2.Height / 2));
            var grupos = new List<List<Tuple<string, RectangleF>>>();
            float cyGrupo = float.MinValue;
            foreach (var p in palabras)
            {
                float cy = p.Item2.Y + p.Item2.Height / 2;
                if (grupos.Count == 0 || cy - cyGrupo > altoMed * 0.55f) { grupos.Add(new List<Tuple<string, RectangleF>>()); cyGrupo = cy; }
                grupos[grupos.Count - 1].Add(p);
            }
            for (int g = 0; g < grupos.Count; g++)
            {
                foreach (var p in grupos[g].OrderBy(x => x.Item2.X))
                {
                    string t = p.Item1; var r = p.Item2;
                    float porChar = r.Width / Math.Max(1, t.Length);
                    int i = 0;
                    while (i < t.Length)
                    {
                        if (!char.IsLetterOrDigit(t[i])) { i++; continue; }
                        int j = i;
                        while (j < t.Length && char.IsLetterOrDigit(t[j])) j++;
                        B.Add(new Corrida { Norm = Normalizar(t.Substring(i, j - i)), X0 = r.X + i * porChar, X1 = r.X + j * porChar, Y0 = r.Y, Y1 = r.Bottom, Grupo = g });
                        i = j;
                    }
                }
            }

            // 3) Alineación local (Smith-Waterman) entre corridas del texto y del OCR.
            var pares = Alinear(A, B);
            u.CorrespondenciasTexto = pares.Count;
            // Sin suficientes coincidencias el texto no está en pantalla (o el OCR falló): no se resalta nada (respaldo: franja).
            // El umbral es sobre lo que se VE (la selección puede ser más larga que la pantalla: historial completo).
            int visibles = Math.Min(A.Count, B.Count);
            if (pares.Count < Math.Max(3, (int)(visibles * 0.3)))
            { u.Diagnostico = "pocas correspondencias (" + pares.Count + " de " + visibles + " visibles; texto " + A.Count + "): no confiable"; u.MsAlinear = sw.ElapsedMilliseconds; return u; }

            // 4) Tamaño de celda. Ancho: pendiente x/columna entre anclas de la misma fila visual y la misma línea.
            var pendientes = new List<float>();
            foreach (var grupo in pares.GroupBy(p => B[p.Item2].Grupo))
            {
                var lista = grupo.ToList();
                for (int a = 0; a < lista.Count; a++)
                    for (int b = a + 1; b < lista.Count; b++)
                    {
                        var ta = A[lista[a].Item1]; var tb = A[lista[b].Item1];
                        if (linea[ta.Inicio] != linea[tb.Inicio]) continue;
                        int dk = k[tb.Inicio] - k[ta.Inicio];
                        if (Math.Abs(dk) < 4) continue;
                        pendientes.Add((B[lista[b].Item2].X0 - B[lista[a].Item2].X0) / dk);
                    }
            }
            double cw = pendientes.Count > 0 ? Mediana(pendientes) : altoMed * 0.6;
            // Alto: separación entre filas visuales consecutivas con anclas.
            var cys = pares.Select(p => B[p.Item2]).GroupBy(c => c.Grupo).Select(gr => gr.Average(c => (c.Y0 + c.Y1) / 2)).OrderBy(x => x).ToList();
            var difs = new List<float>();
            for (int i = 1; i < cys.Count; i++) difs.Add(cys[i] - cys[i - 1]);
            double ch = altoMed * 1.5;
            if (difs.Count > 0)
            {
                float minimo = difs.Where(d => d > altoMed * 0.8f).DefaultIfEmpty(altoMed * 1.5f).Min();
                ch = Mediana(difs.Where(d => d > altoMed * 0.8f).Select(d => d / (float)Math.Max(1, Math.Round(d / minimo))).ToList());
            }
            // xterm dibuja la rejilla con celdas enteras en píxeles físicos: ancho = Cols * Cw exacto (± redondeo del borde).
            u.Cols = Math.Max(1, (int)Math.Round(rejilla.Width / cw));
            // xterm usa celdas de alto ENTERO en píxeles de dispositivo. Se elige el entero cercano cuyo período deja
            // los centros de las filas OCR con la misma fase (dispersión circular mínima).
            {
                double mejorDisp = double.MaxValue, mejorC = Math.Round(ch);
                for (int c = Math.Max(4, (int)Math.Floor(ch) - 2); cys.Count >= 3 && c <= (int)Math.Ceiling(ch) + 2; c++)
                {
                    double sx = 0, sy = 0;
                    foreach (var cy in cys) { double ang = 2 * Math.PI * (cy / c); sx += Math.Cos(ang); sy += Math.Sin(ang); }
                    double disp = 1 - Math.Sqrt(sx * sx + sy * sy) / Math.Max(1, cys.Count);
                    if (disp < mejorDisp - 1e-6) { mejorDisp = disp; mejorC = c; }
                }
                u.Filas = Math.Max(1, (int)Math.Floor((rejilla.Height + 1) / mejorC));
            }
            u.Cw = rejilla.Width / (double)u.Cols;
            u.Ch = rejilla.Height / (double)u.Filas;

            // 5) Anclas: celda de cada corrida reconocida.
            var anclas = new Dictionary<int, List<int[]>>();   // línea -> lista de (k, fila, col)
            double errSuma = 0; int errN = 0;
            foreach (var p in pares)
            {
                var ta = A[p.Item1]; var ob = B[p.Item2];
                int f = (int)Math.Floor(((ob.Y0 + ob.Y1) / 2) / u.Ch);
                int c = (int)Math.Round(ob.X0 / u.Cw);
                if (f < 0 || f >= u.Filas || c < 0 || c >= u.Cols) continue;
                List<int[]> lst;
                if (!anclas.TryGetValue(linea[ta.Inicio], out lst)) anclas[linea[ta.Inicio]] = lst = new List<int[]>();
                lst.Add(new[] { k[ta.Inicio], f, c });
                for (int i = ta.Inicio; i < ta.Inicio + ta.Largo; i++) u.anclado[i] = true;
                errSuma += Math.Abs(ob.X0 - c * u.Cw); errN++;
            }
            u.Anclas = errN;
            u.ErrorMedioPx = errN > 0 ? errSuma / errN : -1;

            // Texto que el OCR vio en cada fila de la rejilla (para validar líneas sin anclas).
            var ocrPorFila = new Dictionary<int, List<string>>();
            foreach (var ob in B)
            {
                int f = (int)Math.Floor(((ob.Y0 + ob.Y1) / 2) / u.Ch);
                List<string> l;
                if (!ocrPorFila.TryGetValue(f, out l)) ocrPorFila[f] = l = new List<string>();
                l.Add(ob.Norm);
            }

            // 6) Celda de cada carácter: desde el ancla más cercana de su línea, avanzando por celdas (con salto de fila suave).
            int lineas = ln + 1;
            int ultimaFila = -1, colInicioPrev = 0;
            for (int L = 0; L < lineas; L++)
            {
                List<int[]> lst;
                bool tiene = anclas.TryGetValue(L, out lst);
                // Anclas incoherentes (OCR emparejó mal): se descartan las que no siguen la tendencia de la línea.
                if (tiene) lst = Coherentes(lst, u.Cols);
                for (int i = 0; i < n; i++)
                {
                    if (linea[i] != L || texto[i] == '\n' || texto[i] == '\r') continue;
                    int f, c;
                    if (tiene && lst.Count > 0)
                    {
                        int[] mejor = lst[0];
                        foreach (var a in lst) if (Math.Abs(a[0] - k[i]) < Math.Abs(mejor[0] - k[i])) mejor = a;
                        long idx = (long)mejor[1] * u.Cols + mejor[2] + (k[i] - mejor[0]);
                        f = (int)Math.Floor(idx / (double)u.Cols); c = (int)(idx - (long)f * u.Cols);
                    }
                    else if (ultimaFila >= 0)
                    {
                        // Línea sin anclas (solo símbolos, emojis, OCR fallido): se supone en la fila siguiente.
                        long idx = (long)(ultimaFila + 1) * u.Cols + colInicioPrev + k[i];
                        f = (int)(idx / u.Cols); c = (int)(idx % u.Cols);
                    }
                    else continue;
                    if (f < 0 || f >= u.Filas) continue;
                    u.fila[i] = f; u.col[i] = c;
                }
                // Línea deducida sin anclas: si en su fila el OCR ve palabras y ninguna se parece a las de la línea,
                // la suposición es falsa (por ejemplo, el texto ya se desplazó): mejor no resaltar que resaltar mal.
                if (!tiene && ultimaFila >= 0)
                {
                    var propias = A.Where(r => linea[r.Inicio] == L && r.Largo >= 3).Select(r => r.Norm).ToList();
                    if (propias.Count > 0)
                    {
                        bool contradice = false;
                        foreach (var f in Enumerable.Range(0, n).Where(i => linea[i] == L && u.fila[i] >= 0).Select(i => u.fila[i]).Distinct())
                        {
                            List<string> vistos;
                            if (!ocrPorFila.TryGetValue(f, out vistos)) continue;
                            var largos = vistos.Where(v => v.Length >= 3).ToList();
                            if (largos.Count > 0 && !largos.Any(v => propias.Any(pp => Similitud(pp, v) >= 0.6))) contradice = true;
                        }
                        if (contradice)
                            for (int i = 0; i < n; i++) if (linea[i] == L) { u.fila[i] = -1; u.col[i] = -1; }
                    }
                }
                // Fila final y columna inicial de esta línea, para la siguiente sin anclas.
                int maxF = -1, colK0 = 0; bool hay = false;
                for (int i = 0; i < n; i++)
                    if (linea[i] == L && u.fila[i] >= 0)
                    {
                        if (!hay) { colK0 = u.col[i] - k[i]; hay = true; }
                        maxF = Math.Max(maxF, u.fila[i]);
                    }
                if (hay) { ultimaFila = maxF; colInicioPrev = Math.Max(0, colK0); }
                else if (ultimaFila >= 0) ultimaFila++;
            }
            u.MsAlinear = sw.ElapsedMilliseconds;
            u.Diagnostico = "celda=" + u.Cw.ToString("0.00", CultureInfo.InvariantCulture) + "x" + u.Ch.ToString("0.00", CultureInfo.InvariantCulture) +
                            " rejilla=" + u.Cols + "x" + u.Filas + " anclas=" + u.Anclas + "/" + A.Count + " errorMedio=" + u.ErrorMedioPx.ToString("0.0", CultureInfo.InvariantCulture) + "px";
            return u;
        }

        // Descarta anclas cuya posición lineal (fila*cols+col - k) se aleja de la mediana de la línea.
        private static List<int[]> Coherentes(List<int[]> lst, int cols)
        {
            if (lst.Count < 3) return lst;
            var off = lst.Select(a => (long)a[1] * cols + a[2] - a[0]).OrderBy(x => x).ToList();
            long med = off[off.Count / 2];
            var res = lst.Where(a => Math.Abs((long)a[1] * cols + a[2] - a[0] - med) <= 1).ToList();
            return res.Count > 0 ? res : lst;
        }

        // ---------------- Consulta (misma firma que UbicadorUia.Rects) ----------------

        public List<Rectangle> Rects(int inicio, int largo)
        {
            var res = new List<Rectangle>();
            var porFila = new SortedDictionary<int, int[]>();
            for (int i = inicio; i < inicio + largo && i < texto.Length; i++)
            {
                if (char.IsWhiteSpace(texto[i]) || fila[i] < 0) continue;
                int[] mm;
                int ancho = AnchoCelda(texto, i);
                if (!porFila.TryGetValue(fila[i], out mm)) porFila[fila[i]] = mm = new[] { col[i], col[i] + ancho };
                mm[0] = Math.Min(mm[0], col[i]); mm[1] = Math.Max(mm[1], col[i] + ancho);
            }
            foreach (var kv in porFila)
            {
                int x0 = Rejilla.Left + (int)Math.Round(kv.Value[0] * Cw);
                int x1 = Rejilla.Left + (int)Math.Round(kv.Value[1] * Cw);
                int y0 = Rejilla.Top + (int)Math.Round(kv.Key * Ch);
                int y1 = Rejilla.Top + (int)Math.Round((kv.Key + 1) * Ch);
                res.Add(new Rectangle(x0, y0, x1 - x0, y1 - y0));
            }
            return res;
        }

        public bool Anclada(int inicio, int largo)
        {
            for (int i = inicio; i < inicio + largo && i < texto.Length; i++) if (anclado[i]) return true;
            return false;
        }

        // ---------------- Utilidades ----------------

        private static List<Corrida> CorridasTexto(string t)
        {
            var res = new List<Corrida>();
            int i = 0;
            while (i < t.Length)
            {
                if (!char.IsLetterOrDigit(t[i])) { i++; continue; }
                int j = i;
                while (j < t.Length && char.IsLetterOrDigit(t[j])) j++;
                res.Add(new Corrida { Norm = Normalizar(t.Substring(i, j - i)), Inicio = i, Largo = j - i });
                i = j;
            }
            return res;
        }

        // Minúsculas, sin tildes y con los confundibles típicos del OCR unificados.
        private static string Normalizar(string s)
        {
            var d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c0 in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c0) == UnicodeCategory.NonSpacingMark) continue;
                char c = char.ToLowerInvariant(c0);
                switch (c)
                {
                    case 'i': case 'l': case '1': case '|': case 'j': c = 'l'; break;
                    case '0': case 'o': case 'q': c = 'o'; break;
                    case '5': c = 's'; break;
                    case '8': c = 'b'; break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static double Similitud(string a, string b)
        {
            if (a == b) return 1;
            int n = a.Length, m = b.Length;
            if (n == 0 || m == 0) return 0;
            var prev = new int[m + 1]; var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return 1.0 - prev[m] / (double)Math.Max(n, m);
        }

        // Smith-Waterman sobre corridas. Devuelve pares (índice en A, índice en B) del mejor tramo.
        private static List<Tuple<int, int>> Alinear(List<Corrida> A, List<Corrida> B)
        {
            int n = A.Count, m = B.Count;
            var res = new List<Tuple<int, int>>();
            if (n == 0 || m == 0) return res;
            var H = new float[(n + 1) * (m + 1)];
            var T = new byte[(n + 1) * (m + 1)];   // 0 fin, 1 diagonal, 2 arriba, 3 izquierda
            float mejor = 0; int bi = 0, bj = 0;
            const float GAP = -0.6f;
            for (int i = 1; i <= n; i++)
            {
                var a = A[i - 1].Norm;
                for (int j = 1; j <= m; j++)
                {
                    var b = B[j - 1].Norm;
                    double s = (Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) / 2 + 1) ? 0 : Similitud(a, b);
                    // Corridas de 1 carácter casan con cualquier cosa: valen poco.
                    float puntos = s >= 0.6 ? (float)(s * Math.Min(2.0, 0.5 + 0.5 * Math.Min(a.Length, b.Length))) : -1f;
                    int ij = i * (m + 1) + j;
                    float d = H[(i - 1) * (m + 1) + j - 1] + puntos;
                    float up = H[(i - 1) * (m + 1) + j] + GAP;
                    float left = H[i * (m + 1) + j - 1] + GAP * 0.5f;   // saltar ruido del OCR es barato
                    float v = 0; byte t = 0;
                    if (d > v) { v = d; t = 1; }
                    if (up > v) { v = up; t = 2; }
                    if (left > v) { v = left; t = 3; }
                    H[ij] = v; T[ij] = t;
                    if (v >= mejor) { mejor = v; bi = i; bj = j; }   // >= : ante empate, la aparición más abajo
                }
            }
            int x = bi, y = bj;
            while (x > 0 && y > 0)
            {
                byte t = T[x * (m + 1) + y];
                if (t == 0) break;
                if (t == 1)
                {
                    double s = Similitud(A[x - 1].Norm, B[y - 1].Norm);
                    if (s >= 0.6) res.Add(Tuple.Create(x - 1, y - 1));
                    x--; y--;
                }
                else if (t == 2) x--; else y--;
            }
            res.Reverse();
            return res;
        }

        // Celdas que ocupa un carácter en xterm: 2 para CJK y la mayoría de emojis, 0 para marcas combinantes.
        public static int AnchoCelda(string t, int i)
        {
            char c = t[i];
            if (char.IsHighSurrogate(c) && i + 1 < t.Length)
            {
                int cp = char.ConvertToUtf32(c, t[i + 1]);
                return (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD) ? 2 : 1;
            }
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || c == '​' || c == '‍' || c == '️') return 0;
            if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
                (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6)) return 2;
            return 1;
        }

        private static float Mediana(List<float> v)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }
    }

    // OCR de Windows (Windows.Media.Ocr) sin el Windows.winmd del SDK: solo los .winmd de System32.
    internal static class OcrWindows
    {
        private static Windows.Media.Ocr.OcrEngine motor;
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);

        public static Windows.Media.Ocr.OcrEngine Motor()
        {
            if (motor != null) return motor;
            foreach (var tag in new[] { "es-MX", "es-ES", "es" })
            {
                try { motor = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag)); } catch { }
                if (motor != null) return motor;
            }
            motor = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            return motor;
        }

        public static Bitmap Capturar(Rectangle r, bool conCapas)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (!conCapas) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);   // sin ventanas por capas (no ve el resaltado)
                else
                {
                    IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
                    try { BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020 | 0x40000000); }   // SRCCOPY | CAPTUREBLT
                    finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
                }
            }
            return bmp;
        }

        public static Task<Windows.Media.Ocr.OcrResult> Reconocer(Bitmap b)
        {
            var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] datos;
            try
            {
                datos = new byte[bd.Stride * b.Height];
                Marshal.Copy(bd.Scan0, datos, 0, datos.Length);
            }
            finally { b.UnlockBits(bd); }
            var sbm = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, b.Width, b.Height, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            sbm.CopyFromBuffer(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(datos));
            var op = Motor().RecognizeAsync(sbm);
            var tcs = new TaskCompletionSource<Windows.Media.Ocr.OcrResult>();
            op.Completed = (o, st) =>
            {
                try
                {
                    if (st == Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                    else tcs.TrySetException(o.ErrorCode ?? new Exception("OCR " + st));
                }
                catch (Exception e) { tcs.TrySetException(e); }
                finally { sbm.Dispose(); }
            };
            return tcs.Task;
        }
    }
}
