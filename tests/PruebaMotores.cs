using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LectorKaraoke;

// Prueba real de los tres motores de voz, del paso de uno a otro y de la reproducción con posición (a volumen 0).
internal static class PruebaMotores
{
    private static int ok, fallos;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string baseDir = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        Directory.CreateDirectory(Path.Combine(baseDir, "tmp"));
        Registro.Iniciar(baseDir, "prueba-motores.log");
        var pythonAntes = Process.GetProcessesByName("python").Select(x => x.Id).ToList();   // el lector abierto tiene el suyo
        var motores = new Motores(baseDir);
        string texto = "Hola, esta es la prueba de los motores. Cada palabra debe tener su tiempo.";

        foreach (var motor in new[] { "edge", "piper", "sapi" })
        {
            var cfg = new Configuracion { Motor = motor };
            var t = Preparador.Preparar(texto, cfg).Trozos[0];
            var sw = Stopwatch.StartNew();
            Audio a = null;
            try { a = motores.Sintetizar(t, cfg, CancellationToken.None).Result; }
            catch (Exception e) { Console.WriteLine("  error: " + (e.InnerException ?? e).Message); }
            Cierto(motor + ": sintetiza (" + sw.ElapsedMilliseconds + " ms)", a != null && a.Motor == motor && File.Exists(a.Archivo));
            if (a == null) continue;
            Cierto(motor + ": todas las palabras con tiempo", t.Palabras.All(p => p.InicioMs >= 0));
            Cierto(motor + ": tiempos en orden", Enumerable.Range(1, t.Palabras.Count - 1).All(i => t.Palabras[i].InicioMs >= t.Palabras[i - 1].InicioMs));
            Cierto(motor + ": la última palabra empieza antes del final (" + (int)t.Palabras.Last().InicioMs + " < " + (int)a.DuracionMs + ")", t.Palabras.Last().InicioMs < a.DuracionMs);
            Console.WriteLine("    tiempos: " + string.Join(", ", t.Palabras.Select(p => p.Habla + "=" + (int)p.InicioMs)));
            Reproducir(a.Archivo, motor);
            MotorEdge.Borrar(a.Archivo);
        }

        // Sin internet permitido: edge se salta y responde piper.
        {
            var cfg = new Configuracion { Motor = "edge", UsarInternet = false };
            var t = Preparador.Preparar("prueba sin internet", cfg).Trozos[0];
            var a = motores.Sintetizar(t, cfg, CancellationToken.None).Result;
            Cierto("usar_internet=no: responde piper", a.Motor == "piper");
            MotorEdge.Borrar(a.Archivo);
        }
        // Voz de edge inexistente: falla edge y cae a piper sin colgarse.
        {
            var cfg = new Configuracion { Motor = "edge", VozEdge = "es-XX-NoExisteNeural" };
            var t = Preparador.Preparar("prueba de respaldo", cfg).Trozos[0];
            var sw = Stopwatch.StartNew();
            Audio a = null;
            try { a = motores.Sintetizar(t, cfg, CancellationToken.None).Result; } catch { }
            Cierto("voz edge inválida: cae a otro motor (" + (a != null ? a.Motor : "ninguno") + ", " + sw.ElapsedMilliseconds + " ms)", a != null && a.Motor != "edge");
            if (a != null) MotorEdge.Borrar(a.Archivo);
        }
        // Voz piper inexistente: cae a sapi.
        {
            var cfg = new Configuracion { Motor = "piper", VozPiper = "no-existe" };
            var t = Preparador.Preparar("respaldo final", cfg).Trozos[0];
            var a = motores.Sintetizar(t, cfg, CancellationToken.None).Result;
            Cierto("voz piper inexistente: responde sapi", a.Motor == "sapi");
            MotorEdge.Borrar(a.Archivo);
        }
        // Cancelación: no deja archivos ni se queda colgado.
        {
            var cfg = new Configuracion { Motor = "edge" };
            var t = Preparador.Preparar(string.Join(" ", Enumerable.Repeat("texto largo para cancelar", 10)), cfg).Trozos[0];
            var cts = new CancellationTokenSource();
            var tarea = motores.Sintetizar(t, cfg, cts.Token);
            Thread.Sleep(150);
            cts.Cancel();
            bool cancelado = false;
            try { tarea.Wait(5000); } catch (AggregateException e) { cancelado = e.InnerExceptions.Any(x => x is OperationCanceledException); }
            Cierto("cancelación responde en < 5 s", tarea.IsCompleted);
            Console.WriteLine("    (cancelado=" + cancelado + ", estado=" + tarea.Status + ")");
        }
        // Velocidad: más rápido = audio más corto.
        {
            var lento = new Configuracion { Motor = "edge", Velocidad = -30 };
            var rapido = new Configuracion { Motor = "edge", Velocidad = 50 };
            var t1 = Preparador.Preparar(texto, lento).Trozos[0];
            var t2 = Preparador.Preparar(texto, rapido).Trozos[0];
            var a1 = motores.Sintetizar(t1, lento, CancellationToken.None).Result;
            var a2 = motores.Sintetizar(t2, rapido, CancellationToken.None).Result;
            Cierto("velocidad: +50% dura menos que -30% (" + (int)t2.Palabras.Last().InicioMs + " < " + (int)t1.Palabras.Last().InicioMs + ")", t2.Palabras.Last().InicioMs < t1.Palabras.Last().InicioMs);
            MotorEdge.Borrar(a1.Archivo); MotorEdge.Borrar(a2.Archivo);
        }

        motores.Liberar();
        Thread.Sleep(500);
        Cierto("motor edge cerrado al liberar", !Process.GetProcessesByName("python").Any(p => { try { return !pythonAntes.Contains(p.Id) && p.MainModule.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase); } catch { return false; } }));
        Console.WriteLine();
        Console.WriteLine("MOTORES: " + ok + " ok, " + fallos + " fallos");
        return fallos;
    }

    private static void Reproducir(string archivo, string motor)
    {
        using (var r = new Reproductor())
        {
            bool abre = r.Abrir(archivo, 0);
            Cierto(motor + ": MCI abre el audio", abre);
            if (!abre) return;
            r.Reproducir();
            Thread.Sleep(700);
            double p1 = r.Posicion();
            bool sonando = r.Sonando();
            Thread.Sleep(400);
            double p2 = r.Posicion();
            Cierto(motor + ": reproduce y la posición avanza (" + (int)p1 + " -> " + (int)p2 + ")", sonando && p2 > p1 && p1 > 0);
            r.Cerrar();
        }
    }

    private static void Cierto(string n, bool c) { if (c) { ok++; Console.WriteLine("  OK    " + n); } else { fallos++; Console.WriteLine("  FALLA " + n); } }
}
