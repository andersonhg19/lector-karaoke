using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectorKaraoke;

// Pruebas unitarias sin interfaz: preparación del texto, secretos, trozos, alineación y configuración.
internal static class Pruebas
{
    private static int ok, fallos;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = new Configuracion { Simbolos = "no", LeerUrls = "ruta" };   // las pruebas base van sin nombrar símbolos

        // ---------- Posiciones: cada palabra apunta a su texto original ----------
        {
            string s = "Hola  mundo,\r\nesto es\tuna prueba.";
            var p = Preparador.Preparar(s, cfg);
            var ps = p.Trozos.SelectMany(t => t.Palabras).ToList();
            Igual("posiciones: cantidad", 6, ps.Count);
            foreach (var w in ps)
            {
                string orig = s.Substring(w.OrigInicio, w.OrigLargo);
                Cierto("posiciones: '" + orig + "' coincide con habla '" + w.Habla + "'",
                    Preparador.Comparable(orig) == Preparador.Comparable(w.Habla));
            }
            foreach (var t in p.Trozos)
                foreach (var w in t.Palabras)
                    Igual("habla dentro del trozo: " + w.Habla, w.Habla, t.Texto.Substring(w.HablaInicio, w.Habla.Length));
        }

        // ---------- ANSI, cajas y spinners ----------
        {
            string s = "\x1b[32m✔\x1b[0m Build ok │ ⠋ esperando ════════ fin";
            var hab = Habla(s, cfg);
            Cierto("ansi: sin escapes ni cajas -> '" + hab + "'", !hab.Contains("[32m") && !hab.Contains("═") && !hab.Contains("│") && !hab.Contains("⠋"));
            Cierto("ansi: conserva palabras", hab.Contains("Build ok") && hab.Contains("esperando") && hab.Contains("fin"));
            var w = Preparador.Preparar(s, cfg).Trozos[0].Palabras.First(x => x.Habla.StartsWith("Build"));
            Igual("ansi: posición de Build", "Build", s.Substring(w.OrigInicio, w.OrigLargo));
        }

        // ---------- Identificadores, rutas, URLs ----------
        Contiene("camelCase", Habla("getUserById", cfg), "get User By Id");
        Contiene("snake_case", Habla("MAX_RETRY_COUNT", cfg), "MAX RETRY COUNT");
        Contiene("kebab", Habla("lector-karaoke", cfg), "lector karaoke");
        Contiene("ruta windows", Habla(@"C:\Users\ander\Documents\archivo.java", cfg), "disco C, Users, ander, Documents, archivo punto java");
        Contiene("ruta unix", Habla("src/main/java/Servicio.java", cfg), "src, main, java, Servicio punto java");
        Contiene("url con ruta", Habla("mira https://www.github.com/andersonhg19/repo?x=1 ahora", cfg), "enlace git hub punto com: andersonhg 19, repo ahora");
        Cierto("url sin ruta", !Habla("https://www.github.com/andersonhg19/repo", cfg).Contains("andersonhg19"));
        Contiene("fecha ISO en palabras", Habla("el 2026-10-20 a las 12:30", cfg), "el 20 de octubre de 2026 a las 12 y 30");
        Contiene("decimal intacto", Habla("vale 3.5 y 50%", cfg), "3.5");
        Contiene("porcentaje", Habla("vale 50%", cfg), "50%");
        Contiene("hash como código", Habla("commit a2a2b501a listo", cfg), "código");
        Contiene("markdown", Habla("**importante** y `codigo`", cfg), "importante y codigo");
        Cierto("emoji fuera", !Habla("listo 🚀 ya", cfg).Contains("🚀"));
        Igual("solo símbolos", 0, Preparador.Preparar("==== ---- **** ////", cfg).Trozos.Count);
        Igual("vacío", 0, Preparador.Preparar("   \r\n\t ", cfg).Trozos.Count);
        Igual("nulo", 0, Preparador.Preparar(null, cfg).Trozos.Count);

