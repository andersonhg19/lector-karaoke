# Lector Karaoke: plan de diseño y matriz de casos

Fecha: 20-10-2026. Este documento se escribió **antes** de programar. Es la checklist contra la que se prueba.

## 1. Qué tiene que hacer
Una sola tecla (por defecto **F9**), en cualquier aplicación de Windows 11:

| Situación al presionar F9 | Qué hace |
|---|---|
| Hay texto seleccionado | Lo lee en voz alta y resalta en naranja, **sobre la misma aplicación**, cada palabra mientras la dice; las ya leídas quedan con un naranja más suave (karaoke). |
| No hay selección | Lee el elemento que está bajo el puntero del mouse: su texto de ayuda (tooltip) si está visible, si no su nombre y descripción, y lo enmarca en naranja. |
| Ya está leyendo | Se detiene. Es la misma tecla: no se usa Esc, porque Esc detiene a Claude Code. |

Nunca habla solo. No abre ventanas propias: solo un icono en la bandeja, una capa transparente que no recibe clics y, cuando la aplicación no da la posición de las palabras, una franja tipo subtítulo junto al puntero que desaparece al terminar.

## 2. Restricciones del usuario
- **Portable:** todo vive en una carpeta (por defecto `C:\LectorKaraoke`), sin instalador del sistema ni permisos de administrador. Para llevarla a otro PC basta copiar la carpeta y ejecutar `Configurar.exe`.
- **Liviano:** en reposo no consume CPU (nada de sondeos) y la memoria se mantiene baja. El motor de voz en Python solo vive mientras se usa y se cierra tras unos minutos sin uso.
- **Configurable en caliente:** `config.ini` con color, velocidad, voz, tecla, etc. Al guardar el archivo se recarga solo; además hay un ejecutable para reiniciar.
- **Voz Jorge:** `es-MX-JorgeNeural` (voces neuronales de Microsoft Edge, mediante la librería libre `edge-tts`). Sin internet, cae a **Piper** (motor libre y local, voz `es_MX-ald-medium`, licencia Unlicense). Si Piper falta, cae a SAPI (Sabina o Helena).
- **Teclas:** se evita F7 (la usa Orca); el lector usa F9, configurable.

## 3. Por qué no es un "servicio de Windows"
Un servicio corre en la sesión 0: no puede dibujar sobre el escritorio, no recibe teclas globales y no lee la interfaz del usuario. Por eso el lector es una aplicación de bandeja que arranca con la sesión del usuario (clave `HKCU\...\Run`). `Configurar.exe` la registra, la repara o la reinicia.

## 4. Arquitectura
```
LectorKaraoke.exe  (C# / .NET Framework 4.8, ya incluido en Windows 11; sin dependencias)
 ├─ Tecla global (RegisterHotKey, sin repetición)
 ├─ Captura  ── 1) UI Automation TextPattern: selección + posición exacta de cada palabra
 │             2) Copia controlada por aplicación (atajo seguro según el programa) → solo texto
 │             3) Sin selección → elemento bajo el mouse / tooltip visible
 ├─ Preparación del texto (limpieza, secretos, identificadores, trozos) con mapa de posiciones
 ├─ Motores de voz: Edge (Jorge, online) → Piper (local) → SAPI (local)
 │     cada motor devuelve audio + tiempo de cada palabra (exacto en Edge y SAPI, estimado en Piper)
 ├─ Reproductor (MCI de Windows) con posición en milisegundos
 └─ Capa de resaltado (ventana por capas, transparente a clics, consciente del DPI por monitor)
motor\python\        Python embebido 3.12 + edge-tts 7.3.1 (descargado por Configurar.exe)
motor\lector_edge.py  servidor JSON por stdin/stdout: texto → mp3 + marcas de palabra
piper\               piper.exe + voz es_MX-ald-medium (descargados por Configurar.exe)
config.ini, logs\, tmp\
```

## 5. Matriz de casos (cada fila es una prueba)

