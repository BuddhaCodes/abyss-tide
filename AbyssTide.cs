// Abyss Tide — agua ASCII 2D detrás de Windows Terminal que reacciona al movimiento de la ventana.
// Escrito en C# 5 para que Windows PowerShell 5.1 (Add-Type) lo compile sin instalar nada.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AbyssTide
{
    // ------------------------------------------------------------------ configuración
    public class Config
    {
        public double FillLevel = 0.36;          // fracción de la altura ocupada por agua
        public string FontFamily = "Cascadia Mono";
        public double FontPx = 14;               // tamaño de los caracteres del agua (px a 96 dpi)
        public int Fps = 60;
        public double Gravity = 2600;            // px/s² — más alto = olas más rápidas y bajas
        public double MoveSensitivity = 1.0;     // cuánto reacciona al mover la ventana
        public double Damping = 0.9;             // qué tan rápido se calma (por segundo)
        public double SprayAmount = 1.0;         // salpicaduras
        public double IdleDrips = 0.5;           // gotas por segundo cuando está quieta (0 = ninguna)
        public string BackgroundColor = "#04101A";
        public double BackgroundAlpha = 0.62;    // opacidad del fondo detrás del texto
        public string SurfaceColor = "#9BF0FF";
        public string DeepColor = "#1A5C9C";
        public string FoamColor = "#FFFFFF";
        public double WaterAlpha = 0.80;         // opacidad de los caracteres del agua
        public double TopInsetPx = 40;           // alto de la barra de pestañas (px a 96 dpi)
        public string WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    }

    internal struct Rgb
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

        public static Rgb Lerp(Rgb a, Rgb b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        }
    }

    // ------------------------------------------------------------------ simulación
    internal class Particle
    {
        public double X, Y, VX, VY, Life, Phase;
        public int Kind;   // 0 = gota, 1 = burbuja
    }

    /// Agua vista de lado: ecuaciones de aguas someras 1D (alturas por columna) en el marco
    /// de referencia de la ventana. Acelerar la ventana genera una pseudo-fuerza opuesta.
    /// Coordenadas en píxeles; y crece hacia arriba desde el fondo.
    public class WaterSim
    {
        public int Cols, Rows;
        public double CellW, CellH, Width, Height;
        double[] h, u, flux, hPrev, rise, foam, tmp;
        double targetVolume;
        readonly List<Particle> parts = new List<Particle>();
        readonly Random rng = new Random();
        readonly Config cfg;
        double time, dripTimer;
        Rgb surface, deep, foamC;

        // salida del render (Cols × Rows, fila 0 = arriba)
        public char[] Ch;
        public float[] R, G, B, A, Tint;

        public WaterSim(Config cfg, double width, double height, double cellW, double cellH)
        {
            this.cfg = cfg;
            surface = Rgb.Parse(cfg.SurfaceColor, new Rgb(0.6f, 0.94f, 1f));
            deep = Rgb.Parse(cfg.DeepColor, new Rgb(0.06f, 0.25f, 0.45f));
            foamC = Rgb.Parse(cfg.FoamColor, new Rgb(1, 1, 1));
            Resize(width, height, cellW, cellH);
        }

        public void Resize(double width, double height, double cellW, double cellH)
        {
            int newCols = Math.Max(4, (int)(width / cellW));
            int newRows = Math.Max(3, (int)(height / cellH));
            double[] nh = new double[newCols];
            double fill = Math.Max(0.02, Math.Min(0.95, cfg.FillLevel));
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
            hPrev = new double[Cols]; rise = new double[Cols]; foam = new double[Cols];
            targetVolume = fill * Height * Cols;
            int n = Cols * Rows;
            Ch = new char[n]; R = new float[n]; G = new float[n]; B = new float[n]; A = new float[n]; Tint = new float[n];
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        double Rand(double a, double b) { return a + rng.NextDouble() * (b - a); }

        double SurfaceAt(double x)
        {
            return h[Clamp((int)(x / CellW), 0, Cols - 1)];
        }

        /// ax, ay: aceleración de la ventana en px/s² (pantalla: +x derecha, +y abajo).
        public void Step(double dt, double ax, double ay)
        {
            time += dt;
            double g = cfg.Gravity;
            double k = 0.28 * cfg.MoveSensitivity;
            double fx = -ax * k;                        // pseudo-fuerza horizontal
            double gRaw = g - ay * k;                   // ventana cae → menos gravedad
            double geff = Math.Max(0.25 * g, Math.Min(4 * g, gRaw));
            double lift = 0.25 * g - gRaw;              // "ingravidez" sobrante → el agua salta

            Array.Copy(h, hPrev, Cols);

            double dx = CellW;
            int sub = (int)Math.Ceiling(dt / 0.0025);
            if (sub < 1) sub = 1; if (sub > 24) sub = 24;
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

            // conservar el volumen (los recortes anteriores lo hacen derivar)
            double vol = 0;
            for (int i = 0; i < Cols; i++) vol += h[i];
            if (vol > 1)
            {
                double f = 1 + (targetVolume / vol - 1) * 0.1;
                for (int i = 0; i < Cols; i++) h[i] *= f;
            }

            for (int i = 0; i < Cols; i++)
            {
                rise[i] = (h[i] - hPrev[i]) / Math.Max(dt, 1e-4);
                double slope = Math.Abs(h[Math.Min(i + 1, Cols - 1)] - h[Math.Max(i - 1, 0)]) / (2 * dx);
                foam[i] += dt * (Math.Max(0, Math.Abs(rise[i]) - 120) / 220 + Math.Max(0, slope - 0.6) * 2.5);
                foam[i] *= Math.Exp(-1.6 * dt);
                if (foam[i] > 1) foam[i] = 1;
            }

            SpawnSpray(dt, lift);
            Drips(dt);
            UpdateParticles(dt, geff, fx);
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

                // burbujas cuando el agua se agita fuerte
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
                        Splash(Clamp((int)(p.X / CellW), 0, Cols - 1), Math.Sqrt(p.VX * p.VX + p.VY * p.VY));
                        dead = true;
                    }
                }
                else
                {
                    int col = Clamp((int)(p.X / CellW), 0, Cols - 1);
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

        // ------------------------------------------------------------ render a celdas
        const string Ramp = " ..,:-~=";

        public void Render()
        {
            int n = Cols * Rows;
            for (int c = 0; c < n; c++) { Ch[c] = ' '; A[c] = 0; Tint[c] = 0; }
            float wa = (float)cfg.WaterAlpha;

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
                        int ri = (int)(v * Ramp.Length);
                        if (ri < 0) ri = 0; if (ri >= Ramp.Length) ri = Ramp.Length - 1;
                        Ch[idx] = Ramp[ri];
                        Rgb col = Rgb.Lerp(deep, surface, (float)(0.15 + near * 0.75 + 0.1 * tex));
                        if (d < 1.6) col = Rgb.Lerp(col, foamC, fo * 0.5f);
                        R[idx] = col.R; G[idx] = col.G; B[idx] = col.B;
                        A[idx] = wa * (float)(0.5 + 0.5 * Math.Min(1, v));
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
                        Rgb col = Rgb.Lerp(surface, foamC, 0.25f + 0.75f * fo);
                        R[idx] = col.R; G[idx] = col.G; B[idx] = col.B;
                        A[idx] = Math.Min(1f, wa + 0.15f);
                        Tint[idx] = (float)(0.24 * frac);
                    }
                }
            }

            foreach (Particle p in parts)
            {
                int ci = (int)(p.X / CellW);
                int r = (int)(p.Y / CellH);
                if (ci < 0 || ci >= Cols || r < 0 || r >= Rows) continue;
                int idx = (Rows - 1 - r) * Cols + ci;
                char c;
                Rgb col;
                float a;
                if (p.Kind == 0)
                {
                    if (p.VY < -650) c = '|';
                    else if (p.VY > 380) c = '\'';
                    else if (p.VY < -250) c = ',';
                    else c = '.';
                    col = Rgb.Lerp(surface, foamC, 0.5f); a = Math.Min(1f, wa + 0.2f);
                }
                else
                {
                    c = (p.Life > 5) ? '.' : 'o';
                    col = Rgb.Lerp(surface, foamC, 0.3f); a = wa;
                }
                Ch[idx] = c; R[idx] = col.R; G[idx] = col.G; B[idx] = col.B; A[idx] = a;
            }
        }

        public string ToAscii()
        {
            StringBuilder sb = new StringBuilder();
            for (int r = 0; r < Rows; r++) { sb.Append(Ch, r * Cols, Cols); sb.Append('\n'); }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ glifos
    internal class GlyphAtlas : IDisposable
    {
        public int CellW, CellH;
        readonly Dictionary<char, byte[]> map = new Dictionary<char, byte[]>();
        Font font;

        public GlyphAtlas(Config cfg, double scale)
        {
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

    // ------------------------------------------------------------------ Win32
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

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT ptDst, ref SIZE size,
            IntPtr hdcSrc, ref POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
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
    }

    // ------------------------------------------------------------------ capa de agua (una por ventana)
    internal class Overlay : Form
    {
        public readonly IntPtr Target;
        readonly Config cfg;
        WaterSim sim;
        GlyphAtlas atlas;
        uint dpi;
        int x, y, w, hgt;
        bool hasPos, shown;
        double vx, vy, vxPrev, vyPrev, ax, ay;
        int[] buf;
        IntPtr memDC, dib, oldBmp, bits;
        uint bgPremul;
        Rgb bg, deep;

        public Overlay(IntPtr target, Config cfg)
        {
            Target = target; this.cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Abyss Tide";
            bg = Rgb.Parse(cfg.BackgroundColor, new Rgb(0.02f, 0.06f, 0.1f));
            deep = Rgb.Parse(cfg.DeepColor, new Rgb(0.06f, 0.25f, 0.45f));
            float ba = (float)Math.Max(0, Math.Min(1, cfg.BackgroundAlpha));
            bgPremul = Pack(bg.R * ba, bg.G * ba, bg.B * ba, ba);
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

        static uint Pack(float r, float g, float b, float a)
        {
            return ((uint)(a * 255 + 0.5f) << 24) | ((uint)(r * 255 + 0.5f) << 16) | ((uint)(g * 255 + 0.5f) << 8) | (uint)(b * 255 + 0.5f);
        }

        void Hide2()
        {
            if (shown) { Native.ShowWindow(Handle, Native.SW_HIDE); shown = false; }
            hasPos = false; vx = vy = vxPrev = vyPrev = ax = ay = 0;
        }

        public bool Tick(double dt, bool paused)
        {
            if (!Native.IsWindow(Target)) return false;
            int cloaked = 0;
            Native.DwmGetWindowAttribute(Target, Native.DWMWA_CLOAKED, out cloaked, 4);
            if (paused || Native.IsIconic(Target) || !Native.IsWindowVisible(Target) || cloaked != 0) { Hide2(); return true; }

            Native.RECT rc;
            if (Native.DwmGetWindowAttribute(Target, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out rc, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                Native.GetWindowRect(Target, out rc);

            uint d = 96;
            try { d = Native.GetDpiForWindow(Target); } catch { }
            if (d == 0) d = 96;
            double scale = d / 96.0;
            int inset = (int)Math.Round(cfg.TopInsetPx * scale);
            int nx = rc.Left, ny = rc.Top + inset, nw = rc.Right - rc.Left, nh = rc.Bottom - rc.Top - inset;
            if (nw < 60 || nh < 40) { Hide2(); return true; }

            bool resized = false;
            if (atlas == null || d != dpi)
            {
                if (atlas != null) atlas.Dispose();
                atlas = new GlyphAtlas(cfg, scale);
                dpi = d; resized = true;
            }
            if (nw != w || nh != hgt) resized = true;

            if (resized)
            {
                w = nw; hgt = nh;
                if (sim == null) sim = new WaterSim(cfg, w, hgt, atlas.CellW, atlas.CellH);
                else sim.Resize(w, hgt, atlas.CellW, atlas.CellH);
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

            sim.Step(dt, ax, ay);
            sim.Render();
            Compose();
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

        void Compose()
        {
            int bgi = unchecked((int)bgPremul);
            for (int i = 0; i < buf.Length; i++) buf[i] = bgi;

            int cw = atlas.CellW, ch = atlas.CellH;
            int yOff = hgt - sim.Rows * ch;
            int xOff = (w - sim.Cols * cw) / 2;
            float bga = (float)Math.Max(0, Math.Min(1, cfg.BackgroundAlpha));

            for (int r = 0; r < sim.Rows; r++)
            {
                int py0 = yOff + r * ch;
                for (int c = 0; c < sim.Cols; c++)
                {
                    int idx = r * sim.Cols + c;
                    int px0 = xOff + c * cw;
                    float t = sim.Tint[idx];
                    if (t > 0)
                    {
                        // masa de agua: tinte azul profundo sobre el fondo
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
                    float ca = sim.A[idx], cr = sim.R[idx], cg = sim.G[idx], cb = sim.B[idx];
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

    // ------------------------------------------------------------------ aplicación
    public static class App
    {
        static Config cfg;
        static readonly Dictionary<IntPtr, Overlay> overlays = new Dictionary<IntPtr, Overlay>();
        static Form host;
        static NotifyIcon tray;
        static Stopwatch clock;
        static double last, lastScan = -1;
        static bool paused, running, busy;

        public static void Run(Config config)
        {
            cfg = config ?? new Config();
            bool created;
            using (Mutex m = new Mutex(true, "Local\\AbyssTideWater", out created))
            {
                if (!created) return;
                try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
                catch { try { Native.SetProcessDPIAware(); } catch { } }

                Application.EnableVisualStyles();
                host = new Form();
                host.ShowInTaskbar = false;
                IntPtr unused = host.Handle;

                tray = new NotifyIcon();
                tray.Icon = MakeIcon();
                tray.Text = "Abyss Tide — agua para Windows Terminal";
                ContextMenuStrip menu = new ContextMenuStrip();
                ToolStripMenuItem pause = new ToolStripMenuItem("Pausar");
                pause.Click += delegate { paused = !paused; pause.Text = paused ? "Reanudar" : "Pausar"; };
                menu.Items.Add(pause);
                menu.Items.Add("Salir", null, delegate { Application.ExitThread(); });
                tray.ContextMenuStrip = menu;
                tray.Visible = true;

                clock = Stopwatch.StartNew();
                running = true;
                try { Native.timeBeginPeriod(1); } catch { }
                Thread pump = new Thread(Pump);
                pump.IsBackground = true;
                pump.Start();

                Application.Run();

                running = false;
                try { Native.timeEndPeriod(1); } catch { }
                tray.Visible = false; tray.Dispose();
                foreach (Overlay o in overlays.Values) o.Dispose();
                overlays.Clear();
                host.Dispose();
            }
        }

        static void Pump()
        {
            int ms = Math.Max(5, 1000 / Math.Max(10, Math.Min(240, cfg.Fps)));
            MethodInvoker tick = Tick;
            while (running)
            {
                Thread.Sleep(ms);
                if (busy) continue;
                busy = true;
                try { host.BeginInvoke(tick); } catch { busy = false; }
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

                List<IntPtr> dead = null;
                foreach (KeyValuePair<IntPtr, Overlay> kv in overlays)
                {
                    bool alive;
                    try { alive = kv.Value.Tick(dt, paused); } catch { alive = true; }
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

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.FromArgb(4, 16, 26));
                    using (SolidBrush water = new SolidBrush(Color.FromArgb(15, 63, 114))) g.FillRectangle(water, 0, 9, 16, 7);
                    using (Pen p = new Pen(Color.FromArgb(155, 240, 255), 2))
                        g.DrawLines(p, new Point[] { new Point(0, 9), new Point(4, 6), new Point(8, 9), new Point(12, 6), new Point(16, 9) });
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }
}
