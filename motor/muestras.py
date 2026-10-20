"""Genera muestras de voces en la carpeta muestras\\ para comparar antes de elegir voz_edge / voz_ingles.

Uso: motor\\python\\python.exe -I motor\\muestras.py
Crea un mp3 por voz y una lista muestras.m3u (doble clic para escucharlas todas seguidas).
"""
import asyncio
import os
import sys

import edge_tts

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CARPETA = os.path.join(BASE, "muestras")

VOCES = [
    ("es-ES-AlvaroNeural", "Álvaro, de España"),
    ("es-ES-ElviraNeural", "Elvira, de España"),
    ("es-ES-XimenaNeural", "Ximena, de España"),
    ("es-MX-JorgeNeural", "Jorge, de México"),
    ("es-MX-DaliaNeural", "Dalia, de México"),
    ("es-CO-GonzaloNeural", "Gonzalo, de Colombia"),
    ("es-AR-TomasNeural", "Tomás, de Argentina"),
    ("es-HN-CarlosNeural", "Carlos, de Honduras"),
    ("es-US-AlonsoNeural", "Alonso, de Estados Unidos"),
    ("es-CL-LorenzoNeural", "Lorenzo, de Chile"),
    ("es-VE-SebastianNeural", "Sebastián, de Venezuela"),
    ("en-US-AndrewMultilingualNeural", "Andrew, multilingüe"),
    ("en-US-BrianMultilingualNeural", "Brian, multilingüe"),
    ("de-DE-FlorianMultilingualNeural", "Florian, multilingüe"),
    ("it-IT-GiuseppeMultilingualNeural", "Giuseppe, multilingüe"),
    ("fr-FR-RemyMultilingualNeural", "Rémy, multilingüe"),
]

FRASE = ("Hola, soy {nombre}. El total del pedido es de 25000 pesos con 50 centavos. "
         "El deploy falló porque el pull request no pasó los checks. "
         "Please review the build logs and try again.")


async def principal():
    os.makedirs(CARPETA, exist_ok=True)
    lista = []
    for i, (voz, nombre) in enumerate(VOCES, 1):
        archivo = os.path.join(CARPETA, f"{i:02d} - {voz}.mp3")
        try:
            await edge_tts.Communicate(FRASE.format(nombre=nombre), voz, rate="+30%").save(archivo)
            lista.append(os.path.basename(archivo))
            print("ok   ", voz)
        except Exception as e:  # noqa: BLE001
            print("falla", voz, e)
    with open(os.path.join(CARPETA, "muestras.m3u"), "w", encoding="utf-8") as f:
        f.write("#EXTM3U\n" + "\n".join(lista) + "\n")


if __name__ == "__main__":
    asyncio.run(principal())
    sys.exit(0)