        // ---------- Secretos: nunca se pronuncian ni salen a internet ----------
        var secretos = new[]
        {
            "password=Sup3rS3creta!", "DB_PASSWORD: hunter2xyz", "token: abc123def456", "api_key=" + "AK" + "IAIOSFODNN7EXAMPLE",
            "Authorization: Bearer " + "ey" + "JhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
            "gh" + "p_" + "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "sk" + "-ant-api03-" + "abcdefghijklmnopqrstuvwx",
            "mongodb" + "://admin:" + "ClaveMuyLarga99" + "@cluster0.mongodb.net/db", "contraseña: Pa55word",
            "AI" + "zaSyD-abcdefghijklmnopqrstuvwxyz012345", "xo" + "xb-123456789012-abcdefghijkl",
            "Q2xhdmVTZWNyZXRhQmFzZTY0TXV5TGFyZ2FFeHRyYTEyMw",
        };
        foreach (var sec in secretos)
        {
            var p = Preparador.Preparar("antes " + sec + " despues", cfg);
            string h = string.Join(" ", p.Trozos.Select(t => t.Texto));
            Cierto("secreto oculto: " + sec.Substring(0, Math.Min(18, sec.Length)) + "… -> '" + h + "'", p.Secretos > 0 && h.Contains("ocult") && !Filtra(h, sec));
        }
        {
            string pem = "clave:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA1234\nabcdEFGH5678\n-----END RSA PRIVATE KEY-----\nfin";
            string h = Habla(pem, cfg);
            Cierto("PEM oculto -> '" + h + "'", h.Contains("bloque de clave oculto") && !h.Contains("MIIEow") && h.Contains("fin"));
        }
        // Falsos positivos que NO deben ocultarse.
        foreach (var normal in new[] { "Tokens used: 1500", "the tokenizer works", "Authentication failed for user", "password incorrecto", "Clave primaria de la tabla" })
        {
            var p = Preparador.Preparar(normal, cfg);
            Igual("no es secreto: " + normal, 0, p.Secretos);
        }

        // ---------- Trozos ----------
        {
            string largo = string.Join(" ", Enumerable.Repeat("Esta es una oración de prueba bastante normal.", 40));
            var p = Preparador.Preparar(largo, cfg);
            Cierto("trozos: varios", p.Trozos.Count > 5);
Cierto("trozos: primero corto (" + p.Trozos[0].Texto.Length + ")", p.Trozos[0].Texto.Length <= 150);
            Cierto("trozos: cada corte cae en una pausa natural", p.Trozos.Take(p.Trozos.Count - 1).All(t => ".,;:!?".IndexOf(t.Texto[t.Texto.Length - 1]) >= 0));
            Igual("texto corto (texto de ayuda) va en un solo trozo", 1, Preparador.Preparar("Control de código fuente (Ctrl+Shift+G)", cfg).Trozos.Count);
            var largoSinPausa = Preparador.Preparar(string.Join(" ", Enumerable.Repeat("palabra", 120)), cfg);
            Cierto("texto largo sin pausas: corte de seguridad", largoSinPausa.Trozos.Count > 1 && largoSinPausa.Trozos.All(t => t.Texto.Length <= 360));
            Cierto("trozos: ninguno > 320", p.Trozos.All(t => t.Texto.Length <= 320));
            int total = p.Trozos.Sum(t => t.Palabras.Count);
            Igual("trozos: no se pierden palabras", 40 * 8, total);
            string sinPunt = string.Join(" ", Enumerable.Repeat("palabra", 300));
            Cierto("trozos: texto sin puntuación también se corta", Preparador.Preparar(sinPunt, cfg).Trozos.All(t => t.Texto.Length <= 360));
        }
        {
            var c2 = new Configuracion { MaxCaracteres = 100 };
            var p = Preparador.Preparar(new string('a', 50) + " " + new string('b', 200), c2);
            Cierto("máximo de caracteres", p.Truncado);
        }
        {
            var h = Habla("linea uno\nlinea dos\n\nparrafo nuevo", cfg);
            Contiene("pausas por salto de línea", h, "linea uno, linea dos. parrafo nuevo");
        }

