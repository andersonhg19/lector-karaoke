using System;
using System.Globalization;
using System.Text;

namespace LectorKaraoke
{
    // Reproduce mp3/wav con MCI (incluido en Windows) y permite preguntar la posición en milisegundos.
    // Se usa siempre desde el hilo de la interfaz.
    internal sealed class Reproductor : IDisposable
    {
        private string alias;
        private static int contador;
        public string UltimoError = "";

        public bool Abrir(string archivo, int volumen)
        {
            Cerrar();
            alias = "lk" + (++contador);
            if (!Mci("open \"" + archivo + "\" type mpegvideo alias " + alias)) { alias = null; return false; }
            Mci("set " + alias + " time format milliseconds");
            Mci("setaudio " + alias + " volume to " + Math.Max(0, Math.Min(1000, volumen * 10)));
            return true;
        }

        public bool Reproducir() { return alias != null && Mci("play " + alias); }

        // Empieza en desdeMs (se salta el silencio inicial que traen los audios de la voz).
        public bool Reproducir(double desdeMs)
        {
            if (alias == null) return false;
            if (desdeMs < 20) return Reproducir();
            return Mci("play " + alias + " from " + ((int)desdeMs).ToString(CultureInfo.InvariantCulture)) || Reproducir();
        }

        public double Posicion()
        {
            if (alias == null) return 0;
            var sb = new StringBuilder(64);
            if (Nativo.mciSendString("status " + alias + " position", sb, sb.Capacity, IntPtr.Zero) != 0) return 0;
            double ms;
            return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out ms) ? ms : 0;
        }

        public bool Sonando()
        {
            if (alias == null) return false;
            var sb = new StringBuilder(64);
            if (Nativo.mciSendString("status " + alias + " mode", sb, sb.Capacity, IntPtr.Zero) != 0) return false;
            string m = sb.ToString();
            return m == "playing" || m == "seeking";
        }

        public void Cerrar()
        {
            if (alias == null) return;
            Nativo.mciSendString("stop " + alias, null, 0, IntPtr.Zero);
            Nativo.mciSendString("close " + alias, null, 0, IntPtr.Zero);
            alias = null;
        }

        private bool Mci(string cmd)
        {
            int r = Nativo.mciSendString(cmd, null, 0, IntPtr.Zero);
            if (r == 0) return true;
            var sb = new StringBuilder(256);
            Nativo.mciGetErrorString(r, sb, sb.Capacity);
            UltimoError = cmd.Split(' ')[0] + ": " + sb;
            Registro.Aviso("MCI " + UltimoError);
            return false;
        }

        public void Dispose() { Cerrar(); }
    }
}
