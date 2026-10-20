# Evaluación de voces (20-10-2026)

Ninguna fuente pública compara las voces multilingües de Microsoft hablando español, así que se midieron directamente. Se evaluaron 17 voces de Edge (las 12 multilingües y 5 nativas en español) con 8 frases: 5 en español, 2 en inglés y 1 mezclada.

## Métricas
- **Suena nativo:** probabilidad que Whisper large-v3 asigna al idioma correcto **sin darle pistas**. Un acento extranjero la baja.
- **Calidad (DNSMOS P.835):** modelo de Microsoft que estima la nota media de oyentes humanos. Se usa en relativo.
- **Inteligibilidad (CER):** error por carácter de la transcripción con Whisper. Todas las voces quedaron por debajo del 1 % en español, así que esta métrica no las diferencia.

## Resultados
| Voz | Español nativo | Inglés nativo | DNSMOS |
|---|---|---|---|
| **fr-FR-RemyMultilingualNeural** (predeterminada) | **99,9 %** | **99,9 %** | **3,43** |
| en-US-AvaMultilingualNeural | 99,9 % | 99,9 % | 3,36 |
| en-US-EmmaMultilingualNeural | 99,9 % | 99,8 % | 3,29 |
| de-DE-FlorianMultilingualNeural | 99,8 % | 99,8 % | 3,40 |
| es-MX-JorgeNeural | 99,8 % | 99,8 % | 3,37 |
| ko-KR-HyunsuMultilingualNeural | 99,7 % | 99,9 % | 3,44 |
| de-DE-SeraphinaMultilingualNeural | 99,9 % | 99,0 % | 3,39 |
| en-US-BrianMultilingualNeural | 99,0 % | 99,7 % | 3,35 |
| en-US-AndrewMultilingualNeural | 98,5 % | 100 % | 3,45 |
| es-ES-AlvaroNeural | 99,9 % | **36,9 %** | 3,45 |
| es-ES-ElviraNeural | 99,9 % | **32,7 %** | 3,39 |
| es-CO-GonzaloNeural | 100 % | **16,2 %** | 3,42 |

## Conclusión
- **Las voces nativas en español leen el inglés con fonética española**: Whisper apenas reconoce como inglés su lectura de una frase en inglés.
- **Las multilingües suenan nativas en los dos idiomas.** De ellas, **Rémy** es la mejor voz masculina: nativa en ambos idiomas y segunda en calidad. Por eso es la predeterminada y no hace falta cambiar de voz para el inglés (`voz_ingles` vacío).
- **Alternativa femenina:** Ava.

`motor\muestras.py` genera muestras de las candidatas para escucharlas antes de elegir.

## Cifras: por qué `numeros = auto`
- **Los lectores de pantalla (NVDA, JAWS, Narrador) no reinterpretan las cifras.** Pasan el texto al sintetizador, que aplica la convención de su idioma. En la convención española `25.000` es veinticinco mil y `25,000` sería un decimal, que la voz lee "veinticinco coma cero cero cero".
- **En textos técnicos y de terminal**, `25,000` casi siempre significa veinticinco mil (convención inglesa).
- **La regla `auto`** trata un solo separador seguido de exactamente 3 cifras (sin cero inicial) como miles. Acierta en ambas convenciones; solo falla con decimales de exactamente 3 cifras.
- **`numeros = literal`** vuelve al comportamiento clásico.
