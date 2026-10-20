using System;
using Microsoft.Win32;

namespace LectorKaraoke
{
    // Arranque con la sesión de Windows (no es un servicio: los servicios no pueden dibujar ni recibir teclas).
    internal static class Inicio
    {
        private const string Clave = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string Nombre = "LectorKaraoke";

        public static void Registrar(string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Clave)) k.SetValue(Nombre, "\"" + exe + "\"");
        }

        public static void Quitar()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Clave, true)) if (k != null) k.DeleteValue(Nombre, false);
        }

        public static string Actual()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Clave)) return k == null ? null : k.GetValue(Nombre) as string;
        }

        // Si la carpeta se movió, la clave de inicio apunta a la ruta vieja: se corrige sola (solo si ya existía).
        public static void Reparar(string exe)
        {
            try
            {
                string v = Actual();
                if (v != null && !v.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase))
                {
                    Registrar(exe);
                    Registro.Info("Clave de inicio corregida a " + exe);
                }
            }
            catch (Exception e) { Registro.Error("Revisando la clave de inicio", e); }
        }
    }
}
