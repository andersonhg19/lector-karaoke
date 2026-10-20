using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace LectorKaraoke
{
    // Lee config.ini. Cualquier valor inválido se reemplaza por el predeterminado y se anota en Problemas.
    internal sealed class Configuracion
    {
        public string Tecla = "F9";
        public uint TeclaVk = 0x78;

        public string Motor = "edge";               // edge | piper | sapi
        public bool UsarInternet = true;
        public string VozEdge = "es-MX-JorgeNeural";
        public string VozIngles = "en-US-AndrewMultilingualNeural";   // vacío = leer el inglés con la voz principal
        public string VozPiper = "es_MX-ald-medium";
        public string VozSapi = "";                 // vacío = la primera voz en español
        public int Velocidad = 0;                   // -50..100 (%)
        public int Volumen = 100;                   // 0..100

        public Color ColorResaltado = Color.FromArgb(255, 140, 0);
        public int OpacidadActual = 45;             // 0..100
        public int OpacidadLeido = 18;              // 0..100
        public int GrosorSubrayado = 4;             // px a 100 % de escala
        public bool MarcarLeido = true;
        public bool SeguirLectura = true;           // desplazar la aplicación hasta la palabra si está fuera de la vista
        public bool Franja = true;
        public int FranjaTamano = 26;               // puntos

        public string LeerUrls = "dominio";         // dominio | completa | no
        public string Simbolos = "algunos";         // no | algunos | todos
        public int MaxCaracteres = 30000;
        public int EsperaMotorMin = 5;
        public bool RegistrarTexto = false;

        public Dictionary<string, string> AtajosCopia = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string AtajoCopiaPredeterminado = "ctrl+c";
        public HashSet<string> SinCopia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Problemas = new List<string>();

        public const string NombreArchivo = "config.ini";

        public Configuracion()
        {
            AtajosCopia["orca"] = "ctrl+shift+c";
            AtajosCopia["code"] = "ctrl+insert";
            AtajosCopia["cursor"] = "ctrl+insert";
            foreach (var p in new[] { "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "mintty",
                "putty", "kitty", "alacritty", "wezterm-gui", "hyper", "tabby", "mobaxterm", "terminus", "wsl", "bash" })
                SinCopia.Add(p);
        }

        public static Configuracion Cargar(string ruta)
        {
            var c = new Configuracion();
            if (!File.Exists(ruta))
            {
                try { File.WriteAllText(ruta, TextoPredeterminado, new UTF8Encoding(true)); }
                catch (Exception e) { c.Problemas.Add("No se pudo crear config.ini: " + e.Message); }
                return c;
            }
            string seccion = "";
            string[] lineas;
            try { lineas = File.ReadAllLines(ruta, Encoding.UTF8); }
            catch (Exception e) { c.Problemas.Add("No se pudo leer config.ini: " + e.Message); return c; }
            for (int n = 0; n < lineas.Length; n++)
            {
                string l = lineas[n].Trim();
                if (l.Length == 0 || l[0] == ';' || l[0] == '#') continue;
                if (l[0] == '[' && l.EndsWith("]")) { seccion = l.Substring(1, l.Length - 2).Trim().ToLowerInvariant(); continue; }
                int eq = l.IndexOf('=');
                if (eq <= 0) { c.Problemas.Add("Línea " + (n + 1) + " sin '=': " + l); continue; }
                string clave = l.Substring(0, eq).Trim().ToLowerInvariant();
                string valor = QuitarComentario(l.Substring(eq + 1).Trim()).Trim();
                try { c.Aplicar(seccion, clave, valor, n + 1); }
                catch (Exception e) { c.Problemas.Add("Línea " + (n + 1) + " (" + clave + "): " + e.Message); }
            }
            return c;
        }

        private static string QuitarComentario(string v)
        {
            // Un ';' o '#' después de un espacio inicia un comentario; "#FF8C00" al inicio es un color.
            for (int i = 1; i < v.Length; i++)
                if ((v[i] == ';' || v[i] == '#') && char.IsWhiteSpace(v[i - 1])) return v.Substring(0, i);
            return v;
        }

        private void Aplicar(string seccion, string clave, string valor, int linea)
        {
            if (seccion == "copiar")
            {
                if (clave == "predeterminado") { AtajoCopiaPredeterminado = ValidarAtajo(valor, linea, AtajoCopiaPredeterminado); return; }
                if (clave == "sin_copia")
                {
                    SinCopia.Clear();
                    foreach (var p in valor.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) SinCopia.Add(p.Trim().ToLowerInvariant().Replace(".exe", ""));
                    return;
                }
                string proc = clave.Replace(".exe", "");
                if (valor.Equals("ninguno", StringComparison.OrdinalIgnoreCase)) { AtajosCopia.Remove(proc); SinCopia.Add(proc); return; }
                AtajosCopia[proc] = ValidarAtajo(valor, linea, "ctrl+c");
                SinCopia.Remove(proc);
                return;
            }
            switch (clave)
            {
                case "tecla":
                    uint vk;
                    if (VkDeTecla(valor, out vk)) { Tecla = valor.ToUpperInvariant(); TeclaVk = vk; }
                    else Problemas.Add("Línea " + linea + ": tecla '" + valor + "' no reconocida (usa F1..F24, Pausa o BloqDespl). Se usa " + Tecla + ".");
                    break;
                case "motor":
                    string m = valor.ToLowerInvariant();
                    if (m == "edge" || m == "piper" || m == "sapi") Motor = m;
                    else Problemas.Add("Línea " + linea + ": motor '" + valor + "' no existe (edge, piper o sapi).");
                    break;
                case "usar_internet": UsarInternet = Booleano(valor, linea, UsarInternet); break;
                case "voz_edge": if (valor.Length > 0) VozEdge = valor; break;
                case "voz_ingles": VozIngles = valor; break;
                case "voz_piper": if (valor.Length > 0) VozPiper = valor; break;
                case "voz_sapi": VozSapi = valor; break;
                case "velocidad": Velocidad = Entero(valor, -50, 100, linea, Velocidad); break;
                case "volumen": Volumen = Entero(valor, 0, 100, linea, Volumen); break;
                case "color":
                    Color col;
                    if (ColorDe(valor, out col)) ColorResaltado = col;
                    else Problemas.Add("Línea " + linea + ": color '" + valor + "' no válido (usa #RRGGBB o naranja, azul, verde, rojo, morado, amarillo, cian, rosado).");
                    break;
                case "opacidad_actual": OpacidadActual = Entero(valor, 5, 100, linea, OpacidadActual); break;
                case "opacidad_leido": OpacidadLeido = Entero(valor, 0, 100, linea, OpacidadLeido); break;
                case "grosor_subrayado": GrosorSubrayado = Entero(valor, 0, 20, linea, GrosorSubrayado); break;
                case "marcar_leido": MarcarLeido = Booleano(valor, linea, MarcarLeido); break;
                case "seguir_lectura": SeguirLectura = Booleano(valor, linea, SeguirLectura); break;
                case "franja": Franja = Booleano(valor, linea, Franja); break;
                case "franja_tamano": FranjaTamano = Entero(valor, 10, 96, linea, FranjaTamano); break;
                case "leer_urls":
                    string u = valor.ToLowerInvariant();
                    if (u == "dominio" || u == "completa" || u == "no") LeerUrls = u;
                    else Problemas.Add("Línea " + linea + ": leer_urls debe ser dominio, completa o no.");
                    break;
                case "simbolos":
                    string sm = valor.ToLowerInvariant();
                    if (sm == "no" || sm == "algunos" || sm == "todos") Simbolos = sm;
                    else Problemas.Add("Línea " + linea + ": simbolos debe ser no, algunos o todos.");
                    break;
                case "max_caracteres": MaxCaracteres = Entero(valor, 100, 500000, linea, MaxCaracteres); break;
                case "espera_motor_min": EsperaMotorMin = Entero(valor, 0, 240, linea, EsperaMotorMin); break;
                case "registrar_texto": RegistrarTexto = Booleano(valor, linea, RegistrarTexto); break;
                default: Problemas.Add("Línea " + linea + ": opción desconocida '" + clave + "'."); break;
            }
        }

        private bool Booleano(string v, int linea, bool def)
        {
            string s = v.ToLowerInvariant();
            if (s == "si" || s == "sí" || s == "true" || s == "1" || s == "yes") return true;
            if (s == "no" || s == "false" || s == "0") return false;
            Problemas.Add("Línea " + linea + ": '" + v + "' debe ser si o no.");
            return def;
        }

        private int Entero(string v, int min, int max, int linea, int def)
        {
            int n;
            string s = v.Replace("%", "").Replace("+", "").Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= min && n <= max) return n;
            Problemas.Add("Línea " + linea + ": '" + v + "' debe ser un número entre " + min + " y " + max + ".");
            return def;
        }

        private string ValidarAtajo(string v, int linea, string def)
        {
            ushort[] vks;
            if (TeclasDeAtajo(v, out vks)) return v.ToLowerInvariant();
            Problemas.Add("Línea " + linea + ": atajo '" + v + "' no válido (ej.: ctrl+c, ctrl+shift+c, ctrl+insert).");
            return def;
        }

        public static bool VkDeTecla(string t, out uint vk)
        {
            vk = 0;
            string s = t.Trim().ToUpperInvariant();
            if (s.StartsWith("F"))
            {
                int n;
                if (int.TryParse(s.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); return true; }
                return false;
            }
            if (s == "PAUSA" || s == "PAUSE") { vk = 0x13; return true; }
            if (s == "BLOQDESPL" || s == "SCROLLLOCK") { vk = 0x91; return true; }
            return false;
        }

        public static bool TeclasDeAtajo(string atajo, out ushort[] vks)
        {
            var lista = new List<ushort>();
            vks = null;
            foreach (var p0 in atajo.ToLowerInvariant().Split('+'))
            {
                string p = p0.Trim();
                ushort vk;
                if (p == "ctrl" || p == "control") vk = 0x11;
                else if (p == "shift" || p == "mayus") vk = 0x10;
                else if (p == "alt") vk = 0x12;
                else if (p == "insert" || p == "ins") vk = 0x2D;
                else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = (ushort)char.ToUpperInvariant(p[0]);
                else return false;
                lista.Add(vk);
            }
            if (lista.Count == 0) return false;
            vks = lista.ToArray();
            return true;
        }

        public static bool ColorDe(string v, out Color c)
        {
            c = Color.Empty;
            string s = v.Trim().ToLowerInvariant();
            switch (s)
            {
                case "naranja": case "naranjado": c = Color.FromArgb(255, 140, 0); return true;
                case "azul": c = Color.FromArgb(30, 120, 255); return true;
                case "verde": c = Color.FromArgb(0, 200, 80); return true;
                case "rojo": c = Color.FromArgb(230, 40, 40); return true;
                case "morado": c = Color.FromArgb(150, 60, 220); return true;
                case "amarillo": c = Color.FromArgb(255, 220, 0); return true;
                case "cian": c = Color.FromArgb(0, 200, 220); return true;
                case "rosado": c = Color.FromArgb(255, 80, 170); return true;
            }
            if (s.StartsWith("#")) s = s.Substring(1);
            int rgb;
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                c = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                return true;
            }
            return false;
        }

        public string Firma()
        {
            // Resumen para el log (sin datos sensibles).
            return "tecla=" + Tecla + " motor=" + Motor + " internet=" + (UsarInternet ? "si" : "no") + " voz=" + VozEdge +
                   " velocidad=" + Velocidad + " volumen=" + Volumen + " color=#" + ColorResaltado.R.ToString("X2") +
                   ColorResaltado.G.ToString("X2") + ColorResaltado.B.ToString("X2") + " franja=" + (Franja ? "si" : "no") + " simbolos=" + Simbolos;
        }

        public const string TextoPredeterminado =