        // ---------- Alineación de marcas ----------
        {
            var p = Preparador.Preparar("Hola Anderson, getUserById devuelve 2026 resultados.", cfg);
            var t = Unir(p);
            var marcas = new List<Marca>
            {
                new Marca("Hola", 100, 300), new Marca("Anderson", 420, 450), new Marca("get", 1000, 150), new Marca("User", 1160, 200),
                new Marca("By", 1370, 100), new Marca("Id", 1480, 150), new Marca("devuelve", 1700, 400), new Marca("2026", 2150, 700),
                new Marca("resultados", 2900, 600),
            };
            Tiempos.Alinear(t, marcas, 3600);
            Igual("alinear: Hola", 100, (int)t.Palabras[0].InicioMs);
            Igual("alinear: identificador al inicio de 'get'", 1000, (int)t.Palabras[2].InicioMs);
            Igual("alinear: resultados", 2900, (int)t.Palabras[5].InicioMs);
            Cierto("alinear: monótono", Monotono(t));
            Igual("palabra en 1200 ms", 2, Tiempos.PalabraEn(t, 1200));
            Igual("palabra en 0 ms", -1, Tiempos.PalabraEn(t, 0));
        }
        {
            var p = Preparador.Preparar("uno dos tres cuatro cinco", cfg);
            var t = Unir(p);
            Tiempos.Alinear(t, new List<Marca> { new Marca("uno", 0, 200), new Marca("cinco", 2000, 300) }, 2400);
            Cierto("interpolar huecos", t.Palabras.All(w => w.InicioMs >= 0) && Monotono(t) && t.Palabras[2].InicioMs > 200 && t.Palabras[2].InicioMs < 2000);
            Tiempos.Alinear(t, new List<Marca>(), 2000);
            Cierto("sin marcas: reparte todo", t.Palabras.All(w => w.InicioMs >= 0) && Monotono(t));
            Tiempos.Alinear(t, new List<Marca> { new Marca("xyz", 0, 10), new Marca("qqq", 50, 10) }, 1000);
            Cierto("marcas que no coinciden: no rompe", Monotono(t));
        }
        {
            var p = Preparador.Preparar("Primera parte, segunda parte. Fin", cfg);
            var t = Unir(p);
            Tiempos.Estimar(t, 100, 2100);
            Cierto("estimar: dentro del rango", t.Palabras.First().InicioMs >= 100 && t.Palabras.Last().FinMs <= 2101 && Monotono(t));
        }

        // ---------- Símbolos por su nombre ----------
        {
            Igual("símbolos: predeterminado", "algunos", new Configuracion().Simbolos);
            var sim = new Configuracion { Simbolos = "natural", LeerUrls = "ruta" };
            Contiene("comillas: se callan", Habla("dijo \"hola mundo\" ayer", sim), "dijo hola mundo ayer");
            Contiene("paréntesis: pausa", Habla("valor (opcional) final", sim), "valor, opcional, final");
            Contiene("llaves mudas y corchetes con pausa", Habla("{a} [b]", sim), "a, b");
            Contiene("barra entre palabras: alternativa", Habla("si/no", sim), "si o no");
            Contiene("ruta sin decir barra", Habla(@"C:\Users\x", sim), "disco C, Users, x");
            Contiene("número con #", Habla("issue #123", sim), "issue número 123");
            Contiene("arroba", Habla("correo@dominio", sim), "correo arroba dominio");
            Contiene("igual y doble igual", Habla("a = b == c", sim), "a igual b igual igual c");
            Contiene("&& es y", Habla("x && z", sim), "x y z");
            Contiene("asterisco entre números", Habla("2 * 3", sim), "2 por 3");
            Contiene("dólares con número", Habla("cuesta $100", sim), "dólares");
            Contiene("por ciento", Habla("50% listo", sim), "50%");
            Igual("diéresis y circunflejo sueltos se callan", "", Habla("¨ ^", sim));
            Contiene("archivo con punto", Habla("Config.java", sim), "Config punto java");
            Contiene("interrogación en medio se calla", Habla("url?x=1", sim), "url x igual 1");
            Contiene("pregunta normal sin nombrar", Habla("¿vienes hoy?", sim), "vienes hoy?");
            Cierto("pregunta normal: no dice 'interrogación'", !Habla("¿vienes hoy?", sim).Contains("interrogación"));
            Contiene("markdown negrita sin leer asteriscos", Habla("**importante** ya", sim), "importante ya");
            Cierto("markdown: no dice asterisco", !Habla("**importante** ya", sim).Contains("asterisco"));
            Igual("línea decorativa", 0, Preparador.Preparar("==========", sim).Trozos.Count);
            Cierto("encabezado markdown sin numeral", !Habla("## Resumen\nTexto", sim).Contains("numeral"));
            Cierto("viñeta sin leer", !Habla("- uno\n- dos", sim).Contains("guion"));
            Contiene("snake_case sin nombrar", Habla("MAX_RETRY", sim), "MAX RETRY");
            Contiene("fecha en palabras", Habla("el 2026-10-20", sim), "el 20 de octubre de 2026");
            Contiene("url resumida", Habla("https://www.github.com/a/b", sim), "enlace git hub punto com: a, b");
            Cierto("secreto sigue oculto", Preparador.Preparar("password=Hola123xyz", sim).Secretos == 1 && !Habla("password=Hola123xyz", sim).Contains("Hola123"));
            // Posiciones intactas con símbolos.
            string s0 = "dijo \"hola\" (ya)";
            foreach (var w in Preparador.Preparar(s0, sim).Trozos.SelectMany(t => t.Palabras))
                Cierto("posición con símbolos: '" + s0.Substring(w.OrigInicio, w.OrigLargo) + "'", w.OrigLargo > 0 && w.OrigInicio + w.OrigLargo <= s0.Length);

            var todos = new Configuracion { Simbolos = "todos" };
            Cierto("todos: punto y coma", Habla("hola, mundo.", todos).Replace(",", "") == "hola coma mundo punto");
            Contiene("todos: interrogación", Habla("¿sí?", todos), "abre interrogación sí interrogación");
            Contiene("todos: guion bajo", Habla("MAX_RETRY", todos), "MAX guion bajo RETRY");
            Contiene("todos: asteriscos de markdown", Habla("**x**", todos), "doble asterisco x doble asterisco");
        }

