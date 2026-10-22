// Abyss Tide — agua ASCII 2D detrás de Windows Terminal que reacciona al movimiento de la ventana.
// Escrito en C# 5 para que Windows PowerShell 5.1 (Add-Type) lo compile sin instalar nada.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AbyssTide
{
    // ================================================================== configuración
    public class Config
    {
        // agua
        public double FillLevel = 0.36;          // fracción de la altura ocupada por agua
        public double Gravity = 2600;            // px/s² — más alto = olas más rápidas y bajas
        public double MoveSensitivity = 1.0;     // cuánto reacciona al mover la ventana
        public double Damping = 0.9;             // qué tan rápido se calma (por segundo)
        public double SprayAmount = 1.0;         // salpicaduras
        public double IdleDrips = 0.5;           // gotas por segundo cuando está quieta (0 = ninguna)
        public double TiltAmount = 0.05;         // inclinación del agua según la posición en la pantalla

        // aspecto
        public string Palette = "abyss";         // abyss, tropical, daynight, lava, toxic, matrix, synthwave, custom
        public string CharMode = "sea";          // "sea" = mar de caracteres, "ramp" = puntos por densidad
        public double BackgroundAlpha = 0.62;    // opacidad del fondo detrás del texto
        public double WaterAlpha = 0.80;         // opacidad de los caracteres del agua
        public string BackgroundColor = "#04101A";   // estos seis solo se usan con Palette = custom
        public string SurfaceColor = "#9BF0FF";
        public string DeepColor = "#1A5C9C";
        public string FoamColor = "#FFFFFF";
        public string SeaChars = "~=+*:;-<>/\\|(){}[]#%&$@?!^01234567890abcdefghijklmnopqrstuvwxyz≈";
        public string SurfaceChars = "~≈~-^≈~=";

        // vida marina
        public int Fish = 4;                     // peces por ventana (0 = ninguno)
        public bool Jellyfish = true;
        public bool Seafloor = true;             // arena y algas

        // interacción
        public bool TypingDrops = true;          // cada tecla deja caer una gota, Enter salpica
        public bool MouseRipples = true;         // el cursor mueve el agua
        public bool ShellSignals = true;         // los comandos que fallan tiñen el agua de rojo (requiere el hook del prompt)
        public bool FocusMode = false;           // atenuar el agua mientras escribes
        public double FocusDim = 0.35;
        public double FocusDelay = 2.5;          // segundos sin teclear para volver a la normalidad

        // rendimiento y ventana
        public bool EcoMode = true;              // baja los fps cuando el agua está en calma o con batería baja
        public int EcoFps = 15;
        public int Fps = 60;
        public string FontFamily = "Cascadia Mono";
        public double FontPx = 14;               // tamaño de los caracteres del agua (px a 96 dpi)
        public double TopInsetPx = 40;           // alto de la barra de pestañas (px a 96 dpi)
        public string WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

        static FieldInfo[] Fields()
        {
            return typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance);
        }

        public void CopyFrom(Config o)
        {
            foreach (FieldInfo f in Fields()) f.SetValue(this, f.GetValue(o));
        }

        public static Config Load(string path, out string error)
        {
            Config c = new Config();
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return c;
            try
            {
                Dictionary<string, object> d = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) throw new FormatException("el archivo no contiene un objeto JSON");
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (kv.Key.StartsWith("_") || kv.Value == null) continue;
                    FieldInfo f = typeof(Config).GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (f == null) continue;
                    try { f.SetValue(c, Convert.ChangeType(kv.Value, f.FieldType, CultureInfo.InvariantCulture)); }
                    catch { }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return c;
        }

        /// Guarda los valores actuales conservando las claves de comentario y el orden del archivo.
        public void Save(string path)
        {
            List<KeyValuePair<string, object>> entries = new List<KeyValuePair<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, object> d = File.Exists(path) ? Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object> : null;
                if (d != null)
                    foreach (KeyValuePair<string, object> kv in d)
                    {
                        FieldInfo f = kv.Key.StartsWith("_") ? null : typeof(Config).GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        entries.Add(new KeyValuePair<string, object>(kv.Key, f != null ? f.GetValue(this) : kv.Value));
                        if (f != null) seen.Add(f.Name);
                    }
            }
            catch { entries.Clear(); seen.Clear(); }
            foreach (FieldInfo f in Fields())
                if (!seen.Contains(f.Name)) entries.Add(new KeyValuePair<string, object>(f.Name, f.GetValue(this)));

            StringBuilder sb = new StringBuilder("{\n");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0 && !entries[i].Key.StartsWith("_") && entries[i - 1].Key.StartsWith("_")) sb.Append('\n');
                sb.Append("  ").Append(Json.Quote(entries[i].Key)).Append(": ").Append(Json.Format(entries[i].Value));
                sb.Append(i < entries.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    /// JSON mínimo (sin dependencias, funciona en .NET Framework y .NET).
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i < s.Length) throw new FormatException("texto sobrante en la posición " + i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON incompleto");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("falta ':' en la posición " + i);
                    i++;
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("falta ',' o '}' en la posición " + i);
                }
            }
            if (c == '[')
            {
                List<object> l = new List<object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("falta ',' o ']' en la posición " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("valor inesperado en la posición " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("se esperaba texto en la posición " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("texto sin cerrar");
        }

        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static string Format(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is string) return Quote((string)v);
            if (v is double || v is int || v is float)
                return Convert.ToDouble(v).ToString("0.####", CultureInfo.InvariantCulture);
            return Quote(v.ToString());
        }
    }

    public struct Rgb
    {
        public float R, G, B;
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }

        public static Rgb Parse(string hex, Rgb fallback)
        {
            try
            {
                string s = hex.Trim().TrimStart('#');
                int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new Rgb(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
            }
            catch { return fallback; }
        }

        public static Rgb Hex(string hex) { return Parse(hex, new Rgb(1, 0, 1)); }

        public static Rgb Lerp(Rgb a, Rgb b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        }
    }

    // ================================================================== paletas
    public class Theme
    {
        public Rgb Bg, Surface, Deep, Foam, Fish1, Fish2, Jelly, Weed, Sand;
        public string SeaChars, SurfChars;

        static readonly Dictionary<string, Theme> palettes = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        const string DefaultSea = "~=+*:;-<>/\\|(){}[]#%&$@?!^01234567890abcdefghijklmnopqrstuvwxyz≈";

        public static readonly string[] Names = { "abyss", "tropical", "daynight", "lava", "toxic", "matrix", "synthwave", "custom" };
        public static readonly string[] Labels = { "Abismo", "Tropical", "Día / noche (según la hora)", "Lava", "Tóxico", "Matrix", "Synthwave", "Personalizada (colores de la config)" };

        static Theme()
        {
            palettes["abyss"] = Make("#04101A", "#9BF0FF", "#1A5C9C", "#FFFFFF", DefaultSea, "~≈~-^≈~=");
            palettes["tropical"] = Make("#05293A", "#C4FFF7", "#1EA7CF", "#FFFFFF", DefaultSea, "~≈~-^≈~=");
            palettes["lava"] = Make("#140604", "#FFD27A", "#B3260B", "#FFF3C4", "~^*%#@&$=+!<>{}()[]", "^~*^~");
            palettes["toxic"] = Make("#06110A", "#C6FF6B", "#1F7A2E", "#F2FFD6", "01oO0#%=+~*.:;xX", "~o~≈");
            palettes["matrix"] = Make("#020804", "#B6FFB0", "#0F5A22", "#FFFFFF", "0101010110|:.=+*<>", "~≈-");
            palettes["synthwave"] = Make("#12061F", "#FF7AD9", "#3A1C8C", "#FFF0FF", DefaultSea, "~≈~-^≈~=");
            palettes["lava"].Weed = Rgb.Hex("#7A2A12"); palettes["lava"].Fish1 = Rgb.Hex("#FFF3C4");
            palettes["matrix"].Fish1 = Rgb.Hex("#E0FFE0"); palettes["matrix"].Fish2 = Rgb.Hex("#7CFF7C");
            palettes["matrix"].Jelly = Rgb.Hex("#A8FFA8"); palettes["matrix"].Weed = Rgb.Hex("#2E8B3A");
            palettes["synthwave"].Weed = Rgb.Hex("#7A4CFF"); palettes["synthwave"].Sand = Rgb.Hex("#FF9E6B");
        }

        static Theme Make(string bg, string surf, string deep, string foam, string sea, string surfc)
        {
            Theme t = new Theme();
            t.Bg = Rgb.Hex(bg); t.Surface = Rgb.Hex(surf); t.Deep = Rgb.Hex(deep); t.Foam = Rgb.Hex(foam);
            t.SeaChars = sea; t.SurfChars = surfc;
            t.Fish1 = Rgb.Hex("#FFB347"); t.Fish2 = Rgb.Hex("#FFE066"); t.Jelly = Rgb.Hex("#FF8AD8");
            t.Weed = Rgb.Hex("#3FBF7F"); t.Sand = Rgb.Hex("#C9B27C");
            return t;
        }

        public Theme Copy() { return (Theme)MemberwiseClone(); }

        public static Theme Lerp(Theme a, Theme b, float t)
        {
            Theme r = a.Copy();
            r.Bg = Rgb.Lerp(a.Bg, b.Bg, t); r.Surface = Rgb.Lerp(a.Surface, b.Surface, t);
            r.Deep = Rgb.Lerp(a.Deep, b.Deep, t); r.Foam = Rgb.Lerp(a.Foam, b.Foam, t);
            if (t >= 0.5f) { r.SeaChars = b.SeaChars; r.SurfChars = b.SurfChars; }
            return r;
        }

        public static Theme For(Config cfg, DateTime now)
        {
            string name = cfg.Palette ?? "abyss";
            Theme t;
            if (string.Equals(name, "custom", StringComparison.OrdinalIgnoreCase))
            {
                t = palettes["abyss"].Copy();
                t.Bg = Rgb.Parse(cfg.BackgroundColor, t.Bg); t.Surface = Rgb.Parse(cfg.SurfaceColor, t.Surface);
                t.Deep = Rgb.Parse(cfg.DeepColor, t.Deep); t.Foam = Rgb.Parse(cfg.FoamColor, t.Foam);
                if (!string.IsNullOrEmpty(cfg.SeaChars)) t.SeaChars = cfg.SeaChars;
                if (!string.IsNullOrEmpty(cfg.SurfaceChars)) t.SurfChars = cfg.SurfaceChars;
            }
            else if (string.Equals(name, "daynight", StringComparison.OrdinalIgnoreCase))
            {
                double hr = now.Hour + now.Minute / 60.0;
                double day = hr > 6 && hr < 20 ? Math.Pow(Math.Sin((hr - 6) / 14 * Math.PI), 0.6) : 0;
                t = Lerp(palettes["abyss"], palettes["tropical"], (float)day);
            }
            else if (!palettes.TryGetValue(name, out t)) t = palettes["abyss"];
            return t;
        }

        /// Tiñe el tema de rojo (comando fallido). a = 0..1
        public Theme WithAlarm(float a)
        {
            if (a <= 0.001f) return this;
            Theme r = Copy();
            r.Surface = Rgb.Lerp(Surface, Rgb.Hex("#FF8A80"), a);
            r.Deep = Rgb.Lerp(Deep, Rgb.Hex("#8C0F1E"), a);
            r.Foam = Rgb.Lerp(Foam, Rgb.Hex("#FFD6D2"), a);
            r.Bg = Rgb.Lerp(Bg, Rgb.Hex("#1E0408"), a * 0.7f);
            return r;
        }
    }

    // ================================================================== simulación
    internal class Particle
    {
        public double X, Y, VX, VY, Life, Phase;
        public int Kind;   // 0 = gota, 1 = burbuja
    }

    internal class Creature
    {
        public double X, Y, TargetY, Speed, Panic, Phase, Turn;
        public int Dir, Kind;   // Kind: 0 pez chico, 1 pez grande, 2 medusa
    }

    /// Agua vista de lado: ecuaciones de aguas someras 1D (alturas por columna) en el marco
    /// de referencia de la ventana. Acelerar la ventana genera una pseudo-fuerza opuesta.
    /// Coordenadas en píxeles; y crece hacia arriba desde el fondo.
    public class WaterSim
    {
        public int Cols, Rows;
        public double CellW, CellH, Width, Height;
        public double MaxSpeed;                  // |u| máxima del último paso (para el modo eco)
        public int ParticleCount { get { return parts.Count; } }

        double[] h, u, flux, hPrev, rise, foam, tmp, clock;
        int[] weed;
        readonly List<Particle> parts = new List<Particle>();
        readonly List<Creature> creatures = new List<Creature>();
        readonly Random rng = new Random();
        readonly Config cfg;
        double time, dripTimer, typeX = -1, mouseX = -1, mouseY = -1, mouseT = -10;

        // salida del render (Cols × Rows, fila 0 = arriba)
        public char[] Ch;
        public float[] R, G, B, A, Tint;

        static readonly string[] Sprites = { "><>", "<><", "><(((°>", "<°)))><" };
        const string JellyTop = "(@)";
        static readonly string[] JellyLegs = { "/|\\", "\\|/" };

        public WaterSim(Config cfg, double width, double height, double cellW, double cellH)
        {
            this.cfg = cfg;
            Resize(width, height, cellW, cellH);
        }

        public void Resize(double width, double height, double cellW, double cellH)
        {
            int newCols = Math.Max(4, (int)(width / cellW));
            int newRows = Math.Max(3, (int)(height / cellH));
            double[] nh = new double[newCols];
            double fill = Fill();
            double oldW = Width, oldH = Height;
            if (h == null)
            {
                for (int i = 0; i < newCols; i++) nh[i] = fill * newRows * cellH;
            }
            else
            {
                double scale = (newRows * cellH) / Math.Max(1, Height);
                for (int j = 0; j < newCols; j++)
                {
                    double x = (j + 0.5) / newCols * Cols - 0.5;
                    int i0 = (int)Math.Floor(x); double f = x - i0;
                    int a = Clamp(i0, 0, Cols - 1), b = Clamp(i0 + 1, 0, Cols - 1);
                    nh[j] = (h[a] * (1 - f) + h[b] * f) * scale;
                }
                parts.Clear();
            }
            Cols = newCols; Rows = newRows; CellW = cellW; CellH = cellH;
            Width = Cols * cellW; Height = Rows * cellH;
            h = nh;
            u = new double[Cols + 1]; flux = new double[Cols + 1]; tmp = new double[Cols + 1];
            hPrev = new double[Cols]; rise = new double[Cols]; foam = new double[Cols]; clock = new double[Cols];
            weed = new int[Cols];
            for (int i = 0; i < Cols; i++)
            {
                uint hs = Hash(i, 0, 99);
                weed[i] = (hs % 100 < 7 && i > 1 && i < Cols - 2) ? 2 + (int)((hs >> 8) % 4) : 0;
            }
            if (oldW > 0)
                foreach (Creature c in creatures) { c.X *= Width / oldW; c.Y *= Height / Math.Max(1, oldH); }
            typeX = -1;
            int n = Cols * Rows;
            Ch = new char[n]; R = new float[n]; G = new float[n]; B = new float[n]; A = new float[n]; Tint = new float[n];
        }

        double Fill() { return Math.Max(0.02, Math.Min(0.95, cfg.FillLevel)); }

        internal static uint Hash(int a, int b, int c)
        {
            unchecked
            {
                uint x = (uint)a * 374761393u + (uint)b * 668265263u + (uint)c * 2246822519u;
                x = (x ^ (x >> 13)) * 1274126177u;
                x ^= x >> 16;
                x *= 2654435761u;
                return x ^ (x >> 15);
            }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        double Rand(double a, double b) { return a + rng.NextDouble() * (b - a); }
        int ColAt(double x) { return Clamp((int)(x / CellW), 0, Cols - 1); }
        double SurfaceAt(double x) { return h[ColAt(x)]; }

        // ------------------------------------------------------------ interacción
        /// Una tecla en la terminal: una gota cae en la "posición de escritura"; Enter salpica fuerte.
        public void Key(bool enter, bool backspace)
        {
            if (typeX < 0) typeX = CellW * 2;
            if (enter)
            {
                for (int k = -1; k <= 1; k++)
                    AddDrop(Math.Max(0, typeX + k * CellW * 0.8), Height - CellH * (0.5 + Math.Abs(k) * 0.6), k * 40, -1100 - Rand(0, 200));
                typeX = CellW * 2;
            }
            else if (backspace)
            {
                typeX = Math.Max(CellW * 2, typeX - CellW);
                AddBubble(typeX, Math.Max(CellH, SurfaceAt(typeX) * 0.3));
            }
            else
            {
                AddDrop(typeX, Height - CellH * 0.5, 0, -260);
                typeX += CellW;
                if (typeX > Width - CellW * 2) typeX = CellW * 2;
            }
        }

        /// Mueve el agua con el cursor. x, y en px dentro de la capa (y desde abajo), velocidad en px/s.
        public void Stir(double x, double yb, double vx, double vy)
        {
            if (x < 0 || x >= Width || yb < 0) return;
            mouseX = x; mouseY = yb; mouseT = time;
            int col = ColAt(x);
            double surf = h[col];
            if (yb > surf + CellH * 1.5) return;
            double sp = Math.Sqrt(vx * vx + vy * vy);
            if (sp < 30) return;
            for (int j = -2; j <= 2; j++)
            {
                int c = col + j;
                if (c < 1 || c >= Cols) continue;
                double w = 1 - Math.Abs(j) / 3.0;
                // arrastra el agua hacia la velocidad del cursor (nunca la supera → estable)
                double target = Math.Max(-500, Math.Min(500, vx * 0.6));
                u[c] += (target - u[c]) * 0.12 * w;
            }
            if (yb > surf - CellH * 1.5)
            {
                foam[col] = Math.Min(1, foam[col] + sp / 3000);
                if (vy > 250 && rng.NextDouble() < 0.5) AddDrop(x, surf + 1, vx * 0.3, vy * 0.6);
            }
            else if (rng.NextDouble() < sp / 4000) AddBubble(x, yb);
        }

        /// Sacudida general (comando fallido, ventana encogida de golpe). s = 0..1
        public void Agitate(double s)
        {
            for (int i = 1; i < Cols; i++) u[i] += Rand(-1, 1) * 350 * s;
            int n = (int)(Cols * 0.25 * s * cfg.SprayAmount);
            for (int k = 0; k < n; k++)
            {
                int c = rng.Next(Cols);
                AddDrop((c + rng.NextDouble()) * CellW, h[c] + 1, Rand(-80, 80), Rand(200, 600) * s + 100);
            }
            for (int i = 0; i < Cols; i++) foam[i] = Math.Min(1, foam[i] + 0.4 * s);
        }

        /// Burbujas de celebración (comando correcto).
        public void Bubbles(int n)
        {
            for (int k = 0; k < n; k++)
            {
                double x = Rand(0.05, 0.95) * Width;
                AddBubble(x, Rand(0.05, 0.3) * SurfaceAt(x));
            }
        }

        // ------------------------------------------------------------ física
        /// ax, ay: aceleración de la ventana en px/s² (pantalla: +x derecha, +y abajo).
        /// tilt: inclinación constante (-1..1 × TiltAmount) según la posición en el monitor.
        public void Step(double dt, double ax, double ay, double tilt)
        {
            time += dt;
            double g = cfg.Gravity;
            double k = 0.28 * cfg.MoveSensitivity;
            double fx = -ax * k + tilt * g;            // pseudo-fuerza horizontal + inclinación
            double gRaw = g - ay * k;                   // ventana cae → menos gravedad
            double geff = Math.Max(0.25 * g, Math.Min(4 * g, gRaw));
            double lift = 0.25 * g - gRaw;              // "ingravidez" sobrante → el agua salta

            Array.Copy(h, hPrev, Cols);

            double dx = CellW;
            int sub = (int)Math.Ceiling(dt / 0.0025);
            if (sub < 1) sub = 1; if (sub > 40) sub = 40;
            double sdt = dt / sub;
            double damp = Math.Exp(-cfg.Damping * sdt);
            double maxU = 0.45 * dx / sdt;

            for (int s = 0; s < sub; s++)
            {
                for (int i = 1; i < Cols; i++)
                {
                    double v = u[i] + sdt * (-geff * (h[i] - h[i - 1]) / dx + fx);
                    if (h[i] < 0.5 && h[i - 1] < 0.5) v = 0;   // nada que mover
                    v *= damp;
                    if (v > maxU) v = maxU; else if (v < -maxU) v = -maxU;
                    u[i] = v;
                }
                u[0] = 0; u[Cols] = 0;
                // viscosidad numérica suave
                for (int i = 1; i < Cols; i++) tmp[i] = u[i] + 0.05 * (u[i - 1] - 2 * u[i] + u[i + 1]);
                for (int i = 1; i < Cols; i++) u[i] = tmp[i];

                for (int i = 1; i < Cols; i++) flux[i] = u[i] * (u[i] > 0 ? h[i - 1] : h[i]);
                flux[0] = 0; flux[Cols] = 0;
                for (int i = 0; i < Cols; i++)
                {
                    double nh = h[i] - sdt * (flux[i + 1] - flux[i]) / dx;
                    if (nh < 0) nh = 0;
                    if (nh > Height) nh = Height;
                    h[i] = nh;
                }
            }

            // conservar el volumen (y seguir FillLevel si cambia en vivo)
            double target = Fill() * Height * Cols;
            double vol = 0;
            for (int i = 0; i < Cols; i++) vol += h[i];
            if (vol > 1)
            {
                double f = 1 + (target / vol - 1) * 0.1;
                for (int i = 0; i < Cols; i++) h[i] *= f;
            }

            MaxSpeed = 0;
            for (int i = 0; i < Cols; i++)
            {
                rise[i] = (h[i] - hPrev[i]) / Math.Max(dt, 1e-4);
                double slope = Math.Abs(h[Math.Min(i + 1, Cols - 1)] - h[Math.Max(i - 1, 0)]) / (2 * dx);
                foam[i] += dt * (Math.Max(0, Math.Abs(rise[i]) - 120) / 220 + Math.Max(0, slope - 0.6) * 2.5);
                foam[i] *= Math.Exp(-1.6 * dt);
                if (foam[i] > 1) foam[i] = 1;
                double uc = Math.Abs(0.5 * (u[i] + u[i + 1]));
                if (uc > MaxSpeed) MaxSpeed = uc;
                clock[i] += dt * (1.2 + uc / 60 + foam[i] * 8);
            }

            SpawnSpray(dt, lift);
            Drips(dt);
            UpdateParticles(dt, geff, fx);
            UpdateCreatures(dt);
        }

        void SpawnSpray(double dt, double lift)
        {
            double amount = cfg.SprayAmount;
            if (amount <= 0) return;
            for (int i = 0; i < Cols; i++)
            {
                if (h[i] < CellH * 0.5) continue;
                double r = rise[i];
                double uc = 0.5 * (u[i] + u[i + 1]);
                if (r > 420)
                {
                    double p = (r - 420) / 2600 * amount * dt * 60 * 0.3;
                    while (p > 0)
                    {
                        if (rng.NextDouble() < p)
                            AddDrop((i + rng.NextDouble()) * CellW, h[i] + 1, uc * 0.8 + Rand(-60, 60), r * Rand(0.6, 1.1) + Rand(0, 120));
                        p -= 1;
                    }
                }
                if (lift > 0 && rng.NextDouble() < Math.Min(0.9, lift / 3000) * amount * dt * 30)
                    AddDrop((i + rng.NextDouble()) * CellW, h[i] + 1, uc * 0.5 + Rand(-40, 40), Rand(0.4, 1.0) * Math.Sqrt(lift) * 14);

                if (Math.Abs(uc) > 220 && rng.NextDouble() < Math.Abs(uc) / 9000 * amount * dt * 60)
                    AddBubble((i + rng.NextDouble()) * CellW, Rand(0.05, 0.7) * h[i]);
            }
        }

        void Drips(double dt)
        {
            if (cfg.IdleDrips <= 0) return;
            dripTimer -= dt;
            if (dripTimer <= 0)
            {
                dripTimer = -Math.Log(1 - rng.NextDouble() * 0.999) / cfg.IdleDrips;
                AddDrop(Rand(0.05, 0.95) * Width, Height - CellH * 0.5, 0, -Rand(20, 80));
            }
        }

        void AddDrop(double x, double y, double vx, double vy)
        {
            if (parts.Count > 700) return;
            Particle p = new Particle();
            p.X = x; p.Y = y; p.VX = vx; p.VY = vy; p.Kind = 0; p.Life = 6;
            parts.Add(p);
        }

        void AddBubble(double x, double y)
        {
            if (parts.Count > 700) return;
            Particle p = new Particle();
            p.X = x; p.Y = y; p.VX = 0; p.VY = Rand(40, 110); p.Kind = 1; p.Life = 8; p.Phase = Rand(0, 6.28);
            parts.Add(p);
        }

        void Splash(int col, double speed)
        {
            double k = Math.Min(speed * 0.10, 140);
            if (col > 0) u[col] -= k;
            if (col + 1 < Cols) u[col + 1] += k;
            foam[col] = Math.Min(1, foam[col] + speed / 4000);
            if (speed > 950 && cfg.SprayAmount > 0 && rng.NextDouble() < 0.35)
            {
                int n = 1 + (int)(speed / 1400 * cfg.SprayAmount);
                for (int j = 0; j < n && j < 2; j++)
                    AddDrop((col + rng.NextDouble()) * CellW, h[col] + 1, Rand(-90, 90), speed * Rand(0.12, 0.3));
            }
            if (speed > 450 && rng.NextDouble() < 0.4) AddBubble((col + rng.NextDouble()) * CellW, h[col] - Rand(CellH, CellH * 3));
        }

        void UpdateParticles(double dt, double geff, double fx)
        {
            for (int n = parts.Count - 1; n >= 0; n--)
            {
                Particle p = parts[n];
                p.Life -= dt;
                bool dead = p.Life <= 0;
                if (p.Kind == 0)
                {
                    p.VY -= geff * dt; p.VX += fx * dt;
                    p.X += p.VX * dt; p.Y += p.VY * dt;
                    if (p.X < 0) { p.X = 0; p.VX = -p.VX * 0.35; }
                    if (p.X > Width - 0.01) { p.X = Width - 0.01; p.VX = -p.VX * 0.35; }
                    if (p.Y > Height - 1) { p.Y = Height - 1; if (p.VY > 0) p.VY = -p.VY * 0.25; }
                    if (p.VY < 0 && p.Y <= SurfaceAt(p.X))
                    {
                        Splash(ColAt(p.X), Math.Sqrt(p.VX * p.VX + p.VY * p.VY));
                        dead = true;
                    }
                }
                else
                {
                    int col = ColAt(p.X);
                    double uc = 0.5 * (u[col] + u[col + 1]);
                    p.X += (Math.Sin(time * 4 + p.Phase) * 18 + uc * 0.6) * dt;
                    p.Y += p.VY * dt;
                    if (p.X < 0) p.X = 0; if (p.X > Width - 0.01) p.X = Width - 0.01;
                    if (p.Y >= SurfaceAt(p.X) - CellH * 0.4)
                    {
                        foam[col] = Math.Min(1, foam[col] + 0.25);
                        dead = true;
                    }
                }
                if (dead) parts.RemoveAt(n);
            }
        }

        // ------------------------------------------------------------ vida marina
        int SpriteLen(Creature c) { return c.Kind == 2 ? 3 : Sprites[c.Kind * 2].Length; }
        double FloorY() { return cfg.Seafloor ? CellH * 1.2 : CellH * 0.3; }

        void UpdateCreatures(double dt)
        {
            int wantFish = Rows < 6 ? 0 : Math.Max(0, Math.Min(16, cfg.Fish));
            int wantJelly = (cfg.Jellyfish && Rows >= 10) ? 1 : 0;
            int nf = 0, nj = 0;
            foreach (Creature c in creatures) { if (c.Kind == 2) nj++; else nf++; }
            for (; nf < wantFish; nf++) Spawn(rng.NextDouble() < 0.7 ? 0 : 1);
            for (; nj < wantJelly; nj++) Spawn(2);
            for (int i = creatures.Count - 1; i >= 0 && (nf > wantFish || nj > wantJelly); i--)
            {
                bool jelly = creatures[i].Kind == 2;
                if (jelly && nj > wantJelly) { creatures.RemoveAt(i); nj--; }
                else if (!jelly && nf > wantFish) { creatures.RemoveAt(i); nf--; }
            }

            bool mouseNear = time - mouseT < 0.4;
            foreach (Creature c in creatures)
            {
                int col = ColAt(c.X + SpriteLen(c) * CellW * 0.5);
                double uc = 0.5 * (u[col] + u[col + 1]);
                double agit = Math.Abs(uc) / 220 + foam[col] * 0.8 + Math.Abs(rise[col]) / 600;
                int flee = 0;
                if (mouseNear)
                {
                    double dxm = (c.X + SpriteLen(c) * CellW * 0.5) - mouseX, dym = c.Y - mouseY;
                    if (dxm * dxm / (CellW * CellW) + dym * dym / (CellH * CellH) < 49) { agit += 1.2; flee = dxm >= 0 ? 1 : -1; }
                }
                c.Panic = Math.Max(c.Panic * Math.Exp(-dt * 0.8), Math.Min(1.5, agit));
                double floor = FloorY();
                double top = h[col] - CellH * (c.Kind == 2 ? 2.4 : 1.4);

                if (c.Kind == 2)
                {
                    c.Phase += dt;
                    double vy = Math.Sin(c.Phase * 2.2) > 0.55 ? 75 : -20;
                    if (c.Panic > 0.5) vy = -90;
                    c.Y += vy * dt;
                    c.X += (Math.Sin(c.Phase * 0.35) * 14 + uc * 0.5) * dt;
                }
                else
                {
                    if (flee != 0) c.Dir = flee;
                    double sp = c.Speed * (1 + c.Panic * 2.5);
                    c.X += (c.Dir * sp + uc * 0.35) * dt;
                    c.Turn -= dt;
                    if (c.Turn <= 0)
                    {
                        c.Turn = Rand(3, 9); c.TargetY = Rand(0.1, 0.9);
                        if (rng.NextDouble() < 0.25) c.Dir = -c.Dir;
                    }
                    double ty = floor + Math.Max(0, top - floor) * (c.Panic > 0.6 ? 0.03 : c.TargetY);
                    c.Y += (ty - c.Y) * Math.Min(1, dt * (0.8 + c.Panic * 3));
                    c.Y += Math.Sin(time * 2 + c.Phase) * 6 * dt;
                }

                double sw = SpriteLen(c) * CellW;
                if (c.X < 0) { c.X = 0; c.Dir = 1; }
                if (c.X > Width - sw) { c.X = Width - sw; c.Dir = -1; }
                if (c.Y > top) c.Y = top;
                if (c.Y < floor) c.Y = floor;
            }
        }

        void Spawn(int kind)
        {
            Creature c = new Creature();
            c.Kind = kind;
            c.Dir = rng.NextDouble() < 0.5 ? -1 : 1;
            c.X = Rand(0.1, 0.8) * Width;
            c.TargetY = Rand(0.2, 0.8);
            c.Y = FloorY() + Fill() * Height * c.TargetY * 0.6;
            c.Speed = kind == 0 ? Rand(45, 75) : Rand(28, 45);
            c.Phase = Rand(0, 6.28); c.Turn = Rand(1, 6);
            creatures.Add(c);
        }

        // ------------------------------------------------------------ render a celdas
        const string Ramp = " ..,:-~=";

        void Put(int col, int row, char ch, Rgb c, float a)
        {
            if (col < 0 || col >= Cols || row < 0 || row >= Rows) return;
            int idx = (Rows - 1 - row) * Cols + col;
            Ch[idx] = ch; R[idx] = c.R; G[idx] = c.G; B[idx] = c.B; A[idx] = a;
        }

        bool Underwater(int col, int row)
        {
            return col >= 0 && col < Cols && row < Math.Floor(h[col] / CellH);
        }

        public void Render(Theme th)
        {
            int n = Cols * Rows;
            for (int c = 0; c < n; c++) { Ch[c] = ' '; A[c] = 0; Tint[c] = 0; }
            float wa = (float)cfg.WaterAlpha;
            bool sea = !string.Equals(cfg.CharMode, "ramp", StringComparison.OrdinalIgnoreCase);
            string seaChars = string.IsNullOrEmpty(th.SeaChars) ? "~=+*" : th.SeaChars;
            string surfChars = string.IsNullOrEmpty(th.SurfChars) ? "~-^" : th.SurfChars;

            for (int i = 0; i < Cols; i++)
            {
                double hc = h[i] / CellH;
                int top = (int)Math.Floor(hc);
                double frac = hc - top;
                double slope = (h[Math.Min(i + 1, Cols - 1)] - h[Math.Max(i - 1, 0)]) / CellH * 0.5;
                float fo = (float)foam[i];

                for (int r = 0; r < Rows && r <= top; r++)
                {
                    int idx = (Rows - 1 - r) * Cols + i;
                    if (r < top)
                    {
                        double d = hc - (r + 0.5);                       // profundidad en filas
                        double wave = Math.Sin(i * 0.31 + time * 1.15 + r * 0.85 + 2.2 * Math.Sin(i * 0.07 - time * 0.45));
                        double wave2 = Math.Sin(i * 0.13 - time * 0.8 - r * 0.37);
                        double tex = 0.5 + 0.35 * wave + 0.15 * wave2;
                        double near = Math.Exp(-d * 0.22);
                        double v = (0.32 + 0.68 * near) * (0.3 + 0.7 * tex) + (d < 1.6 ? fo * 0.35 : 0);
                        Rgb col = Rgb.Lerp(th.Deep, th.Surface, (float)(0.15 + near * 0.75 + 0.1 * tex));
                        if (d < 1.6) col = Rgb.Lerp(col, th.Foam, fo * 0.5f);
                        if (sea)
                        {
                            // mar de caracteres: cada celda tiene su símbolo y lo cambia a su ritmo;
                            // donde el agua se agita, cambian más rápido
                            uint hs = Hash(i, r, 7);
                            double speed = 0.45 + (hs & 1023) / 1023.0;
                            long tick = (long)Math.Floor(clock[i] * speed + (hs >> 10) % 97);
                            string pool = d < 1.2 ? surfChars : seaChars;
                            Ch[idx] = pool[(int)(Hash(i, r, (int)tick) % (uint)pool.Length)];
                            if ((Hash(i, r, (int)(tick * 3 + 1)) & 255) < 4) { col = Rgb.Lerp(col, th.Foam, 0.7f); v += 0.6; }
                            col = Rgb.Lerp(col, th.Surface, 0.2f);
                            A[idx] = wa * (float)(0.45 + 0.55 * Math.Min(1, v));
                        }
                        else
                        {
                            int ri = (int)(v * Ramp.Length);
                            if (ri < 0) ri = 0; if (ri >= Ramp.Length) ri = Ramp.Length - 1;
                            Ch[idx] = Ramp[ri];
                            A[idx] = wa * (float)(0.5 + 0.5 * Math.Min(1, v));
                        }
                        R[idx] = col.R; G[idx] = col.G; B[idx] = col.B;
                        Tint[idx] = (float)(0.24 + 0.16 * near);
                    }
                    else
                    {
                        if (frac < 0.06 && fo < 0.2) continue;
                        char c;
                        if (slope > 0.85) c = '/';
                        else if (slope < -0.85) c = '\\';
                        else if (fo > 0.8) c = '*';
                        else if (frac < 0.2) c = '_';
                        else if (frac < 0.42) c = '.';
                        else if (frac < 0.62) c = '-';
                        else if (frac < 0.9 || rise[i] < 30) c = '~';
                        else c = '^';
                        Ch[idx] = c;
                        Rgb col = Rgb.Lerp(th.Surface, th.Foam, 0.25f + 0.75f * fo);
                        R[idx] = col.R; G[idx] = col.G; B[idx] = col.B;
                        A[idx] = Math.Min(1f, wa + 0.15f);
                        Tint[idx] = (float)(0.24 * frac);
                    }
                }
            }

            // fondo marino: arena y algas que se mecen con la corriente
            if (cfg.Seafloor && Rows >= 6)
            {
                const string sand = "_.,_._,.";
                for (int i = 0; i < Cols; i++)
                    Put(i, 0, sand[(int)(Hash(i, 0, 5) % (uint)sand.Length)], th.Sand, wa * 0.75f);
                for (int i = 0; i < Cols; i++)
                {
                    if (weed[i] == 0) continue;
                    double uc = 0.5 * (u[i] + u[i + 1]);
                    for (int rr = 1; rr <= weed[i]; rr++)
                    {
                        double ph = time * 1.4 + i * 0.9 + rr * 0.6;
                        double sway = Math.Sin(ph) * 0.22 * rr + uc / 500 * rr;
                        int cc = i + (int)Math.Round(sway);
                        if (!Underwater(cc, rr)) continue;
                        Put(cc, rr, Math.Cos(ph) + uc / 300 > 0 ? ')' : '(', th.Weed, wa);
                    }
                }
            }

            // peces y medusa (solo las partes que quedan bajo el agua)
            foreach (Creature c in creatures)
            {
                int c0 = (int)Math.Round(c.X / CellW);
                int row = (int)(c.Y / CellH);
                float a = Math.Min(1f, wa + 0.2f);
                if (c.Kind == 2)
                {
                    string legs = JellyLegs[(int)(c.Phase * 2.5) & 1];
                    for (int k = 0; k < 3; k++)
                    {
                        if (Underwater(c0 + k, row + 1)) Put(c0 + k, row + 1, JellyTop[k], th.Jelly, a);
                        if (Underwater(c0 + k, row)) Put(c0 + k, row, legs[k], th.Jelly, a * 0.8f);
                    }
                }
                else
                {
                    string s = Sprites[c.Kind * 2 + (c.Dir > 0 ? 0 : 1)];
                    Rgb fc = Rgb.Lerp(c.Kind == 0 ? th.Fish1 : th.Fish2, th.Foam, (float)Math.Min(0.5, c.Panic * 0.3));
                    for (int k = 0; k < s.Length; k++)
                        if (Underwater(c0 + k, row)) Put(c0 + k, row, s[k], fc, a);
                }
            }

            foreach (Particle p in parts)
            {
                int ci = (int)(p.X / CellW);
                int r = (int)(p.Y / CellH);
                if (p.Kind == 0)
                {
                    char c;
                    if (p.VY < -650) c = '|';
                    else if (p.VY > 380) c = '\'';
                    else if (p.VY < -250) c = ',';
                    else c = '.';
                    Put(ci, r, c, Rgb.Lerp(th.Surface, th.Foam, 0.5f), Math.Min(1f, wa + 0.2f));
                }
                else Put(ci, r, p.Life > 5 ? '.' : 'o', Rgb.Lerp(th.Surface, th.Foam, 0.3f), wa);
            }
        }

        public string ToAscii()
        {
            StringBuilder sb = new StringBuilder();
            for (int r = 0; r < Rows; r++) { sb.Append(Ch, r * Cols, Cols); sb.Append('\n'); }
            return sb.ToString();
        }
    }

    // ================================================================== glifos
    internal class GlyphAtlas : IDisposable
    {
        public int CellW, CellH;
        public readonly string Key;
        readonly Dictionary<char, byte[]> map = new Dictionary<char, byte[]>();
        Font font;

        public GlyphAtlas(Config cfg, double scale, string key)
        {
            Key = key;
            float px = (float)Math.Max(6, cfg.FontPx * scale);
            font = MakeFont(cfg.FontFamily, px);
            using (Bitmap probe = new Bitmap(8, 8))
            using (Graphics g = Graphics.FromImage(probe))
            {
                SizeF s = g.MeasureString("MMMMMMMMMM", font, PointF.Empty, StringFormat.GenericTypographic);
                CellW = Math.Max(3, (int)Math.Round(s.Width / 10));
                CellH = Math.Max(6, (int)Math.Ceiling(font.GetHeight(g)));
            }
        }

        static Font MakeFont(string family, float px)
        {
            string[] tries = { family, "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New" };
            foreach (string f in tries)
            {
                if (string.IsNullOrEmpty(f)) continue;
                Font ft = new Font(f, px, FontStyle.Regular, GraphicsUnit.Pixel);
                if (string.Equals(ft.Name, f, StringComparison.OrdinalIgnoreCase)) return ft;
                ft.Dispose();
            }
            return new Font(FontFamily.GenericMonospace, px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public byte[] Get(char c)
        {
            byte[] cov;
            if (map.TryGetValue(c, out cov)) return cov;
            cov = new byte[CellW * CellH];
            using (Bitmap bmp = new Bitmap(CellW, CellH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(c.ToString(), font, Brushes.White, 0, 0, StringFormat.GenericTypographic);
                }
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, CellW, CellH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int[] px = new int[CellW * CellH];
                for (int y = 0; y < CellH; y++)
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), px, y * CellW, CellW);
                bmp.UnlockBits(bd);
                for (int i = 0; i < px.Length; i++)
                {
                    int r = (px[i] >> 16) & 255, gg = (px[i] >> 8) & 255, b = px[i] & 255;
                    cov[i] = (byte)Math.Max(r, Math.Max(gg, b));
                }
            }
            map[c] = cov;
            return cov;
        }

        public void Dispose() { if (font != null) font.Dispose(); font = null; }
    }

    // ================================================================== Win32
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT ptDst, ref SIZE size,
            IntPtr hdcSrc, ref POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public const uint GW_HWNDPREV = 3;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const int ULW_ALPHA = 2;
        public const int WH_KEYBOARD_LL = 13;
    }

    // ================================================================== capa de agua (una por ventana)
    internal class Overlay : Form
    {
        public readonly IntPtr Target;
        readonly Config cfg;
        WaterSim sim;
        GlyphAtlas atlas;
        int x, y, w, hgt;
        bool hasPos, shown;
        double vx, vy, vxPrev, vyPrev, ax, ay;
        int[] buf;
        IntPtr memDC, dib, oldBmp, bits;

        // interacción
        readonly List<int> keys = new List<int>();
        int mouseX = int.MinValue, mouseY;
        double lastKey = -100, alarm, dim = 1, ecoAcc, now;

        public Overlay(IntPtr target, Config cfg)
        {
            Target = target; this.cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Abyss Tide";
            CreateHandle();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }   // WM_NCHITTEST → HTTRANSPARENT
            base.WndProc(ref m);
        }

        public void OnKey(int vk) { if (keys.Count < 64) keys.Add(vk); }

        public void OnSignal(string s)
        {
            if (sim == null) return;
            if (s.StartsWith("error")) { alarm = 1; sim.Agitate(0.6); }
            else if (s.StartsWith("ok")) sim.Bubbles(5);
        }

        static uint Pack(float r, float g, float b, float a)
        {
            return ((uint)(a * 255 + 0.5f) << 24) | ((uint)(r * 255 + 0.5f) << 16) | ((uint)(g * 255 + 0.5f) << 8) | (uint)(b * 255 + 0.5f);
        }

        void HideLayer()
        {
            if (shown) { Native.ShowWindow(Handle, Native.SW_HIDE); shown = false; }
            hasPos = false; vx = vy = vxPrev = vyPrev = ax = ay = 0;
            keys.Clear();
        }

        public bool Tick(double dt, bool paused, Theme theme, Native.POINT cursor, bool lowBattery)
        {
            now += dt;
            if (!Native.IsWindow(Target)) return false;
            int cloaked = 0;
            Native.DwmGetWindowAttribute(Target, Native.DWMWA_CLOAKED, out cloaked, 4);
            if (paused || Native.IsIconic(Target) || !Native.IsWindowVisible(Target) || cloaked != 0) { HideLayer(); return true; }

            Native.RECT rc;
            if (Native.DwmGetWindowAttribute(Target, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out rc, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                Native.GetWindowRect(Target, out rc);

            uint d = 96;
            try { d = Native.GetDpiForWindow(Target); } catch { }
            if (d == 0) d = 96;
            double scale = d / 96.0;
            int inset = (int)Math.Round(cfg.TopInsetPx * scale);
            int nx = rc.Left, ny = rc.Top + inset, nw = rc.Right - rc.Left, nh = rc.Bottom - rc.Top - inset;
            if (nw < 60 || nh < 40) { HideLayer(); return true; }

            bool resized = false;
            string key = cfg.FontFamily + "|" + cfg.FontPx.ToString(CultureInfo.InvariantCulture) + "|" + d;
            if (atlas == null || atlas.Key != key)
            {
                if (atlas != null) atlas.Dispose();
                atlas = new GlyphAtlas(cfg, scale, key);
                resized = true;
            }
            if (nw != w || nh != hgt) resized = true;

            if (resized)
            {
                double shrink = hgt > 0 ? (hgt - nh) / (double)hgt : 0;
                w = nw; hgt = nh;
                if (sim == null) sim = new WaterSim(cfg, w, hgt, atlas.CellW, atlas.CellH);
                else sim.Resize(w, hgt, atlas.CellW, atlas.CellH);
                if (shrink > 0.04) sim.Agitate(Math.Min(1, shrink * 3));   // encoger de golpe aprieta el agua
                AllocBuffers();
                hasPos = false;
            }

            // velocidad y aceleración de la ventana (suavizadas)
            if (hasPos)
            {
                double rvx = (nx - x) / dt, rvy = (ny - y) / dt;
                double kv = Math.Min(1, dt * 22);
                vx += (rvx - vx) * kv; vy += (rvy - vy) * kv;
                double rax = (vx - vxPrev) / dt, ray = (vy - vyPrev) / dt;
                double ka = Math.Min(1, dt * 28);
                ax += (rax - ax) * ka; ay += (ray - ay) * ka;
                const double lim = 45000;
                if (ax > lim) ax = lim; if (ax < -lim) ax = -lim;
                if (ay > lim) ay = lim; if (ay < -lim) ay = -lim;
            }
            else { vx = vy = ax = ay = 0; }
            vxPrev = vx; vyPrev = vy;
            x = nx; y = ny; hasPos = true;

            // teclado
            bool input = false;
            if (keys.Count > 0)
            {
                foreach (int vk in keys)
                    if (cfg.TypingDrops) sim.Key(vk == 0x0D, vk == 0x08);
                keys.Clear();
                lastKey = now; input = true;
            }

            // ratón (coordenadas de la capa, y desde abajo)
            int offX = (w - sim.Cols * atlas.CellW) / 2;
            int lx = cursor.X - x - offX, lyb = (y + hgt) - cursor.Y;
            bool inside = cursor.X >= x && cursor.X < x + w && cursor.Y >= y && cursor.Y < y + hgt;
            if (cfg.MouseRipples && inside && mouseX != int.MinValue && (cursor.X != mouseX || cursor.Y != mouseY))
            {
                double mvx = (cursor.X - mouseX) / dt, mvy = -(cursor.Y - mouseY) / dt;
                sim.Stir(lx, lyb, mvx, mvy);
                input = true;
            }
            mouseX = cursor.X; mouseY = cursor.Y;

            // inclinación según la posición en el monitor
            double tilt = 0;
            if (cfg.TiltAmount != 0)
            {
                Rectangle wa = Screen.FromRectangle(new Rectangle(x, y, w, hgt)).WorkingArea;
                double off = ((x + w / 2.0) - (wa.Left + wa.Width / 2.0)) / Math.Max(1, wa.Width / 2.0);
                tilt = Math.Max(-1, Math.Min(1, off)) * cfg.TiltAmount;
            }

            // modo foco y alarma
            double dimTarget = (cfg.FocusMode && now - lastKey < cfg.FocusDelay) ? cfg.FocusDim : 1;
            dim += (dimTarget - dim) * Math.Min(1, dt * 6);
            alarm = Math.Max(0, alarm - dt / 1.8);

            // modo eco: menos fotogramas cuando no pasa nada
            ecoAcc += dt;
            bool active = input || Math.Abs(ax) + Math.Abs(ay) > 300 || Math.Abs(vx) + Math.Abs(vy) > 5
                || sim.MaxSpeed > 25 || sim.ParticleCount > 0 || alarm > 0 || Math.Abs(dim - dimTarget) > 0.01 || resized;
            double interval = cfg.EcoMode && (!active || lowBattery) ? 1.0 / Math.Max(5, cfg.EcoFps) : 0;
            if (ecoAcc < interval) return true;
            double stepDt = Math.Min(ecoAcc, 0.1);
            ecoAcc = 0;

            Theme th = theme.WithAlarm((float)alarm);
            sim.Step(stepDt, ax, ay, tilt);
            sim.Render(th);
            Compose(th, (float)dim);
            Present();
            return true;
        }

        void AllocBuffers()
        {
            FreeDib();
            buf = new int[w * hgt];
            Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = w; bi.biHeight = -hgt; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            dib = Native.CreateDIBSection(memDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = Native.SelectObject(memDC, dib);
        }

        void FreeDib()
        {
            if (memDC != IntPtr.Zero)
            {
                Native.SelectObject(memDC, oldBmp);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero; dib = IntPtr.Zero; bits = IntPtr.Zero;
            }
        }

        void Compose(Theme th, float dimF)
        {
            float bga = (float)Math.Max(0, Math.Min(1, cfg.BackgroundAlpha));
            Rgb bg = th.Bg, deep = th.Deep;
            int bgi = unchecked((int)Pack(bg.R * bga, bg.G * bga, bg.B * bga, bga));
            for (int i = 0; i < buf.Length; i++) buf[i] = bgi;

            int cw = atlas.CellW, ch = atlas.CellH;
            int yOff = hgt - sim.Rows * ch;
            int xOff = (w - sim.Cols * cw) / 2;
            float tintMul = 0.5f + 0.5f * dimF;

            for (int r = 0; r < sim.Rows; r++)
            {
                int py0 = yOff + r * ch;
                for (int c = 0; c < sim.Cols; c++)
                {
                    int idx = r * sim.Cols + c;
                    int px0 = xOff + c * cw;
                    float t = sim.Tint[idx] * tintMul;
                    if (t > 0)
                    {
                        // masa de agua: tinte del color profundo sobre el fondo
                        float ta = t, oa = ta + bga * (1 - ta);
                        uint cell = Pack(deep.R * ta + bg.R * bga * (1 - ta), deep.G * ta + bg.G * bga * (1 - ta), deep.B * ta + bg.B * bga * (1 - ta), oa);
                        int ci = unchecked((int)cell);
                        for (int yy = 0; yy < ch; yy++)
                        {
                            int row = (py0 + yy) * w + px0;
                            for (int xx = 0; xx < cw; xx++) buf[row + xx] = ci;
                        }
                    }
                    char g = sim.Ch[idx];
                    if (g == ' ' || sim.A[idx] <= 0) continue;
                    byte[] cov = atlas.Get(g);
                    float ca = sim.A[idx] * dimF, cr = sim.R[idx], cg = sim.G[idx], cb = sim.B[idx];
                    for (int yy = 0; yy < ch; yy++)
                    {
                        int row = (py0 + yy) * w + px0;
                        int crow = yy * cw;
                        for (int xx = 0; xx < cw; xx++)
                        {
                            int cv = cov[crow + xx];
                            if (cv == 0) continue;
                            float a = cv * (1f / 255f) * ca;
                            uint dst = unchecked((uint)buf[row + xx]);
                            float ia = 1 - a;
                            float da = (dst >> 24) / 255f, dr = ((dst >> 16) & 255) / 255f, dg = ((dst >> 8) & 255) / 255f, db = (dst & 255) / 255f;
                            buf[row + xx] = unchecked((int)Pack(cr * a + dr * ia, cg * a + dg * ia, cb * a + db * ia, a + da * ia));
                        }
                    }
                }
            }
        }

        void Present()
        {
            Marshal.Copy(buf, 0, bits, buf.Length);
            Native.POINT dst = new Native.POINT(); dst.X = x; dst.Y = y;
            Native.POINT src = new Native.POINT();
            Native.SIZE sz = new Native.SIZE(); sz.CX = w; sz.CY = hgt;
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, memDC, ref src, 0, ref bf, Native.ULW_ALPHA);

            // siempre justo debajo de la terminal
            if (!shown || Native.GetWindow(Handle, Native.GW_HWNDPREV) != Target)
                Native.SetWindowPos(Handle, Target, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (!shown) { Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); shown = true; }
        }

        protected override void Dispose(bool disposing)
        {
            FreeDib();
            if (atlas != null) { atlas.Dispose(); atlas = null; }
            base.Dispose(disposing);
        }
    }

    // ================================================================== aplicación
    public static class App
    {
        static Config cfg;
        static string configPath, signalPath;
        static readonly Dictionary<IntPtr, Overlay> overlays = new Dictionary<IntPtr, Overlay>();
        static Form host;
        static NotifyIcon tray;
        static Stopwatch clock;
        static double last, lastScan = -1, lastSignalCheck, lastPowerCheck = -100, reloadAt = -1, ignoreWatcherUntil;
        static DateTime signalStamp;
        static bool paused, running, busy, lowBattery;
        static volatile bool configDirty;
        static FileSystemWatcher watcher;
        static Native.LowLevelKeyboardProc hookProc;
        static IntPtr hookId, lastTyped;

        public static void Run(string path)
        {
            configPath = path;
            bool created;
            using (Mutex m = new Mutex(true, "Local\\AbyssTideWater", out created))
            {
                if (!created) return;
                try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
                catch { try { Native.SetProcessDPIAware(); } catch { } }

                string err;
                cfg = Config.Load(configPath, out err);

                Application.EnableVisualStyles();
                host = new Form();
                host.ShowInTaskbar = false;
                IntPtr unused = host.Handle;

                BuildTray();
                if (err != null) Notify("No se pudo leer la configuración: " + err);
                WatchConfig();
                signalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbyssTide", "signal.txt");
                try { if (File.Exists(signalPath)) signalStamp = File.GetLastWriteTimeUtc(signalPath); } catch { }
                EnsureHook();

                clock = Stopwatch.StartNew();
                running = true;
                try { Native.timeBeginPeriod(1); } catch { }
                Thread pump = new Thread(Pump);
                pump.IsBackground = true;
                pump.Start();

                Application.Run();

                running = false;
                if (hookId != IntPtr.Zero) Native.UnhookWindowsHookEx(hookId);
                try { Native.timeEndPeriod(1); } catch { }
                if (watcher != null) watcher.Dispose();
                tray.Visible = false; tray.Dispose();
                foreach (Overlay o in overlays.Values) o.Dispose();
                overlays.Clear();
                host.Dispose();
            }
        }

        static void Pump()
        {
            while (running)
            {
                int ms = Math.Max(5, 1000 / Math.Max(10, Math.Min(240, cfg.Fps)));
                Thread.Sleep(ms);
                if (busy) continue;
                busy = true;
                try { host.BeginInvoke(new MethodInvoker(Tick)); } catch { busy = false; }
            }
        }

        static void Tick()
        {
            try
            {
                double now = clock.Elapsed.TotalSeconds;
                double dt = now - last; last = now;
                if (dt <= 0.0005) dt = 0.0005; if (dt > 0.05) dt = 0.05;

                if (lastScan < 0 || now - lastScan > 0.5) { Scan(); lastScan = now; }
                if (configDirty) { configDirty = false; if (now > ignoreWatcherUntil) reloadAt = now + 0.3; }
                if (reloadAt > 0 && now >= reloadAt) { reloadAt = -1; Reload(); }
                if (now - lastSignalCheck > 0.25) { lastSignalCheck = now; CheckSignal(); }
                if (now - lastPowerCheck > 10) { lastPowerCheck = now; CheckPower(); }

                Theme theme = Theme.For(cfg, DateTime.Now);
                Native.POINT cur;
                if (!Native.GetCursorPos(out cur)) { cur.X = int.MinValue / 2; cur.Y = 0; }

                List<IntPtr> dead = null;
                foreach (KeyValuePair<IntPtr, Overlay> kv in overlays)
                {
                    bool alive;
                    try { alive = kv.Value.Tick(dt, paused, theme, cur, lowBattery); } catch { alive = true; }
                    if (!alive) { if (dead == null) dead = new List<IntPtr>(); dead.Add(kv.Key); }
                }
                if (dead != null) foreach (IntPtr hw in dead) { overlays[hw].Dispose(); overlays.Remove(hw); }
            }
            finally { busy = false; }
        }

        static void Scan()
        {
            List<IntPtr> found = new List<IntPtr>();
            StringBuilder sb = new StringBuilder(256);
            Native.EnumWindows(delegate (IntPtr hw, IntPtr lp)
            {
                sb.Length = 0;
                Native.GetClassName(hw, sb, sb.Capacity);
                if (sb.ToString() == cfg.WindowClass) found.Add(hw);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr hw in found)
                if (!overlays.ContainsKey(hw)) overlays[hw] = new Overlay(hw, cfg);
        }

        // ------------------------------------------------------------ configuración en caliente
        static void WatchConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
                if (!Directory.Exists(dir)) return;
                watcher = new FileSystemWatcher(dir, Path.GetFileName(configPath));
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                FileSystemEventHandler h = delegate { configDirty = true; };
                watcher.Changed += h; watcher.Created += h;
                watcher.Renamed += delegate { configDirty = true; };
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        static void Reload()
        {
            string err;
            Config nc = Config.Load(configPath, out err);
            if (err != null) { Notify("La configuración tiene un error y no se aplicó: " + err); return; }
            cfg.CopyFrom(nc);
            EnsureHook();
            Notify("Configuración recargada.");
        }

        static void SaveConfig()
        {
            try
            {
                ignoreWatcherUntil = clock.Elapsed.TotalSeconds + 1.5;
                cfg.Save(configPath);
                Notify("Ajustes guardados en " + Path.GetFileName(configPath));
            }
            catch (Exception ex) { Notify("No se pudieron guardar los ajustes: " + ex.Message); }
        }

        // ------------------------------------------------------------ señales del shell y energía
        static void CheckSignal()
        {
            if (!cfg.ShellSignals) return;
            try
            {
                if (!File.Exists(signalPath)) return;
                DateTime t = File.GetLastWriteTimeUtc(signalPath);
                if (t <= signalStamp) return;
                signalStamp = t;
                string s = File.ReadAllText(signalPath).Trim().ToLowerInvariant();
                Overlay o;
                IntPtr fg = Native.GetForegroundWindow();
                if (overlays.TryGetValue(fg, out o) || overlays.TryGetValue(lastTyped, out o)) o.OnSignal(s);
                else foreach (Overlay ov in overlays.Values) ov.OnSignal(s);
            }
            catch { }
        }

        static void CheckPower()
        {
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                lowBattery = ps.PowerLineStatus == PowerLineStatus.Offline && ps.BatteryLifePercent < 0.25f;
            }
            catch { lowBattery = false; }
        }

        // ------------------------------------------------------------ teclado (solo cuenta pulsaciones en la terminal)
        static void EnsureHook()
        {
            bool need = cfg.TypingDrops || cfg.FocusMode;
            if (need && hookId == IntPtr.Zero)
            {
                hookProc = HookCallback;
                hookId = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
            }
            else if (!need && hookId != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }

        static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                long msg = wParam.ToInt64();
                if (nCode >= 0 && (msg == 0x100 || msg == 0x104))
                {
                    int vk = Marshal.ReadInt32(lParam);
                    bool modifier = (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14;
                    if (!modifier)
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        Overlay o;
                        if (overlays.TryGetValue(fg, out o)) { o.OnKey(vk); lastTyped = fg; }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        // ------------------------------------------------------------ bandeja
        static void Notify(string text)
        {
            if (tray == null) return;
            try { tray.ShowBalloonTip(3000, "Abyss Tide", text, ToolTipIcon.Info); } catch { }
        }

        static ToolStripMenuItem Choice(string label, Func<bool> isChecked, Action apply)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(label);
            it.Tag = isChecked;
            it.Click += delegate { apply(); };
            return it;
        }

        static ToolStripMenuItem Sub(string label, string[] labels, double[] values, Func<double> get, Action<double> set)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(label);
            for (int i = 0; i < labels.Length; i++)
            {
                double v = values[i];
                m.DropDownItems.Add(Choice(labels[i], delegate { return Math.Abs(get() - v) < 1e-6; }, delegate { set(v); }));
            }
            return m;
        }

        static void Refresh(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                Func<bool> f = mi.Tag as Func<bool>;
                if (f != null) mi.Checked = f();
                if (mi.DropDownItems.Count > 0) Refresh(mi.DropDownItems);
            }
        }

        static void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "Abyss Tide — agua para Windows Terminal";
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem pal = new ToolStripMenuItem("Paleta");
            for (int i = 0; i < Theme.Names.Length; i++)
            {
                string name = Theme.Names[i];
                pal.DropDownItems.Add(Choice(Theme.Labels[i],
                    delegate { return string.Equals(cfg.Palette, name, StringComparison.OrdinalIgnoreCase); },
                    delegate { cfg.Palette = name; }));
            }
            menu.Items.Add(pal);
            menu.Items.Add(Sub("Sensibilidad", new[] { "Baja", "Normal", "Alta", "Caos" }, new[] { 0.5, 1.0, 2.0, 3.5 },
                delegate { return cfg.MoveSensitivity; }, delegate (double v) { cfg.MoveSensitivity = v; }));
            menu.Items.Add(Sub("Nivel del agua", new[] { "Bajo", "Medio", "Alto", "Desbordado" }, new[] { 0.2, 0.36, 0.55, 0.75 },
                delegate { return cfg.FillLevel; }, delegate (double v) { cfg.FillLevel = v; }));
            menu.Items.Add(Sub("Peces", new[] { "Ninguno", "Pocos", "Normal", "Banco" }, new[] { 0.0, 2.0, 4.0, 9.0 },
                delegate { return cfg.Fish; }, delegate (double v) { cfg.Fish = (int)v; }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Choice("Gotas al escribir", delegate { return cfg.TypingDrops; }, delegate { cfg.TypingDrops = !cfg.TypingDrops; EnsureHook(); }));
            menu.Items.Add(Choice("Ondas con el ratón", delegate { return cfg.MouseRipples; }, delegate { cfg.MouseRipples = !cfg.MouseRipples; }));
            menu.Items.Add(Choice("Fondo marino", delegate { return cfg.Seafloor; }, delegate { cfg.Seafloor = !cfg.Seafloor; }));
            menu.Items.Add(Choice("Medusa", delegate { return cfg.Jellyfish; }, delegate { cfg.Jellyfish = !cfg.Jellyfish; }));
            menu.Items.Add(Choice("Agua roja si un comando falla", delegate { return cfg.ShellSignals; }, delegate { cfg.ShellSignals = !cfg.ShellSignals; }));
            menu.Items.Add(Choice("Modo foco (atenuar al escribir)", delegate { return cfg.FocusMode; }, delegate { cfg.FocusMode = !cfg.FocusMode; EnsureHook(); }));
            menu.Items.Add(Choice("Modo eco", delegate { return cfg.EcoMode; }, delegate { cfg.EcoMode = !cfg.EcoMode; }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Probar: comando fallido", null, delegate
            {
                foreach (Overlay o in overlays.Values) o.OnSignal("error");
                if (!File.Exists(signalPath)) Notify("El agua reacciona, pero el hook del prompt aún no ha enviado ninguna señal. Reinstala con install.ps1 y abre una pestaña nueva.");
            });
            menu.Items.Add("Guardar estos ajustes", null, delegate { SaveConfig(); });
            menu.Items.Add("Abrir configuración", null, delegate
            {
                try { Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true }); }
                catch (Exception ex) { Notify("No se pudo abrir: " + ex.Message); }
            });
            ToolStripMenuItem pause = Choice("Pausar", delegate { return paused; }, delegate { paused = !paused; });
            menu.Items.Add(pause);
            menu.Items.Add("Salir", null, delegate { Application.ExitThread(); });
            menu.Opening += delegate { Refresh(menu.Items); };

            tray.ContextMenuStrip = menu;
            tray.Visible = true;
        }

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.FromArgb(4, 16, 26));
                    using (SolidBrush water = new SolidBrush(Color.FromArgb(26, 92, 156))) g.FillRectangle(water, 0, 9, 16, 7);
                    using (Pen p = new Pen(Color.FromArgb(155, 240, 255), 2))
                        g.DrawLines(p, new Point[] { new Point(0, 9), new Point(4, 6), new Point(8, 9), new Point(12, 6), new Point(16, 9) });
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }
}
