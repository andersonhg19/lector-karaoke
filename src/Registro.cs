using System;
using System.IO;
using System.Text;

namespace LectorKaraoke
{
    // Log en logs\lector.log, rotado a 1 MB. Nunca guarda el texto leído salvo que se active en config.ini.
    internal static class Registro
    {
        private static readonly object candado = new object();
        private static string archivo;

        public static void Iniciar(string carpetaBase, string nombre)
        {
            try
            {
                string dir = Path.Combine(carpetaBase, "logs");
                Directory.CreateDirectory(dir);
                archivo = Path.Combine(dir, nombre);
            }
            catch { archivo = null; }
        }

        public static void Info(string msg) { Escribir("INFO ", msg); }
        public static void Aviso(string msg) { Escribir("AVISO", msg); }
        public static void Error(string msg, Exception e)
        {
            Escribir("ERROR", msg + (e != null ? " | " + e.GetType().Name + ": " + e.Message : ""));
        }

        private static void Escribir(string nivel, string msg)
        {
            if (archivo == null) return;
            lock (candado)
            {
                try
                {
                    var fi = new FileInfo(archivo);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string viejo = archivo + ".1";
                        if (File.Exists(viejo)) File.Delete(viejo);
                        File.Move(archivo, viejo);
                    }
                    File.AppendAllText(archivo, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + nivel + " " + msg + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }
}