        // ---------- Lectura natural (NVDA en "some", RAE, lo que se oyó con las voces de Edge) ----------
        {
            var es = new Configuracion { VozEdge = "es-ES-AlvaroNeural", Simbolos = "natural", LeerUrls = "ruta" };
            // Paréntesis, rayas y comillas: pausa o silencio, nunca "abre paréntesis".
            Igual("paréntesis en prosa", "El modelo, versión 2, funciona mejor que el anterior, ver tabla.", Habla("El modelo (versión 2) funciona mejor que el anterior (ver tabla).", es));
            Contiene("raya como inciso", Habla("fue clave — sobre todo en IA — y siguió", es), "fue clave, sobre todo en IA, y siguió");
            Igual("comillas mudas", "Hola, dijo ella. Bien respondió él. Perfecto.", Habla("\"Hola\", dijo ella. 'Bien' respondió él. «Perfecto».", es));
            Cierto("nunca 'abre paréntesis' por defecto", !Habla("f(x) [a] {b} \"c\"", es).Contains("abre"));
            // Rangos, signos y opciones.
            Contiene("rango con guion", Habla("páginas 10-25", es), "páginas 10 a 25");
            Contiene("rango con raya corta", Habla("periodo 2020–2024", es), "periodo 2020 a 2024");
            Contiene("negativo", Habla("bajó a -5 °C", es), "menos 5 grados Celsius");
            Contiene("opción larga", Habla("git --help", es), "git guion guion help");
            Contiene("opción corta", Habla("npm -v", es), "N P M guion v");
            Contiene("opción con valor", Habla("--voz=Jorge", es), "guion guion voz igual Jorge");
            Contiene("guion suelto en prosa = pausa", Habla("Notepad - Orca", es), "Notepad, Orca");
            // Cifras, fechas, unidades, ordinales.
            Contiene("fecha d/m/a", Habla("el 20/10/2026", es), "el 20 de octubre de 2026");
            Contiene("versión", Habla("v2.3.1", es), "versión 2 punto 3 punto 1");
            Contiene("unidad pegada", Habla("1.5GB en 100ms", es), "1,5 gigabytes en 100 milisegundos");
            Contiene("unidad suelta", Habla("pesa 1 kg y 16 GB", es), "1 kilogramo y 16 gigabytes");
            Contiene("multiplicador", Habla("3x más rápido", es), "3 veces más rápido");
            Contiene("dimensión", Habla("1920x1080", es), "1920 por 1080");
            Contiene("más menos", Habla("±2 dB", es), "más menos 2 decibelios");
            Contiene("progreso", Habla("Tests (152/152)", es), "Tests, 152 de 152");
            Contiene("fracción común: a la voz", Habla("1/2 taza", es), "1/2 taza");
            Contiene("ordinales", Habla("1º y 2ª edición, 3er lugar", es), "primero y segunda edición, tercer lugar");
            Contiene("romanos tras siglo", Habla("Siglo XXI, capítulo IV", es), "Siglo 21, capítulo 4");
            Cierto("romano suelto no se toca", Habla("Yo vi a Juan", es).Contains("vi"));
            Contiene("teléfono", Habla("Llama al 300 555 1234 hoy", es), "300, 555, 1234 hoy");
            Contiene("dinero con centavos", Habla("cuesta $1,250.50", es), "1250 dólares con 50");
            Contiene("miles no son versión", Habla("1.000.000 de usuarios", es), "1000000 de usuarios");
            Contiene("abreviaturas", Habla("aprox. 5 min. Ver pág. 3 y núm. 7", es), "aproximadamente 5 minutos. Ver página 3 y número 7");
            Contiene("p. ej.", Habla("por ejemplo: p. ej., una tabla", es), "por ejemplo, una tabla");
            // URLs, correos, rutas, archivos.
            Contiene("url con línea", Habla("https://github.com/a/repo/blob/main/x.cs#L120", es), "enlace git hub punto com: a, repo, blob, main, y 1 parte más, línea 120");
            Contiene("url sin esquema", Habla("github.com/andersonhg19/lector-karaoke", es), "enlace git hub punto com: andersonhg 19, lector karaoke");
            Contiene("url solo dominio", Habla("https://x.com/a/b", new Configuracion { LeerUrls = "dominio" }), "enlace x punto com");
            Contiene("correo", Habla("soporte.tecnico@empresa.com.co", es), "soporte punto tecnico arroba empresa punto com punto co");
            Contiene("carpeta personal", Habla("~/proyectos/app", es), "carpeta personal, proyectos, app");
            Contiene("archivo y línea", Habla("Lector.cs:351", es), "Lector punto C S, línea 351");
            Contiene("extensión pronunciable", Habla("config.json", es), "config punto json");
            Contiene("una barra entre siglas", Habla("HTTP/2 y TCP/IP", es), "HTTP 2 y TCP IP");
            Contiene("C# y .NET", Habla("C#/.NET", es), "C sharp punto net");
            Contiene("enlace markdown: solo el texto", Habla("lee [la guía](https://x.com/guia) hoy", es), "lee la guía hoy");
            // Código: operadores con nombre, delimitadores en silencio.
            Contiene("operadores", Habla("if (x != null && y >= 0) return a => a * 2;", es), "x distinto de null y y mayor o igual que 0, return a flecha a por 2;");
            Contiene("llamada sin argumentos", Habla("obtener() listo", es), "obtener listo");
            // Markdown y salida de Claude Code.
            Contiene("título y lista", Habla("## Resumen\n- uno\n- dos", es), "Resumen. uno. dos");
            Contiene("negrita y código en línea", Habla("- **Captura**: usa `TextPattern` ya", es), "Captura: usa Text Pattern ya");
            Contiene("herramienta de Claude Code", Habla("● Update(src/Captura.cs)\n  ⎿  Updated", es), "Edita src, Captura punto C S. Updated");
            Igual("animación en silencio", "", Habla("✻ Thinking… (12s · esc to interrupt)", es));
            Contiene("marcas de estado", Habla("✓ listo\n✗ Build failed", es), "listo listo. falla Build failed");
            Cierto("pista de interfaz callada", !Habla("… +12 lines (ctrl+o to expand)", es).Contains("ctrl"));
        }
        // ---------- Tablas ----------
        {
            var es = new Configuracion { VozEdge = "es-ES-AlvaroNeural", Simbolos = "natural", LeerUrls = "ruta" };
            string md = "| Archivo | Cambios | Estado |\n|---|---|---|\n| Captura.cs | 3 | ✓ |\n| Lector.cs | 12 | ✗ |";
            Igual("tabla markdown", "Tabla de 3 columnas y 2 filas. Archivo, Cambios, Estado. Captura punto C S: Cambios, 3; Estado, listo. Lector punto C S: Cambios, 12; Estado, falla.",
                Habla(md, es).Replace(" | ", " "));
            string caja = "┌──────┬──────┐\n│ Clave│ Valor│\n├──────┼──────┤\n│ tecla│ F9   │\n│ voz  │ Rémy │\n└──────┴──────┘";
            Igual("tabla de caja de 2 columnas: clave: valor", "Tabla de 2 columnas y 2 filas. Clave, Valor. tecla: F 9. voz: Rémy.", Habla(caja, es).Replace(" | ", " "));
            string partida = "┌──────┬──────────┐\n│ Paso │ Detalle  │\n├──────┼──────────┤\n│ uno  │ primera  │\n│      │ segunda  │\n│ dos  │ otra     │\n└──────┴──────────┘";
            Contiene("celda partida en dos líneas se lee seguida", Habla(partida, es).Replace(" | ", " "), "uno: primera segunda. dos: otra.");
            Contiene("tabla de 2 filas: aviso", Habla(partida, es), "Tabla de 2 columnas y 2 filas.");
            Cierto("recuadro de una columna no es tabla", !Habla("╭──────────╮\n│ Bienvenido │\n╰──────────╯", es).Contains("Tabla"));
            // Las palabras de la tabla siguen apuntando a su texto original (para el resaltado).
            foreach (var w in Preparador.Preparar(md, es).Trozos.SelectMany(t => t.Palabras))
            {
                string orig = md.Substring(w.OrigInicio, w.OrigLargo);
                Cierto("tabla: posición de '" + orig + "'", orig.Length > 0 && orig.IndexOf('|') < 0 && orig.IndexOf('\n') < 0);
            }
            // simbolos = todos: lectura literal, para dictar.
            Contiene("literal: paréntesis con nombre", Habla("valor (opcional)", new Configuracion { Simbolos = "todos" }), "abre paréntesis");
        }

