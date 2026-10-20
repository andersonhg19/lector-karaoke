using System;
using System.Collections.Generic;

namespace LectorKaraoke
{
    internal struct Marca
    {
        public string Texto;
        public double InicioMs;
        public double DuracionMs;
        public Marca(string t, double i, double d) { Texto = t; InicioMs = i; DuracionMs = d; }
    }

    // Asigna a cada palabra el momento en que suena.
    internal static class Tiempos
    {
        // Edge devuelve una marca por palabra pronunciada; puede no coincidir 1 a 1 con nuestras palabras
        // (números, abreviaturas, identificadores partidos). Se alinea en orden con tolerancia y lo que falte se interpola.
        public static void Alinear(Trozo t, IList<Marca> marcas, double duracionMs)
        {
            var sub = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < t.Palabras.Count; i++)
            {
                t.Palabras[i].InicioMs = -1;
                t.Palabras[i].FinMs = -1;
                foreach (var w in t.Palabras[i].Habla.Split(' '))
                {
                    string n = Preparador.Comparable(w);
                    if (n.Length > 0) sub.Add(new KeyValuePair<int, string>(i, n));
                }
            }
            int j = 0;
            int ultimaPalabra = -1;
            foreach (var m in marcas)
            {
                string mn = Preparador.Comparable(m.Texto);
                if (mn.Length == 0) continue;
                int hallado = -1;
                for (int k = j; k < sub.Count && k < j + 8; k++)
                {
                    if (Coincide(sub[k].Value, mn)) { hallado = k; break; }
                }
                if (hallado >= 0)
                {
                    var p = t.Palabras[sub[hallado].Key];
                    if (p.InicioMs < 0) p.InicioMs = m.InicioMs;
                    p.FinMs = Math.Max(p.FinMs, m.InicioMs + m.DuracionMs);
                    ultimaPalabra = sub[hallado].Key;
                    j = hallado + 1;
                }
                else if (ultimaPalabra >= 0)
                {
                    // Continuación de la palabra anterior (p. ej. "2026" dicho como varias marcas).
                    var p = t.Palabras[ultimaPalabra];
                    p.FinMs = Math.Max(p.FinMs, m.InicioMs + m.DuracionMs);
                }
            }
            Interpolar(t, duracionMs);
        }

        private static bool Coincide(string nuestra, string marca)
        {
            if (nuestra == marca) return true;
            if (marca.Length >= 3 && nuestra.Length >= 3 && (nuestra.StartsWith(marca) || marca.StartsWith(nuestra))) return true;
            if (char.IsDigit(nuestra[0]) && char.IsDigit(marca[0]) && nuestra[0] == marca[0]) return true;
            return false;
        }

        // Rellena las palabras sin tiempo repartiendo el hueco entre las vecinas según su largo.
        public static void Interpolar(Trozo t, double duracionMs)
        {
            var ps = t.Palabras;
            if (ps.Count == 0) return;
            int i = 0;
            while (i < ps.Count)
            {
                if (ps[i].InicioMs >= 0) { i++; continue; }
                int a = i;
                while (i < ps.Count && ps[i].InicioMs < 0) i++;
                int b = i; // [a, b) sin tiempo
                double desde = a > 0 ? Math.Max(ps[a - 1].FinMs, ps[a - 1].InicioMs) : 0;
                double hasta = b < ps.Count ? ps[b].InicioMs : Math.Max(duracionMs, desde + 1);
                if (hasta < desde) hasta = desde;
                double total = 0;
                for (int k = a; k < b; k++) total += Peso(ps[k].Habla);
                double cur = desde;
                for (int k = a; k < b; k++)
                {
                    double d = total > 0 ? (hasta - desde) * Peso(ps[k].Habla) / total : 0;
                    ps[k].InicioMs = cur;
                    ps[k].FinMs = cur + d;
                    cur += d;
                }
            }
            // Monotonía y fin mínimo.
            for (int k = 0; k < ps.Count; k++)
            {
                if (k > 0 && ps[k].InicioMs < ps[k - 1].InicioMs) ps[k].InicioMs = ps[k - 1].InicioMs;
                if (ps[k].FinMs < ps[k].InicioMs) ps[k].FinMs = ps[k].InicioMs;
            }
        }

        // Piper no da marcas: se estima por sílabas dentro de la parte con voz del audio.
        public static void Estimar(Trozo t, double vozInicioMs, double vozFinMs)
        {
            var ps = t.Palabras;
            if (ps.Count == 0) return;
            var pesos = new double[ps.Count];
            var pausas = new double[ps.Count];
            double total = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                pesos[i] = Peso(ps[i].Habla);
                char u = ps[i].Habla[ps[i].Habla.Length - 1];
                pausas[i] = (u == ',' || u == ';' || u == ':') ? 1.5 : (u == '.' || u == '!' || u == '?') ? 2.5 : 0;
                if (i == ps.Count - 1) pausas[i] = 0;
                total += pesos[i] + pausas[i];
            }
            double escala = total > 0 ? (vozFinMs - vozInicioMs) / total : 0;
            double cur = vozInicioMs;
            for (int i = 0; i < ps.Count; i++)
            {
                ps[i].InicioMs = cur;
                cur += pesos[i] * escala;
                ps[i].FinMs = cur;
                cur += pausas[i] * escala;
            }
        }

        // Aproximación de sílabas: grupos de vocales; los dígitos cuentan más porque se pronuncian largos.
        public static double Peso(string habla)
        {
            double s = 0;
            bool enVocal = false;
            foreach (char c0 in habla.ToLowerInvariant())
            {
                char c = c0;
                if (char.IsDigit(c)) { s += 1.6; enVocal = false; continue; }
                bool v = "aeiouáéíóúüy".IndexOf(c) >= 0;
                if (v && !enVocal) s += 1;
                enVocal = v;
                if (c == ' ') s += 0.15;
            }
            return Math.Max(1, s);
        }

        // Índice de la palabra que suena en la posición dada (o la última que ya empezó).
        public static int PalabraEn(Trozo t, double ms)
        {
            var ps = t.Palabras;
            int idx = -1;
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].InicioMs <= ms + 30) idx = i; else break;
            }
            return idx;
        }
    }
}
