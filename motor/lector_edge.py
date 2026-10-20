"""Motor de voz de Lector Karaoke (voces neuronales de Microsoft Edge vía la librería edge-tts).

Protocolo: una petición JSON por línea en stdin y una respuesta JSON por línea en stdout.
  {"accion": "hablar", "id": 7, "texto": "...", "voz": "es-MX-JorgeNeural",
   "velocidad": "+0%", "volumen": "+0%", "archivo": "C:\\...\\tmp\\t7.mp3"}
  -> {"id": 7, "ok": true, "marcas": [["Hola", 100.0, 300.0], ...], "ms": 812}
  -> {"id": 7, "ok": false, "error": "...", "tipo": "red" | "bloqueo" | "otro"}
  {"accion": "cancelar", "id": 7}
  {"accion": "ping", "id": 1} -> {"id": 1, "ok": true, "version": "7.3.1"}
Termina cuando se cierra stdin. No escribe nada más en stdout.
"""
import asyncio
import json
import os
import sys
import time

import edge_tts


def responder(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=True) + "\n")
    sys.stdout.flush()


def clasificar(exc):
    texto = f"{type(exc).__name__}: {exc}"
    estado = getattr(exc, "status", None)
    if estado in (401, 403, 429) or " 403" in texto or "403," in texto:
        return "bloqueo", texto
    nombres = type(exc).__name__ + " " + " ".join(t.__name__ for t in type(exc).__mro__)
    if any(n in nombres for n in ("ClientConnectorError", "ServerTimeoutError", "TimeoutError",
                                  "ClientOSError", "gaierror", "ConnectionError", "ServerDisconnectedError")):
        return "red", texto
    return "otro", texto


async def hablar(req):
    inicio = time.monotonic()
    archivo = req["archivo"]
    temporal = archivo + ".parcial"
    marcas = []
    try:
        com = edge_tts.Communicate(
            req["texto"], req.get("voz", "es-MX-JorgeNeural"),
            rate=req.get("velocidad", "+0%"), volume=req.get("volumen", "+0%"),
            boundary="WordBoundary", connect_timeout=5, receive_timeout=15)
        with open(temporal, "wb") as f:
            async for parte in com.stream():
                if parte["type"] == "audio":
                    f.write(parte["data"])
                elif parte["type"] == "WordBoundary":
                    marcas.append([parte["text"], parte["offset"] / 1e4, parte["duration"] / 1e4])
        os.replace(temporal, archivo)
        responder({"id": req["id"], "ok": True, "marcas": marcas,
                   "ms": int((time.monotonic() - inicio) * 1000)})
    except asyncio.CancelledError:
        _borrar(temporal)
        responder({"id": req["id"], "ok": False, "error": "cancelado", "tipo": "cancelado"})
    except Exception as exc:  # noqa: BLE001 - se informa cualquier fallo al lector
        _borrar(temporal)
        tipo, texto = clasificar(exc)
        responder({"id": req["id"], "ok": False, "error": texto[:400], "tipo": tipo})


def _borrar(ruta):
    try:
        os.remove(ruta)
    except OSError:
        pass


async def principal():
    bucle = asyncio.get_running_loop()
    tareas = {}
    while True:
        linea = await bucle.run_in_executor(None, sys.stdin.readline)
        if not linea:
            break
        linea = linea.strip()
        if not linea:
            continue
        try:
            req = json.loads(linea)
        except ValueError:
            continue
        accion = req.get("accion")
        if accion == "ping":
            responder({"id": req.get("id", 0), "ok": True, "version": getattr(edge_tts, "__version__", "?")})
        elif accion == "cancelar":
            tarea = tareas.pop(req.get("id"), None)
            if tarea is not None:
                tarea.cancel()
        elif accion == "hablar":
            tarea = asyncio.create_task(hablar(req))
            tareas[req["id"]] = tarea
            tarea.add_done_callback(lambda _t, i=req["id"]: tareas.pop(i, None))
    for tarea in list(tareas.values()):
        tarea.cancel()


if __name__ == "__main__":
    try:
        asyncio.run(principal())
    except KeyboardInterrupt:
        pass