        // ---------- Símbolos con nombre entre mini pausas (predeterminado, como JAWS "Most") ----------
        {
            var j = new Configuracion { VozEdge = "es-ES-AlvaroNeural", PausaSimbolos = "corta" };
            Igual("paréntesis con nombre y mini pausa", "El modelo · abre paréntesis · versión 2 · cierra paréntesis · funciona.", Habla("El modelo (versión 2) funciona.", j));
            Contiene("ruta con todos sus signos", Habla(@"C:\Users\ander\mi_carpeta", j), "C · dos puntos barra invertida · Users · barra invertida · ander · barra invertida · mi · guion bajo · carpeta");
            Contiene("guion dentro de palabra", Habla("pre-entrenado", j), "pre · guion · entrenado");
            Contiene("guion medio y raya", Habla("a – b — c", j), "a · guion medio · b · raya · c");
            Contiene("ampersand y barra vertical", Habla("a & b | c", j), "a · ampersand · b · barra vertical · c");
            Contiene("operador agrupado", Habla("x != y", j), "x · exclamación igual · y");
            Contiene("opción", Habla("git --help", j), "git · doble guion · help");
            Contiene("signo de número sin pausa", Habla("bajó a -5 y subió +3", j), "bajó a menos 5 y subió más 3");
            Contiene("comillas con nombre", Habla("dijo \"hola\" ya", j), "dijo · abre comillas · hola · cierra comillas · ya");
            Contiene("url completa", Habla("https://github.com/a/lector-karaoke?x=1", j), "enlace git hub punto com · barra · a · barra · lector · guion · karaoke · interrogación · x · igual · 1");
            Contiene("correo", Habla("ana_b@x.com", j), "ana · guion bajo · b · arroba · x punto com");
            Contiene("archivo y extensión", Habla("main.py", j), "main punto P Y");
            Contiene("puntuación final solo pausa", Habla("Nota: listo, ya.", j), "Nota: listo, ya.");
            Contiene("viñeta", Habla("- uno\n- dos", j), "viñeta · uno. viñeta · dos");
            Contiene("línea decorativa", Habla("texto\n-----\nmás", j), "línea de guiones.");
            Contiene("se conservan fechas y unidades", Habla("el 20/10/2026 en 100ms", j), "el 20 de octubre de 2026 en 100 milisegundos");
            Contiene("pausa media = coma", Habla("El modelo (versión 2) funciona.", new Configuracion { PausaSimbolos = "media" }), "El modelo, abre paréntesis, versión 2, cierra paréntesis, funciona.");
            Igual("sin pausa", "El modelo abre paréntesis versión 2 cierra paréntesis funciona.", Habla("El modelo (versión 2) funciona.", new Configuracion { PausaSimbolos = "no" }));
            // Posiciones intactas con nombres.
            string s0 = "ver (a_b) & C:\\x";
            foreach (var w in Preparador.Preparar(s0, j).Trozos.SelectMany(t => t.Palabras))
                Cierto("posición con nombres: '" + s0.Substring(w.OrigInicio, w.OrigLargo) + "'", w.OrigLargo > 0 && w.OrigInicio + w.OrigLargo <= s0.Length);
        }

