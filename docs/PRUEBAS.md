# Resultados de pruebas (20-10-2026)

Equipo: Windows 11, 3 monitores (dos al 100 % y uno al 125 %), Orca 1.4.224, Windows Terminal, Bloc de notas de Windows 11.

| Batería | Resultado |
|---|---|
| Unitarias (`tests\Pruebas.exe`) | **122 / 122** (incluye 36 de lectura de símbolos) |
| Motores de voz y reproducción (`tests\PruebaMotores.exe`) | **24 / 24** |
| Integración real en pantalla (`tests\integracion.ps1 -Caso todos`) | **44 / 44**, y 16 / 16 en el caso del Bloc de notas con símbolos y velocidad +20 % |
| Configuración en caliente, conflicto de teclas, instancia única, reinicio | 6 / 6 (manual guiado) |
| Portabilidad (carpeta copiada a otra ruta) y autocorrección de la clave de inicio | 2 / 2 |

## Qué cubre la integración
- **Bloc de notas:**
  - selección por UI Automation y voz de Jorge;
  - el secreto (`password=…`) se dice como "valor oculto";
  - el identificador `getUserById` se lee separado;
  - naranja verificado por píxeles sobre la palabra;
  - el resaltado avanza palabra por palabra y desaparece al terminar;
  - F9 detiene.
- **Modo mouse:** lee el nombre del botón bajo el puntero (también en apps XAML, donde la consulta por coordenadas devuelve la ventana) y lo enmarca en naranja.
- **Windows Terminal:** selección por UI Automation, posición de palabras y resaltado sobre la terminal.
- **Terminal de Orca**, con un proceso que registra cada tecla recibida:
  - sin selección, el lector **no envía `^C`** (verificado: el proceso no recibió ninguna interrupción);
  - con selección, copia con `Ctrl+Shift+C`, lee y **deja el portapapeles exactamente como estaba**;
  - la franja tipo subtítulo es visible.
- **Adversarial:**
  - un texto de 30.000 caracteres empieza a hablar en menos de 1 s;
  - dos pulsaciones en 60 ms detienen una sola vez;
  - al cerrar la ventana a mitad de lectura no queda naranja en pantalla y el lector sigue vivo;
  - el resaltado es exacto en los 3 monitores, incluido el de 125 %.

## Defectos encontrados y corregidos durante las pruebas
| Defecto | Corrección |
|---|---|
| El relleno naranja quedaba por encima de la palabra en apps que informan el rectángulo de la línea con interlineado | Se ajusta el alto del relleno al de las letras, estimado por el ancho medio de cada carácter |
| Una capa podía quedar pegada en pantalla (Windows Forms no registraba que estaba visible) | Se oculta también por la vía nativa (`ShowWindow`) |
| En el Bloc de notas de Windows 11 (XAML) el modo mouse leía el título de la ventana | Se busca el elemento más pequeño bajo el puntero que tenga nombre |
| En selecciones largas, las palabras fuera de la vista no se resaltaban | Nueva opción `seguir_lectura`: la aplicación se desplaza hasta la palabra que se lee |
| Si la tecla nueva estaba ocupada, el lector se quedaba sin tecla | Se conserva la anterior y se avisa |
| Una aplicación colgada podía bloquear el hilo de accesibilidad | Tiempo límite por consulta; si se excede, se usa un hilo nuevo |
| Si el motor de Edge se caía al arrancar, se reintentaba (con demora) en cada trozo | Se marca como no disponible 30 s y se usa la voz local |
| `Configurar.exe` dejaba la consola "colgada" para quien leyera su salida | El lector se lanza desacoplado de la consola |
| La espera inicial era de 1,2 a 2,2 s | Primer trozo corto y motor precalentado al presionar la tecla: 0,9 s con texto largo |

## Rendimiento medido
- **Reposo:** 0 ms de CPU en 10 s. Memoria del lector: unos 35 MB al iniciar y unos 60 MB después de leer. El motor Python (unos 43 MB) se cierra a los 5 minutos sin uso.
- **Jorge (Edge):** primera frase en 0,9 a 1,8 s según el estado del motor.
- **Piper:** 2,7 s por trozo, solo como respaldo sin internet.
- **SAPI:** 0,3 s.

## Cómo reproducir
```
build.cmd
tests\Pruebas.exe
tests\PruebaMotores.exe C:\LectorKaraoke
LectorKaraoke.exe --reiniciar --prueba
powershell -ExecutionPolicy Bypass -File tests\integracion.ps1 -Caso todos -Espacio <carpeta de un espacio de trabajo de Orca>
```
La integración abre el Bloc de notas, Windows Terminal y una pestaña de Orca, envía F9 y toma capturas. Espera a que no se use el teclado ni el mouse durante 12 s antes de cada caso.