### 5.1 Tecla y estados
| # | Caso | Resultado esperado |
|---|---|---|
| T1 | F9 en reposo con selección | Lee y resalta |
| T2 | F9 mientras lee | Se detiene en < 150 ms y desaparece el resaltado |
| T3 | F9 mientras todavía está capturando o sintetizando | Cancela: no empieza a hablar después |
| T4 | Tecla mantenida (autorepetición) | Un solo disparo (`MOD_NOREPEAT`) |
| T5 | Dos pulsaciones en < 250 ms | Se ignora la segunda (antirrebote) |
| T6 | F9 ya registrada por otro programa | Aviso en la bandeja y en el log; no se cae |
| T7 | Cambio de tecla en config.ini | Se libera la anterior y se registra la nueva sin reiniciar |
| T8 | Escritorio seguro, UAC o bloqueo | No recibe la tecla; no hace nada (correcto) |

### 5.2 Captura del texto
| # | Caso | Resultado esperado |
|---|---|---|
| C1 | Bloc de notas, Word, Edge o Chrome con selección | Vía UIA, con posición de palabras |
| C2 | Windows Terminal y consola clásica con selección | Vía UIA, con posición de palabras |
| C3 | Terminal de Orca (xterm, sin UIA de texto) | Copia con `Ctrl+Shift+C` (atajo de Orca); **nunca** `Ctrl+C`, que sin selección interrumpe a Claude |
| C4 | Orca sin selección | El atajo no copia nada → pasa a leer bajo el mouse; se verifica que **no** llegue `^C` al proceso |
| C5 | VS Code | `Ctrl+Insert` (copia en editor; en terminal no interrumpe) |
| C6 | Otras terminales sin UIA (mintty, PuTTY, Alacritty, WezTerm…) | No se envía ninguna tecla; lee bajo el mouse |
| C7 | Aplicación normal sin UIA de texto | `Ctrl+C` (sin selección no hace daño) |
| C8 | Campo de contraseña | No se lee nunca (IsPassword) |
| C9 | Ventana elevada (administrador) | UIA y teclas bloqueadas por Windows: aviso sonoro, sin enviar teclas |
| C10 | Selección vacía o solo espacios | Pasa a modo mouse |
| C11 | Selección enorme | Se limita a 30.000 caracteres y se avisa en el log |
| C12 | Varias selecciones (UIA devuelve varios rangos) | Se leen en orden |
| C13 | El portapapeles está bloqueado por otro programa | Reintentos cortos; si falla, no se toca nada y se informa |
| C14 | Portapapeles previo con imagen, archivos o formatos propios | Se guarda y se restaura tal cual tras copiar |
| C15 | Historial del portapapeles (Win+V) o portapapeles en la nube | La copia hecha por la otra app puede quedar en el historial (límite de Windows); la restauración del lector se marca para excluirse del historial. Documentado |

### 5.3 Modo mouse (sin selección)
| # | Caso | Resultado esperado |
|---|---|---|
| M1 | Tooltip visible sobre un botón | Lee el texto del tooltip y lo enmarca |
| M2 | Botón sin tooltip | Lee nombre + descripción (sin repetir si son iguales) |
| M3 | Elemento sin nombre | Prueba valor y ayuda; si nada, aviso sonoro corto |
| M4 | Puntero sobre el propio resaltado | La capa es transparente a UIA y clics: se lee lo de abajo |