        // ---------- Números ----------
        {
            var mx = new Configuracion { VozEdge = "es-MX-JorgeNeural" };   // es-MX: decimal con punto
            var es = new Configuracion { VozEdge = "es-ES-AlvaroNeural" };  // es-ES: decimal con coma
            Contiene("25.000", Habla("son 25.000 pesos", mx), "25000");
            Contiene("25,000", Habla("son 25,000 pesos", mx), "25000");
            Contiene("25000", Habla("son 25000 pesos", mx), "25000");
            Contiene("1.234.567", Habla("total 1.234.567", mx), "1234567");
            Contiene("1.234,56 en México", Habla("total 1.234,56", mx), "1234.56");
            Contiene("1,234.56 en España", Habla("total 1,234.56", es), "1234,56");
            Contiene("decimal 3,5 en México", Habla("vale 3,5", mx), "3.5");
            Contiene("decimal 3.5 en España", Habla("vale 3.5", es), "3,5");
            Contiene("0,500 es decimal", Habla("pesa 0,500 kg", mx), "0.500");
            Contiene("versión con punto", Habla("versión 1.2.3", mx), "versión 1 punto 2 punto 3");
            Contiene("IP con punto", Habla("host 192.168.0.1", mx), "192 punto 168 punto 0 punto 1");
            Contiene("fecha en palabras (es-MX)", Habla("el 2026-10-20", mx), "el 20 de octubre de 2026");
            Contiene("hora: 12 y 30", Habla("a las 12:30", mx), "a las 12 y 30");
            Contiene("moneda", Habla("cuesta $25,000", mx), "25000 dólares");
            Contiene("porcentaje", Habla("sube 12,5%", mx), "12.5%");
            Contiene("negativo", Habla("saldo -25.000", mx), "25000");
            Contiene("números sin símbolos", Habla("son 25.000 pesos", new Configuracion { Simbolos = "no" }), "25000");
            Contiene("numeros = literal", Habla("son 25,000 pesos", new Configuracion { Numeros = "literal" }), "25,000");
            Contiene("voz multilingüe: decimal con punto", Habla("vale 3,5", new Configuracion()), "3.5");
        }

