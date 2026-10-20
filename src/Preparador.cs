using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LectorKaraoke
{
    // Una palabra del texto original y cómo se pronuncia. Las posiciones son del texto original,
    // para poder resaltarla en la aplicación; los tiempos los rellena el motor de voz.
    internal sealed class Palabra
    {
        public int OrigInicio;
        public int OrigLargo;
        public string Habla;
        public int HablaInicio;
        public double InicioMs = -1;
        public double FinMs = -1;
    }

    internal sealed class Trozo
    {
        public string Texto = "";
        public List<Palabra> Palabras = new List<Palabra>();
        public bool TeniaSecretos;
    }

    internal sealed class Preparado
    {
        public List<Trozo> Trozos = new List<Trozo>();
        public int Secretos;
        public bool Truncado;
    }

    // Convierte el texto seleccionado en trozos listos para hablar, sin perder la posición de cada palabra.
    internal static class Preparador
    {
        private sealed class Ficha
        {
            public int Inicio, Largo;
            public string Texto;
            public int SaltosAntes;     // saltos de línea entre la ficha anterior y esta
            public string Habla = "";
            public bool Secreto;
        }

        private static readonly Regex Ansi = new Regex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);

        private const string PalabrasClave = @"(?:pass(?:word|wd|phrase)?|pwd|contrase(?:ñ|n)a|clave|secret[o]?|token|api[_\-]?key|apikey|access[_\-]?key|private[_\-]?key|client[_\-]?secret|credential[s]?|auth(?:orization)?|bearer|cookie|session[_\-]?id)";
        // La palabra clave puede llevar prefijo o sufijo separado por _ - . (DB_PASSWORD, api-key, x.token), nunca pegado
        // (así "tokenizer" o "Tokens used" no cuentan). Sola, exige ':' o '=' al final.
        private const string ConAfijos = @"(?:[\w]*[_\-.])?" + PalabrasClave + @"(?:[_\-.][\w]*)?";
        private static readonly Regex ClaveSola = new Regex(@"^[""'`]?" + ConAfijos + @"[""'`]?[:=]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClaveValor = new Regex(@"^[""'`]?(" + ConAfijos + @")[""'`]?\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HexCorto = new Regex(@"^(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{7,}$", RegexOptions.Compiled);
        private static readonly Regex[] SecretosConocidos =
        {
            new Regex(@"^eyJ[\w-]{5,}\.[\w-]{5,}\.[\w-]{5,}", RegexOptions.Compiled),          // JWT
            new Regex(@"^(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}", RegexOptions.Compiled),   // GitHub
            new Regex(@"^sk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),                         // OpenAI / Anthropic
            new Regex(@"^(?:AKIA|ASIA)[0-9A-Z]{16}$", RegexOptions.Compiled),                    // AWS
            new Regex(@"^xox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled),                   // Slack
            new Regex(@"^AIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),                        // Google
            new Regex(@"^glpat-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),                       // GitLab
        };
        private static readonly Regex UriConCredenciales = new Regex(@"^([a-z][a-z0-9+.\-]*)://[^/\s:@]+:[^@\s]+@([^/\s?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Url = new Regex(@"^(?:(?:https?|ftp)://|www\.)([^/\s?#:]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Hex = new Regex(@"^[0-9a-fA-F]{32,}$", RegexOptions.Compiled);
        private static readonly Regex Base64Largo = new Regex(@"^[A-Za-z0-9+/=_\-]{32,}$", RegexOptions.Compiled);

        // Separador decimal que entiende la voz elegida (es-ES dice "coma", es-MX "punto").
        [ThreadStatic] private static char separadorDecimal;
        [ThreadStatic] private static bool numerosLiterales;    // numeros = literal: no se reinterpretan los separadores

        public static Preparado Preparar(string original, Configuracion cfg)
        {
            separadorDecimal = SeparadorDecimalDe(cfg.VozEdge);
            numerosLiterales = cfg.Numeros == "literal";
            var res = new Preparado();
            if (string.IsNullOrEmpty(original)) return res;
            if (original.Length > cfg.MaxCaracteres)
            {
                original = original.Substring(0, cfg.MaxCaracteres);
                res.Truncado = true;
            }

            var fichas = Fichas(original);
            res.Secretos = MarcarSecretos(fichas);
            for (int i = 0; i < fichas.Count; i++)
            {
                var f = fichas[i];
                if (f.Secreto) continue;
                bool inicioLinea = i == 0 || f.SaltosAntes > 0;
                f.Habla = cfg.Simbolos == "no" ? Normalizar(f.Texto, cfg) : NormalizarConSimbolos(f.Texto, cfg, inicioLinea);
            }

            AgregarPausas(fichas);
            res.Trozos = Trocear(fichas);
            return res;
        }

        // ---------------- 1. Fichas con posición ----------------
        private static List<Ficha> Fichas(string s)
        {
            var ignorar = new bool[s.Length];
            foreach (Match m in Ansi.Matches(s))
                for (int k = m.Index; k < m.Index + m.Length; k++) ignorar[k] = true;

            var lista = new List<Ficha>();
            int i = 0, saltos = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (ignorar[i] || EsSeparador(c))
                {
                    if (c == '\n') saltos++;
                    i++;
                    continue;
                }
                int ini = i;
                while (i < s.Length && !ignorar[i] && !EsSeparador(s[i])) i++;
                lista.Add(new Ficha { Inicio = ini, Largo = i - ini, Texto = s.Substring(ini, i - ini), SaltosAntes = saltos });
                saltos = 0;
            }
            return lista;
        }

        private static bool EsSeparador(char c)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
            if (c >= '─' && c <= '▟') return true;   // cajas y bloques
            if (c >= '⠀' && c <= '⣿') return true;   // braille (spinners)
            if (c == '​' || c == '‌' || c == '‍' || c == '﻿') return true;
            return false;
        }

        // ---------------- 2. Secretos ----------------
        private static int MarcarSecretos(List<Ficha> fichas)
        {
            int n = 0;
            for (int i = 0; i < fichas.Count; i++)
            {
                var f = fichas[i];
                string t = Recortar(f.Texto);

                // Bloque PEM: se oculta entero.
                if (f.Texto.Contains("-----BEGIN"))
                {
                    int fin = i;
                    for (int k = i; k < fichas.Count && k < i + 400; k++)
                    {
                        if (fichas[k].Texto.Contains("-----END")) { fin = k; break; }
                    }
                    if (fin > i)
                    {
                        // "-----END" suele ir seguido de "PRIVATE KEY-----"
                        while (fin + 1 < fichas.Count && !fichas[fin].Texto.EndsWith("-----") && fin < i + 405) fin++;
                        for (int k = i; k <= fin; k++) { fichas[k].Secreto = true; fichas[k].Habla = k == i ? "bloque de clave oculto." : ""; }
                        n++;
                        i = fin;
                        continue;
                    }
                }

                var mUri = UriConCredenciales.Match(t);
                if (mUri.Success)
                {
                    f.Secreto = true;
                    f.Habla = "enlace " + mUri.Groups[2].Value + ", con credenciales ocultas";
                    n++;
                    continue;
                }

                var mCv = ClaveValor.Match(t);
                if (mCv.Success && Recortar(mCv.Groups[2].Value).Length > 0)
                {
                    f.Secreto = true;
                    f.Habla = SepararIdentificador(mCv.Groups[1].Value) + ", valor oculto";
                    n++;
                    continue;
                }

                if (EsSecretoSuelto(t))
                {
                    f.Secreto = true;
                    f.Habla = "valor oculto";
                    n++;
                    continue;
                }

                // "password: xyz", "Authorization: Bearer xyz", "Bearer xyz": se oculta lo que sigue en la misma línea.
                string crudo = f.Texto.Trim('"', '\'', '`', '(', '[', '{');
                bool esBearer = t.Equals("bearer", StringComparison.OrdinalIgnoreCase);
                if ((ClaveSola.IsMatch(crudo) || esBearer) && crudo.Length <= 40)
                {
                    int k = i + 1;
                    // "Authorization: Bearer abc" -> saltar la palabra "Bearer"
                    if (k < fichas.Count && fichas[k].SaltosAntes == 0 && Recortar(fichas[k].Texto).Equals("bearer", StringComparison.OrdinalIgnoreCase)) k++;
                    if (k < fichas.Count && fichas[k].SaltosAntes == 0)
                    {
                        string sig = Recortar(fichas[k].Texto);
                        if (sig.Length >= 3 && !EsPalabraComun(sig))
                        {
                            fichas[k].Secreto = true;
                            fichas[k].Habla = "valor oculto";
                            n++;
                            i = k;
                        }
                    }
                }
            }
            return n;
        }

        private static bool EsSecretoSuelto(string t)
        {
            foreach (var r in SecretosConocidos) if (r.IsMatch(t)) return true;
            if (Hex.IsMatch(t)) return false;                       // hashes: no son secretos (se leen como "código")
            if (Base64Largo.IsMatch(t) && !t.Contains("--"))
            {
                bool may = false, min = false, dig = false;
                foreach (char c in t) { if (char.IsUpper(c)) may = true; else if (char.IsLower(c)) min = true; else if (char.IsDigit(c)) dig = true; }
                if (may && min && dig) return true;
            }
            return false;
        }

        private static readonly HashSet<string> Comunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "de", "del", "la", "el", "los", "las", "es", "is", "the", "for", "para", "por", "con", "with", "and", "y", "o", "or", "a", "an", "to", "en", "in",
          "invalido", "inválido", "invalid", "incorrecto", "incorrect", "expired", "expirado", "required", "requerido", "missing", "vacío", "empty",
          "manager", "de", "nueva", "new", "actual", "current", "reset", "field", "campo" };

        private static bool EsPalabraComun(string s) { return Comunes.Contains(s); }

        // ---------------- 3. Normalización ----------------
        private static string Recortar(string t)
        {
            int a = 0, b = t.Length;
            while (a < b && "([{<\"'«“‘`*_~>".IndexOf(t[a]) >= 0) a++;
            while (b > a && ")]}>\"'»”’`*_~.,;:!?…".IndexOf(t[b - 1]) >= 0) b--;
            return t.Substring(a, b - a);
        }

        internal static string Normalizar(string token, Configuracion cfg)
        {
            // Puntuación final que sirve de pausa.
            string fin = "";
            int b = token.Length;
            while (b > 0 && ")]}>\"'»”’`*_~".IndexOf(token[b - 1]) >= 0) b--;
            if (b > 0)
            {
                char u = token[b - 1];
                if (".,;:!?…".IndexOf(u) >= 0)
                {
                    fin = u == '…' ? "." : u.ToString();
                    while (b > 0 && ".,;:!?…".IndexOf(token[b - 1]) >= 0) b--;
                }
            }
            int a = 0;
            while (a < b && "([{<\"'«“‘`*_~>¿¡#".IndexOf(token[a]) >= 0) a++;
            string nucleo = token.Substring(a, b - a);
            if (nucleo.Length == 0) return "";
            string pre = "";
            if (a > 0 && (token[a - 1] == '¿' || token[a - 1] == '¡')) pre = token[a - 1].ToString();

            string habla;
            var mUrl = Url.Match(nucleo);
            if (mUrl.Success)
            {
                if (cfg.LeerUrls == "completa") habla = nucleo;
                else if (cfg.LeerUrls == "no") habla = "enlace";
                else
                {
                    string host = mUrl.Groups[1].Value;
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                    habla = "enlace " + host;
                }
            }
            else if (Hex.IsMatch(nucleo) || HexCorto.IsMatch(nucleo)) habla = "código";
            else if (EsRuta(nucleo))
            {
                var partes = nucleo.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var sb = new StringBuilder();
                foreach (var p0 in partes)
                {
                    string p = p0.EndsWith(":") && p0.Length == 2 ? p0.Substring(0, 1) : p0;
                    string s = SepararIdentificador(p);
                    if (s.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(s);
                }
                habla = sb.ToString();
            }
            else habla = SepararIdentificador(nucleo);

            habla = Limpiar(habla);
            if (habla.Length == 0) return "";
            return pre + habla + fin;
        }

        // ---------------- 3b. Normalización que nombra los símbolos ----------------
        // "algunos": nombra comillas, paréntesis, llaves, corchetes y / \ | # $ % & = @ * ¨ ~ ^ + < >; la puntuación normal
        // sigue siendo pausa. "todos": además punto, coma, interrogación, guiones, etc. Las decoraciones (markdown,
        // líneas de ===, viñetas) no se leen.
        private static readonly Dictionary<char, string> Nombres = new Dictionary<char, string>
        {
            { '/', "barra" }, { '\\', "barra invertida" }, { '|', "barra vertical" }, { '#', "numeral" }, { '$', "signo de dólar" },
            { '%', "por ciento" }, { '&', "ampersand" }, { '=', "igual" }, { '@', "arroba" }, { '*', "asterisco" }, { '¨', "diéresis" },
            { '~', "virgulilla" }, { '^', "circunflejo" }, { '+', "más" }, { '_', "guion bajo" }, { '<', "menor que" }, { '>', "mayor que" },
            { '{', "abre llave" }, { '}', "cierra llave" }, { '[', "abre corchete" }, { ']', "cierra corchete" },
            { '(', "abre paréntesis" }, { ')', "cierra paréntesis" }, { '“', "abre comillas" }, { '”', "cierra comillas" },
            { '«', "abre comillas" }, { '»', "cierra comillas" }, { '‘', "abre comilla simple" }, { '’', "cierra comilla simple" },
            { '`', "comilla invertida" }, { '´', "acento" }, { '°', "grados" }, { '§', "párrafo" }, { '€', "euros" }, { '£', "libras" },
            { '•', "viñeta" }, { '·', "punto medio" }, { '–', "raya" }, { '—', "raya" }, { '…', "puntos suspensivos" },
            { '-', "guion" }, { '¿', "abre interrogación" }, { '¡', "abre exclamación" }, { '?', "interrogación" }, { '!', "exclamación" },
            { '.', "punto" }, { ',', "coma" }, { ';', "punto y coma" }, { ':', "dos puntos" },
        };
        private const string Puntuacion = ".,;:?!¿¡…-–—";            // en "algunos" son pausa, no se nombran
        private const string Decoracion = "*_~`#=-+•·";               // repetidos o en markdown no se leen

        internal static string NormalizarConSimbolos(string token, Configuracion cfg, bool inicioLinea)
        {
            bool todos = cfg.Simbolos == "todos";
            if (token.Length == 0) return "";

            // Líneas de decoración (=====, -----, ****) y viñetas o encabezados markdown al inicio de línea: no se leen.
            if (token.Length >= 3 && token.Trim(token[0]).Length == 0 && Decoracion.IndexOf(token[0]) >= 0) return "";
            if (inicioLinea && (token == "-" || token == "*" || token == "•" || token == "+")) return "";
            if (inicioLinea && token.Trim('#').Length == 0) return "";

            // URLs y códigos largos se resumen igual que sin símbolos.
            string sinEnvoltura = token.Trim('"', '\'', '(', ')', '[', ']', '<', '>', '`', '*', ',', '.', ';', ':');
            if (Url.IsMatch(sinEnvoltura) || Hex.IsMatch(sinEnvoltura) || HexCorto.IsMatch(sinEnvoltura)) return Normalizar(token, cfg);

            // Énfasis markdown (**texto**, _texto_, `codigo`): se quita la envoltura sin leerla.
            string t = token;
            if (!todos)
                foreach (char m in new[] { '*', '_', '~', '`' })
                {
                    int a = 0, b = t.Length;
                    while (a < b && t[a] == m) a++;
                    int b0 = b;
                    while (b > a && ".,;:!?".IndexOf(t[b - 1]) >= 0) b--;
                    int cola = b;
                    while (b > a && t[b - 1] == m) b--;
                    if (a > 0 && b < cola && a < b) t = t.Substring(a, b - a) + t.Substring(cola, b0 - cola);
                }

            // Puntuación final: pausa (en "todos" además se nombra).
            int finNucleo = t.Length;
            while (finNucleo > 0 && ".,;:!?…".IndexOf(t[finNucleo - 1]) >= 0) finNucleo--;
            string fin = "";
            if (finNucleo < t.Length)
            {
                char u = t[t.Length - 1];
                fin = (u == '…' || u == '!' || u == '?') ? (u == '…' ? "." : u.ToString()) : u.ToString();
            }

            var partes = new List<string>();
            int i = 0;
            while (i < finNucleo)
            {
                char c = t[i];
                bool moneda = (c == '$' || c == '€' || c == '£') && i + 1 < finNucleo && char.IsDigit(t[i + 1]);
                if (char.IsLetterOrDigit(c) || moneda)
                {
                    int ini = i;
                    if (moneda) i++;
                    while (i < finNucleo && ParteDePalabra(t, i, finNucleo, todos)) i++;
                    if (i == ini) i++;
                    string trozo = Limpiar(SepararIdentificador(t.Substring(ini, i - ini)));
                    if (trozo.Length > 0) partes.Add(trozo);
                    continue;
                }
                int j = i;
                while (j < finNucleo && t[j] == c) j++;
                int n = j - i;
                string nombre = NombreSimbolo(t, i, finNucleo, todos);
                if (nombre.Length > 0 && n < 3) partes.Add(n == 2 ? "doble " + nombre : nombre);
                i = j;
            }
            if (todos)
                for (int k = finNucleo; k < t.Length; k++)
                {
                    string nombre;
                    if (Nombres.TryGetValue(t[k], out nombre)) partes.Add(nombre);
                }
            string habla = string.Join(" ", partes).Trim();
            if (habla.Length == 0) return "";
            if (todos) return habla + (fin.Length > 0 ? "," : "");
            return habla + fin;
        }

        // ¿El carácter t[i] sigue formando parte de la palabra o número actual?
        private static bool ParteDePalabra(string t, int i, int fin, bool todos)
        {
            char c = t[i];
            if (char.IsLetterOrDigit(c)) return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark) return true;
            char prev = i > 0 ? t[i - 1] : '\0';
            char next = i + 1 < fin ? t[i + 1] : '\0';
            // Números: 3.5, 1,234, 12:30, 50%, 2026-10-20; apóstrofo dentro de palabras.
            if ((c == '.' || c == ',' || c == ':') && char.IsDigit(prev) && char.IsDigit(next)) return true;
            if (c == '-' && char.IsDigit(prev) && char.IsDigit(next)) return true;
            if (c == '%' && char.IsDigit(prev)) return true;
            if (c == '\'' && char.IsLetter(prev) && char.IsLetter(next)) return true;
            // Identificadores: snake_case y kebab-case se leen separados sin nombrar el guion (salvo en "todos").
            if (!todos && (c == '_' || c == '-') && char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next)) return true;
            return false;
        }

        private static string NombreSimbolo(string t, int i, int fin, bool todos)
        {
            char c = t[i];
            char prev = i > 0 ? t[i - 1] : '\0';
            char next = i + 1 < fin ? t[i + 1] : '\0';
            if (c == '"') return i == 0 ? "abre comillas" : (i >= fin - 1 ? "cierra comillas" : "comillas");
            if (c == '\'') return i == 0 ? "abre comilla simple" : (i >= fin - 1 ? "cierra comilla simple" : (todos ? "apóstrofo" : ""));
            if ((c == '$' || c == '€' || c == '£') && char.IsDigit(next)) return c == '$' ? "dólares" : c == '€' ? "euros" : "libras";
            if (c == '-' && char.IsDigit(next) && !char.IsLetterOrDigit(prev)) return "menos";
            if (c == '.' && char.IsLetter(prev) && char.IsLetter(next)) return "punto";
            if (c == '?' && next != '\0') return "interrogación";
            if (!todos && Puntuacion.IndexOf(c) >= 0) return "";
            string nombre;
            if (Nombres.TryGetValue(c, out nombre)) return nombre;
            if (char.IsSurrogate(c) || char.IsControl(c)) return "";
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherSymbol) return "";
            return todos ? "símbolo" : "";
        }

        private static bool EsRuta(string s)
        {
            if (s.IndexOf('\\') >= 0 && s.Length > 2) return true;
            if (s.IndexOf('/') < 0) return false;
            bool letras = false;
            foreach (char c in s) if (char.IsLetter(c)) { letras = true; break; }
            if (!letras) return false;                  // 10/20/2026 es una fecha
            int barras = 0;
            foreach (char c in s) if (c == '/') barras++;
            return barras >= 2 || s.IndexOf('.') >= 0 || s.StartsWith("/");
        }

        internal static string SepararIdentificador(string s)
        {
            if (s.Length == 0) return s;
            // Fechas y números (2026-10-20, 1.234,56, 3.5) se dejan tal cual.
            bool soloNum = true;
            foreach (char c in s) if (!(char.IsDigit(c) || c == '-' || c == '.' || c == ',' || c == ':' || c == '%' || c == '$' || c == '+' || c == '€' || c == '£')) { soloNum = false; break; }
            if (soloNum) return NormalizarNumero(s);

            var sb = new StringBuilder();
            int puntos = 0;
            foreach (char c in s) if (c == '.') puntos++;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char prev = i > 0 ? s[i - 1] : '\0';
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '_' || c == '=' || c == '|' || c == '\\' || c == '/') { sb.Append(' '); continue; }
                if (c == '-' && char.IsLetter(prev) && char.IsLetter(next)) { sb.Append(' '); continue; }
                if (c == '.' && puntos >= 2 && char.IsLetter(prev) && char.IsLetter(next)) { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(prev)) sb.Append(' ');
                else if (i > 0 && char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next) && i > 1) sb.Append(' ');   // HTTPServer -> HTTP Server
                else if (i > 0 && char.IsDigit(c) && char.IsLetter(prev)) sb.Append(' ');
                else if (i > 0 && char.IsLetter(c) && char.IsDigit(prev)) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------- Números ----------------
        private static readonly Regex MilesPunto = new Regex(@"^(\d{1,3})((?:\.\d{3})+)(?:,(\d+))?$", RegexOptions.Compiled);   // 25.000  1.234.567,89
        private static readonly Regex MilesComa = new Regex(@"^(\d{1,3})((?:,\d{3})+)(?:\.(\d+))?$", RegexOptions.Compiled);    // 25,000  1,234,567.89
        private static readonly Regex DecimalSimple = new Regex(@"^(\d+)[.,](\d+)$", RegexOptions.Compiled);                     // 3,5  3.14

        internal static char SeparadorDecimalDe(string voz)
        {
            string v = (voz ?? "").ToLowerInvariant();
            foreach (var pre in new[] { "es-mx", "es-us", "es-gt", "es-hn", "es-ni", "es-sv", "es-pa", "es-do", "es-pr", "en-" })
                if (v.StartsWith(pre)) return '.';
            return v.StartsWith("es-") ? ',' : '.';
        }

        // Quita los separadores de miles para que la voz diga "veinticinco mil" y deja el decimal en la forma de la voz.
        // 25.000, 25,000 y 25000 -> 25000. 3,5 / 3.5 -> decimal. 1.2.3 y 192.168.0.1 (versiones, IP) se dejan.
        internal static string NormalizarNumero(string s)
        {
            if (s.Length == 0 || numerosLiterales) return s;
            char dec = separadorDecimal == '\0' ? '.' : separadorDecimal;
            string moneda = "";
            string porciento = "";
            string signo = "";
            string n = s;
            if (n[0] == '$' || n[0] == '€' || n[0] == '£')
            {
                moneda = n[0] == '$' ? " dólares" : n[0] == '€' ? " euros" : " libras";
                n = n.Substring(1);
            }
            if (n.EndsWith("%")) { porciento = "%"; n = n.Substring(0, n.Length - 1); }
            if (n.StartsWith("-") || n.StartsWith("+")) { signo = n.Substring(0, 1); n = n.Substring(1); }
            if (n.Length == 0) return s;

            string resultado = null;
            var mp = MilesPunto.Match(n);
            var mc = MilesComa.Match(n);
            if (mp.Success && mp.Groups[1].Value != "0")
                resultado = mp.Groups[1].Value + mp.Groups[2].Value.Replace(".", "") + (mp.Groups[3].Success ? dec + mp.Groups[3].Value : "");
            else if (mc.Success && mc.Groups[1].Value != "0")
                resultado = mc.Groups[1].Value + mc.Groups[2].Value.Replace(",", "") + (mc.Groups[3].Success ? dec + mc.Groups[3].Value : "");
            else
            {
                var md = DecimalSimple.Match(n);
                if (md.Success) resultado = md.Groups[1].Value + dec + md.Groups[2].Value;
                else if (moneda.Length > 0 && Regex.IsMatch(n, @"^\d+$")) resultado = n;     // $100
            }
            if (resultado == null) return s;            // fechas, horas, versiones, IP: tal cual
            return signo + resultado + porciento + moneda;
        }

        // ---------------- Idioma del trozo ----------------
        private static readonly HashSet<string> PalabrasIngles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "is", "are", "to", "of", "for", "with", "this", "that", "you", "it", "be", "on", "not", "was", "were",
          "have", "has", "will", "can", "from", "by", "an", "or", "if", "when", "what", "your", "please", "should", "would",
          "which", "there", "their", "they", "we", "our", "my", "into", "about", "been", "but", "all", "do", "does", "did",
          "how", "why", "where", "who", "these", "those", "than", "then", "also", "only", "just", "more", "some", "any", "no" };
        private static readonly HashSet<string> PalabrasEspanol = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "el", "la", "los", "las", "de", "que", "y", "en", "un", "una", "es", "por", "para", "con", "se", "del", "al", "lo",
          "como", "más", "mas", "pero", "su", "sus", "este", "esta", "esto", "son", "fue", "hay", "ya", "muy", "también", "sin",
          "sobre", "entre", "cuando", "donde", "porque", "qué", "cómo", "si", "sí", "le", "les", "nos", "yo", "tu", "tú", "mi" };

        // true si el trozo está claramente en inglés (más palabras funcionales inglesas que españolas).
        internal static bool EsIngles(string texto)
        {
            int en = 0, es = 0;
            foreach (var w0 in Regex.Split(texto, @"[^\p{L}']+"))
            {
                if (w0.Length == 0) continue;
                if (PalabrasIngles.Contains(w0)) en++;
                else if (PalabrasEspanol.Contains(w0)) es++;
            }
            return en >= 2 && en > es * 2;
        }

        private static string Limpiar(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c)) { i++; continue; }           // emojis y símbolos fuera del plano básico
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (char.IsLetterOrDigit(c) || c == ' ') { sb.Append(c); continue; }
                if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark) { sb.Append(c); continue; }
                if ("'.,:%$€@&+-".IndexOf(c) >= 0)
                {
                    // Solo si queda entre letras o números (no-reply, 3.5, 50%, $100)
                    bool izq = sb.Length > 0 && char.IsLetterOrDigit(sb[sb.Length - 1]);
                    bool der = i + 1 < s.Length && char.IsLetterOrDigit(s[i + 1]);
                    if ((c == '%' || c == '$' || c == '€') && (izq || der)) { sb.Append(c); continue; }
                    if (izq && der) { sb.Append(c == '\'' ? ' ' : c); continue; }
                    sb.Append(' ');
                    continue;
                }
                sb.Append(' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        // ---------------- 4. Pausas por saltos de línea ----------------
        private static void AgregarPausas(List<Ficha> fichas)
        {
            Ficha anterior = null;
            foreach (var f in fichas)
            {
                if (f.Habla.Length == 0) continue;
                if (anterior != null && f.SaltosAntes > 0 && !TerminaEnPausa(anterior.Habla))
                    anterior.Habla += f.SaltosAntes >= 2 ? "." : ",";
                anterior = f;
            }
        }

        private static bool TerminaEnPausa(string s)
        {
            return s.Length > 0 && ".,;:!?".IndexOf(s[s.Length - 1]) >= 0;
        }

        // ---------------- 5. Trozos ----------------
        private static List<Trozo> Trocear(List<Ficha> fichas)
        {
            var trozos = new List<Trozo>();
            var actual = new Trozo();
            var sb = new StringBuilder();
            foreach (var f in fichas)
            {
                if (f.Habla.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                var p = new Palabra { OrigInicio = f.Inicio, OrigLargo = f.Largo, Habla = f.Habla, HablaInicio = sb.Length };
                sb.Append(f.Habla);
                actual.Palabras.Add(p);
                if (f.Secreto) actual.TeniaSecretos = true;

                // Trozos crecientes: el primero de unas 3 palabras para que la voz empiece cuanto antes
                // (la espera del servidor crece con el largo del texto); mientras suena, se sintetizan los siguientes.
                int n = trozos.Count;
                int limite = n == 0 ? 22 : n == 1 ? 60 : n == 2 ? 140 : 280;
                int minimo = n == 0 ? 10 : n == 1 ? 25 : 50;
                char ult = f.Habla[f.Habla.Length - 1];
                bool finOracion = ult == '.' || ult == '!' || ult == '?';
                bool pausa = ult == ',' || ult == ';' || ult == ':';
                bool corte = (finOracion && sb.Length >= minimo)
                          || (n == 0 && actual.Palabras.Count >= 3 && sb.Length >= minimo)
                          || (pausa && sb.Length >= Math.Max(minimo, limite - 40))
                          || sb.Length >= limite;
                if (corte)
                {
                    actual.Texto = sb.ToString();
                    trozos.Add(actual);
                    actual = new Trozo();
                    sb.Clear();
                }
            }
            if (actual.Palabras.Count > 0)
            {
                actual.Texto = sb.ToString();
                trozos.Add(actual);
            }
            return trozos;
        }

        // ---------------- Utilidades para alinear ----------------
        internal static string Comparable(string s)
        {
            var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