@"; ==========================================================================
;  Lector Karaoke - configuración
;  Guarda el archivo y el lector la recarga sola (no hace falta reiniciar).
;  Si un valor no es válido se usa el predeterminado y se avisa en la bandeja.
; ==========================================================================

[general]
; Tecla para leer y para detener: F1..F24, Pausa o BloqDespl
tecla = F9

; Motor de voz: edge (voces neuronales de Microsoft, necesita internet),
; piper (local, sin internet) o sapi (voces de Windows)
motor = edge
; no = nunca enviar texto a internet (usa piper o sapi)
usar_internet = si

; Voces. Algunas de Edge: es-MX-JorgeNeural, es-MX-DaliaNeural,
; es-ES-AlvaroNeural, es-ES-ElviraNeural, es-ES-XimenaNeural
voz_edge = es-MX-JorgeNeural
; Voz para los trozos que están en inglés (vacío = usar la principal). Las voces Multilingual pronuncian bien ambos idiomas.
voz_ingles = en-US-AndrewMultilingualNeural
voz_piper = es_MX-ald-medium
; vacío = la primera voz de Windows en español
voz_sapi =

; Velocidad en %: 0 = normal, 25 = más rápido, -20 = más lento (-50 a 100)
velocidad = 0
; Volumen de 0 a 100
volumen = 100