        // ---------- Idioma ----------
        {
            Cierto("inglés detectado", Preparador.EsIngles("The build failed because the tests are not passing."));
            Cierto("español no es inglés", !Preparador.EsIngles("La compilación falló porque las pruebas no pasan."));
            Cierto("español con términos en inglés sigue español", !Preparador.EsIngles("El pull request tiene un merge conflict en la rama de deploy."));
            Cierto("texto corto sin pistas no es inglés", !Preparador.EsIngles("Build OK"));
            var c = new Configuracion { VozEdge = "es-MX-JorgeNeural", VozIngles = "en-US-AndrewMultilingualNeural" };
            var ten = Preparador.Preparar("Please check the logs and try again.", c).Trozos[0];
            var tes = Preparador.Preparar("Por favor revisa los registros.", c).Trozos[0];
            Igual("voz para inglés", "en-US-AndrewMultilingualNeural", MotorEdge.VozPara(ten, c));
            Igual("voz para español", "es-MX-JorgeNeural", MotorEdge.VozPara(tes, c));
            var sin = new Configuracion { VozEdge = "es-MX-JorgeNeural", VozIngles = "" };
            Igual("sin voz inglesa usa la principal", "es-MX-JorgeNeural", MotorEdge.VozPara(ten, sin));
        }

