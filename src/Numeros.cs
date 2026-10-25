using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LectorKaraoke
{
    // Cifras, fechas, rangos, unidades, ordinales y abreviaturas en palabras que la voz lee bien.
    // Edge no acepta SSML (ni say-as ni break): todo se resuelve en el texto. Las reglas siguen las gramáticas
    // de NeMo para español y la taxonomía de Sproat (rango, fecha, versión, IP, identificador, unidad...).
    // Lo que la voz ya lee bien (cardinales, años, porcentajes, horas) se deja tal cual.
    internal static class Numeros
    {
        private static readonly string[] Meses = { "", "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio",
            "agosto", "septiembre", "octubre", "noviembre", "diciembre" };

        // ---------------- Unidades ----------------
        private sealed class Unidad
        {
            public string Singular, Plural;
            public Unidad(string s, string p) { Singular = s; Plural = p; }
        }

        // Sensible a mayúsculas: "m" es metro, "M" no; "MB" megabyte, "mb" se acepta igual.
        private static readonly Dictionary<string, Unidad> Unidades = new Dictionary<string, Unidad>(StringComparer.Ordinal)
        {
            { "ns", new Unidad("nanosegundo", "nanosegundos") }, { "µs", new Unidad("microsegundo", "microsegundos") },
            { "us", new Unidad("microsegundo", "microsegundos") }, { "ms", new Unidad("milisegundo", "milisegundos") },
            { "s", new Unidad("segundo", "segundos") }, { "seg", new Unidad("segundo", "segundos") },
            { "min", new Unidad("minuto", "minutos") }, { "h", new Unidad("hora", "horas") },
            { "Hz", new Unidad("hercio", "hercios") }, { "kHz", new Unidad("kilohercio", "kilohercios") },
            { "MHz", new Unidad("megahercio", "megahercios") }, { "GHz", new Unidad("gigahercio", "gigahercios") },
            { "B", new Unidad("byte", "bytes") }, { "KB", new Unidad("kilobyte", "kilobytes") }, { "kB", new Unidad("kilobyte", "kilobytes") },
            { "MB", new Unidad("megabyte", "megabytes") }, { "GB", new Unidad("gigabyte", "gigabytes") },
            { "TB", new Unidad("terabyte", "terabytes") }, { "KiB", new Unidad("kibibyte", "kibibytes") },
            { "MiB", new Unidad("mebibyte", "mebibytes") }, { "GiB", new Unidad("gibibyte", "gibibytes") },
            { "kb", new Unidad("kilobit", "kilobits") }, { "Mb", new Unidad("megabit", "megabits") },
            { "Kbps", new Unidad("kilobit por segundo", "kilobits por segundo") }, { "kbps", new Unidad("kilobit por segundo", "kilobits por segundo") },
            { "Mbps", new Unidad("megabit por segundo", "megabits por segundo") }, { "Gbps", new Unidad("gigabit por segundo", "gigabits por segundo") },
            { "MB/s", new Unidad("megabyte por segundo", "megabytes por segundo") }, { "GB/s", new Unidad("gigabyte por segundo", "gigabytes por segundo") },
            { "px", new Unidad("píxel", "píxeles") }, { "pt", new Unidad("punto", "puntos") }, { "fps", new Unidad("cuadro por segundo", "cuadros por segundo") },
            { "dpi", new Unidad("punto por pulgada", "puntos por pulgada") }, { "ppp", new Unidad("punto por pulgada", "puntos por pulgada") },
            { "mm", new Unidad("milímetro", "milímetros") }, { "cm", new Unidad("centímetro", "centímetros") },
            { "m", new Unidad("metro", "metros") }, { "km", new Unidad("kilómetro", "kilómetros") },
            { "mg", new Unidad("miligramo", "miligramos") }, { "g", new Unidad("gramo", "gramos") },
            { "kg", new Unidad("kilogramo", "kilogramos") }, { "t", new Unidad("tonelada", "toneladas") },
            { "ml", new Unidad("mililitro", "mililitros") }, { "mL", new Unidad("mililitro", "mililitros") },
            { "l", new Unidad("litro", "litros") }, { "L", new Unidad("litro", "litros") },
            { "km/h", new Unidad("kilómetro por hora", "kilómetros por hora") }, { "m/s", new Unidad("metro por segundo", "metros por segundo") },
            { "°C", new Unidad("grado Celsius", "grados Celsius") }, { "ºC", new Unidad("grado Celsius", "grados Celsius") },
            { "°F", new Unidad("grado Fahrenheit", "grados Fahrenheit") }, { "°", new Unidad("grado", "grados") },
            { "dB", new Unidad("decibelio", "decibelios") }, { "V", new Unidad("voltio", "voltios") }, { "mV", new Unidad("milivoltio", "milivoltios") },
            { "W", new Unidad("vatio", "vatios") }, { "kW", new Unidad("kilovatio", "kilovatios") }, { "kWh", new Unidad("kilovatio hora", "kilovatios hora") },
            { "mAh", new Unidad("miliamperio hora", "miliamperios hora") }, { "A", new Unidad("amperio", "amperios") },
            { "ha", new Unidad("hectárea", "hectáreas") }, { "m²", new Unidad("metro cuadrado", "metros cuadrados") },
            { "m2", new Unidad("metro cuadrado", "metros cuadrados") }, { "km²", new Unidad("kilómetro cuadrado", "kilómetros cuadrados") },
        };

        // Sueltas tras una cifra son ambiguas con palabras ("2 a", "5 us", "3 ha"): ahí se deja a la voz,
        // que ya dice "2,5 metros" o "1,5 kilogramos" por su cuenta.
        private static readonly HashSet<string> AmbiguasSueltas = new HashSet<string>(StringComparer.Ordinal) { "us", "pt", "ha", "seg", "t", "A", "V", "W", "B" };

        // ¿Es "token" una unidad que sigue a un número separado por espacio? (100 ms, 16 GB, 20 °C)
        public static string UnidadSuelta(string token, string numeroAnterior)
        {
            Unidad u;
            if (!Unidades.TryGetValue(token, out u)) return null;
            if (AmbiguasSueltas.Contains(token) || (token.Length == 1 && token != "°")) return null;
            return EsUno(numeroAnterior) ? u.Singular : u.Plural;
        }

        private static bool EsUno(string n)
        {
            string t = n.TrimStart('+', '-', '−', '±');
            return t == "1" || t == "1,0" || t == "1.0";
        }

        // ---------------- Ordinales ----------------
        private static readonly string[] OrdUnidades = { "", "primer", "segund", "tercer", "cuart", "quint", "sext", "séptim", "octav", "noven" };
        private static readonly string[] OrdDecenas = { "", "décim", "vigésim", "trigésim", "cuadragésim", "quincuagésim", "sexagésim", "septuagésim", "octogésim", "nonagésim" };

        // n: 1..99. genero: 'o' (primero), 'a' (primera), 'r' (primer, tercer: apócope).
        public static string Ordinal(int n, char genero)
        {
            if (n < 1 || n > 99) return null;
            string fin = genero == 'a' ? "a" : "o";
            string s;
            if (n == 11) s = genero == 'a' ? "undécima" : "undécimo";
            else if (n == 12) s = genero == 'a' ? "duodécima" : "duodécimo";
            else
            {
                int d = n / 10, u = n % 10;
                var partes = new List<string>();
                if (d > 0) partes.Add(OrdDecenas[d] + fin);
                if (u > 0)
                {
                    string b = OrdUnidades[u];
                    // "primer"/"tercer" ya traen la r: primero, primera, tercero, tercera.
                    if (u == 1 || u == 3) partes.Add(b + (genero == 'r' && d == 0 ? "" : fin));
                    else partes.Add(b + fin);
                }
                s = string.Join(" ", partes);
            }
            return s;
        }

        // ---------------- Romanos (solo tras una palabra que los anuncia) ----------------
        public static readonly HashSet<string> DisparadoresRomanos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "siglo", "siglos", "capítulo", "capitulo", "cap.", "tomo", "volumen", "vol.", "parte", "libro", "acto", "fase", "título", "titulo",
          "artículo", "articulo", "anexo", "apéndice", "apendice", "lección", "leccion", "unidad", "tema", "nivel", "grado", "clase", "etapa" };

        private static readonly Regex Romano = new Regex(@"^(?=[MDCLXVI])M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.Compiled);

        public static int DeRomano(string s)
        {
            if (string.IsNullOrEmpty(s) || !Romano.IsMatch(s)) return 0;
            var v = new Dictionary<char, int> { { 'I', 1 }, { 'V', 5 }, { 'X', 10 }, { 'L', 50 }, { 'C', 100 }, { 'D', 500 }, { 'M', 1000 } };
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int a = v[s[i]], b = i + 1 < s.Length ? v[s[i + 1]] : 0;
                total += a < b ? -a : a;
            }
            return total;
        }

        // ---------------- Abreviaturas ----------------
        public static readonly Dictionary<string, string> Abreviaturas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "aprox.", "aproximadamente" }, { "etc.", "etcétera" }, { "pág.", "página" }, { "págs.", "páginas" }, { "pp.", "páginas" },
            { "núm.", "número" }, { "nro.", "número" }, { "n.º", "número" }, { "nº", "número" },
            { "tel.", "teléfono" }, { "tlf.", "teléfono" }, { "cel.", "celular" }, { "vs.", "versus" }, { "vs", "versus" },
            { "sr.", "señor" }, { "sra.", "señora" }, { "srta.", "señorita" }, { "dr.", "doctor" }, { "dra.", "doctora" },
            { "ing.", "ingeniero" }, { "lic.", "licenciado" }, { "prof.", "profesor" }, { "ud.", "usted" }, { "uds.", "ustedes" },
            { "av.", "avenida" }, { "admón.", "administración" }, { "dpto.", "departamento" }, { "depto.", "departamento" },
            { "máx.", "máximo" }, { "mín.", "mínimo" }, { "cap.", "capítulo" },
            { "vol.", "volumen" }, { "ed.", "edición" }, { "ej.", "ejemplo" }, { "obs.", "observación" }, { "art.", "artículo" },
            { "s.f.", "sin fecha" }, { "a.m.", "a eme" }, { "p.m.", "pe eme" }, { "e.g.", "por ejemplo" }, { "i.e.", "es decir" },
            { "approx.", "aproximadamente" }, { "aprox", "aproximadamente" }, { "q.e.p.d.", "que en paz descanse" },
        };

        // ---------------- Patrones de una ficha ----------------
        private static readonly Regex FechaIso = new Regex(@"^(\d{4})-(\d{1,2})-(\d{1,2})(?:[T ](\d{1,2}:\d{2}(?::\d{2})?))?$", RegexOptions.Compiled);
        private static readonly Regex FechaDma = new Regex(@"^(\d{1,2})[/.](\d{1,2})[/.](\d{4}|\d{2})$", RegexOptions.Compiled);
        private static readonly Regex Rango = new Regex(@"^([+-]?\d+(?:[.,]\d+)?)([-–—]|\.\.)(\d+(?:[.,]\d+)?)(%?)$", RegexOptions.Compiled);
        private static readonly Regex Barra2 = new Regex(@"^(\d+)/(\d+)$", RegexOptions.Compiled);
        private static readonly Regex Version = new Regex(@"^[vV]?(\d+(?:\.\d+){2,3})(?:-([A-Za-z][\w.]*))?$", RegexOptions.Compiled);
        private static readonly Regex VersionV = new Regex(@"^[vV](\d+(?:\.\d+)?)$", RegexOptions.Compiled);
        private static readonly Regex Ip = new Regex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?::(\d{1,5}))?$", RegexOptions.Compiled);
        private static readonly Regex OrdinalRx = new Regex(@"^(\d{1,2})\.?(º|ª|°|o|a|er|ra|do|da|ro|to|ta|vo|va|mo|ma|no|na)$", RegexOptions.Compiled);
        private static readonly Regex Multiplo = new Regex(@"^(\d+(?:[.,]\d+)?)[xX×]$", RegexOptions.Compiled);
        private static readonly Regex Dimension = new Regex(@"^(\d+(?:[.,]\d+)?)[xX×](\d+(?:[.,]\d+)?)(?:[xX×](\d+(?:[.,]\d+)?))?$", RegexOptions.Compiled);
        private static readonly Regex Cientifica = new Regex(@"^(\d+(?:[.,]\d+)?)[eE]([+-]?\d+)$", RegexOptions.Compiled);
        private static readonly Regex HexLiteral = new Regex(@"^0[xX]([0-9A-Fa-f]+)$", RegexOptions.Compiled);
        private static readonly Regex ConUnidad = new Regex(@"^([+-]?\d+(?:[.,]\d+)?)((?:[a-zA-Zµ°º²]+)(?:/[a-zA-Z]+)?)$", RegexOptions.Compiled);
        private static readonly Regex Numeral = new Regex(@"^#(\d+)$", RegexOptions.Compiled);
        private static readonly Regex MasMenos = new Regex(@"^±(\d+(?:[.,]\d+)?)(.*)$", RegexOptions.Compiled);
        private static readonly Regex TelefonoGuiones = new Regex(@"^(\d{3})-(\d{3,4})-(\d{4})$", RegexOptions.Compiled);
        private static readonly Regex Hora =new Regex(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?$", RegexOptions.Compiled);

        // Intenta leer una ficha completa (ya sin envolturas ni puntuación final). null = no es un caso numérico especial.
        // numero(x) normaliza un número suelto con el separador decimal de la voz.
        public static string Ficha(string t, Func<string, string> numero)
        {
            if (t.Length == 0 || !(char.IsDigit(t[0]) || t[0] == '#' || t[0] == '±' || t[0] == 'v' || t[0] == 'V' || t[0] == '+' || t[0] == '-')) return null;
            if (Preparador.EsMiles(t)) return null;     // 1.000.000 es un número, no una versión ni una IP
            Match m;

            m = TelefonoGuiones.Match(t);
            if (m.Success) return m.Groups[1].Value + ", " + m.Groups[2].Value + ", " + m.Groups[3].Value;

            m = FechaIso.Match(t);
            if (m.Success)
            {
                string f = Fecha(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value);
                if (f != null) return m.Groups[4].Success ? f + ", " + m.Groups[4].Value : f;
            }
            m = FechaDma.Match(t);
            if (m.Success)
            {
                string anio = m.Groups[3].Value.Length == 2 ? "20" + m.Groups[3].Value : m.Groups[3].Value;
                string f = Fecha(m.Groups[1].Value, m.Groups[2].Value, anio);
                if (f != null) return f;
            }
            m = Ip.Match(t);
            if (m.Success && ValidaIp(m))
            {
                string s = m.Groups[1].Value + " punto " + m.Groups[2].Value + " punto " + m.Groups[3].Value + " punto " + m.Groups[4].Value;
                if (m.Groups[5].Success) s += ", puerto " + m.Groups[5].Value;
                return s;
            }
            m = Version.Match(t);
            if (m.Success)
            {
                string s = (t[0] == 'v' || t[0] == 'V' ? "versión " : "") + m.Groups[1].Value.Replace(".", " punto ");
                if (m.Groups[2].Success) s += " " + m.Groups[2].Value.Replace(".", " ");
                return s;
            }
            m = VersionV.Match(t);
            if (m.Success) return "versión " + m.Groups[1].Value.Replace(".", " punto ");
            m = Rango.Match(t);
            if (m.Success)
            {
                string a = m.Groups[1].Value, b = m.Groups[3].Value, pct = m.Groups[4].Value;
                // 300-555-1234 no llega aquí (tres partes). "3-2" (marcador) también se lee "3 a 2".
                return numero(a) + pct + " a " + numero(b) + pct;
            }
            m = Barra2.Match(t);
            if (m.Success) return Barra(m.Groups[1].Value, m.Groups[2].Value);
            m = OrdinalRx.Match(t);
            if (m.Success && !(m.Groups[2].Value == "°"))
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string suf = m.Groups[2].Value;
                char g = suf == "ª" || suf == "a" || suf.EndsWith("a") ? 'a' : suf == "er" ? 'r' : 'o';
                // "1o" o "2a" pegados sin punto son raros; se aceptan solo º, ª, er, .o, .a y las terminaciones claras.
                if ((suf == "o" || suf == "a") && t.IndexOf('.') < 0) return null;
                string o = Ordinal(n, g);
                if (o != null) return o;
            }
            m = Dimension.Match(t);
            if (m.Success)
            {
                string s = numero(m.Groups[1].Value) + " por " + numero(m.Groups[2].Value);
                if (m.Groups[3].Success) s += " por " + numero(m.Groups[3].Value);
                return s;
            }
            m = Multiplo.Match(t);
            if (m.Success) return numero(m.Groups[1].Value) + (EsUno(m.Groups[1].Value) ? " vez" : " veces");
            m = Cientifica.Match(t);
            if (m.Success)
            {
                string exp = m.Groups[2].Value;
                string e = exp.StartsWith("-") ? "menos " + exp.Substring(1) : exp.TrimStart('+');
                return numero(m.Groups[1].Value) + " por 10 a la " + e;
            }
            m = HexLiteral.Match(t);
            if (m.Success) return "hexadecimal " + Deletrear(m.Groups[1].Value.ToUpperInvariant());
            m = Numeral.Match(t);
            if (m.Success) return "número " + m.Groups[1].Value;
            m = MasMenos.Match(t);
            if (m.Success)
            {
                string resto = m.Groups[2].Value;
                string unidad = "";
                Unidad u;
                if (resto.Length > 0 && Unidades.TryGetValue(resto, out u)) unidad = " " + u.Plural;
                else if (resto.Length > 0) return null;
                return "más menos " + numero(m.Groups[1].Value) + unidad;
            }
            m = ConUnidad.Match(t);
            if (m.Success)
            {
                Unidad u;
                if (Unidades.TryGetValue(m.Groups[2].Value, out u))
                    return Signo(m.Groups[1].Value, numero) + " " + (EsUno(m.Groups[1].Value) ? u.Singular : u.Plural);
            }
            return null;
        }

        private static string Signo(string n, Func<string, string> numero)
        {
            if (n.StartsWith("-")) return "menos " + numero(n.Substring(1));
            if (n.StartsWith("+")) return "más " + numero(n.Substring(1));
            return numero(n);
        }

        private static bool ValidaIp(Match m)
        {
            for (int g = 1; g <= 4; g++) if (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) > 255) return false;
            return true;
        }

        private static string Fecha(string d, string mes, string anio)
        {
            int dd, mm;
            if (!int.TryParse(d, out dd) || !int.TryParse(mes, out mm)) return null;
            if (mm < 1 || mm > 12 || dd < 1 || dd > 31) return null;
            return (dd == 1 ? "1" : dd.ToString(CultureInfo.InvariantCulture)) + " de " + Meses[mm] + " de " + anio;
        }

        // a/b: fracción común (1/2, 3/4), progreso (152/152, 10/20) o cociente (24/7).
        private static string Barra(string a, string b)
        {
            int x, y;
            if (!int.TryParse(a, out x) || !int.TryParse(b, out y) || y == 0) return a + " barra " + b;
            // 1/2, 3/4: la voz de Edge ya concuerda en género ("media taza", "tres cuartas partes"): se deja tal cual.
            if (x < y && y >= 2 && y <= 10 && x <= 9) return a + "/" + b;
            if (x <= y) return a + " de " + b;
            return a + " barra " + b;
        }

        public static string Deletrear(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // 14:30 -> "14 y 30": la voz de España leía "14:30" como "las 2 del mediodía y 30". 9:00 -> "9".
        // Con segundos (marcas de tiempo de un log) se deja tal cual.
        public static string LeerHora(string t)
        {
            var m = Hora.Match(t);
            if (!m.Success || m.Groups[3].Success || t.IndexOf('.') >= 0) return t;
            int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return mi == 0 ? h.ToString(CultureInfo.InvariantCulture) : h + " y " + mi;
        }

        // ¿La ficha es una hora (14:30, 9:05:12)?
        public static bool EsHora(string t)
        {
            var m = Hora.Match(t);
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return h <= 24 && mi <= 59;
        }
    }
}