### 5.4 Preparación del texto
| # | Caso | Resultado esperado |
|---|---|---|
| P1 | Códigos ANSI, cajas (─│┌), spinners, líneas `=====` | Se omiten al hablar, el mapa de posiciones se mantiene |
| P2 | Markdown (`**`, `` ` ``, `#`, viñetas) | Se omite la marca y se lee el contenido |
| P3 | Identificadores `getUserById`, `snake_case`, `kebab-case` | Se leen separados |
| P4 | URLs | Se lee "enlace" + dominio |
| P5 | Rutas `C:\Users\x\archivo.java` | Se leen por segmentos, sin decir "barra" cada vez |
| P6 | **Secretos** (tokens, claves, JWT, `password=`, cadenas largas hex/base64, URIs con usuario:clave) | Se reemplazan por "valor oculto" y **nunca** se envían a internet |
| P7 | Emojis y símbolos sueltos | Se omiten |
| P8 | Solo símbolos | Nada que leer → aviso sonoro |
| P10 | Símbolos (`simbolos = algunos`) | Comillas, paréntesis, llaves, corchetes y `/ \ \| # $ % & = @ * ¨ ~ ^ + < >` se nombran; la puntuación normal es pausa; markdown y líneas decorativas no se leen |
| P9 | Trozos | El primer trozo es corto para que empiece a hablar rápido; los demás, por oración (≤ 280 caracteres) |

### 5.5 Voz
| # | Caso | Resultado esperado |
|---|---|---|
| V1 | Con internet | Jorge, marcas exactas por palabra |
| V2 | Sin internet o DNS caído | Cae a Piper en < 3 s y marca Edge como no disponible 60 s |
| V3 | Microsoft cambia el protocolo (403) | Cae a Piper; en segundo plano intenta actualizar edge-tts una vez al día; log claro |
| V4 | El proceso Python se cae | Se relanza en la siguiente lectura |
| V5 | Falta Python o Piper | Usa el siguiente motor disponible |
| V6 | Mientras suena un trozo | Se sintetiza el siguiente (sin pausas largas entre trozos) |
| V7 | Las marcas de Edge no coinciden con las palabras (números, abreviaturas) | Alineación secuencial tolerante; si se pierde, interpola |
| V8 | Velocidad y volumen de config.ini | Se aplican a los tres motores |
| V9 | Sin dispositivo de audio o error MCI | Log + aviso sonoro; no se cuelga |

### 5.6 Resaltado
| # | Caso | Resultado esperado |
|---|---|---|
| R1 | Monitores con distinto escalado (DPI) | El naranja cae exactamente sobre la palabra |
| R2 | Palabra partida en dos líneas | Se dibujan los dos rectángulos |
| R3 | El texto se desplaza (llega salida nueva a la terminal) | Se recalcula la posición en cada palabra; si sale de pantalla, se oculta |
| R4 | La ventana se minimiza o se cierra | Se oculta el resaltado; la voz sigue hasta que se detenga |
| R5 | Lupa de Windows con zoom alto | La capa se ve aumentada como el resto de la pantalla |
| R6 | La capa nunca toma el foco, no aparece en Alt+Tab ni en la barra de tareas y no intercepta clics | Verificado |
| R7 | Sin posiciones (copia por portapapeles) | Franja tipo subtítulo junto al puntero con la palabra actual en naranja (desactivable) |

### 5.7 Configuración, arranque y portabilidad
| # | Caso | Resultado esperado |
|---|---|---|
| K1 | config.ini con un valor inválido | Se usa el valor por defecto y se avisa (bandeja + log) |
| K2 | Guardar config.ini | Se recarga solo en < 1 s |
| K3 | Segunda instancia | No arranca otra; `--reiniciar` cierra la anterior y arranca limpia |
| K4 | Carpeta movida a otra ruta | El propio lector corrige la clave de inicio al arrancar; `Configurar.exe` también |
| K5 | `Configurar.exe` con todo ya descargado | No vuelve a descargar (idempotente) |
| K6 | Descarga corrupta o incompleta | Verificación SHA-256: se descarta y se informa |
| K7 | Reposo | 0 % de CPU, sin Python en memoria tras el tiempo de espera |
| K8 | Logs | Nunca guardan el texto leído (salvo que se active a mano); se rotan a 1 MB |
| K9 | Archivos temporales | En `tmp\` de la carpeta; se limpian al terminar cada trozo y al arrancar |

## 6. Pruebas
1. **Unitarias** (sin interfaz): preparación del texto y mapa de posiciones, secretos, trozos, alineación de marcas, lectura del config, atajos de copia.
2. **Integración real en este PC:** Bloc de notas, Windows Terminal, terminal de Orca (con un proceso de prueba que confirma que no recibe `^C`), modo mouse con tooltip, portapapeles con formatos variados, motores Edge/Piper/SAPI, corte de red simulado, recarga del config, reinicio, carpeta movida.
3. **Verificación visual:** capturas de pantalla durante la lectura, buscando píxeles naranjas en la posición de la palabra esperada.
4. **Ronda adversarial final:** casos raros (pulsaciones rápidas, cerrar la ventana a mitad, apps elevadas, textos gigantes).