[resaltado]
; #RRGGBB o un nombre: naranja, azul, verde, rojo, morado, amarillo, cian, rosado
color = naranja
; Opacidad (0 a 100) de la palabra que se está leyendo y de las ya leídas
opacidad_actual = 45
opacidad_leido = 18
; Grosor del subrayado en píxeles (0 = sin subrayado)
grosor_subrayado = 4
; Dejar marcadas, más suave, las palabras ya leídas
marcar_leido = si
; Si la palabra que se lee está fuera de la vista, desplazar la aplicación hasta ella
seguir_lectura = si
; Si la aplicación no dice dónde está cada palabra (por ejemplo la terminal de Orca),
; mostrar el texto en una franja junto al puntero
franja = si
franja_tamano = 26

[avanzado]
; Leer los símbolos por su nombre:
;   no      = no se nombran (solo hacen pausa)
;   algunos = comillas, paréntesis, llaves, corchetes y / \ | # $ % & = @ * ¨ ~ ^ + < > (abre comillas ... cierra comillas)
;   todos   = además punto, coma, interrogación, guiones, etc.
simbolos = algunos
; Cómo leer las direcciones web: dominio, completa o no
leer_urls = dominio
max_caracteres = 30000
; Minutos que el motor de voz se queda listo después de leer (0 = cerrarlo enseguida)
espera_motor_min = 5
; si = guardar en el log el texto leído (solo para diagnóstico)
registrar_texto = no

[copiar]
; Atajo para copiar la selección en programas que no la exponen de otra forma.
; NUNCA se usa Ctrl+C en terminales: sin selección interrumpe el proceso (por ejemplo, Claude).
orca = ctrl+shift+c
code = ctrl+insert
cursor = ctrl+insert
predeterminado = ctrl+c
; Programas a los que nunca se les envía un atajo de copia (se leen por accesibilidad o bajo el mouse)
sin_copia = windowsterminal, openconsole, conhost, cmd, powershell, pwsh, mintty, putty, kitty, alacritty, wezterm-gui, hyper, tabby, mobaxterm, terminus, wsl, bash
";
    }
}