        // ---------- Configuración ----------
        {
            string ruta = Path.Combine(Path.GetTempPath(), "lk-prueba-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(ruta, "[general]\ntecla = F10 ; comentario\nvelocidad = +25%\nvolumen = 300\nmotor = otro\n[resaltado]\ncolor = #1E78FF\nfranja = no\n[copiar]\norca = ninguno\nnotepad = ctrl+insert\nraro = ctrl+zz\n");
            var c = Configuracion.Cargar(ruta);
            Igual("config: tecla", "F10", c.Tecla);
            Igual("config: vk F10", 0x79u, c.TeclaVk);
            Igual("config: velocidad +25%", 25, c.Velocidad);
            Igual("config: volumen inválido -> predeterminado", 100, c.Volumen);
            Igual("config: motor inválido -> edge", "edge", c.Motor);
            Igual("config: color hex", 0x1E, (int)c.ColorResaltado.R);
            Cierto("config: franja no", !c.Franja);
            Cierto("config: orca ninguno", c.SinCopia.Contains("orca") && !c.AtajosCopia.ContainsKey("orca"));
            Igual("config: atajo propio", "ctrl+insert", c.AtajosCopia["notepad"]);
            Igual("config: problemas detectados", 3, c.Problemas.Count);
            var cs = Configuracion.Cargar(ruta2Simbolos());
            File.Delete(ruta);

            string ruta2 = Path.Combine(Path.GetTempPath(), "lk-prueba-" + Guid.NewGuid().ToString("N") + ".ini");
            var c2 = Configuracion.Cargar(ruta2);
            Cierto("config: se crea si falta", File.Exists(ruta2) && c2.Problemas.Count == 0);
            var c3 = Configuracion.Cargar(ruta2);
            Igual("config: el archivo predeterminado se lee sin problemas", 0, c3.Problemas.Count);
            Igual("config: predeterminado naranja", 140, (int)c3.ColorResaltado.G);
            Igual("config: voz predeterminada multilingüe", "fr-FR-RemyMultilingualNeural", c3.VozEdge);
            File.Delete(ruta2);
        }
        {
            string r = Path.Combine(Path.GetTempPath(), "lk-voz-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(r, "[general]\nvoz_edge = es-ES-AlvaroNeural,es-MX-JorgeNeural\nvoz_ingles = en-US-AvaMultilingualNeural\nvelocidad = rápido\n");
            var c = Configuracion.Cargar(r);
            Igual("config: lista de voces rechazada", "fr-FR-RemyMultilingualNeural", c.VozEdge);
            Igual("config: avisa la voz inválida", 1, c.Problemas.Count);
            Igual("config: voz inglesa válida", "en-US-AvaMultilingualNeural", c.VozIngles);
            Igual("config: velocidad 'rápido'", 25, c.Velocidad);
            Cierto("config: voz con región", Configuracion.EsVozEdge("zh-CN-shaanxi-XiaoniNeural") && Configuracion.EsVozEdge("es-MX-JorgeNeural") && !Configuracion.EsVozEdge("Jorge"));
            File.Delete(r);
        }
        {
            ushort[] v;
            Cierto("atajo ctrl+shift+c", Configuracion.TeclasDeAtajo("ctrl+shift+c", out v) && v.Length == 3 && v[2] == 'C');
            Cierto("atajo ctrl+insert", Configuracion.TeclasDeAtajo("Ctrl+Insert", out v) && v[1] == 0x2D);
            Cierto("atajo inválido", !Configuracion.TeclasDeAtajo("ctrl+", out v));
            uint vk;
            Cierto("tecla F24", Configuracion.VkDeTecla("f24", out vk) && vk == 0x87);
            Cierto("tecla F25 no existe", !Configuracion.VkDeTecla("F25", out vk));
            Cierto("tecla Pausa", Configuracion.VkDeTecla("Pausa", out vk) && vk == 0x13);
        }

        Console.WriteLine();
        Console.WriteLine("RESULTADO: " + ok + " ok, " + fallos + " fallos");
        return fallos == 0 ? 0 : 1;
    }

    private static string ruta2Simbolos()
    {
        string r = Path.Combine(Path.GetTempPath(), "lk-sim-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(r, "[avanzado]\nsimbolos = todos\n");
        var c = Configuracion.Cargar(r);
        Cierto("config: simbolos = todos", c.Simbolos == "todos" && c.Problemas.Count == 0);
        File.WriteAllText(r, "[avanzado]\nsimbolos = muchos\n");
        c = Configuracion.Cargar(r);
        Cierto("config: simbolos inválido avisa", c.Simbolos == "algunos" && c.Problemas.Count == 1);
        return r;
    }

    // Une los trozos en uno (las pruebas de tiempos necesitan la frase completa).
    private static Trozo Unir(Preparado p)
    {
        var t = new Trozo();
        foreach (var x in p.Trozos) t.Palabras.AddRange(x.Palabras);
        t.Texto = string.Join(" ", t.Palabras.Select(w => w.Habla));
        return t;
    }

    private static string Habla(string s, Configuracion cfg)
    {
        return string.Join(" ", Preparador.Preparar(s, cfg).Trozos.Select(t => t.Texto));
    }

    private static bool Filtra(string habla, string secreto)
    {
        // Ningún fragmento de 6+ caracteres del valor secreto aparece en lo que se habla.
        string valor = secreto.Contains("=") ? secreto.Substring(secreto.IndexOf('=') + 1) : secreto.Contains(": ") ? secreto.Substring(secreto.LastIndexOf(' ') + 1) : secreto;
        if (valor.Contains("://") && valor.Contains("@"))
        {
            int a = valor.IndexOf("://") + 3;
            valor = valor.Substring(a, valor.IndexOf('@') - a);      // solo usuario:clave
        }
        string h = habla.Replace(" ", "");
        for (int i = 0; i + 6 <= valor.Length; i++)
            if (h.IndexOf(valor.Substring(i, 6), StringComparison.Ordinal) >= 0 && !"cluster".Contains(valor.Substring(i, 6))) return true;
        return false;
    }

    private static bool Monotono(Trozo t)
    {
        for (int i = 1; i < t.Palabras.Count; i++) if (t.Palabras[i].InicioMs < t.Palabras[i - 1].InicioMs) return false;
        return true;
    }

    private static void Cierto(string nombre, bool c) { if (c) ok++; else { fallos++; Console.WriteLine("FALLA: " + nombre); } }
    private static void Igual<T>(string nombre, T esperado, T real)
    {
        if (EqualityComparer<T>.Default.Equals(esperado, real)) ok++;
        else { fallos++; Console.WriteLine("FALLA: " + nombre + " -> esperado '" + esperado + "', real '" + real + "'"); }
    }
    private static void Contiene(string nombre, string texto, string parte)
    {
        if (texto.Contains(parte)) ok++;
        else { fallos++; Console.WriteLine("FALLA: " + nombre + " -> '" + texto + "' no contiene '" + parte + "'"); }
    }
}
