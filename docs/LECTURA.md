# Cómo se lee el texto (25-10-2026)

Reglas de `src/Preparador.cs` y `src/Numeros.cs`.

## Principio: no perder información, pero que se entienda
Quien escucha sin ver necesita el texto real. Por eso, por defecto (`simbolos = algunos`), **cada signo se dice por su nombre**, como lo hace JAWS en su nivel predeterminado ("La mayoría"). Para que no se mezcle con las palabras, **cada nombre va entre pausas**:

> `C:\Users\ander\mi_carpeta (copia)` → "C, dos puntos barra invertida, Users, barra invertida, ander, barra invertida, mi, guion bajo, carpeta, abre paréntesis, copia, cierra paréntesis"

## De dónde sale
- **JAWS** (Freedom Scientific):
  - Por defecto usa el nivel "La mayoría" (Most). En ese nivel nombra `( ) [ ] { } " - _ \ | : ; < > ~ \`` y también `# $ % & * + / = @ ^`.
  - Separa cada nombre con espacios.
  - En los signos de pausa deja el signo tras el nombre para que la voz haga la pausa.
  - Filtra los caracteres repetidos.
  - Dice "viñeta" incluso sin puntuación.
  - Lee las rutas como "C colon back slash Users".
- **NVDA, en español:** usa nombres parecidos ("abrir paréntesis", "guion", "subrayado", "barra inversa"). A partir de 4 signos iguales dice la cantidad ("5 guion").
- **Edge no acepta SSML**, así que la única forma de pausar es con la puntuación del texto. Se midió el silencio que deja cada separador con Álvaro y Rémy a +50 %:

  | Separador | Pausa |
  |---|---|
  | espacio | 0 ms |
  | `·` | unos 60 ms (la voz no lo pronuncia) |
  | coma | unos 200 ms |
  | barra | unos 470 ms |

- **Oído con Whisper:**
  - Con la pausa corta (`·`), a +50 % algunas palabras se pegan: "config guion bajo local ampersand" se oyó "config-blocal-ampersand".
  - Con la pausa media (coma) se distingue todo: "config-local, ampersand, datos, barra vertical, salida".
  - Por eso la predeterminada es `pausa_simbolos = media`.

## Reglas del modo predeterminado (`simbolos = algunos`)
- **Se nombran** paréntesis, corchetes, llaves, comillas, guion, guion medio (–), raya (—), guion bajo, barra, barra invertida, barra vertical, numeral, signo de dólar, por ciento, ampersand, arroba, igual, más, asterisco, menor que, mayor que, exclamación e interrogación (dentro de una palabra), virgulilla, circunflejo, punto, coma y dos puntos (dentro de una palabra).
- **Signos seguidos forman un grupo:** `!=` es "exclamación igual"; `--` es "doble guion"; `&&` es "doble ampersand"; `===` es "triple igual".
- **Puntuación normal al final de una palabra** (`. , ; : ? !`): solo hace pausa, como en JAWS. Dentro de una palabra se nombra: `a:b` es "a, dos puntos, b".
- **Punto entre letras** (archivos y dominios): se dice "punto" sin pausa (`main.py` es "main punto P Y"; `github.com` es "git hub punto com"). Las extensiones sin vocales se deletrean.
- **Signo de un número:** sin pausa (`-5` es "menos 5", `+3` es "más 3").
- **URLs** (`leer_urls = completa`): todo con sus signos, sin el `https://` ni `www.`. Por ejemplo, "enlace git hub punto com, barra, a, barra, lector, guion, karaoke, interrogación, x, igual, 1".
- **Listas y separadores:** "viñeta" al inicio de un elemento; `-----` es "línea de guiones".
- **Se conservan las lecturas que dicen lo mismo con palabras:**

  | Entrada | Se dice |
  |---|---|
  | `20/10/2026` | 20 de octubre de 2026 |
  | `14:30` | 14 y 30 |
  | `100ms` | 100 milisegundos |
  | `1º` | primero |
  | `10-25` | 10 a 25 |
  | `v2.3.1` | versión 2 punto 3 punto 1 |
  | `Lector.cs:351` | Lector punto C S, línea 351 |
  | `aprox.` | aproximadamente |
  | `300 555 1234` | por grupos |

- **Formato de markdown que no es contenido:** `**`, comillas invertidas de código, `#` de títulos y las barras de las tablas no se nombran. Claude Code los muestra ya con formato y no aparecen en pantalla.
- **Tablas:**
  - Se lee "Tabla de 3 columnas y 2 filas", los encabezados una vez y cada fila como "Captura punto C S: Cambios, 3; Estado, listo".
  - Es lo mismo que JAWS ("table with 7 columns and 5 rows" y el encabezado junto a cada celda) y el modo para lectores de pantalla de Claude Code.
- **Claude Code:**

  | Entrada | Se dice |
  |---|---|
  | `● Update(x.cs)` | Edita, abre paréntesis, x punto C S, cierra paréntesis |
  | `✓` `✗` `⚠` | listo, falla, atención |
  | Animaciones (`✻ Thinking…`) y pistas (`ctrl+o to expand`) | silencio |

## Opciones
- **`pausa_simbolos`:**

  | Valor | Pausa alrededor de cada nombre |
  |---|---|
  | `media` (predeterminado) | como una coma |
  | `corta` | unos 60 ms, más fluido; a mucha velocidad puede juntar palabras |
  | `no` | ninguna |

- **`simbolos`:**

  | Valor | Qué hace |
  |---|---|
  | `algunos` (predeterminado) | lo descrito arriba |
  | `natural` | Lectura "de persona": paréntesis y rayas como pausa, comillas calladas, operadores con sentido ("distinto de"), URLs y rutas sin signos ("disco C, Users"). Más fluido, pero pierde detalle |
  | `todos` | además nombra la puntuación normal (coma, punto…): para dictar algo exacto |
  | `no` | solo pausas |

- **`leer_urls`:** `completa` (predeterminado), `ruta` (dominio y partes, sin signos), `dominio` o `no`.

## Muestras
`muestras\simbolos\` tiene las mismas cinco frases con pausa corta y con pausa media, leídas por Álvaro a +50 %.

## Fuentes
- JAWS, niveles y comportamiento (grabado de JAWS 2025): https://github.com/monatis/okur/blob/main/docs/references/jaws-punctuation-levels.md · https://github.com/monatis/okur/blob/main/docs/references/jaws-reading-settings-research.md
- JAWS, configuración de la voz: https://www.thisisgain.com/post/how-to-configure-jaws-individual-voice-settings
- JAWS, tablas: https://support.freedomscientific.com/SurfsUp/8-Tables.htm
- ONCE CTI, novedades de JAWS 2021: https://cti-repos.once.es/cti/biblioteca/Lectores%20de%20pantalla/JAWS/JAWS_2021/Novedades_jaws_2021.pdf
- NVDA, `symbols.dic` en español y `characterProcessing.py`: https://github.com/nvaccess/nvda/blob/master/source/locale/es/symbols.dic · https://github.com/nvaccess/nvda/blob/master/source/characterProcessing.py
- WebAIM, puntuación para código: https://webaim.org/discussion/mail_message?id=33801
- RAE: https://www.rae.es/dpd/guion · https://www.rae.es/dpd/raya · https://www.rae.es/buen-uso-espa%C3%B1ol/la-barra
- edge-tts (sin SSML personalizado): https://github.com/rany2/edge-tts
- NeMo, normalización de texto en español: https://github.com/NVIDIA/NeMo-text-processing/tree/main/nemo_text_processing/text_normalization/es
- Accesibilidad de Claude Code: https://code.claude.com/docs/en/accessibility
