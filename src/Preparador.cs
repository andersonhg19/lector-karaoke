using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    //
    // Criterio (lectores de pantalla en su nivel por defecto, RAE y lo que se oyó con las voces de Edge):
    //  - Los delimitadores (paréntesis, corchetes, rayas) son PAUSA, no palabras; comillas, llaves y marcas
    //    de markdown se callan. Edge no acepta SSML: la única pausa posible es la puntuación.
    //  - Se nombra solo lo que cambia el sentido: operadores, signo menos, %, @, #número, opciones --x.
    //  - Tablas: "Tabla de N columnas y M filas", encabezados una vez y cada fila como "valor: Encabezado, valor".
    //  - URLs, rutas, cifras, fechas, unidades y abreviaturas se leen como las diría una persona.
    // simbolos = todos conserva la lectura literal (nombra cada signo) para dictar o copiar.
    internal static class Preparador
    {
        private sealed class Ficha
        {
            public int Inicio, Largo;
            public string Texto;
            public int SaltosAntes;     // saltos de línea entre la ficha anterior y esta
            public string Habla = "";
            public bool Secreto;
            public int Linea;           // índice en la lista de líneas
        }

        private enum Tipo { Normal, Silencio, Fila, Cerco, Titulo, Lista }

        private sealed class Linea
        {
            public int Inicio, Fin;                 // [Inicio, Fin) sin el salto de línea
            public string Texto = "";
            public Tipo Tipo = Tipo.Normal;
            public bool Codigo;                     // dentro de ``` o con aspecto de código
            public bool Borde, Interior;            // borde de tabla (Interior: separa encabezado o filas)
            public int Tabla = -1, Fila = -1;       // fila lógica (las continuaciones comparten la de arriba)
            public bool Encabezado;
            public List<int> Barras = new List<int>();  // posiciones absolutas de los separadores de celda
            public bool BarraInicial;
            public string Verbo;                    // línea de herramienta de Claude Code ("● Update(...)")
            public string Herramienta;
            public string Lenguaje;                 // cerco de código con lenguaje
        }

        private sealed class Tabla
        {
            public int Columnas, Filas;
            public bool ConEncabezado;
            public List<string> Encabezados = new List<string>();
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
        private static readonly Regex UrlPartes = new Regex(@"^(?:(?:https?|ftp)://)?(?:www\.)?([^/\s?#:]+)(?::\d+)?(/[^\s?#]*)?(\?[^\s#]*)?(#\S*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DominioConRuta = new Regex(@"^[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.(?:com|org|net|io|dev|co|es|mx|ar|cl|pe|ai|app|gov|edu|info|me|ly|so|sh|gg|tv|us|uk|de|fr|it|br|cloud|site|xyz|tech)(?:/\S*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Correo = new Regex(@"^[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+$", RegexOptions.Compiled);
        private static readonly Regex Hex = new Regex(@"^[0-9a-fA-F]{32,}$", RegexOptions.Compiled);
        private static readonly Regex Base64Largo = new Regex(@"^[A-Za-z0-9+/=_\-]{32,}$", RegexOptions.Compiled);
        private static readonly Regex Opcion = new Regex(@"^--?[A-Za-z][\w\-]*(?:=\S+)?$", RegexOptions.Compiled);
        private static readonly Regex ArchivoLinea = new Regex(@"^(.+\.[A-Za-z][A-Za-z0-9]{0,5}):(\d+)(?::(\d+))?$", RegexOptions.Compiled);
        private const string Extensiones = "cs|csproj|sln|js|mjs|cjs|ts|tsx|jsx|py|pyw|java|kt|kts|go|rs|rb|php|c|h|cpp|hpp|cc|swift|m|scala|sql|md|mdx|json|jsonl|yml|yaml|xml|html|htm|css|scss|less|txt|log|ini|cfg|conf|toml|env|lock|sh|bash|zsh|ps1|psm1|bat|cmd|exe|dll|msi|zip|gz|tar|7z|rar|csv|tsv|xlsx|xls|docx|doc|pptx|ppt|pdf|png|jpg|jpeg|gif|svg|webp|ico|mp3|wav|mp4|ipynb|vue|svelte|dart|lua|r|gradle|properties|config|resx|xaml|onnx|wasm|map";
        private static readonly Regex Archivo = new Regex(@"^[\w\-]+(?:\.[\w\-]+)*\.(?:" + Extensiones + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Telefono = new Regex(@"^\+?\d{1,4}$", RegexOptions.Compiled);

        // Líneas: cercos de código, títulos, listas, herramientas de Claude Code y animaciones.
        private static readonly Regex Titulo = new Regex(@"^#{1,6}\s+\S", RegexOptions.Compiled);
        // Viñetas, numeración y las marcas de estado de Claude Code (cada una es un enunciado aparte).
        private static readonly Regex ItemLista = new Regex(@"^(?:[-*+•◦]\s+\S|\d{1,3}[.)]\s+\S|[a-z][.)]\s+\S|[✓✔✗✘⚠●⏺⎿☐☒]\s*\S)", RegexOptions.Compiled);
        private static readonly Regex HerramientaRx = new Regex(@"^[●⏺]\s*([A-Z][A-Za-z]+)\(", RegexOptions.Compiled);
        private static readonly Regex Animacion = new Regex(@"^[✻✶✳✢✽✺✹✷]\s", RegexOptions.Compiled);
        private static readonly Regex PistaInterfaz = new Regex(@"\(?\b(?:ctrl|esc|shift|alt)\+?\w* to (?:expand|interrupt|cycle|collapse|toggle)\)?|\(?esc to interrupt\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EnlaceMarkdown = new Regex(@"\[([^\]\n]{1,200})\]\(([^)\s]{1,500})\)", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> Verbos = new Dictionary<string, string>
        {
            { "Update", "Edita" }, { "Edit", "Edita" }, { "MultiEdit", "Edita" }, { "Write", "Crea" }, { "Read", "Lee" },
            { "Bash", "Ejecuta" }, { "PowerShell", "Ejecuta" }, { "Search", "Busca" }, { "Grep", "Busca" }, { "Glob", "Busca" },
            { "Fetch", "Consulta" }, { "WebFetch", "Consulta" }, { "WebSearch", "Busca en internet" }, { "Task", "Agente" },
            { "Agent", "Agente" }, { "TodoWrite", "Actualiza las tareas" }, { "NotebookEdit", "Edita" }, { "Skill", "Usa la habilidad" },
        };

        private static readonly Dictionary<string, string> Lenguajes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cs", "C sharp" }, { "csharp", "C sharp" }, { "c#", "C sharp" }, { "js", "JavaScript" }, { "javascript", "JavaScript" },
            { "ts", "TypeScript" }, { "typescript", "TypeScript" }, { "tsx", "TypeScript" }, { "jsx", "JavaScript" }, { "py", "Python" },
            { "python", "Python" }, { "sh", "shell" }, { "bash", "bash" }, { "shell", "shell" }, { "console", "consola" },
            { "powershell", "PowerShell" }, { "ps1", "PowerShell" }, { "pwsh", "PowerShell" }, { "json", "JSON" }, { "sql", "SQL" },
            { "html", "HTML" }, { "css", "CSS" }, { "java", "Java" }, { "go", "Go" }, { "rust", "Rust" }, { "yaml", "YAML" },
            { "yml", "YAML" }, { "xml", "XML" }, { "diff", "diff" }, { "text", "texto" }, { "md", "markdown" }, { "markdown", "markdown" },
        };

        // Separador decimal que entiende la voz elegida (es-ES dice "coma", es-MX "punto").
        [ThreadStatic] private static char separadorDecimal;
        [ThreadStatic] private static bool numerosLiterales;    // numeros = literal: no se reinterpretan los separadores
        [ThreadStatic] private static Configuracion actual;
        // simbolos = algunos: cada signo se nombra entre mini pausas (como JAWS en "Most"). La mini pausa es "·":
        // la voz de Edge no lo pronuncia y hace un silencio de unos 60 ms (una coma hace unos 200 ms a +50 %).
        [ThreadStatic] private static bool nombrar;
        [ThreadStatic] private static string mini;

        public static Preparado Preparar(string original, Configuracion cfg)
        {
            separadorDecimal = SeparadorDecimalDe(cfg.VozEdge);
            numerosLiterales = cfg.Numeros == "literal";
            actual = cfg;
            nombrar = cfg.Simbolos == "algunos";
            mini = "·";      // se convierte al final según pausa_simbolos (después de mover las pausas entre fichas)
            var res = new Preparado();
            if (string.IsNullOrEmpty(original)) return res;
            if (original.Length > cfg.MaxCaracteres)
            {
                original = original.Substring(0, cfg.MaxCaracteres);
                res.Truncado = true;
            }

            List<Tabla> tablas;
            var lineas = Lineas(original, out tablas);
            var fichas = Fichas(original, Ignorar(original, lineas), lineas);
            res.Secretos = MarcarSecretos(fichas);
            bool literal = cfg.Simbolos == "todos";
            for (int i = 0; i < fichas.Count; i++)
            {
                var f = fichas[i];
                if (f.Secreto) continue;
                var l = lineas[f.Linea];
                bool inicioLinea = i == 0 || fichas[i - 1].Linea != f.Linea;
                if (l.Tipo == Tipo.Cerco)
                {
                    string nombre;
                    f.Habla = l.Lenguaje != null && inicioLinea ? "Código " + (Lenguajes.TryGetValue(l.Lenguaje, out nombre) ? nombre : SepararIdentificador(l.Lenguaje)) + ":" : "";
                    continue;
                }
                f.Habla = literal ? NormalizarConSimbolos(f.Texto, cfg, inicioLinea) : NormalizarFicha(f.Texto, cfg, inicioLinea, l.Codigo);
            }
            if (!literal) Contexto(fichas, lineas);
            Estructura(fichas, lineas, tablas);
            MoverPausas(fichas);
            AgregarPausas(fichas, lineas);
            foreach (var f in fichas)
            {
                string h = Colapsar(f.Habla);
                if (h.IndexOf('·') >= 0 && cfg.PausaSimbolos != "corta")
                    h = cfg.PausaSimbolos == "media" ? Colapsar(h.Replace(" ·", ",").Replace("·", ",")) : Regex.Replace(h.Replace("·", ""), @"\s+", " ").Trim();
                f.Habla = h;
            }
            res.Trozos = Trocear(fichas);
            return res;
        }

        // ---------------- 0. Líneas y tablas ----------------
        private static List<Linea> Lineas(string s, out List<Tabla> tablas)
        {
            var ls = new List<Linea>();
            int i = 0;
            bool enCodigo = false;
            while (true)
            {
                int j = s.IndexOf('\n', i);
                if (j < 0) j = s.Length;
                int fin = j;
                if (fin > i && s[fin - 1] == '\r') fin--;
                var l = new Linea { Inicio = i, Fin = fin, Texto = s.Substring(i, fin - i) };
                string t = Ansi.Replace(l.Texto, "").Trim();
                if (t.StartsWith("```") || t.StartsWith("~~~"))
                {
                    l.Tipo = Tipo.Cerco;
                    if (!enCodigo)
                    {
                        string len = t.TrimStart('`', '~').Trim();
                        if (len.Length > 0 && len.Length <= 20 && len.IndexOf(' ') < 0) l.Lenguaje = len;
                    }
                    enCodigo = !enCodigo;
                }
                else
                {
                    l.Codigo = enCodigo || EsCodigo(t);
                    if (Animacion.IsMatch(t)) l.Tipo = Tipo.Silencio;
                    else if (EsBorde(t, out l.Interior)) { l.Tipo = Tipo.Silencio; l.Borde = true; }
                    else if (!enCodigo && EsFila(s, l)) l.Tipo = Tipo.Fila;
                    else if (Titulo.IsMatch(t)) l.Tipo = Tipo.Titulo;
                    else if (ItemLista.IsMatch(t)) l.Tipo = Tipo.Lista;
                    var mh = HerramientaRx.Match(t);
                    string verbo;
                    if (mh.Success && Verbos.TryGetValue(mh.Groups[1].Value, out verbo)) { l.Verbo = verbo; l.Herramienta = mh.Groups[1].Value; l.Codigo = false; }
                }
                ls.Add(l);
                if (j >= s.Length) break;
                i = j + 1;
            }
            tablas = AgruparTablas(ls, s);
            return ls;
        }

        // Código: operadores de programación o aspecto de comando. Un solo indicio fuerte basta.
        private static readonly Regex IndicioCodigo = new Regex(@"(?:==|!=|&&|\|\||=>|->|::|\+\+|\+=|-=|<=|>=)|[;{}]\s*$|^\s*(?:\$|PS [A-Z]:\\.*>|>>>)\s|^\s*(?:if|for|while|return|var|let|const|def|class|import|using|public|private|function)\b.*[({=;:]", RegexOptions.Compiled);

        private static bool EsCodigo(string t)
        {
            return t.Length > 0 && IndicioCodigo.IsMatch(t);
        }

        private static bool EsCaja(char c) { return c >= '\u2500' && c <= '\u257F'; }
        private static bool EsVertical(char c) { return c == '│' || c == '┃' || c == '║' || c == '┆' || c == '┊'; }

        // Línea de borde: solo caracteres de caja (o |-: de markdown, o +-= de tablas ASCII).
        private static bool EsBorde(string t, out bool interior)
        {
            interior = false;
            if (t.Length < 3) return false;
            bool caja = false, guion = false, barra = false, mas = false;
            foreach (char c in t)
            {
                if (EsCaja(c)) { caja = true; if ("├┼┤╞╪╡╟╫╢┣╋┫╠╬╣".IndexOf(c) >= 0) interior = true; continue; }
                if (c == '-' || c == '=' || c == ':') { guion = true; continue; }
                if (c == '|') { barra = true; continue; }
                if (c == '+') { mas = true; continue; }
                if (c == ' ') continue;
                return false;
            }
            if (barra && guion && !caja) interior = true;          // |---|---| de markdown
            if (mas && guion && !caja) interior = true;            // +----+----+ ASCII
            return caja || (barra && guion) || (mas && guion);
        }

        private static bool EsFila(string s, Linea l)
        {
            string t = l.Texto;
            int primero = 0;
            while (primero < t.Length && t[primero] == ' ') primero++;
            if (primero >= t.Length) return false;
            bool md = t[primero] == '|';
            for (int k = 0; k < t.Length; k++)
            {
                char c = t[k];
                if (EsVertical(c) || (md && c == '|' && !Escapada(t, k))) l.Barras.Add(l.Inicio + k);
            }
            if (l.Barras.Count == 0) return false;
            l.BarraInicial = l.Barras[0] == l.Inicio + primero;
            if (md) return l.Barras.Count >= 2;
            return l.BarraInicial || l.Barras.Count >= 2;
        }

        private static bool Escapada(string t, int k) { return k > 0 && t[k - 1] == '\\'; }

        private static int Celda(Linea l, int pos)
        {
            int n = 0;
            foreach (int b in l.Barras) if (b < pos) n++;
            return l.BarraInicial ? n - 1 : n;
        }

        private static int Celdas(Linea l)
        {
            int n = l.Barras.Count + 1;
            if (l.BarraInicial) n--;
            string resto = l.Texto.Substring(l.Barras[l.Barras.Count - 1] - l.Inicio + 1).Trim();
            if (resto.Length == 0) n--;
            return n;
        }

        private static List<Tabla> AgruparTablas(List<Linea> ls, string s)
        {
            var tablas = new List<Tabla>();
            int k = 0;
            while (k < ls.Count)
            {
                if (ls[k].Tipo != Tipo.Fila) { k++; continue; }
                int a = k;
                while (a > 0 && ls[a - 1].Borde) a--;
                int b = k;
                while (b < ls.Count && (ls[b].Tipo == Tipo.Fila || ls[b].Borde)) b++;
                k = b;
                var filas = new List<Linea>();
                for (int x = a; x < b; x++) if (ls[x].Tipo == Tipo.Fila) filas.Add(ls[x]);
                int cols = filas.Max(f => Celdas(f));
                if (cols < 2) continue;     // recuadro de una columna (bienvenida, avisos): se lee como texto
                var tb = new Tabla { Columnas = cols };
                int indice = tablas.Count;
                tablas.Add(tb);
                // Encabezado: la primera fila va seguida de un borde interior y hay más filas.
                int pos0 = ls.IndexOf(filas[0]);
                bool interior = false;
                for (int x = pos0 + 1; x < b && ls[x].Tipo != Tipo.Fila; x++) if (ls[x].Interior) interior = true;
                tb.ConEncabezado = filas.Count >= 2 && interior;
                int fila = -1;
                bool bordeEntre = true;
                for (int x = a; x < b; x++)
                {
                    var l = ls[x];
                    if (l.Borde) { bordeEntre = true; continue; }
                    // Celda partida en varias líneas (tabla de caja sin borde entre ellas): primera celda vacía.
                    bool continuacion = !bordeEntre && fila >= 0 && !EsMarkdown(l) && PrimeraCeldaVacia(l) && !(tb.ConEncabezado && fila == 0);
                    if (!continuacion) fila++;
                    l.Tabla = indice;
                    l.Fila = fila;
                    l.Encabezado = tb.ConEncabezado && fila == 0;
                    bordeEntre = false;
                }
                tb.Filas = fila + 1 - (tb.ConEncabezado ? 1 : 0);
            }
            return tablas;
        }

        private static bool EsMarkdown(Linea l) { return l.Texto.TrimStart().StartsWith("|"); }

        private static bool PrimeraCeldaVacia(Linea l)
        {
            int desde = l.BarraInicial ? l.Barras[0] + 1 : l.Inicio;
            int hasta = l.BarraInicial ? (l.Barras.Count > 1 ? l.Barras[1] : l.Fin) : l.Barras[0];
            return l.Texto.Substring(desde - l.Inicio, hasta - desde).Trim().Length == 0;
        }

        // Caracteres que no se leen ni se resaltan: ANSI, bordes, separadores de celda, sintaxis de enlaces markdown,
        // líneas de animación y pistas de la interfaz ("ctrl+o to expand").
        private static bool[] Ignorar(string s, List<Linea> lineas)
        {
            var ign = new bool[s.Length];
            foreach (Match m in Ansi.Matches(s))
                for (int k = m.Index; k < m.Index + m.Length; k++) ign[k] = true;
            foreach (var l in lineas)
            {
                if (l.Tipo == Tipo.Silencio)
                    for (int k = l.Inicio; k < l.Fin; k++) ign[k] = true;
                if (l.Tipo == Tipo.Fila)
                    foreach (int b in l.Barras) ign[b] = true;
                if (l.Tipo == Tipo.Cerco)
                    for (int k = l.Inicio; k < l.Fin; k++) if (s[k] == '`' || s[k] == '~') ign[k] = true;
            }
            foreach (Match m in EnlaceMarkdown.Matches(s))
            {
                ign[m.Index] = true;   // [
                int cierre = m.Groups[1].Index + m.Groups[1].Length;
                for (int k = cierre; k < m.Index + m.Length; k++) ign[k] = true;   // ](url)
            }
            foreach (Match m in PistaInterfaz.Matches(s))
                for (int k = m.Index; k < m.Index + m.Length; k++) ign[k] = true;
            return ign;
        }

        // ---------------- 1. Fichas con posición ----------------
        private static List<Ficha> Fichas(string s, bool[] ignorar, List<Linea> lineas)
        {
            var lista = new List<Ficha>();
            int i = 0, saltos = 0, li = 0;
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
                while (li + 1 < lineas.Count && lineas[li + 1].Inicio <= ini) li++;
                lista.Add(new Ficha { Inicio = ini, Largo = i - ini, Texto = s.Substring(ini, i - ini), SaltosAntes = saltos, Linea = li });
                saltos = 0;
            }
            return lista;
        }

        private static bool EsSeparador(char c)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;
            if (c >= '─' && c <= '▟') return true;   // cajas y bloques
            if (c >= '⠀' && c <= '⣿') return true;   // braille (spinners)
            if (c == '​' || c == '‌' || c == '‍' || c == '﻿' || c == '\uFE0F') return true;
            // Viñetas y marcas de la interfaz de Claude Code: ● ⏺ acción, ⎿ resultado, ▎ cita.
            if ("●⏺⎿▎○◦■□▪▫►▶◆◇".IndexOf(c) >= 0) return true;
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
                    f.Habla = "enlace " + Dominio(mUri.Groups[2].Value) + ", con credenciales ocultas";
                    n++;
                    continue;
                }

                var mCv = ClaveValor.Match(t);
                if (mCv.Success && Recortar(mCv.Groups[2].Value).Length > 0)
                {
                    f.Secreto = true;
                    f.Habla = SepararIdentificador(mCv.Groups[1].Value).Replace('_', ' ').Replace('-', ' ') + ", valor oculto";
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
            if (Base64Largo.IsMatch(t) && !t.Contains("--") && t.IndexOf('/') < 0)
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

        // ---------------- 3. Normalización de cada ficha ----------------
        private static string Recortar(string t)
        {
            int a = 0, b = t.Length;
            while (a < b && "([{<\"'«“‘`*_~>".IndexOf(t[a]) >= 0) a++;
            while (b > a && ")]}>\"'»”’`*_~.,;:!?…".IndexOf(t[b - 1]) >= 0) b--;
            return t.Substring(a, b - a);
        }

        private const string Decoracion = "*_~`#=-+•·";               // repetidos o en markdown no se leen

        private static string NormalizarFicha(string token, Configuracion cfg, bool inicioLinea, bool codigo)
        {
            if (token.Length == 0) return "";
            // Líneas de decoración (=====, -----, ****) y viñetas o encabezados markdown al inicio de línea: no se leen.
            if (token.Length >= 3 && token.Trim(token[0]).Length == 0 && Decoracion.IndexOf(token[0]) >= 0)
                return nombrar ? LineaDecorativa(token[0]) : "";
            // Viñeta de lista: JAWS dice "bullet" incluso sin puntuación; aquí "viñeta".
            if (inicioLinea && (token == "-" || token == "*" || token == "•" || token == "+" || token == "◦"))
                return nombrar ? "viñeta " + mini : "";
            if (inicioLinea && token == ">") return nombrar ? "cita " + mini : "";
            if (inicioLinea && token.Trim('#').Length == 0) return "";
            if (inicioLinea && (token == "$" || token == ">>>")) return "";        // símbolo del intérprete
            if (nombrar)
            {
                string glifo = Glifo(token, codigo);
                if (glifo != null) return glifo;
                bool soloSignos = true;
                foreach (char c in token) if (char.IsLetterOrDigit(c)) { soloSignos = false; break; }
                if (soloSignos)
                {
                    // "%," "):" : la puntuación final es pausa, no se nombra.
                    int b = token.Length;
                    while (b > 1 && ".,;:!?".IndexOf(token[b - 1]) >= 0) b--;
                    string fin = b < token.Length ? token.Substring(b, 1) : "";
                    return Nombrar(token.Substring(0, b), codigo) + fin;
                }
                return Frase(token, cfg, codigo);
            }
            string op = OperadorSuelto(token, codigo, cfg.Simbolos == "no");
            if (op != null) return op;
            return Frase(token, cfg, codigo);
        }

        // ----, ====, ****: separadores. Se dicen una vez, sin contar cada signo.
        private static string LineaDecorativa(char c)
        {
            switch (c)
            {
                case '-': return "línea de guiones.";
                case '=': return "línea de iguales.";
                case '*': return "línea de asteriscos.";
                case '_': return "línea de guiones bajos.";
                case '~': return "línea de virgulillas.";
                case '#': return "";
                default: return "línea.";
            }
        }

        // Marcas con significado propio (Claude Code y emojis de estado) y puntos suspensivos sueltos.
        private static string Glifo(string t, bool codigo)
        {
            switch (t)
            {
                case "...": case "…": return ".";
                case "→": case "⇒": case "➜": case "➔": return "flecha";
                case "✓": case "✔": case "✅": case "☑": return "listo";
                case "✗": case "✘": case "❌": case "✖": return "falla";
                case "⚠": case "⚠️": return "atención";
                case "ℹ": case "ℹ️": return "nota";
            }
            return null;
        }

        // ---------------- Nombrar signos (simbolos = algunos) ----------------
        // Cada signo se dice por su nombre entre mini pausas: "carpeta · abre paréntesis · datos".
        // Las letras y cifras se leen como palabras; el punto dentro de nombres (archivo.cs, github.com) se dice
        // "punto" sin pausa, como al dictar un dominio. Repetidos: "doble guion", "triple igual".
        private static string Nombrar(string t, bool codigo)
        {
            var partes = new List<string>();
            int i = 0, fin = t.Length;
            bool trasPunto = false;
            bool enSignos = false;       // signos seguidos (!=, :\, ->) forman un grupo con una sola pausa a cada lado
            while (i < fin)
            {
                char c = t[i];
                bool moneda = (c == '$' || c == '€' || c == '£') && i + 1 < fin && char.IsDigit(t[i + 1]);
                if (char.IsLetterOrDigit(c) || moneda || EsMarca(c))
                {
                    int ini = i;
                    if (moneda) i++;
                    while (i < fin && ParteDePalabra(t, i, fin, true)) i++;
                    if (i == ini) i++;
                    string w = t.Substring(ini, i - ini);
                    string h, pron;
                    bool finSegmento = i == fin || "/\\#?:&=".IndexOf(t[i]) >= 0;
                    if (trasPunto && finSegmento && EsExtension(w)) h = Extension(w);
                    else if (Pronunciacion.TryGetValue(w, out pron)) h = pron;
                    else h = Limpiar(SepararIdentificador(w));
                    if (enSignos) partes.Add(mini);
                    enSignos = false;
                    if (h.Length > 0) partes.Add(h);
                    trasPunto = false;
                    continue;
                }
                int j = i;
                while (j < fin && t[j] == c) j++;
                int n = j - i;
                if (n >= 3 && Decoracion.IndexOf(c) >= 0 && c != '-' && c != '=') { i = j; continue; }   // **** ~~~
                char antes = i > 0 ? t[i - 1] : '\0', despues = j < fin ? t[j] : '\0';
                if (c == '.' && n == 1 && char.IsLetterOrDigit(antes) && char.IsLetterOrDigit(despues))
                {
                    if (enSignos) partes.Add(mini);
                    enSignos = false;
                    partes.Add("punto");
                    trasPunto = true;
                    i = j;
                    continue;
                }
                string nombre = NombreDe(c, antes, despues, codigo);
                // Signo de un número (-5, +3): va pegado a la cifra, sin pausa.
                bool signo = n == 1 && (nombre == "menos" || (c == '+' && char.IsDigit(despues) && !char.IsLetterOrDigit(antes)));
                if (signo)
                {
                    if (enSignos) partes.Add(mini);
                    enSignos = false;
                    partes.Add(c == '+' ? "más" : "menos");
                    i = j;
                    continue;
                }
                if (nombre.Length > 0)
                {
                    if (n == 2) nombre = "doble " + nombre;
                    else if (n == 3) nombre = "triple " + nombre;
                    else if (n > 3) nombre = n + " " + Plural(nombre);
                    if (!enSignos) partes.Add(mini);
                    partes.Add(nombre);
                    enSignos = true;
                }
                i = j;
            }
            if (enSignos) partes.Add(mini);
            return Colapsar(string.Join(" ", partes).Trim());
        }

        private static bool EsMarca(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark;
        }

        private static string Plural(string nombre)
        {
            int esp = nombre.IndexOf(' ');
            string primera = esp < 0 ? nombre : nombre.Substring(0, esp), resto = esp < 0 ? "" : nombre.Substring(esp);
            if (primera.EndsWith("s")) return nombre;
            return primera + (".aeiouáéíóú".IndexOf(primera[primera.Length - 1]) > 0 ? "s" : "es") + resto;
        }

        private static bool EsExtension(string w)
        {
            return Regex.IsMatch(w, "^(?:" + Extensiones + ")$", RegexOptions.IgnoreCase);
        }

        private static string NombreDe(char c, char antes, char despues, bool codigo)
        {
            switch (c)
            {
                case '-': return char.IsDigit(despues) && !char.IsLetterOrDigit(antes) ? "menos" : "guion";
                case '–': return "guion medio";
                case '—': case '―': return "raya";
                case '−': return "menos";
                case '_': return "guion bajo";
                case '/': return "barra";
                case '\\': return "barra invertida";
                case '|': return "barra vertical";
                case '#': return "numeral";
                case '$': return char.IsDigit(despues) ? "dólares" : "signo de dólar";
                case '%': return "por ciento";
                case '&': return "ampersand";
                case '@': return "arroba";
                case '=': return "igual";
                case '+': return "más";
                case '*': return "asterisco";
                case '<': return "menor que";
                case '>': return "mayor que";
                case '!': return "exclamación";
                case '?': return "interrogación";
                case '¿': return "abre interrogación";
                case '¡': return "abre exclamación";
                case '~': return "virgulilla";
                case '^': return "circunflejo";
                case '.': return "punto";
                case ',': return "coma";
                case ':': return "dos puntos";
                case ';': return "punto y coma";
                case '"': return char.IsLetterOrDigit(antes) ? "cierra comillas" : "abre comillas";
                case '\'': case '‘': case '’': return "comilla simple";
                case '`': return "comilla invertida";
                case '°': return "grados";
                case '±': return "más menos";
                case '×': return "por";
                case '÷': return "entre";
                case '≠': return "distinto de";
                case '≤': return "menor o igual que";
                case '≥': return "mayor o igual que";
                case '→': case '⇒': case '➜': case '➔': return "flecha";
                case '✓': case '✔': case '✅': case '☑': return "listo";
                case '✗': case '✘': case '❌': case '✖': return "falla";
                case '⚠': return "atención";
                case 'ℹ': return "nota";
            }
            string nombre;
            if (Nombres.TryGetValue(c, out nombre)) return nombre;
            return "";
        }

        // Delimitadores dentro de una ficha cuando se nombran ("(versión" -> "abre paréntesis versión").
        // null: no es un delimitador que se nombre aquí (markdown, apóstrofo dentro de palabra...).
        private static string NombreDelimitador(string token, int i)
        {
            char c = token[i];
            char prev = i > 0 ? token[i - 1] : '\0';
            char next = i + 1 < token.Length ? token[i + 1] : '\0';
            switch (c)
            {
                case '(': return "abre paréntesis";
                case ')': return "cierra paréntesis";
                case '[': return "abre corchete";
                case ']': return "cierra corchete";
                case '{': return "abre llave";
                case '}': return "cierra llave";
                case '“': case '«': return "abre comillas";
                case '”': case '»': return "cierra comillas";
                case '"': return char.IsLetterOrDigit(prev) || ".,;:!?)".IndexOf(prev) >= 0 ? "cierra comillas" : "abre comillas";
                case '\'': case '‘': case '’': return char.IsLetter(prev) && char.IsLetter(next) ? null : "comilla simple";
                case '—': case '―': return "raya";
                case '–': return char.IsDigit(prev) && char.IsDigit(next) ? null : "guion medio";
            }
            return null;
        }

        private static void AgregarNombre(StringBuilder sb, string nombre)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            sb.Append(mini).Append(' ').Append(nombre).Append(' ').Append(mini);
        }

        // Un signo separado por espacios: operador entre operandos o pausa.
        private static string OperadorSuelto(string t, bool codigo, bool callar)
        {
            switch (t)
            {
                case "-": return codigo ? "menos" : ",";
                case "–": case "—": case "―": case "--": return ",";
                case "|": return codigo ? "tubería" : ",";
                case "/": case "\\": return ",";
                case "...": case "…": return ".";
                case "→": case "⇒": case "➜": case "➔": case "->": case "=>": return codigo ? "flecha" : "a";
                case "✓": case "✔": case "✅": case "☑": return "listo";
                case "✗": case "✘": case "❌": case "✖": return "falla";
                case "⚠": case "⚠️": return "atención";
                case "ℹ": case "ℹ️": return "nota";
            }
            if (callar)
            {
                bool solo = true;
                foreach (char c in t) if (char.IsLetterOrDigit(c)) { solo = false; break; }
                return solo ? "" : null;
            }
            switch (t)
            {
                case "=": return "igual";
                case "==": return "igual igual";
                case "===": return "igual igual igual";
                case "!=": case "!==": case "≠": case "<>": return "distinto de";
                case ">=": case "≥": return "mayor o igual que";
                case "<=": case "≤": return "menor o igual que";
                case ">": return "mayor que";
                case "<": return "menor que";
                case "&&": case "&": return "y";
                case "||": return "o";
                case "+": return "más";
                case "*": case "×": return "por";
                case "÷": return "entre";
                case "%": return "por ciento";
                case "±": return "más menos";
                case "@": return "arroba";
                case "+=": return "más igual";
                case "-=": return "menos igual";
                case "::": case "#": case "~": case "^": case "`": case "```": case "\"": case "'": return "";
            }
            return null;
        }

        // Delimitadores dentro de una ficha: paréntesis, corchetes y rayas son pausa; comillas y marcas se callan.
        private static string Frase(string token, Configuracion cfg, bool codigo)
        {
            var sb = new StringBuilder();
            var pieza = new StringBuilder();
            int n = token.Length;
            for (int i = 0; i < n; i++)
            {
                char c = token[i];
                char prev = i > 0 ? token[i - 1] : '\0';
                char next = i + 1 < n ? token[i + 1] : '\0';
                if (nombrar)
                {
                    string nd = NombreDelimitador(token, i);
                    if (c == '$' && next == '{') nd = "signo de dólar";
                    if (nd != null)
                    {
                        Vaciar(sb, pieza, cfg, codigo);
                        AgregarNombre(sb, nd);
                        continue;
                    }
                }
                bool pausa =c == '(' || c == ')' || c == '[' || c == ']' || ((c == '—' || c == '–') && !(char.IsDigit(prev) && char.IsDigit(next)));
                bool mudo = c == '{' || c == '}' || c == '"' || c == '“' || c == '”' || c == '«' || c == '»' || c == '`' || c == '´'
                         || ((c == '\'' || c == '‘' || c == '’') && !(char.IsLetter(prev) && char.IsLetter(next)))
                         || ((c == '*' || c == '_') && (i == 0 || i == n - 1 || token[i - 1] == c || token[i + 1] == c || !char.IsLetterOrDigit(prev) || !char.IsLetterOrDigit(next)) && !(c == '*' && char.IsDigit(prev) && char.IsDigit(next)))
                         || (c == '~' && (prev == '~' || next == '~'));      // ~~tachado~~; ~/ es la carpeta personal
                // "${VAR}" en código: el $ se calla junto con la llave.
                if (c == '$' && next == '{') mudo = true;
                if (c == '(' && next == ')') { Vaciar(sb, pieza, cfg, codigo); i++; continue; }   // foo() se lee "foo"
                if (pausa || mudo)
                {
                    Vaciar(sb, pieza, cfg, codigo);
                    if (pausa) AgregarPausa(sb, ',');
                    continue;
                }
                pieza.Append(c);
            }
            Vaciar(sb, pieza, cfg, codigo);
            return sb.ToString().Trim();
        }

        private static void Vaciar(StringBuilder sb, StringBuilder pieza, Configuracion cfg, bool codigo)
        {
            if (pieza.Length == 0) return;
            string h = Pieza(pieza.ToString(), cfg, codigo);
            pieza.Clear();
            if (h.Length == 0) return;
            if (".,;:!?".IndexOf(h[0]) >= 0 && h.Length == 1) { AgregarPausa(sb, h[0]); return; }
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            sb.Append(h);
        }

        private static void AgregarPausa(StringBuilder sb, char p)
        {
            // Sin texto todavía: la pausa queda al inicio y luego se pasa a la ficha anterior.
            int k = sb.Length;
            while (k > 0 && sb[k - 1] == ' ') k--;
            sb.Length = k;
            sb.Append(p);
        }

        // Una pieza sin delimitadores: abreviatura, URL, correo, cifra, ruta, archivo, opción o palabra con símbolos.
        private static string Pieza(string p, Configuracion cfg, bool codigo)
        {
            if (p == "..") return "a";       // `F1`..`F24`
            string abrev;
            string sinPausa = p.TrimEnd(',', ';', ':');
            if (Numeros.Abreviaturas.TryGetValue(sinPausa, out abrev))
                return abrev + p.Substring(sinPausa.Length);
            // Puntuación final: pausa ("..." y "…" = punto).
            int b = p.Length;
            while (b > 0 && ".,;:!?…".IndexOf(p[b - 1]) >= 0) b--;
            string fin = "";
            if (b < p.Length)
            {
                string cola = p.Substring(b);
                if (cola.Contains("…") || cola.Contains("..") || cola.Contains(".")) fin = cola.IndexOf('?') >= 0 ? "?" : cola.IndexOf('!') >= 0 ? "!" : ".";
                else fin = cola[cola.Length - 1].ToString();
            }
            int a = 0;
            while (a < b && (p[a] == '¿' || p[a] == '¡')) a++;
            string inicio = p.Substring(0, a);             // ¿ ¡ ayudan a la voz con la entonación
            string nucleo = p.Substring(a, b - a);
            if (nucleo.Length == 0) return fin;
            if (Numeros.Abreviaturas.TryGetValue(nucleo + ".", out abrev) && fin == ".") return abrev;   // "etc." al final
            return inicio + PiezaNucleo(nucleo, cfg, codigo) + fin;
        }

        // simbolos = algunos: no se pierde ningún signo. Se conservan solo las lecturas que dicen lo mismo con
        // palabras (fechas, horas, unidades, ordinales, rangos) y "línea N" en archivo:N.
        private static string PiezaNombrada(string t, Configuracion cfg, bool codigo)
        {
            if (Correo.IsMatch(t)) return Nombrar(t, codigo);
            if (Url.IsMatch(t) || (DominioConRuta.IsMatch(t) && (t.IndexOf('/') > 0 || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase))))
            {
                if (cfg.LeerUrls != "completa") return LeerUrl(t, cfg);
                string sin = Regex.Replace(t, @"^(?:(?:https?|ftp)://)?(?:www\.)?", "", RegexOptions.IgnoreCase);
                return "enlace " + Nombrar(sin, codigo);
            }
            if (Numeros.EsHora(t)) return Numeros.LeerHora(t);
            string pron;
            if (Pronunciacion.TryGetValue(t, out pron)) return pron;
            if (!numerosLiterales)
            {
                string num = Numeros.Ficha(t, NormalizarNumero);
                if (num != null) return num;
            }
            var mL = ArchivoLinea.Match(t);
            if (mL.Success)
            {
                string s = PiezaNombrada(mL.Groups[1].Value, cfg, codigo) + " " + mini + " línea " + mL.Groups[2].Value;
                if (mL.Groups[3].Success) s += " " + mini + " columna " + mL.Groups[3].Value;
                return s;
            }
            string termino;
            if (Terminos.TryGetValue(t, out termino) && t.IndexOf('/') < 0 && t != "&") return termino;
            return Nombrar(t, codigo);
        }

        private static string PiezaNucleo(string t, Configuracion cfg, bool codigo)
        {
            if (nombrar) return PiezaNombrada(t, cfg, codigo);
            if (Correo.IsMatch(t)) return LeerCorreo(t);
            if (Url.IsMatch(t) || (DominioConRuta.IsMatch(t) && (t.IndexOf('/') > 0 || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase))))
                return LeerUrl(t, cfg);
            if (Numeros.EsHora(t)) return Numeros.LeerHora(t);
            string pron;
            if (Pronunciacion.TryGetValue(t, out pron)) return pron;
            if (!numerosLiterales)
            {
                string num = Numeros.Ficha(t, NormalizarNumero);
                if (num != null) return num;
            }
            var mL = ArchivoLinea.Match(t);
            if (mL.Success && !Numeros.EsHora(t))
            {
                string s = PiezaNucleo(mL.Groups[1].Value, cfg, codigo) + ", línea " + mL.Groups[2].Value;
                if (mL.Groups[3].Success) s += ", columna " + mL.Groups[3].Value;
                return s;
            }
            if (EsRuta(t) && !BarraEntrePalabras(t)) return LeerRuta(t);
            if (Archivo.IsMatch(t)) return NombreArchivo(t);
            if (Opcion.IsMatch(t))
            {
                int eq = t.IndexOf('=');
                string nombre = eq > 0 ? t.Substring(0, eq) : t;
                string s = (nombre.StartsWith("--") ? "guion guion " : "guion ") + Identificador(nombre.TrimStart('-'));
                if (eq > 0) s += " igual " + PiezaNucleo(t.Substring(eq + 1), cfg, codigo);
                return s;
            }
            if (Hex.IsMatch(t) || HexCorto.IsMatch(t)) return "código";
            // F1..F24, a..z: intervalo.
            int dos = t.IndexOf("..", StringComparison.Ordinal);
            if (dos > 0 && dos + 2 < t.Length && char.IsLetterOrDigit(t[dos - 1]) && char.IsLetterOrDigit(t[dos + 2]) && t.IndexOf("..", dos + 2, StringComparison.Ordinal) < 0)
                return PiezaNucleo(t.Substring(0, dos), cfg, codigo) + " a " + PiezaNucleo(t.Substring(dos + 2), cfg, codigo);
            string termino;
            if (Terminos.TryGetValue(t, out termino)) return termino;
            // Una sola barra entre dos palabras: y/o, TCP/IP, HTTP/2, hombre/mujer (en minúsculas = alternativa).
            int barra = t.IndexOf('/');
            if (barra > 0 && barra < t.Length - 1 && t.IndexOf('/', barra + 1) < 0)
            {
                string a = t.Substring(0, barra), b = t.Substring(barra + 1);
                string ha = PiezaNucleo(a, cfg, codigo), hb = PiezaNucleo(b, cfg, codigo);
                bool alternativa = !codigo && a.All(char.IsLower) && b.All(char.IsLower) && !t.Equals("y/o", StringComparison.OrdinalIgnoreCase);
                return (ha + (alternativa ? " o " : " ") + hb).Trim();
            }
            return Simbolos(t, cfg, codigo);
        }

        // "C#/.NET", "HTTP/2", "y/o": una sola barra y a la derecha no hay un archivo (src/main.py sí es ruta).
        private static bool BarraEntrePalabras(string t)
        {
            int barra = t.IndexOf('/');
            if (barra <= 0 || barra == t.Length - 1 || t.IndexOf('/', barra + 1) >= 0 || t.IndexOf('\\') >= 0) return false;
            if (t.StartsWith("~") || t.StartsWith(".") || Unidad.IsMatch(t)) return false;
            return !Archivo.IsMatch(t.Substring(barra + 1));
        }

        // Palabras técnicas que las voces en español pronuncian mal ("github" sonaba "jitub").
        private static readonly Dictionary<string, string> Pronunciacion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "github", "git hub" }, { "gitlab", "git lab" }, { "npm", "N P M" }, { "pnpm", "P N P M" }, { "npx", "N P X" },
            { "nginx", "engine X" }, { "cli", "C L I" }, { "uuid", "U U I D" },
        };

        private static readonly Dictionary<string, string> Terminos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "C#", "C sharp" }, { "F#", "F sharp" }, { ".NET", "punto net" }, { "C++", "C más más" }, { "y/o", "y o" },
            { "&", "y" }, { "i18n", "internacionalización" }, { "a11y", "accesibilidad" }, { "k8s", "kubernetes" },
        };

        // Palabra con signos internos (a=b, x!=y, C++, 50%, user@host, foo.bar...).
        private static readonly string[] OperadoresInternos = { "!==", "===", "!=", "==", ">=", "<=", "=>", "->", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "::", "<<", ">>" };

        private static string Simbolos(string t, Configuracion cfg, bool codigo)
        {
            bool callar = cfg.Simbolos == "no";
            var partes = new List<string>();
            int i = 0, fin = t.Length;
            while (i < fin)
            {
                char c = t[i];
                bool moneda = (c == '$' || c == '€' || c == '£') && i + 1 < fin && char.IsDigit(t[i + 1]);
                if (char.IsLetterOrDigit(c) || moneda)
                {
                    int ini = i;
                    if (moneda) i++;
                    while (i < fin && ParteDePalabra(t, i, fin, false)) i++;
                    if (i == ini) i++;
                    string trozo = Limpiar(SepararIdentificador(t.Substring(ini, i - ini)));
                    if (trozo.Length > 0) partes.Add(trozo);
                    continue;
                }
                string op = null;
                foreach (var o in OperadoresInternos)
                    if (string.CompareOrdinal(t, i, o, 0, o.Length) == 0 && (o != "--" || i > 0)) { op = o; break; }
                if (op != null)
                {
                    string nombre = callar ? "" : OperadorSuelto(op, true, false) ?? "";
                    if (op == "++" || op == "--") nombre = callar ? "" : (op == "++" ? "más más" : "menos menos");
                    if (nombre.Length > 0) partes.Add(nombre);
                    i += op.Length;
                    continue;
                }
                int j = i;
                while (j < fin && t[j] == c) j++;
                if (j - i >= 3) { i = j; continue; }          // ---- ==== ****: decoración
                string n = callar ? NombreMudo(t, i, fin) : NombreSimbolo(t, i, fin, codigo);
                if (n.Length > 0)
                {
                    partes.Add(n);
                    if (j - i == 2 && c == '+') partes.Add(n);      // C++
                }
                i = j;
            }
            return string.Join(" ", partes).Trim();
        }

        // Signos con simbolos = no: solo los que cambian el sentido de un número.
        private static string NombreMudo(string t, int i, int fin)
        {
            char c = t[i];
            char prev = i > 0 ? t[i - 1] : '\0';
            char next = i + 1 < fin ? t[i + 1] : '\0';
            if (c == '-' && char.IsDigit(next) && !char.IsLetterOrDigit(prev)) return "menos";
            if ((c == '$' || c == '€' || c == '£') && char.IsDigit(next)) return c == '$' ? "dólares" : c == '€' ? "euros" : "libras";
            if (c == '.' && char.IsLetter(prev) && char.IsLetter(next)) return "punto";
            if (c == '@') return "arroba";
            if (c == '%') return "por ciento";
            return "";
        }

        private static string NombreSimbolo(string t, int i, int fin, bool codigo)
        {
            char c = t[i];
            char prev = i > 0 ? t[i - 1] : '\0';
            char next = i + 1 < fin ? t[i + 1] : '\0';
            switch (c)
            {
                case '/': return char.IsLower(prev) && char.IsLower(next) && !codigo ? "o" : "";
                case '\\': return "";
                case '|': return codigo ? "tubería" : "";
                case '#': return char.IsDigit(next) ? "número" : "";
                case '$': return char.IsDigit(next) ? (actual != null && actual.VozEdge.StartsWith("es-MX", StringComparison.OrdinalIgnoreCase) ? "pesos" : "dólares") : "";
                case '€': return char.IsDigit(next) ? "euros" : "";
                case '£': return char.IsDigit(next) ? "libras" : "";
                case '%': return "por ciento";
                case '&': return "y";
                case '@': return "arroba";
                case '=': return "igual";
                case '+': return char.IsDigit(next) || codigo || prev == '+' || next == '+' || char.IsLetterOrDigit(prev) ? "más" : "";
                case '-': return char.IsDigit(next) && !char.IsLetterOrDigit(prev) ? "menos" : "";
                case '*': return char.IsDigit(prev) && char.IsDigit(next) ? "por" : "";
                case '<': return codigo ? "menor que" : "";
                case '>': return codigo ? "mayor que" : "";
                case '!': return codigo && char.IsLetter(next) ? "no" : "";
                case '~': return char.IsDigit(next) ? "aproximadamente" : "";
                case '^': return char.IsDigit(prev) && char.IsDigit(next) ? "elevado a" : "";
                case '.': return char.IsLetter(prev) && char.IsLetter(next) ? "punto" : "";
                case '°': return char.IsDigit(prev) ? "grados" : "";
                case '✓': case '✔': case '✅': case '☑': return "listo";
                case '✗': case '✘': case '❌': case '✖': return "falla";
                case '⚠': return "atención";
                case 'ℹ': return "nota";
                case '→': case '⇒': case '➜': case '➔': return codigo ? "flecha" : "a";
            }
            return "";
        }

        // ---------------- URLs, correos, rutas y archivos ----------------
        private static string Dominio(string host)
        {
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
            var partes = host.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            string pron;
            return string.Join(" punto ", partes.Select(p => Pronunciacion.TryGetValue(p, out pron) ? pron : Identificador(p)));
        }

        private static string LeerUrl(string t, Configuracion cfg)
        {
            var m = UrlPartes.Match(t);
            if (!m.Success || cfg.LeerUrls == "no") return "enlace";
            string s = "enlace " + Dominio(m.Groups[1].Value);
            if (cfg.LeerUrls == "dominio") return s;
            string ruta = m.Groups[2].Success ? m.Groups[2].Value : "";
            var segs = ruta.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (cfg.LeerUrls == "completa")
            {
                foreach (var g in segs) s += " barra " + Segmento(g, false);
                if (m.Groups[3].Success && m.Groups[3].Value.Length > 1)
                    s += ", interrogación " + Limpiar(m.Groups[3].Value.Substring(1).Replace("=", " igual ").Replace("&", " y ").Replace("_", " ").Replace("-", " "));
                if (m.Groups[4].Success && m.Groups[4].Value.Length > 1) s += ", numeral " + Limpiar(SepararIdentificador(m.Groups[4].Value.Substring(1)));
                return s;
            }
            // "ruta": dominio y hasta 4 partes de la ruta, sin "barra"; consulta omitida; #L120 = línea.
            var dichos = new List<string>();
            for (int k = 0; k < segs.Length && k < 4; k++) dichos.Add(Segmento(segs[k], true));
            if (dichos.Count > 0) s += ": " + string.Join(", ", dichos);
            if (segs.Length > 4) s += ", y " + (segs.Length - 4) + (segs.Length - 4 == 1 ? " parte más" : " partes más");
            if (m.Groups[4].Success)
            {
                var ml = Regex.Match(m.Groups[4].Value, @"^#L(\d+)(?:-L(\d+))?$");
                if (ml.Success) s += ", línea " + ml.Groups[1].Value + (ml.Groups[2].Success ? " a " + ml.Groups[2].Value : "");
            }
            return s;
        }

        private static string Segmento(string g, bool resumir)
        {
            g = Uri.UnescapeDataString(g);
            if (resumir && (HexCorto.IsMatch(g) || Hex.IsMatch(g) || (g.Length >= 16 && g.All(char.IsLetterOrDigit) && g.Any(char.IsDigit) && g.Any(char.IsLetter))))
                return "un identificador";
            if (Archivo.IsMatch(g)) return NombreArchivo(g);
            return Identificador(g);
        }

        private static string LeerCorreo(string t)
        {
            int arroba = t.IndexOf('@');
            return Partes(t.Substring(0, arroba)) + " arroba " + Partes(t.Substring(arroba + 1));
        }

        private static string Partes(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '.') sb.Append(" punto ");
                else if (c == '_') sb.Append(" guion bajo ");
                else if (c == '-') sb.Append(" guion ");
                else if (c == '+') sb.Append(" más ");
                else sb.Append(c);
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static readonly Regex Unidad = new Regex(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

        // C:\Users\ander\x.docx -> "disco C, Users, ander, x punto docx". Las rutas largas se resumen por el medio.
        private static string LeerRuta(string t)
        {
            var partes = new List<string>();
            string s = t;
            if (Unidad.IsMatch(s)) { partes.Add("disco " + char.ToUpperInvariant(s[0])); s = s.Substring(3); }
            else if (s.StartsWith("~/") || s.StartsWith("~\\")) { partes.Add("carpeta personal"); s = s.Substring(2); }
            else if (s.StartsWith("\\\\")) { partes.Add("red"); s = s.Substring(2); }
            var segs = s.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Where(g => g != ".").ToList();
            var dichos = new List<string>();
            foreach (var g in segs)
            {
                if (g == "..") { dichos.Add("carpeta superior"); continue; }
                dichos.Add(g.IndexOf('.') > 0 || g.StartsWith(".") ? NombreArchivo(g) : Identificador(g));
            }
            if (dichos.Count > 6)
                dichos = dichos.Take(2).Concat(new[] { "…" }).Concat(dichos.Skip(dichos.Count - 3)).ToList();
            partes.AddRange(dichos);
            return string.Join(", ", partes).Replace(", …,", "…");
        }

        // archivo.test.ts -> "archivo punto test punto T S"; .gitignore -> "punto gitignore".
        private static string NombreArchivo(string g)
        {
            int p = g.LastIndexOf('.');
            if (p < 0) return Identificador(g);
            string nombre = g.Substring(0, p), ext = g.Substring(p + 1);
            string e = Extension(ext);
            if (nombre.Length == 0) return "punto " + e;
            string n = string.Join(" punto ", nombre.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Identificador(x)));
            return e.Length > 0 ? n + " punto " + e : n;
        }

        // Extensiones sin vocales (cs, md, txt, py) se deletrean; las pronunciables (json, docx, java) se dicen.
        private static string Extension(string ext)
        {
            if (ext.Length == 0) return "";
            bool vocal = ext.ToLowerInvariant().IndexOfAny("aeiou".ToCharArray()) >= 0;
            if (!vocal && ext.Length <= 4) return Numeros.Deletrear(ext.ToUpperInvariant());
            return ext.ToLowerInvariant();
        }

        private static string Identificador(string s)
        {
            return Limpiar(SepararIdentificador(s.Replace('_', ' ').Replace('-', ' ')));
        }

        // ---------------- 3b. Normalización literal (simbolos = todos) ----------------
        // Nombra cada signo: útil para dictar o copiar algo exacto.
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
        private const string Puntuacion = ".,;:?!¿¡…-–—";

        internal static string NormalizarConSimbolos(string token, Configuracion cfg, bool inicioLinea)
        {
            bool todos = cfg.Simbolos == "todos";
            if (token.Length == 0) return "";

            if (token.Length >= 3 && token.Trim(token[0]).Length == 0 && Decoracion.IndexOf(token[0]) >= 0) return "";
            if (inicioLinea && (token == "-" || token == "*" || token == "•" || token == "+")) return "";
            if (inicioLinea && token.Trim('#').Length == 0) return "";

            string sinEnvoltura = token.Trim('"', '\'', '(', ')', '[', ']', '<', '>', '`', '*', ',', '.', ';', ':');
            if (Url.IsMatch(sinEnvoltura) || Hex.IsMatch(sinEnvoltura) || HexCorto.IsMatch(sinEnvoltura)) return Normalizar(token, cfg);

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
                string nombre = NombreLiteral(t, i, finNucleo, todos);
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

        private static string NombreLiteral(string t, int i, int fin, bool todos)
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

        // Lectura breve de URLs y hashes para el modo literal.
        internal static string Normalizar(string token, Configuracion cfg)
        {
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
            string habla;
            if (Url.IsMatch(nucleo)) habla = LeerUrl(nucleo, cfg);
            else if (Hex.IsMatch(nucleo) || HexCorto.IsMatch(nucleo)) habla = "código";
            else habla = Limpiar(SepararIdentificador(nucleo));
            if (habla.Length == 0) return "";
            return habla + fin;
        }

        private static bool EsRuta(string s)
        {
            if (Unidad.IsMatch(s) || s.StartsWith("~/") || s.StartsWith("~\\") || s.StartsWith("\\\\")) return true;
            if (s.IndexOf('\\') >= 0 && s.Length > 2 && s.Any(char.IsLetter)) return true;
            if (s.IndexOf('/') < 0) return false;
            bool letras = false;
            foreach (char c in s) if (char.IsLetter(c)) { letras = true; break; }
            if (!letras) return false;                  // 10/20/2026 es una fecha
            int barras = 0;
            foreach (char c in s) if (c == '/') barras++;
            if (barras == 1 && !s.StartsWith("/") && !s.StartsWith("./") && s.IndexOf('.') < 0) return false;   // y/o, TCP/IP, km/h
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

        internal static bool EsMiles(string s) { return MilesPunto.IsMatch(s) || MilesComa.IsMatch(s); }

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
                else if (Regex.IsMatch(n, @"^\d+$")) resultado = n;
            }
            if (resultado == null) return s;            // fechas, horas, versiones, IP: tal cual
            // $1,250.50 -> "1250 dólares con 50", como lo diría una persona.
            if (moneda.Length > 0)
            {
                int d = resultado.IndexOf(dec);
                if (d > 0 && resultado.Length - d - 1 == 2)
                {
                    string centavos = resultado.Substring(d + 1);
                    resultado = resultado.Substring(0, d) + moneda + (centavos == "00" ? "" : " con " + centavos.TrimStart('0'));
                    moneda = "";
                }
            }
            if (signo == "-") signo = "menos ";
            else if (signo == "+") signo = "más ";
            return signo + resultado + porciento + moneda;
        }

        // ---------------- 4. Contexto entre fichas ----------------
        // Abreviaturas de dos fichas (p. ej.), romanos tras "siglo", unidades sueltas tras una cifra y teléfonos.
        private static void Contexto(List<Ficha> fichas, List<Linea> lineas)
        {
            for (int i = 0; i < fichas.Count; i++)
            {
                var f = fichas[i];
                if (f.Secreto || f.Habla.Length == 0) continue;
                Ficha ant = i > 0 && fichas[i - 1].Linea == f.Linea ? fichas[i - 1] : null;
                Ficha sig = i + 1 < fichas.Count && fichas[i + 1].Linea == f.Linea ? fichas[i + 1] : null;
                string t = f.Texto;
                string limpio = Recortar(t);
                string fin = Final(t);

                if (sig != null && t.Equals("p.", StringComparison.OrdinalIgnoreCase) && Recortar(sig.Texto).Equals("ej", StringComparison.OrdinalIgnoreCase))
                {
                    f.Habla = "por";
                    string fs = Final(sig.Texto);
                    sig.Habla = "ejemplo" + (fs == "." ? "" : fs);   // el punto es de la abreviatura
                    i++;
                    continue;
                }
                // "...etc. Luego": el punto de la abreviatura también cierra la oración si sigue una mayúscula.
                string abrev;
                if (t.EndsWith(".") && Numeros.Abreviaturas.TryGetValue(t, out abrev) && i + 1 < fichas.Count
                    && fichas[i + 1].Texto.Length > 0 && char.IsUpper(fichas[i + 1].Texto.TrimStart('¿', '¡', '(', '"')[0]) && !TerminaEnPausa(f.Habla))
                    f.Habla += ".";
                if (ant != null && Numeros.DisparadoresRomanos.Contains(Recortar(ant.Texto)))
                {
                    int r = Numeros.DeRomano(limpio);
                    if (r > 0) { f.Habla = r.ToString(CultureInfo.InvariantCulture) + fin; continue; }
                }
                if (ant != null && ant.Habla.Length > 0 && FinDeCifra(ant.Texto))
                {
                    string u = Numeros.UnidadSuelta(limpio, Recortar(ant.Texto));
                    if (u == null && limpio == "h" && Numeros.EsHora(Recortar(ant.Texto))) u = "horas";
                    if (u != null) { f.Habla = u + fin; continue; }
                }
            }
            Telefonos(fichas);
        }

        private static bool FinDeCifra(string t)
        {
            string r = Recortar(t);
            return r.Length > 0 && char.IsDigit(r[r.Length - 1]);
        }

        private static string Final(string t)
        {
            int b = t.Length;
            while (b > 0 && ")]}>\"'»”’`*_~".IndexOf(t[b - 1]) >= 0) b--;
            if (b > 0 && ".,;:!?".IndexOf(t[b - 1]) >= 0) return t[b - 1].ToString();
            return "";
        }

        // 300 555 1234 / +57 300 555 1234: la voz los junta mal ("30 0555"). Se separan los grupos con pausas.
        private static void Telefonos(List<Ficha> fichas)
        {
            int i = 0;
            while (i < fichas.Count)
            {
                int j = i, digitos = 0;
                while (j < fichas.Count)
                {
                    if (j > i && (fichas[j].Linea != fichas[i].Linea || Final(fichas[j - 1].Texto).Length > 0)) break;
                    string r = Recortar(fichas[j].Texto);
                    if (!Telefono.IsMatch(r) || (j > i && r.StartsWith("+"))) break;
                    digitos += r.TrimStart('+').Length;
                    j++;
                }
                if (j - i >= 3 && digitos >= 9 && digitos <= 15)
                {
                    bool prefijo = fichas[i].Texto.StartsWith("+");
                    for (int k = i; k < j - 1; k++)
                        if (!(k == i && prefijo)) fichas[k].Habla = fichas[k].Habla.TrimEnd(',') + ",";
                    i = j;
                }
                else i++;
            }
        }

        // ---------------- 5. Estructura: tablas, herramientas de Claude Code, títulos ----------------
        private static void Estructura(List<Ficha> fichas, List<Linea> lineas, List<Tabla> tablas)
        {
            // Herramientas: "● Update(src/x.cs)" -> "Edita src, x punto C S".
            foreach (var g in fichas.GroupBy(f => f.Linea))
            {
                var l = lineas[g.Key];
                if (l.Verbo == null) continue;
                var primera = g.FirstOrDefault(f => f.Habla.Length > 0);
                if (primera == null) continue;
                string nombre = SepararIdentificador(l.Herramienta);
                if (primera.Habla.StartsWith(nombre, StringComparison.Ordinal))
                {
                    string resto = primera.Habla.Substring(nombre.Length).TrimStart(',', ' ');
                    primera.Habla = resto.Length > 0 ? l.Verbo + " " + resto : l.Verbo;
                }
            }

            if (tablas.Count == 0) return;
            // Orden de lectura dentro de cada tabla: fila, celda, línea (una celda partida en dos líneas se lee seguida).
            var reordenadas = new List<Ficha>(fichas.Count);
            int i = 0;
            while (i < fichas.Count)
            {
                int t = lineas[fichas[i].Linea].Tabla;
                if (t < 0) { reordenadas.Add(fichas[i]); i++; continue; }
                int j = i;
                while (j < fichas.Count && lineas[fichas[j].Linea].Tabla == t) j++;
                var bloque = fichas.GetRange(i, j - i);
                var orden = bloque.OrderBy(f => lineas[f.Linea].Fila).ThenBy(f => Celda(lineas[f.Linea], f.Inicio)).ThenBy(f => f.Linea).ThenBy(f => f.Inicio).ToList();
                LinealizarTabla(orden, lineas, tablas[t]);
                reordenadas.AddRange(orden);
                i = j;
            }
            fichas.Clear();
            fichas.AddRange(reordenadas);
        }

        private static void LinealizarTabla(List<Ficha> orden, List<Linea> lineas, Tabla tb)
        {
            // Celdas: (fila, celda) -> fichas habladas.
            var celdas = orden.Where(f => f.Habla.Length > 0)
                              .GroupBy(f => new KeyValuePair<int, int>(lineas[f.Linea].Fila, Celda(lineas[f.Linea], f.Inicio)))
                              .ToList();
            if (celdas.Count == 0) return;
            if (tb.ConEncabezado)
                foreach (var c in celdas.Where(c => c.Key.Key == 0).OrderBy(c => c.Key.Value))
                {
                    while (tb.Encabezados.Count < c.Key.Value) tb.Encabezados.Add("");
                    tb.Encabezados.Add(string.Join(" ", c.Select(f => f.Habla)).TrimEnd(',', '.', ';', ':', ' '));
                }
            bool dosColumnas = tb.Columnas == 2;
            string intro = "Tabla de " + tb.Columnas + " columnas y " + tb.Filas + (tb.Filas == 1 ? " fila." : " filas.");
            var primera = celdas[0].First();
            primera.Habla = intro + " " + primera.Habla;

            foreach (var fila in celdas.GroupBy(c => c.Key.Key))
            {
                var lista = fila.OrderBy(c => c.Key.Value).ToList();
                bool encabezado = tb.ConEncabezado && fila.Key == 0;
                for (int k = 0; k < lista.Count; k++)
                {
                    var c = lista[k];
                    int col = c.Key.Value;
                    bool ultima = k == lista.Count - 1;
                    var ultimaFicha = c.Last();
                    char cierre;
                    if (encabezado || !tb.ConEncabezado) cierre = ultima ? '.' : ',';
                    else if (k == 0 && col == 0 && lista.Count > 1) cierre = ':';
                    else cierre = ultima ? '.' : ';';
                    Terminar(ultimaFicha, cierre);
                    // Encabezado delante de cada valor, salvo la primera columna (nombra la fila). En tablas de 2 columnas
                    // basta "clave: valor".
                    bool conNombre = !encabezado && tb.ConEncabezado && col > 0 && col < tb.Encabezados.Count
                                     && tb.Encabezados[col].Length > 0 && (!dosColumnas || k == 0);
                    if (conNombre)
                    {
                        var pf = c.First();
                        pf.Habla = tb.Encabezados[col] + ", " + pf.Habla;
                    }
                }
            }
        }

        private static void Terminar(Ficha f, char p)
        {
            string h = f.Habla.TrimEnd();
            while (h.Length > 0 && ".,;:".IndexOf(h[h.Length - 1]) >= 0) h = h.Substring(0, h.Length - 1);
            f.Habla = h + p;
        }

        // ---------------- 6. Pausas ----------------
        // Una pausa al inicio de una ficha ("(versión" -> ", versión") se pasa al final de la ficha hablada anterior.
        private static void MoverPausas(List<Ficha> fichas)
        {
            Ficha anterior = null;
            foreach (var f in fichas)
            {
                string h = f.Habla;
                int k = 0;
                while (k < h.Length && (".,;:!?·".IndexOf(h[k]) >= 0 || h[k] == ' ')) k++;
                if (k > 0)
                {
                    string pausa = h.Substring(0, k).Trim();
                    f.Habla = h.Substring(k);
                    if (anterior != null && pausa.Length > 0) anterior.Habla = Colapsar(anterior.Habla + " " + pausa);
                }
                if (f.Habla.Length > 0) anterior = f;
            }
        }

        private static void AgregarPausas(List<Ficha> fichas, List<Linea> lineas)
        {
            Ficha anterior = null;
            foreach (var f in fichas)
            {
                if (f.Habla.Length == 0) continue;
                if (anterior != null && f.Linea != anterior.Linea)
                {
                    var la = lineas[anterior.Linea];
                    var lf = lineas[f.Linea];
                    // Celda de tabla partida en varias líneas: es el mismo texto, sin pausa.
                    if (la.Tabla >= 0 && la.Tabla == lf.Tabla && la.Fila == lf.Fila) { anterior = f; continue; }
                    bool fuerte =f.SaltosAntes >= 2 || la.Tipo == Tipo.Titulo || la.Tipo == Tipo.Lista || lf.Tipo == Tipo.Lista
                               || lf.Tipo == Tipo.Titulo || la.Verbo != null || lf.Verbo != null || la.Codigo || lf.Codigo;
                    if (!TerminaEnPausa(anterior.Habla)) anterior.Habla += fuerte ? "." : ",";
                    else if (fuerte && Fuerza(anterior.Habla[anterior.Habla.Length - 1]) < 3)
                        anterior.Habla = anterior.Habla.Substring(0, anterior.Habla.Length - 1) + ".";
                }
                anterior = f;
            }
        }

        private static bool TerminaEnPausa(string s)
        {
            return s.Length > 0 && ".,;:!?".IndexOf(s[s.Length - 1]) >= 0;
        }

        // ",." -> "."  ";," -> ";"  " ," -> ","  (se queda la pausa más fuerte).
        private static string Colapsar(string s)
        {
            if (s.Length == 0) return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (".,;:!?·".IndexOf(c) < 0) { sb.Append(c); i++; continue; }
                // 1,5  14:30  3.14: el signo entre cifras o letras es parte del número, no una pausa.
                if ((c == ',' || c == '.' || c == ':') && i > 0 && i + 1 < s.Length && char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i + 1]))
                { sb.Append(c); i++; continue; }
                int j = i;
                char fuerte = c;
                while (j < s.Length && (".,;:!?·".IndexOf(s[j]) >= 0 || s[j] == ' '))
                {
                    if (s[j] != ' ' && Fuerza(s[j]) > Fuerza(fuerte)) fuerte = s[j];
                    j++;
                }
                while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                // La mini pausa "·" va separada por espacios y se conserva al inicio (luego pasa a la ficha anterior).
                if (fuerte == '·') sb.Append(sb.Length > 0 ? " ·" : "·");
                else if (sb.Length > 0) sb.Append(fuerte);
                if (j < s.Length) sb.Append(' ');
                i = j;
            }
            return sb.ToString().Trim();
        }

        private static int Fuerza(char c) { return c == '.' || c == '?' || c == '!' ? 4 : c == ';' ? 3 : c == ':' ? 2 : c == '·' ? 0 : 1; }

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

        // ---------------- 7. Trozos ----------------
        // Cortes solo en pausas naturales: cortar a mitad de frase deja un silencio entre audios y la voz retoma
        // con entonación de inicio de oración. Se prefiere el fin de oración; una coma solo corta si el trozo ya
        // es largo. Un texto corto va entero.
        private const int TextoCorto = 160;

        private static List<Trozo> Trocear(List<Ficha> fichas)
        {
            int total = 0;
            foreach (var f in fichas) if (f.Habla.Length > 0) total += f.Habla.Length + 1;
            bool unSoloTrozo = total <= TextoCorto;

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
                if (unSoloTrozo) continue;

                // Primero corto (para empezar rápido) y luego más largos.
                int n = trozos.Count;
                int limite = n == 0 ? 90 : n == 1 ? 160 : 280;
                int minimo = n == 0 ? 12 : 60;
                char ult = f.Habla[f.Habla.Length - 1];
                // "4." de una lista o título numerado no cierra la oración: no se corta entre "4." y su texto.
                bool numeracion = f.Habla.Length <= 4 && f.Habla.TrimEnd('.', ')').All(char.IsDigit);
                bool finOracion = (ult == '.' || ult == '!' || ult == '?') && !numeracion;
                bool pausa = ult == ',' || ult == ';' || ult == ':';
                bool corte = (finOracion && sb.Length >= minimo)
                          || (pausa && sb.Length >= (n == 0 ? 40 : limite * 0.6))
                          || sb.Length >= limite + 60;            // sin ninguna pausa en mucho texto: corte de seguridad
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
