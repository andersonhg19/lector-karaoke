# Lector Karaoke

Lector de pantalla **a pedido** para Windows 11: seleccionas un texto, presionas **una tecla** (F9 por defecto) y se lee en voz alta mientras cada palabra se resalta **sobre la misma aplicación**, al estilo karaoke. Sin selección, lee el botón o el texto de ayuda (tooltip) que está bajo el puntero del mouse. La misma tecla detiene la lectura.

Pensado para personas que usan la Lupa de Windows con mucho aumento y no alcanzan a leer los textos de ayuda, o que quieren escuchar un bloque de texto (por ejemplo, la salida de una terminal) sin abrir otra aplicación ni copiar y pegar.

*English summary: on-demand screen reader for Windows 11. Select text, press one key (F9), and it is read aloud while each word is highlighted in place over the application (karaoke style). With nothing selected it reads the control or tooltip under the mouse. Neural voices (Microsoft Edge voices via `edge-tts`, offline fallback with Piper). Portable, no admin rights, no installer.*

## Qué hace

| Al presionar la tecla | Resultado |
|---|---|
| Hay texto seleccionado | Lo lee y resalta en naranja la palabra que se está diciendo; las ya leídas quedan con un naranja más suave. |
| No hay selección | Lee el elemento bajo el mouse: su tooltip si está visible, si no su nombre y descripción; lo enmarca en naranja. |
| Ya está leyendo | Se detiene. Es la misma tecla: no usa Esc, que en muchas herramientas (por ejemplo Claude Code) detiene el proceso. |

- **Nunca habla por su cuenta.** Solo cuando presionas la tecla.
- **No abre ventanas:** un icono en la bandeja y una capa transparente que no recibe clics ni toma el foco.
- **Voces neuronales:** por defecto *Rémy* (`fr-FR-RemyMultilingualNeural`), una voz multilingüe de Microsoft Edge que suena nativa en español y en inglés (ver la evaluación en [`docs/VOCES.md`](docs/VOCES.md)). Sin internet usa **Piper** (local, voz `es_MX-ald`). Si Piper falta, usa las voces de Windows.
- **Cifras como las diría una persona:** `25.000` y `25,000` son "veinticinco mil"; `10-25` es "10 a 25"; `20/10/2026` es "20 de octubre de 2026"; `1.5GB` es "1,5 gigabytes"; `v2.3.1` es "versión 2 punto 3 punto 1"; `1º`, "primero"; `300 555 1234` se lee por grupos. Los trozos en inglés se leen con una voz nativa.
- **Protege secretos:** contraseñas, tokens, claves de API, JWT, URIs con usuario y clave, y bloques de clave privada se dicen como "valor oculto" y **nunca** salen a internet.
- **Lee como JAWS, sin perder información:**
  - Cada signo se nombra entre pausas cortas, para que se entienda sin ver el texto. Por ejemplo, `C:\Users\mi_carpeta (copia)` se lee "C, dos puntos barra invertida, Users, barra invertida, mi, guion bajo, carpeta, abre paréntesis, copia, cierra paréntesis".
  - Las tablas se leen como "Tabla de 3 columnas y 2 filas…" y luego, fila por fila, "Encabezado, valor".
  - Fechas, unidades y ordinales se dicen en palabras ("20 de octubre de 2026", "100 milisegundos", "primero").
  - Detalle, mediciones y fuentes en [`docs/LECTURA.md`](docs/LECTURA.md).
- **Portable:** todo vive en una carpeta. Para llevarlo a otro PC se copia la carpeta y se ejecuta `Configurar.exe`.
- **Liviano:** en reposo no consume CPU y ocupa unos 35 MB de memoria. El motor de voz se cierra solo tras unos minutos sin uso.

## Instalación

1. Descarga la última versión desde *Releases* (o compílala, ver más abajo) y descomprímela donde quieras, por ejemplo `C:\LectorKaraoke`.
2. Ejecuta **`Configurar.exe`**. Descarga lo que falte, verificando cada archivo con su huella SHA-256:
   - Python embebido 3.12 y la librería `edge-tts` (para las voces de Edge);
   - Piper y la voz `es_MX-ald-medium` (para leer sin internet).

   Después registra el arranque automático con Windows y abre el lector.
3. Listo: aparece una "L" naranja en la bandeja. Selecciona un texto y presiona **F9**.

No necesita permisos de administrador ni instala nada fuera de su carpeta (salvo la entrada de inicio en `HKCU\...\Run`).

