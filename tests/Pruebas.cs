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
        var cfg = new Configuracion { Simbolos = "no" };   // las pruebas base van sin nombrar símbolos

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
        Contiene("ruta windows", Habla(@"C:\Users\ander\Documents\archivo.java", cfg), "C Users ander Documents archivo.java");
        Contiene("ruta unix", Habla("src/main/java/Servicio.java", cfg), "src main java Servicio.java");
        Contiene("url dominio", Habla("mira https://www.github.com/andersonhg19/repo?x=1 ahora", cfg), "enlace github.com");
        Cierto("url sin ruta", !Habla("https://www.github.com/andersonhg19/repo", cfg).Contains("andersonhg19"));
        Contiene("fecha intacta", Habla("el 2026-10-20 a las 12:30", cfg), "2026-10-20");
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
            var sim = new Configuracion();     // predeterminado: "algunos"
            Igual("símbolos: predeterminado", "algunos", sim.Simbolos);
            Contiene("comillas", Habla("dijo \"hola mundo\" ayer", sim), "abre comillas hola mundo cierra comillas");
            Contiene("paréntesis", Habla("valor (opcional) final", sim), "abre paréntesis opcional cierra paréntesis");
            Contiene("llaves y corchetes", Habla("{a} [b]", sim), "abre llave a cierra llave abre corchete b cierra corchete");
            Contiene("barra", Habla("si/no", sim), "si barra no");
            Contiene("ruta con barras", Habla(@"C:\Users\x", sim), "C barra invertida Users barra invertida x");
            Contiene("numeral", Habla("issue #123", sim), "numeral 123");
            Contiene("arroba", Habla("correo@dominio", sim), "correo arroba dominio");
            Contiene("igual y doble igual", Habla("a = b == c", sim), "a igual b doble igual c");
            Contiene("ampersand doble", Habla("x && y", sim), "x doble ampersand y");
            Contiene("asterisco suelto", Habla("2 * 3", sim), "2 asterisco 3");
            Contiene("dólares con número", Habla("cuesta $100", sim), "dólares");
            Contiene("por ciento", Habla("50% listo", sim), "50%");
            Contiene("diéresis y circunflejo", Habla("¨ ^", sim), "diéresis circunflejo");
            Contiene("archivo con punto", Habla("Config.java", sim), "Config punto java");
            Contiene("interrogación en medio", Habla("url?x=1", sim), "url interrogación x igual 1");
            Contiene("pregunta normal sin nombrar", Habla("¿vienes hoy?", sim), "vienes hoy?");
            Cierto("pregunta normal: no dice 'interrogación'", !Habla("¿vienes hoy?", sim).Contains("interrogación"));
            Contiene("markdown negrita sin leer asteriscos", Habla("**importante** ya", sim), "importante ya");
            Cierto("markdown: no dice asterisco", !Habla("**importante** ya", sim).Contains("asterisco"));
            Igual("línea decorativa", 0, Preparador.Preparar("==========", sim).Trozos.Count);
            Cierto("encabezado markdown sin numeral", !Habla("## Resumen\nTexto", sim).Contains("numeral"));
            Cierto("viñeta sin leer", !Habla("- uno\n- dos", sim).Contains("guion"));
            Contiene("snake_case sin nombrar", Habla("MAX_RETRY", sim), "MAX RETRY");
            Contiene("fecha intacta", Habla("el 2026-10-20", sim), "2026-10-20");
            Contiene("url resumida", Habla("https://www.github.com/a/b", sim), "enlace github.com");
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
            Contiene("versión intacta", Habla("versión 1.2.3", mx), "1.2.3");
            Contiene("IP intacta", Habla("host 192.168.0.1", mx), "192.168.0.1");
            Contiene("fecha intacta", Habla("el 2026-10-20", mx), "2026-10-20");
            Contiene("hora intacta", Habla("a las 12:30", mx), "12:30");
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