### Otras opciones de `Configurar.exe`
| Opción | Qué hace |
|---|---|
| *(sin opciones)* | Instala o repara lo que falte, registra el arranque y reinicia el lector |
| `/reiniciar` | Solo reinicia el lector |
| `/desinstalar` | Quita el arranque automático y cierra el lector (no borra la carpeta) |
| `/actualizar-voz` | Actualiza `edge-tts` (si Microsoft cambia el acceso a sus voces) |
| `/sin-descargas`, `/silencioso` | Para usos automatizados |

El menú del icono de la bandeja también permite leer o detener, abrir la configuración, reiniciar y salir.

## Elegir voz
Ejecuta `motor\python\python.exe -I motor\muestras.py`: crea en `muestras\` un mp3 por cada voz candidata (España, México, Colombia, Argentina, multilingües…) con la misma frase, y `muestras.m3u` para escucharlas seguidas. Copia el nombre de la que más te guste en `voz_edge`.

## Configuración (`config.ini`)

El archivo se crea con valores comentados. **Al guardarlo, el lector lo recarga solo**; si un valor no es válido, usa el predeterminado y avisa en la bandeja.

| Opción | Predeterminado | Descripción |
|---|---|---|
| `tecla` | `F9` | `F1`..`F24`, `Pausa` o `BloqDespl`. Si la nueva está ocupada, se mantiene la anterior y se avisa |
| `motor` | `edge` | `edge`, `piper` o `sapi` |
| `usar_internet` | `si` | `no` = nunca enviar texto a internet |
| `voz_edge` | `fr-FR-RemyMultilingualNeural` | Una sola voz. También `es-MX-DaliaNeural`, `es-ES-AlvaroNeural`, `es-ES-ElviraNeural`, `es-ES-XimenaNeural`… Un nombre mal escrito se avisa en la bandeja |
| `voz_ingles` | *(vacío)* | Voz para los trozos en inglés; vacío = la principal (las multilingües leen ambos idiomas) |
| `numeros` | `auto` | `auto`: `25.000` / `25,000` = veinticinco mil; `literal`: tal cual a la voz |
| `velocidad` | `0` | En %, de `-50` a `100`, o `lenta`, `normal`, `rápida`, `muy rápida` |
| `volumen` | `100` | De 0 a 100 |
| `color` | `naranja` | `#RRGGBB` o un nombre: naranja, azul, verde, rojo, morado, amarillo, cian, rosado |
| `opacidad_actual` / `opacidad_leido` | `45` / `18` | Opacidad de la palabra actual y de las ya leídas |
| `grosor_subrayado` | `4` | Píxeles a 100 % de escala; crece con el tamaño del texto |
| `marcar_leido` | `si` | Dejar marcadas las palabras ya leídas |
| `ocr_terminal` / `ocr_programas` | `si` / `orca` | Ubicar las palabras con el OCR de Windows en terminales que no informan posiciones y resaltarlas sobre el texto |
| `franja` / `franja_tamano` | `si` / `26` | Subtítulo junto al puntero cuando la aplicación no informa la posición de las palabras |
| `simbolos` | `algunos` | `algunos`: nombra cada signo (abre paréntesis, guion bajo, barra invertida, ampersand…) entre pausas, como JAWS. `natural`: paréntesis y rayas como pausa y solo operadores, más fluido. `todos`: además coma, punto, etc. `no`: solo pausas |
| `pausa_simbolos` | `media` | Pausa alrededor de cada signo nombrado: `media` (como una coma), `corta` (unos 60 ms) o `no` |
| `leer_urls` | `completa` | `completa` (todo con sus signos, sin `https://`), `ruta` (dominio y partes, sin signos), `dominio` o `no` |
| `espera_motor_min` | `5` | Minutos que el motor de voz se queda listo tras leer |
| `registrar_texto` | `no` | `si` guarda el texto leído en el log (solo para diagnóstico) |
| `[copiar]` | ver archivo | Atajo de copia por programa y lista de programas a los que nunca se les envía uno |

## Cómo funciona

```
Tecla global (RegisterHotKey)
  └─ Captura del texto
       1. UI Automation (TextPattern): selección + rectángulo exacto de cada palabra en pantalla
       2. Copia controlada por programa (si la app no expone su texto) → el portapapeles se restaura tal cual
       3. Sin selección: elemento o tooltip bajo el mouse
  └─ Preparación: limpieza, secretos, identificadores, trozos (el primero corto para empezar rápido)
  └─ Voz: Edge (marcas exactas por palabra) → Piper (tiempos estimados por sílabas) → SAPI (marcas exactas)
       el trozo siguiente se sintetiza mientras suena el actual
  └─ Reproducción (MCI de Windows) con posición en milisegundos
  └─ Resaltado: ventana por capas, transparente a clics, consciente del DPI de cada monitor
```

- **Escrito en C# para .NET Framework 4.8**, que ya viene con Windows 10/11, y compilado con el compilador incluido en Windows. Sin dependencias.
- **El motor de Edge** es un pequeño proceso Python (`motor/lector_edge.py`) que habla por stdin/stdout en JSON. Usa la librería [`edge-tts`](https://github.com/rany2/edge-tts) para pedir el audio y las marcas `WordBoundary` (el momento exacto de cada palabra).
- **Con un solo atajo** se cubren las aplicaciones sin UI Automation de texto. Por ejemplo, en la terminal de Orca el lector usa `Ctrl+Shift+C`, que en Windows Orca nunca reenvía al proceso. **Nunca envía `Ctrl+C` a una terminal**, porque sin selección interrumpe el programa que corre en ella.

## Privacidad
- **Con `motor = edge`**, el texto que lees se envía a un servicio de Microsoft (el mismo que usa la función "Leer en voz alta" de Edge) para convertirlo en audio. Antes de enviarlo se ocultan los valores que parecen secretos.
- **Para que nada salga del equipo**, usa `usar_internet = no`: lee con Piper, localmente.
- **El log no guarda el texto leído**, salvo que actives `registrar_texto`.
- **Si una aplicación solo permite leer copiando**, la copia la hace esa aplicación. Si tienes activado el historial del portapapeles de Windows (Win+V), ese texto puede quedar en el historial. El lector restaura tu portapapeles y marca la restauración para que no entre al historial.

## Límites conocidos
- `edge-tts` usa un servicio de Microsoft no documentado. Si Microsoft lo cambia, el lector pasa a la voz local, intenta actualizar `edge-tts` una vez al día y avisa. También se puede forzar con `Configurar.exe /actualizar-voz`.
- **Terminal de Orca:** el resaltado va sobre el texto gracias al OCR de Windows (local, sin internet), que ubica las palabras copiadas en la rejilla de la terminal. Si el texto se desplaza mientras se lee, el resaltado no lo sigue. Si la ubicación no es confiable (texto fuera de pantalla), se usa la franja.
- **Sin posición de cada palabra** (VS Code y otras apps Electron), el resaltado se muestra en la franja junto al puntero.
- No sigue la Lupa de Windows automáticamente: si la palabra resaltada queda fuera de la zona ampliada, hay que mover la vista.
- **Con ventanas de administrador no funciona** si el lector no corre también como administrador: Windows bloquea leer otra ventana elevada y enviarle teclas.

## Compilar
```
build.cmd
```
Genera `LectorKaraoke.exe`, `Configurar.exe` y las pruebas. Usa el `csc.exe` de .NET Framework 4.8 que trae Windows.

## Pruebas
- `tests\Pruebas.exe`: pruebas unitarias de preparación del texto (símbolos, cifras, URLs, rutas, tablas, Claude Code), secretos, trozos, alineación de tiempos y configuración.
- `tests\PruebaMotores.exe`: síntesis real con los tres motores, cambio de motor ante fallos, cancelación, velocidad y reproducción.
- `tests\integracion.ps1`: pruebas reales con el lector en modo `--prueba`. Cubre el Bloc de notas, Windows Terminal, la terminal de Orca, el modo mouse, el texto largo, las pulsaciones rápidas, cerrar la ventana a mitad de lectura y el resaltado en varios monitores con distinta escala. Verifica los píxeles naranjas en pantalla y espera a que no se esté usando el equipo.

La matriz completa de casos está en [`docs/PLAN.md`](docs/PLAN.md) y los resultados en [`docs/PRUEBAS.md`](docs/PRUEBAS.md).

## Licencias y créditos
- **Lector Karaoke:** licencia MIT (ver `LICENSE`).
- **Componentes que descarga `Configurar.exe`**, con sus propias licencias:
  - [edge-tts](https://github.com/rany2/edge-tts): LGPL-3.0;
  - [Piper](https://github.com/rhasspy/piper): MIT;
  - la voz [es_MX-ald-medium](https://huggingface.co/rhasspy/piper-voices): Unlicense;
  - [Python](https://www.python.org/): PSF.
- No está afiliado con Microsoft. Las voces neuronales de Edge son de Microsoft.
