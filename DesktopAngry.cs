using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drw = System.Drawing;

namespace DesktopAngry
{
    internal static class Diag
    {
        internal static void Log(string msg)
        {
            try
            {
                string p = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AngryDesktop_diag.log");
                System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SystemParametersInfo(int uAction, uint uParam, StringBuilder lpvParam, int fuWinIni);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE { public int cx; public int cy; public SIZE(int c, int y) { cx = c; cy = y; } }

        [Flags]
        internal enum SIIGBF
        {
            ResizeToFit = 1, BiggerSizeOk = 2, MemoryOnly = 4, IconOnly = 8,
            ThumbnailOnly = 16, InCacheOnly = 32, CropToSquare = 64,
            WideThumbnails = 128, IconBackground = 256, ScaleUp = 512
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        internal static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    }

    internal static class Assets
    {
        private static List<string> _desktopCache;
        private static bool _shellBroken;

        internal static BitmapSource LoadWallpaper()
        {
            var candidates = new List<string>();
            try
            {
                var sb = new StringBuilder(1024);
                Native.SystemParametersInfo(0x0073, 0, sb, 0);
                if (sb.Length > 0) candidates.Add(sb.ToString());
            }
            catch (Exception ex) { Diag.Log("SPI wall fail: " + ex.Message); }

            try
            {
                candidates.Add(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper"));
            }
            catch { }

            for (int i = 0; i < candidates.Count; i++)
            {
                string p = candidates[i];
                Diag.Log("wall candidate[" + i + "]: " + p + " exists=" + System.IO.File.Exists(p));
                if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) continue;
                try
                {
                    using (var fs = System.IO.File.OpenRead(p))
                    {
                        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var f = dec.Frames[0];
                        f.Freeze();
                        Diag.Log("wall loaded " + f.PixelWidth + "x" + f.PixelHeight);
                        return f;
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log("wall decode fail: " + ex.Message);
                }
            }
            return null;
        }

        internal static BitmapSource LoadIcon(string filePath, int size)
        {
            IntPtr hbmp = IntPtr.Zero;
            if (!_shellBroken)
            {
                try
                {
                    Guid iid = typeof(Native.IShellItemImageFactory).GUID;
                    Native.IShellItemImageFactory fac = null;
                    Native.SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out fac);
                    if (fac != null)
                    {
                        int hr = fac.GetImage(new Native.SIZE(size, size),
                            Native.SIIGBF.IconOnly | Native.SIIGBF.BiggerSizeOk | Native.SIIGBF.CropToSquare,
                            out hbmp);
                        if (hr == 0 && hbmp != IntPtr.Zero)
                        {
                            var bs = Imaging.CreateBitmapSourceFromHBitmap(
                                hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            Diag.Log("iconOK shell " + filePath);
                            return bs;
                        }
                        Diag.Log("icon shell hr=" + hr + " " + filePath);
                        _shellBroken = true;
                    }
                    else
                    {
                        Diag.Log("icon shell factory null " + filePath);
                        _shellBroken = true;
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log("icon shell EX " + ex.GetType().Name + " " + ex.Message + " " + filePath);
                    _shellBroken = true;
                }
                finally
                {
                    if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                }
            }

            try
            {
                var ico = Drw.Icon.ExtractAssociatedIcon(filePath);
                if (ico != null)
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    Diag.Log("iconOK fallback " + filePath);
                    return bs;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("icon fallback EX " + ex.Message);
            }

            Diag.Log("icon FAIL " + filePath);
            return null;
        }

        internal static List<string> DesktopItems()
        {
            if (_desktopCache != null) return _desktopCache;
            var list = new List<string>();
            try
            {
                string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                foreach (var f in System.IO.Directory.GetFileSystemEntries(d))
                {
                    string name = System.IO.Path.GetFileName(f);
                    if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(f);
                }
            }
            catch { }
            _desktopCache = list;
            return list;
        }
    }

    internal class MixSet
    {
        internal MediaPlayer[] Multi;
        internal System.Media.SoundPlayer Single;
    }

    internal static class Sfx
    {
        private const int Rate = 22050;
        // 值可能是 SoundPlayer[]（单通道，会被后一个音效打断）或 MediaPlayer[]（多通道，可叠加）
        private static readonly Dictionary<string, object> Sets =
            new Dictionary<string, object>();
        private static readonly System.Collections.Generic.Dictionary<string, long> LastMs =
            new System.Collections.Generic.Dictionary<string, long>();
        private static readonly Random Rng = new Random(91);
        internal static string Dir;

        private static string SfxDir()
        {
            if (!string.IsNullOrEmpty(Dir)) return Dir;
            try { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sfx"); }
            catch { return "sfx"; }
        }

        // 同材质有多个变体，每次随机取一个，避免同一个声音重复播放的机械感
        private static void Register(string key, params string[] files)
        {
            var list = new List<System.Media.SoundPlayer>();
            string dir = SfxDir();
            foreach (var f in files)
            {
                try
                {
                    string p = System.IO.Path.Combine(dir, f);
                    if (!System.IO.File.Exists(p)) continue;
                    var sp = new System.Media.SoundPlayer(p);
                    sp.Load();
                    list.Add(sp);
                }
                catch { }
            }
            if (list.Count > 0) Sets[key] = list.ToArray();
        }

        // 必须能同时响的音（爆炸、碎裂、消灭）：走 MediaPlayer，多通道互不打断
        private static void RegisterMix(string key, params string[] files)
        {
            var list = new List<MediaPlayer>();
            string dir = SfxDir();
            var seen = new List<string>();
            foreach (var f in files)
            {
                if (seen.Contains(f)) continue;
                seen.Add(f);
                try
                {
                    string p = System.IO.Path.Combine(dir, f);
                    if (!System.IO.File.Exists(p)) continue;
                    for (int i = 0; i < 3; i++)
                    {
                        var mp = new MediaPlayer();
                        mp.Open(new Uri(p));
                        mp.Volume = 1.0;
                        mp.IsMuted = false;
                        list.Add(mp);
                    }
                }
                catch { }
            }
            if (list.Count == 0) return;

            System.Media.SoundPlayer sp = null;
            try
            {
                string p0 = System.IO.Path.Combine(dir, files[0]);
                if (System.IO.File.Exists(p0)) { sp = new System.Media.SoundPlayer(p0); sp.Load(); }
            }
            catch { }

            Sets[key] = new MixSet { Multi = list.ToArray(), Single = sp };
        }

        // 把某些 key 的“上次播放时间”推到未来，让节流逻辑在这段时间内跳过它们
        internal static void Mute(long ms, params string[] keys)
        {
            try
            {
                long until = DateTime.Now.Ticks / 10000L + ms;
                foreach (var k in keys) LastMs[k] = until;
            }
            catch { }
        }

        private static void Fallback(string key, float[] data)
        {
            if (Sets.ContainsKey(key) || data == null) return;
            try { Sets[key] = new[] { Make(data) }; }
            catch { }
        }

        internal static void Init()
        {
            // 真实录音素材（exe 同目录 sfx/），缺文件时自动退回合成音兜底
            Register("stretch", "slingshot streched.wav");
            Register("launch", "boomerang_swish.wav");
            RegisterMix("explode", "tnt box explodes.wav");

            Register("hitWood",
                "wood collision a1.wav", "wood collision a2.wav", "wood collision a3.wav",
                "wood collision a4.wav", "wood collision a5.wav", "wood collision a6.wav");
            Register("damageWood",
                "wood damage a1.wav", "wood damage a2.wav", "wood damage a3.wav");
            RegisterMix("breakWood",
                "wood destroyed a1.wav", "wood destroyed a2.wav", "wood destroyed a3.wav");

            Register("hitGlass",
                "ice light collision a1.wav", "ice light collision a2.wav",
                "ice light collision a3.wav", "ice light collision a4.wav",
                "ice light collision a5.wav", "ice light collision a6.wav");
            Register("damageGlass",
                "light damage a1.wav", "light damage a2.wav", "light damage a3.wav");
            RegisterMix("breakGlass",
                "light destroyed a1.wav", "light destroyed a2.wav", "light destroyed a3.wav");

            Register("hitRock",
                "rock collision a1.wav", "rock collision a2.wav",
                "rock collision a3.wav", "rock collision a4.wav", "rock collision a5.wav");
            Register("damageRock",
                "rock damage a1.wav", "rock damage a2.wav", "rock damage a3.wav");
            RegisterMix("breakRock",
                "rock destroyed a1.wav", "rock destroyed a2.wav", "rock destroyed a3.wav");

            Register("hurt",
                "piglette damage a1.wav", "piglette damage a2.wav",
                "piglette damage a3.wav", "piglette damage a5.wav");
            RegisterMix("pop",
                "balloon_pop.wav", "balloon_pop.wav");
            Register("clear", "star_collect.wav");
            Register("click", "menu select.wav");

            // 兜底：素材缺失时用合成音，至少不会没声音
            Fallback("stretch", BuildStretch());
            Fallback("launch", BuildLaunch());
            Fallback("explode", BuildExplode());
            Fallback("hitWood", BuildWood(1.0));
            Fallback("damageWood", BuildWood(0.72));
            Fallback("breakWood", BuildWoodBreak());
            Fallback("hitGlass", BuildGlass(1.0));
            Fallback("damageGlass", BuildGlass(0.8));
            Fallback("breakGlass", BuildGlassBreak());
            Fallback("hitRock", BuildMetal(1.0));
            Fallback("damageRock", BuildMetal(0.78));
            Fallback("breakRock", BuildMetalBreak());
            Fallback("hurt", BuildHurt());
            Fallback("pop", BuildPop());
            Fallback("clear", BuildClear());
            Fallback("click", BuildClick());
        }

        private static int MinGap(string key)
        {
            if (key.StartsWith("break") || key == "explode" || key == "clear") return 420;
            if (key == "stretch") return 300;
            if (key == "hurt") return 260;
            if (key == "pop") return 200;
            if (key.StartsWith("hit") || key.StartsWith("damage")) return 150;
            return 120;
        }

        internal static void Play(string key)
        {
            try
            {
                long now = DateTime.Now.Ticks / 10000L;
                long last;
                if (LastMs.TryGetValue(key, out last) && now - last < MinGap(key)) return;
                LastMs[key] = now;

                var obj = Sets[key];
                if (obj == null) return;

                var mix = obj as MixSet;
                if (mix != null)
                {
                    MediaPlayer mp = null;
                    if (mix.Multi != null && mix.Multi.Length > 0)
                        mp = mix.Multi[Rng.Next(mix.Multi.Length)];

                    bool ok = false;
                    if (mp != null && mp.NaturalDuration.HasTimeSpan)
                    {
                        try { mp.Position = TimeSpan.Zero; mp.Play(); ok = true; } catch { ok = false; }
                    }
                    // 多通道还没加载完就退回单通道，宁可被打断也不能没有声音
                    if (!ok && mix.Single != null) mix.Single.Play();
                    return;
                }

                var spArr = obj as System.Media.SoundPlayer[];
                if (spArr != null && spArr.Length > 0) spArr[Rng.Next(spArr.Length)].Play();
            }
            catch { }
        }

        private static double Rnd(int i)
        {
            double v = Math.Sin(i * 12.9898) * 43758.5453;
            return v - Math.Floor(v);
        }

        private static void Lp(float[] s, double fc)
        {
            double a = 1.0 - Math.Exp(-2.0 * Math.PI * fc / Rate);
            double y = 0;
            for (int i = 0; i < s.Length; i++) { y += a * (s[i] - y); s[i] = (float)y; }
        }

        private static void Hp(float[] s, double fc)
        {
            double a = 1.0 - Math.Exp(-2.0 * Math.PI * fc / Rate);
            double y = 0;
            for (int i = 0; i < s.Length; i++) { y += a * (s[i] - y); s[i] = (float)(s[i] - y); }
        }

        private static void Mode(float[] s, double f, double amp, double decay)
        {
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                s[i] += (float)(Math.Sin(2 * Math.PI * f * t) * amp * Math.Exp(-t * decay));
            }
        }

        private static float[] BuildStretch()
        {
            double dur = 0.30;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++) nz[i] = (float)(Rnd(i) * 2 - 1);
            // 皮筋是橡胶摩擦：只要很低的沙沙底噪，高频噪声全部滤掉（否则就是刺啦刺啦）
            Lp(nz, 520);
            Hp(nz, 70);
            double p1 = 0, p2 = 0, p3 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;

                // 主音：被拉紧的橡胶低频“嗡”，随拉伸略微升高
                double f1 = 58 + 44 * u;
                p1 += 2 * Math.PI * f1 / Rate;
                // 次音：中低频摩擦，一点点抖动模拟皮筋表面摩擦
                double f2 = 168 + 96 * u;
                double wob = 1.0 + 0.02 * Math.Sin(2 * Math.PI * 5.5 * t);
                p2 += 2 * Math.PI * f2 * wob / Rate;
                // 第三层：更低的共鸣，给它"厚"的感觉
                double f3 = 33 + 12 * u;
                p3 += 2 * Math.PI * f3 / Rate;

                // 包络：缓慢起，尾端自然收敛（不是爆发式，避免"刺"）
                double env = Math.Pow(u, 0.55) * (1 - Math.Pow(u, 6));

                s[i] = (float)((Math.Sin(p1) * 0.30
                    + Math.Sin(p1 * 2) * 0.10
                    + Math.Sin(p2) * 0.16
                    + Math.Sin(p3) * 0.14
                    + nz[i] * 0.10) * env);
            }
            return s;
        }

        private static float[] BuildLaunch()
        {
            double dur = 0.42;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++) nz[i] = (float)(Rnd(i) * 2 - 1);
            Lp(nz, 4200);
            Hp(nz, 420);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;
                double f = 52 + 170 * Math.Exp(-u * 2.4);
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Exp(-u * 3.0);
                s[i] = (float)((Math.Sin(phase) * 0.50 + nz[i] * 0.44 * Math.Exp(-u * 1.1)) * env);
            }
            return s;
        }

        private static float[] BuildBoost()
        {
            double dur = 0.36;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++) nz[i] = (float)(Rnd(i) * 2 - 1);
            Lp(nz, 5200);
            Hp(nz, 900);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;
                double f = 260 + 1500 * u * u;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Pow(u, 0.5) * Math.Exp(-u * 1.6);
                s[i] = (float)((Math.Sin(phase) * 0.26 + nz[i] * 0.40) * env);
            }
            return s;
        }

        private static float[] BuildWood(double pitch)
        {
            double dur = 0.26;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++)
                nz[i] = (float)((Rnd(i) * 2 - 1) * Math.Exp(-(double)i / Rate * 72));
            Lp(nz, 800);
            for (int i = 0; i < n; i++) s[i] = nz[i] * 0.34f;
            Mode(s, 186 * pitch, 0.34, 26);
            Mode(s, 430 * pitch, 0.22, 38);
            Mode(s, 620 * pitch, 0.08, 56);
            Lp(s, 2600);
            Norm(s, 0.88f);
            return s;
        }

        private static float[] BuildWoodBreak()
        {
            double dur = 0.55;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var rnd = new Random(31);
            for (int k = 0; k < 9; k++)
            {
                int st = (int)(Rate * (0.01 + rnd.NextDouble() * 0.34));
                int len = (int)(Rate * (0.025 + rnd.NextDouble() * 0.05));
                for (int i = 0; i < len && st + i < n; i++)
                {
                    double t = (double)i / Rate;
                    s[st + i] += (float)((Rnd(st + i) * 2 - 1) * 0.42 * Math.Exp(-t * 85));
                }
            }
            // 原来是 Hp 700（只留高频，听起来就是刺啦），改成低通保留闷响
            Lp(s, 2400);
            Mode(s, 88, 0.26, 5);
            Mode(s, 190, 0.17, 7);
            Mode(s, 430, 0.11, 12);
            Mode(s, 820, 0.05, 18);
            Lp(s, 2800);
            Norm(s, 0.90f);
            return s;
        }

        private static float[] BuildGlass(double pitch)
        {
            double dur = 0.22;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++)
                nz[i] = (float)((Rnd(i) * 2 - 1) * Math.Exp(-(double)i / Rate * 92));
            // 原来是 Hp 2400 + 3.3k/5.1k/7.2k 三个尖峰，撞玻璃像指甲刮黑板；改成闷的钝响
            Lp(nz, 1500);
            for (int i = 0; i < n; i++) s[i] = nz[i] * 0.24f;
            Mode(s, 140, 0.18, 12);
            Mode(s, 620 * pitch, 0.30, 30);
            Mode(s, 1150 * pitch, 0.14, 44);
            Mode(s, 1700 * pitch, 0.06, 58);
            Lp(s, 2800);
            Norm(s, 0.86f);
            return s;
        }

        private static float[] BuildGlassBreak()
        {
            double dur = 0.55;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var rnd = new Random(77);
            for (int k = 0; k < 14; k++)
            {
                int st = (int)(Rate * rnd.NextDouble() * 0.30);
                int len = (int)(Rate * (0.02 + rnd.NextDouble() * 0.06));
                for (int i = 0; i < len && st + i < n; i++)
                {
                    double t = (double)i / Rate;
                    s[st + i] += (float)((Rnd(st + i) * 2 - 1) * 0.36 * Math.Exp(-t * 68));
                }
            }
            Lp(s, 2600);
            Mode(s, 95, 0.24, 6);
            Mode(s, 240, 0.16, 9);
            Mode(s, 780, 0.11, 14);
            Mode(s, 1500, 0.05, 22);
            Lp(s, 3000);
            Norm(s, 0.90f);
            return s;
        }

        private static float[] BuildMetal(double pitch)
        {
            double dur = 0.34;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++)
                nz[i] = (float)((Rnd(i) * 2 - 1) * Math.Exp(-(double)i / Rate * 58));
            Lp(nz, 1300);
            for (int i = 0; i < n; i++) s[i] = nz[i] * 0.26f;
            Mode(s, 148 * pitch, 0.30, 16);
            Mode(s, 512 * pitch, 0.19, 22);
            Mode(s, 780 * pitch, 0.08, 30);
            Lp(s, 2600);
            Norm(s, 0.88f);
            return s;
        }

        private static float[] BuildMetalBreak()
        {
            double dur = 0.70;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++)
                nz[i] = (float)((Rnd(i) * 2 - 1) * Math.Exp(-(double)i / Rate * 26));
            Lp(nz, 1800);
            for (int i = 0; i < n; i++) s[i] = nz[i] * 0.32f;
            Mode(s, 96, 0.34, 6);
            Mode(s, 205, 0.24, 8);
            Mode(s, 470, 0.17, 13);
            Mode(s, 900, 0.08, 19);
            Lp(s, 2400);
            Norm(s, 0.90f);
            return s;
        }

        // 红球爆炸：亚低频冲击 + 一层闷滚雷，全程低通，绝不刺耳
        private static float[] BuildExplode()
        {
            double dur = 0.90;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++) nz[i] = (float)(Rnd(i) * 2 - 1);
            Lp(nz, 420);

            double ph1 = 0, ph2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;

                double f1 = 30 + 46 * Math.Exp(-u * 2.6);   // 76Hz 下沉到 30Hz
                ph1 += 2 * Math.PI * f1 / Rate;
                double f2 = 118 - 40 * u;
                ph2 += 2 * Math.PI * f2 / Rate;

                double env = Math.Exp(-u * 2.4);
                double crack = Math.Exp(-u * 16.0);

                s[i] = (float)((Math.Sin(ph1) * 0.52
                    + Math.Sin(ph2) * 0.16
                    + nz[i] * (0.26 * Math.Exp(-u * 6.0) + 0.06)
                    + (Rnd(i + 977) * 2 - 1) * 0.10 * crack) * env);
            }
            Lp(s, 1400);
            Norm(s, 0.92f);
            return s;
        }

        private static void Norm(float[] s, float peak)
        {
            float m = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float a = s[i] < 0 ? -s[i] : s[i];
                if (a > m) m = a;
            }
            if (m < 1e-5f) return;
            float k = peak / m;
            for (int i = 0; i < s.Length; i++) s[i] *= k;
        }

        private static float[] BuildHurt()
        {
            double dur = 0.34;
            int n = (int)(Rate * dur);
            var s = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;
                double f0 = 150 + 330 * Math.Exp(-u * 2.0);
                phase += 2 * Math.PI * f0 / Rate;
                double env = Math.Sin(Math.PI * Math.Min(1.0, u * 1.05));
                double v = Math.Sin(phase) * 0.48 + Math.Sin(2 * phase) * 0.24 + Math.Sin(3 * phase) * 0.12;
                double nasal = Math.Sin(phase * 2.4) * 0.05 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 26 * t));
                s[i] = (float)((v + nasal) * env);
            }
            return s;
        }

        private static float[] BuildPop()
        {
            double dur = 0.24;
            int n = (int)(Rate * dur);
            var s = new float[n];
            var nz = new float[n];
            for (int i = 0; i < n; i++) nz[i] = (float)(Rnd(i) * 2 - 1);
            Hp(nz, 1800);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double u = t / dur;
                double f = 900 - 640 * u;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Exp(-u * 5);
                s[i] = (float)((Math.Sin(phase) * 0.30 + nz[i] * 0.48 * Math.Exp(-u * 7)) * env);
            }
            return s;
        }

        private static float[] BuildClear()
        {
            double[] notes = { 523.25, 659.25, 783.99, 1046.50, 1318.51 };
            int per = (int)(Rate * 0.11);
            int n = per * notes.Length + (int)(Rate * 0.26);
            var s = new float[n];
            for (int k = 0; k < notes.Length; k++)
            {
                int st = k * per;
                for (int i = 0; st + i < n && i < per * 6; i++)
                {
                    double t = (double)i / Rate;
                    double env = Math.Exp(-t * 7.5);
                    double v = Math.Sin(2 * Math.PI * notes[k] * t) * 0.32
                             + Math.Sin(4 * Math.PI * notes[k] * t) * 0.11;
                    s[st + i] += (float)(v * env);
                }
            }
            return s;
        }

        private static float[] BuildClick()
        {
            int n = (int)(Rate * 0.05);
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                s[i] = (float)((Rnd(i) * 2 - 1) * 0.40 * Math.Exp(-t * 130)
                             + Math.Sin(2 * Math.PI * 1500 * t) * 0.22 * Math.Exp(-t * 85));
            }
            return s;
        }

        private static System.Media.SoundPlayer Make(float[] samples)
        {
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + samples.Length * 2);
            bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(Rate);
            bw.Write(Rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(samples.Length * 2);
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i];
                if (v > 1f) v = 1f;
                if (v < -1f) v = -1f;
                bw.Write((short)(v * 30000));
            }
            bw.Flush();
            ms.Position = 0;
            var sp = new System.Media.SoundPlayer(ms);
            sp.Load();
            return sp;
        }
    }

    internal static class Art
    {
        internal static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        internal static SolidColorBrush BA(string hex, double alpha)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        }

        private static Ellipse El(double w, double h, double x, double y, Brush fill)
        {
            var e = new Ellipse { Width = w, Height = h, Fill = fill };
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            return e;
        }

        internal static Canvas DrawPig(bool boss, int variant)
        {
            double k = boss ? 1.78 : 1.22;
            double W = 150 * k;
            double H = 150 * k;
            var c = new Canvas { Width = W, Height = H };

            Func<double, double> Z = v => v * k;

            // ears
            double[] earX = { 26, 86 };
            for (int i = 0; i < 2; i++)
            {
                var ear = new Ellipse
                {
                    Width = Z(38),
                    Height = Z(42),
                    Fill = B("#EE879E"),
                    Stroke = B("#CE687D"),
                    StrokeThickness = Z(1.4)
                };
                Canvas.SetLeft(ear, Z(earX[i]));
                Canvas.SetTop(ear, Z(16));
                ear.RenderTransform = new RotateTransform(i == 0 ? -22 : 22, Z(19), Z(21));
                c.Children.Add(ear);
            }

            // head
            var head = new Ellipse { Width = Z(112), Height = Z(100) };
            var rg = new RadialGradientBrush
            {
                Center = new Point(0.42, 0.3),
                GradientOrigin = new Point(0.42, 0.3),
                RadiusX = 0.8,
                RadiusY = 0.8
            };
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFD9E2"), 0));
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F39AA6"), 0.58));
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#D96F82"), 1));
            head.Fill = rg;
            head.Stroke = B("#BE5B70");
            head.StrokeThickness = Z(1.4);
            Canvas.SetLeft(head, Z(19));
            Canvas.SetTop(head, Z(38));
            c.Children.Add(head);

            // cheeks
            c.Children.Add(El(Z(30), Z(18), Z(20), Z(80), BA("#EF6E86", 0.42)));
            c.Children.Add(El(Z(30), Z(18), Z(100), Z(80), BA("#EF6E86", 0.42)));

            // snout
            var snout = El(Z(48), Z(34), Z(51), Z(78), B("#E87A90"));
            snout.Stroke = B("#C55F74");
            snout.StrokeThickness = Z(1.4);
            c.Children.Add(snout);
            c.Children.Add(El(Z(8), Z(11), Z(61), Z(88), B("#9E4152")));
            c.Children.Add(El(Z(8), Z(11), Z(81), Z(88), B("#9E4152")));

            if (boss)
            {
                // angry brows
                for (int i = 0; i < 2; i++)
                {
                    var brow = new Rectangle
                    {
                        Width = Z(30),
                        Height = Z(6),
                        Fill = B("#3A262B"),
                        RadiusX = Z(3),
                        RadiusY = Z(3)
                    };
                    Canvas.SetLeft(brow, Z(i == 0 ? 44 : 76));
                    Canvas.SetTop(brow, Z(50));
                    brow.RenderTransform = new RotateTransform(i == 0 ? 12 : -12, Z(15), Z(3));
                    c.Children.Add(brow);
                }
            }

            // eyes
            c.Children.Add(El(Z(24), Z(25), Z(43), Z(56), Brushes.White));
            c.Children.Add(El(Z(24), Z(25), Z(83), Z(56), Brushes.White));
            double pupil = boss ? 11 : 13;
            c.Children.Add(El(Z(pupil), Z(pupil + 1), Z(52), Z(63), B("#2A1B20")));
            c.Children.Add(El(Z(pupil), Z(pupil + 1), Z(92), Z(63), B("#2A1B20")));
            c.Children.Add(El(Z(5), Z(5), Z(54), Z(66), Brushes.White));
            c.Children.Add(El(Z(5), Z(5), Z(94), Z(66), Brushes.White));

            // mouth
            var mouth = new Polyline
            {
                Stroke = B("#8E3A4A"),
                StrokeThickness = Z(3),
                StrokeLineJoin = PenLineJoin.Round
            };
            mouth.Points.Add(new Point(Z(64), Z(106)));
            mouth.Points.Add(new Point(Z(75), Z(112)));
            mouth.Points.Add(new Point(Z(86), Z(106)));
            c.Children.Add(mouth);

            if (boss)
            {
                // tusks
                for (int i = 0; i < 2; i++)
                {
                    var tusk = new Polygon { Fill = Brushes.White, Stroke = B("#D9D2C8"), StrokeThickness = 1 };
                    double tx = i == 0 ? 56 : 92;
                    tusk.Points.Add(new Point(Z(tx), Z(104)));
                    tusk.Points.Add(new Point(Z(tx + 6), Z(104)));
                    tusk.Points.Add(new Point(Z(tx - 1), Z(90)));
                    c.Children.Add(tusk);
                }

                // gold tie
                var tie = new Polygon { Fill = B("#E8B23C"), Stroke = B("#B98A24"), StrokeThickness = Z(1.2) };
                tie.Points.Add(new Point(Z(62), Z(132)));
                tie.Points.Add(new Point(Z(88), Z(132)));
                tie.Points.Add(new Point(Z(83), Z(150)));
                tie.Points.Add(new Point(Z(67), Z(150)));
                c.Children.Add(tie);

                // hair strands
                for (int i = 0; i < 3; i++)
                {
                    var hair = new Line
                    {
                        X1 = Z(70 + i * 9), Y1 = Z(38),
                        X2 = Z(70 + i * 9 - 3), Y2 = Z(24),
                        Stroke = B("#4A2E34"),
                        StrokeThickness = Z(2)
                    };
                    c.Children.Add(hair);
                }
            }
            else
            {
                // badge
                var badge = new Rectangle
                {
                    Width = Z(42),
                    Height = Z(20),
                    Fill = Brushes.White,
                    Stroke = B("#C9D2DA"),
                    StrokeThickness = Z(1),
                    RadiusX = Z(2),
                    RadiusY = Z(2)
                };
                Canvas.SetLeft(badge, Z(54));
                Canvas.SetTop(badge, Z(124));
                c.Children.Add(badge);

                string[] tagColors = { "#3D8BFD", "#E84E5A", "#22A06B" };
                var tag = new Rectangle
                {
                    Width = Z(42),
                    Height = Z(6),
                    Fill = B(tagColors[variant % 3]),
                    RadiusX = Z(2),
                    RadiusY = Z(2)
                };
                Canvas.SetLeft(tag, Z(54));
                Canvas.SetTop(tag, Z(124));
                c.Children.Add(tag);
            }

            return c;
        }

        internal static Canvas DrawPig2(bool boss, int variant)
        {
            double k = boss ? 1.78 : 1.22;
            double W = 150 * k;
            double H = 150 * k;
            var c = new Canvas { Width = W, Height = H };
            Func<double, double> Z = v => v * k;

            Func<string, string, string, double, double, RadialGradientBrush> Rad =
                delegate(string a, string b2, string e2, double cx, double cy)
                {
                    var g = new RadialGradientBrush
                    {
                        Center = new Point(cx, cy),
                        GradientOrigin = new Point(cx, cy),
                        RadiusX = 0.82,
                        RadiusY = 0.82
                    };
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b2), 0.5));
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(e2), 1));
                    return g;
                };

            c.Children.Add(El(Z(74), Z(13), Z(38), Z(131), BA("#07120A", 0.16)));

            double[] earX = { 16, 109 };
            for (int i = 0; i < 2; i++)
            {
                var ear = new Ellipse
                {
                    Width = Z(26),
                    Height = Z(31),
                    Fill = Rad("#A6DB70", "#5FA53D", "#356B26", 0.45, 0.3),
                    Stroke = B("#24561B"),
                    StrokeThickness = Z(1.4)
                };
                Canvas.SetLeft(ear, Z(earX[i]));
                Canvas.SetTop(ear, Z(10));
                ear.RenderTransform = new RotateTransform(i == 0 ? -24 : 24, Z(13), Z(15.5));
                c.Children.Add(ear);

                var inner = new Ellipse { Width = Z(11), Height = Z(13), Fill = BA("#2B5320", 0.6) };
                Canvas.SetLeft(inner, Z(earX[i] + 7));
                Canvas.SetTop(inner, Z(17));
                inner.RenderTransform = new RotateTransform(i == 0 ? -24 : 24, Z(13), Z(15.5));
                c.Children.Add(inner);
            }

            var body = new Rectangle
            {
                Width = Z(58),
                Height = Z(36),
                RadiusX = Z(18),
                RadiusY = Z(18),
                Fill = Rad("#8CC955", "#4E9130", "#2A5C1D", 0.35, 0.2),
                Stroke = B("#24561B"),
                StrokeThickness = Z(1.4)
            };
            Canvas.SetLeft(body, Z(46));
            Canvas.SetTop(body, Z(112));
            c.Children.Add(body);

            var head = new Ellipse
            {
                Width = Z(100),
                Height = Z(96),
                Fill = Rad("#BEEA82", "#69B33F", "#2C6720", 0.36, 0.24),
                Stroke = B("#24561B"),
                StrokeThickness = Z(1.6)
            };
            Canvas.SetLeft(head, Z(25));
            Canvas.SetTop(head, Z(32));
            c.Children.Add(head);

            c.Children.Add(El(Z(36), Z(26), Z(36), Z(42), BA("#FFFFFF", 0.26)));
            c.Children.Add(El(Z(42), Z(30), Z(72), Z(94), BA("#0C2606", 0.20)));

            c.Children.Add(El(Z(24), Z(15), Z(28), Z(84), BA("#E06060", 0.26)));
            c.Children.Add(El(Z(24), Z(15), Z(100), Z(84), BA("#E06060", 0.26)));

            var snout = new Ellipse
            {
                Width = Z(48),
                Height = Z(33),
                Fill = Rad("#9BD664", "#4E9232", "#285E1C", 0.42, 0.3),
                Stroke = B("#22511A"),
                StrokeThickness = Z(1.6)
            };
            Canvas.SetLeft(snout, Z(51));
            Canvas.SetTop(snout, Z(76));
            c.Children.Add(snout);

            var n1 = new Ellipse { Width = Z(9), Height = Z(12), Fill = B("#1F2F0E") };
            Canvas.SetLeft(n1, Z(60));
            Canvas.SetTop(n1, Z(84));
            c.Children.Add(n1);

            var n2 = new Ellipse { Width = Z(9), Height = Z(12), Fill = B("#1F2F0E") };
            Canvas.SetLeft(n2, Z(80));
            Canvas.SetTop(n2, Z(84));
            c.Children.Add(n2);

            double eyeY = boss ? 50 : 53;
            double eyeH = boss ? 25 : 28;
            for (int i = 0; i < 2; i++)
            {
                var eye = new Ellipse
                {
                    Width = Z(26),
                    Height = Z(eyeH),
                    Fill = B("#FBFCF4"),
                    Stroke = B("#3C5C2E"),
                    StrokeThickness = Z(1.2)
                };
                Canvas.SetLeft(eye, Z(i == 0 ? 40 : 84));
                Canvas.SetTop(eye, Z(eyeY));
                c.Children.Add(eye);
            }

            double ppx = boss ? 50 : 49;
            double ppy = boss ? 57 : 60;

            var pu1 = new Ellipse { Width = Z(12), Height = Z(13), Fill = B("#1A2A10") };
            Canvas.SetLeft(pu1, Z(ppx));
            Canvas.SetTop(pu1, Z(ppy));
            c.Children.Add(pu1);

            var pu2 = new Ellipse { Width = Z(12), Height = Z(13), Fill = B("#1A2A10") };
            Canvas.SetLeft(pu2, Z(ppx + 44));
            Canvas.SetTop(pu2, Z(ppy));
            c.Children.Add(pu2);

            c.Children.Add(El(Z(5), Z(5), Z(ppx + 1), Z(ppy + 1), BA("#FFFFFF", 0.92)));
            c.Children.Add(El(Z(5), Z(5), Z(ppx + 45), Z(ppy + 1), BA("#FFFFFF", 0.92)));

            var mouth = new Polyline
            {
                Stroke = B("#7E3320"),
                StrokeThickness = Z(2.6),
                StrokeLineJoin = PenLineJoin.Round
            };
            mouth.Points.Add(new Point(Z(64), Z(108)));
            mouth.Points.Add(new Point(Z(75), Z(114)));
            mouth.Points.Add(new Point(Z(86), Z(108)));
            c.Children.Add(mouth);

            if (boss)
            {
                for (int i = 0; i < 2; i++)
                {
                    var brow = new Rectangle
                    {
                        Width = Z(28),
                        Height = Z(6),
                        Fill = B("#3A262B"),
                        RadiusX = Z(3),
                        RadiusY = Z(3)
                    };
                    Canvas.SetLeft(brow, Z(i == 0 ? 38 : 85));
                    Canvas.SetTop(brow, Z(44));
                    brow.RenderTransform = new RotateTransform(i == 0 ? 14 : -14, Z(14), Z(3));
                    c.Children.Add(brow);
                }

                for (int i = 0; i < 2; i++)
                {
                    var tusk = new Polygon { Fill = B("#FFFDF2"), Stroke = B("#C9C2B2"), StrokeThickness = 1 };
                    double tx = i == 0 ? 60 : 87;
                    tusk.Points.Add(new Point(Z(tx), Z(111)));
                    tusk.Points.Add(new Point(Z(tx + 7), Z(111)));
                    tusk.Points.Add(new Point(Z(tx + 1), Z(96)));
                    c.Children.Add(tusk);
                }

                var tie = new Polygon { Fill = B("#D8A63A"), Stroke = B("#A87D22"), StrokeThickness = Z(1.2) };
                tie.Points.Add(new Point(Z(65), Z(120)));
                tie.Points.Add(new Point(Z(85), Z(120)));
                tie.Points.Add(new Point(Z(80), Z(146)));
                tie.Points.Add(new Point(Z(70), Z(146)));
                c.Children.Add(tie);

                for (int i = 0; i < 3; i++)
                {
                    var hair = new Line
                    {
                        X1 = Z(68 + i * 9), Y1 = Z(34),
                        X2 = Z(68 + i * 9 - 4), Y2 = Z(20),
                        Stroke = B("#3A262B"),
                        StrokeThickness = Z(2.2)
                    };
                    c.Children.Add(hair);
                }
            }
            else
            {
                var badge = new Rectangle
                {
                    Width = Z(34),
                    Height = Z(16),
                    Fill = B("#F5F8FA"),
                    Stroke = B("#B9C4CE"),
                    StrokeThickness = Z(1),
                    RadiusX = Z(2),
                    RadiusY = Z(2)
                };
                Canvas.SetLeft(badge, Z(58));
                Canvas.SetTop(badge, Z(126));
                c.Children.Add(badge);

                string[] tagColors = { "#3D8BFD", "#E84E5A", "#22A06B" };
                var tag = new Rectangle
                {
                    Width = Z(34),
                    Height = Z(5),
                    Fill = B(tagColors[variant % 3]),
                    RadiusX = Z(2),
                    RadiusY = Z(2)
                };
                Canvas.SetLeft(tag, Z(58));
                Canvas.SetTop(tag, Z(126));
                c.Children.Add(tag);
            }

            return c;
        }

        internal static Canvas DrawGuy(bool boss, int variant)
        {
            double k = boss ? 1.34 : 1.0;
            double W = 100 * k;
            double H = 124 * k;
            var c = new Canvas { Width = W, Height = H };
            Func<double, double> Z = v => v * k;

            Func<string, string, string, double, double, RadialGradientBrush> Rad =
                delegate(string a, string b2, string e2, double cx, double cy)
                {
                    var g = new RadialGradientBrush
                    {
                        Center = new Point(cx, cy),
                        GradientOrigin = new Point(cx, cy),
                        RadiusX = 0.80,
                        RadiusY = 0.80
                    };
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b2), 0.52));
                    g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(e2), 1));
                    return g;
                };

            string accent = variant == 0 ? "#C0392B" : (variant == 1 ? "#1B6FD6" : "#148F6B");

            c.Children.Add(El(Z(62), Z(12), Z(19), Z(111), BA("#0A1020", 0.15)));

            var suit = new Rectangle
            {
                Width = Z(80),
                Height = Z(48),
                RadiusX = Z(15),
                RadiusY = Z(15),
                Fill = Rad("#5C6779", "#3A4456", "#222A36", 0.30, 0.22),
                Stroke = B("#161C26"),
                StrokeThickness = Z(1.4)
            };
            Canvas.SetLeft(suit, Z(10));
            Canvas.SetTop(suit, Z(78));
            c.Children.Add(suit);

            var shirt = new Rectangle
            {
                Width = Z(24),
                Height = Z(44),
                RadiusX = Z(5),
                RadiusY = Z(5),
                Fill = B("#F4F7FA"),
                Stroke = B("#C2CBD6"),
                StrokeThickness = Z(1)
            };
            Canvas.SetLeft(shirt, Z(38));
            Canvas.SetTop(shirt, Z(80));
            c.Children.Add(shirt);

            var tie = new Polygon { Fill = B(accent), Stroke = BA("#000000", 0.30), StrokeThickness = 1 };
            tie.Points.Add(new Point(Z(46), Z(84)));
            tie.Points.Add(new Point(Z(54), Z(84)));
            tie.Points.Add(new Point(Z(53), Z(106)));
            tie.Points.Add(new Point(Z(50), Z(114)));
            tie.Points.Add(new Point(Z(47), Z(106)));
            c.Children.Add(tie);

            var knot = new Polygon { Fill = B(accent) };
            knot.Points.Add(new Point(Z(45), Z(82)));
            knot.Points.Add(new Point(Z(55), Z(82)));
            knot.Points.Add(new Point(Z(53), Z(88)));
            knot.Points.Add(new Point(Z(47), Z(88)));
            c.Children.Add(knot);

            for (int i = 0; i < 2; i++)
            {
                var lapel = new Polygon { Fill = BA("#0C1119", 0.42) };
                if (i == 0)
                {
                    lapel.Points.Add(new Point(Z(31), Z(80)));
                    lapel.Points.Add(new Point(Z(38), Z(80)));
                    lapel.Points.Add(new Point(Z(38), Z(104)));
                }
                else
                {
                    lapel.Points.Add(new Point(Z(69), Z(80)));
                    lapel.Points.Add(new Point(Z(62), Z(80)));
                    lapel.Points.Add(new Point(Z(62), Z(104)));
                }
                c.Children.Add(lapel);
            }

            var neck = new Rectangle
            {
                Width = Z(22),
                Height = Z(15),
                RadiusX = Z(5),
                RadiusY = Z(5),
                Fill = Rad("#E7B58C", "#D09566", "#B57B4E", 0.40, 0.30)
            };
            Canvas.SetLeft(neck, Z(39));
            Canvas.SetTop(neck, Z(70));
            c.Children.Add(neck);

            double hw = boss ? 78 : 76;
            double hh = boss ? 78 : 74;
            double hx = (100 - hw) / 2.0;

            var head = new Ellipse
            {
                Width = Z(hw),
                Height = Z(hh),
                Fill = Rad("#FBD9BA", "#EDB389", "#CB8A5D", 0.36, 0.28),
                Stroke = B("#96603F"),
                StrokeThickness = Z(1.3)
            };
            Canvas.SetLeft(head, Z(hx));
            Canvas.SetTop(head, Z(14));
            c.Children.Add(head);

            double[] earX = { hx - 2, hx + hw - 5 };
            for (int i = 0; i < 2; i++)
            {
                var ear = new Ellipse
                {
                    Width = Z(9),
                    Height = Z(15),
                    Fill = Rad("#F0C29E", "#DDA177", "#BE8357", 0.45, 0.35),
                    Stroke = BA("#96603F", 0.55),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(ear, Z(earX[i]));
                Canvas.SetTop(ear, Z(52));
                c.Children.Add(ear);
            }

            if (!boss)
            {
                var hair = new Rectangle
                {
                    Width = Z(hw + 4),
                    Height = Z(24),
                    RadiusX = Z(13),
                    RadiusY = Z(13),
                    Fill = Rad("#4B3524", "#2E2015", "#1A120B", 0.42, 0.22),
                    Stroke = BA("#12100C", 0.5),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(hair, Z(hx - 2));
                Canvas.SetTop(hair, Z(11));
                c.Children.Add(hair);

                var fringe = new Polygon { Fill = B("#332315") };
                fringe.Points.Add(new Point(Z(hx + 14), Z(14)));
                fringe.Points.Add(new Point(Z(hx + 30), Z(14)));
                fringe.Points.Add(new Point(Z(hx + 24), Z(27)));
                c.Children.Add(fringe);
            }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    var sideHair = new Ellipse
                    {
                        Width = Z(8),
                        Height = Z(20),
                        Fill = Rad("#8C8A82", "#6C6A63", "#4E4C46", 0.42, 0.3)
                    };
                    Canvas.SetLeft(sideHair, Z(i == 0 ? hx - 4 : hx + hw - 5));
                    Canvas.SetTop(sideHair, Z(24));
                    c.Children.Add(sideHair);
                }

                var bald = El(Z(50), Z(12), Z(50 - 25), Z(22), BA("#FFFFFF", 0.36));
                c.Children.Add(bald);

                for (int i = 0; i < 2; i++)
                {
                    var wrinkle = new Rectangle
                    {
                        Width = Z(20),
                        Height = Z(2),
                        RadiusX = Z(1),
                        RadiusY = Z(1),
                        Fill = BA("#A9714C", 0.45)
                    };
                    Canvas.SetLeft(wrinkle, Z(40 + i * 18));
                    Canvas.SetTop(wrinkle, Z(23));
                    c.Children.Add(wrinkle);
                }
            }

            double browY = boss ? 33 : 38;
            double[] browX = { 22, 56 };
            for (int i = 0; i < 2; i++)
            {
                var brow = new Rectangle
                {
                    Width = Z(boss ? 24 : 21),
                    Height = Z(boss ? 7 : 5),
                    RadiusX = Z(2),
                    RadiusY = Z(2),
                    Fill = B(boss ? "#2A1C13" : "#3B2A1D")
                };
                Canvas.SetLeft(brow, Z(browX[i]));
                Canvas.SetTop(brow, Z(browY));
                brow.RenderTransformOrigin = new Point(0.5, 0.5);
                double tilt = boss ? (i == 0 ? 18.0 : -18.0) : (i == 0 ? 5.0 : -5.0);
                brow.RenderTransform = new RotateTransform(tilt);
                c.Children.Add(brow);
            }

            double eyeY = boss ? 44 : 46;
            double[] eyeX = { 24, 55 };
            for (int i = 0; i < 2; i++)
            {
                var eye = new Ellipse
                {
                    Width = Z(boss ? 20 : 19),
                    Height = Z(boss ? 14 : 17),
                    Fill = B("#FCFDFF"),
                    Stroke = B("#8A5A3C"),
                    StrokeThickness = Z(1.1)
                };
                Canvas.SetLeft(eye, Z(eyeX[i]));
                Canvas.SetTop(eye, Z(eyeY));
                c.Children.Add(eye);

                double pw = boss ? 7 : 8;
                var pupil = new Ellipse { Width = Z(pw), Height = Z(boss ? 8 : 10), Fill = B("#1A1A21") };
                Canvas.SetLeft(pupil, Z(eyeX[i] + (boss ? 7 : 6)));
                Canvas.SetTop(pupil, Z(eyeY + (boss ? 3 : 3)));
                c.Children.Add(pupil);

                c.Children.Add(El(Z(3), Z(3), Z(eyeX[i] + (boss ? 9 : 8)), Z(eyeY + 3), BA("#FFFFFF", 0.9)));
            }

            if (!boss)
            {
                for (int i = 0; i < 2; i++)
                {
                    var lens = new Ellipse
                    {
                        Width = Z(25),
                        Height = Z(24),
                        Stroke = B("#22303F"),
                        StrokeThickness = Z(1.5),
                        Fill = BA("#CFE6F5", 0.10)
                    };
                    Canvas.SetLeft(lens, Z(eyeX[i] - 3));
                    Canvas.SetTop(lens, Z(eyeY - 4));
                    c.Children.Add(lens);
                }
                var bridge = new Rectangle { Width = Z(9), Height = Z(2), Fill = B("#22303F") };
                Canvas.SetLeft(bridge, Z(46));
                Canvas.SetTop(bridge, Z(eyeY + 8));
                c.Children.Add(bridge);
            }

            double nw = boss ? 27 : 22;
            var nose = new Ellipse
            {
                Width = Z(nw),
                Height = Z(boss ? 22 : 18),
                Fill = Rad("#F6CFAE", "#E2A87E", "#C5885B", 0.40, 0.34),
                Stroke = BA("#8A5A3C", 0.55),
                StrokeThickness = 1
            };
            Canvas.SetLeft(nose, Z(50 - nw / 2));
            Canvas.SetTop(nose, Z(boss ? 56 : 57));
            c.Children.Add(nose);
            c.Children.Add(El(Z(9), Z(5), Z(46), Z(boss ? 59 : 60), BA("#FFFFFF", 0.42)));

            if (boss)
            {
                c.Children.Add(El(Z(46), Z(14), Z(27), Z(84), BA("#D69A6D", 0.5)));
            }

            var mouth = new Polyline
            {
                Stroke = B("#7A4A33"),
                StrokeThickness = Z(1.9),
                StrokeLineJoin = PenLineJoin.Round
            };
            mouth.Points.Add(new Point(Z(45), Z(boss ? 73 : 72)));
            mouth.Points.Add(new Point(Z(50), Z(boss ? 75 : 74)));
            mouth.Points.Add(new Point(Z(55), Z(boss ? 73 : 72)));
            c.Children.Add(mouth);

            var card = new Rectangle
            {
                Width = Z(boss ? 24 : 21),
                Height = Z(boss ? 16 : 14),
                RadiusX = Z(3),
                RadiusY = Z(3),
                Fill = B("#FAFBFC"),
                Stroke = B("#BFC8D3"),
                StrokeThickness = 1
            };
            Canvas.SetLeft(card, Z(boss ? 64 : 66));
            Canvas.SetTop(card, Z(boss ? 96 : 94));
            c.Children.Add(card);

            var strip = new Rectangle { Width = Z(boss ? 24 : 21), Height = Z(4), Fill = B(accent) };
            Canvas.SetLeft(strip, Z(boss ? 64 : 66));
            Canvas.SetTop(strip, Z(boss ? 96 : 94));
            c.Children.Add(strip);

            c.Children.Add(El(Z(6), Z(5), Z(boss ? 67 : 69), Z(boss ? 104 : 101), BA("#8C97A6", 0.75)));

            return c;
        }

        internal static Canvas DrawCover(string mat, int w, int h, bool slab)
        {
            var c = new Canvas { Width = w, Height = h };

            if (slab)
            {
                string c1, c2, edge;
                if (mat == "wood") { c1 = "#D9B47F"; c2 = "#A87C46"; edge = "#77552F"; }
                else if (mat == "glass") { c1 = "#86CCE0"; c2 = "#2A6E86"; edge = "#1D5266"; }
                else { c1 = "#A7AEB8"; c2 = "#666D77"; edge = "#474D54"; }

                var body = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = B(edge),
                    StrokeThickness = 2,
                    RadiusX = 3,
                    RadiusY = 3
                };
                var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0));
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c2), 1));
                body.Fill = lg;
                c.Children.Add(body);

                var hi = new Rectangle
                {
                    Width = Math.Max(4, w - 10),
                    Height = Math.Max(3, h * 0.16),
                    Fill = BA("#FFFFFF", 0.24),
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(hi, 5);
                Canvas.SetTop(hi, Math.Max(3, h * 0.12));
                c.Children.Add(hi);

                if (mat == "wood" && w >= 80)
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        var grain = new Rectangle { Width = w - 8, Height = 1, Fill = BA("#6E4C28", 0.5) };
                        Canvas.SetLeft(grain, 4);
                        Canvas.SetTop(grain, h * i / 4.0);
                        c.Children.Add(grain);
                    }
                }
                if (mat == "metal")
                {
                    double ry = Math.Min(6, h * 0.2);
                    var rv1 = new Ellipse { Width = 6, Height = ry, Fill = B("#DCE2E8") };
                    Canvas.SetLeft(rv1, 6);
                    Canvas.SetTop(rv1, 6);
                    c.Children.Add(rv1);
                    var rv2 = new Ellipse { Width = 6, Height = ry, Fill = B("#DCE2E8") };
                    Canvas.SetLeft(rv2, Math.Max(6, w - 12));
                    Canvas.SetTop(rv2, 6);
                    c.Children.Add(rv2);
                }
                if (mat == "glass")
                {
                    var shine = new Rectangle
                    {
                        Width = Math.Max(3, w * 0.2),
                        Height = Math.Max(6, h - 12),
                        Fill = BA("#FFFFFF", 0.3),
                        RenderTransform = new SkewTransform(-12, 0)
                    };
                    Canvas.SetLeft(shine, w * 0.16);
                    Canvas.SetTop(shine, 6);
                    c.Children.Add(shine);
                }

                if (h >= 150)
                {
                    int segH = 108;
                    for (int i = segH; i < h - 20; i += segH)
                    {
                        var seam = new Rectangle { Width = w, Height = 2, Fill = BA("#111820", 0.34) };
                        Canvas.SetTop(seam, i);
                        c.Children.Add(seam);

                        var seamHi = new Rectangle { Width = w, Height = 1, Fill = BA("#FFFFFF", 0.14) };
                        Canvas.SetTop(seamHi, i + 2);
                        c.Children.Add(seamHi);
                    }
                }
                return c;
            }

            if (mat == "glass")
            {
                var frame = new Rectangle
                {
                    Width = w,
                    Height = h - 12,
                    Fill = B("#2E343B"),
                    Stroke = B("#4E555E"),
                    StrokeThickness = 2,
                    RadiusX = 7,
                    RadiusY = 7
                };
                Canvas.SetLeft(frame, 0);
                Canvas.SetTop(frame, 0);
                c.Children.Add(frame);

                var screen = new Rectangle
                {
                    Width = w - 20,
                    Height = h - 32,
                    Stroke = B("#B8E6F0"),
                    StrokeThickness = 1,
                    RadiusX = 3,
                    RadiusY = 3
                };
                var lg = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1)
                };
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8FDCF0"), 0));
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2A7E9C"), 1));
                screen.Fill = lg;
                Canvas.SetLeft(screen, 10);
                Canvas.SetTop(screen, 10);
                c.Children.Add(screen);

                var gloss = new Rectangle
                {
                    Width = 26,
                    Height = h - 32,
                    Fill = BA("#FFFFFF", 0.26),
                    RenderTransform = new SkewTransform(-14, 0)
                };
                Canvas.SetLeft(gloss, 30);
                Canvas.SetTop(gloss, 10);
                c.Children.Add(gloss);

                var stand = new Rectangle
                {
                    Width = 18,
                    Height = 12,
                    Fill = B("#3B424A")
                };
                Canvas.SetLeft(stand, (w - 18) / 2.0);
                Canvas.SetTop(stand, h - 12);
                c.Children.Add(stand);

                var basePlate = new Rectangle
                {
                    Width = Math.Min(w, 58),
                    Height = 6,
                    Fill = B("#3B424A"),
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(basePlate, (w - Math.Min(w, 58)) / 2.0);
                Canvas.SetTop(basePlate, h - 3);
                c.Children.Add(basePlate);
            }
            else if (mat == "wood")
            {
                var box = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = B("#8E6A3C"),
                    StrokeThickness = 2,
                    RadiusX = 2,
                    RadiusY = 2
                };
                var lg = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1)
                };
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#D6B283"), 0));
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#B0864F"), 1));
                box.Fill = lg;
                c.Children.Add(box);

                var tapeV = new Rectangle
                {
                    Width = 16,
                    Height = h,
                    Fill = BA("#EADFC4", 0.85)
                };
                Canvas.SetLeft(tapeV, (w - 16) / 2.0);
                c.Children.Add(tapeV);

                var tapeH = new Rectangle
                {
                    Width = w,
                    Height = 12,
                    Fill = BA("#EADFC4", 0.55)
                };
                Canvas.SetTop(tapeH, h * 0.42);
                c.Children.Add(tapeH);

                var label = new TextBlock
                {
                    Text = "归档",
                    FontSize = 13,
                    Foreground = B("#6F5228"),
                    FontFamily = new FontFamily("Microsoft YaHei UI")
                };
                Canvas.SetLeft(label, w * 0.16);
                Canvas.SetTop(label, h * 0.66);
                c.Children.Add(label);
            }
            else
            {
                var body = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = B("#4F555D"),
                    StrokeThickness = 2,
                    RadiusX = 4,
                    RadiusY = 4
                };
                var lg = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0)
                };
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#9AA1AB"), 0));
                lg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#6C737D"), 1));
                body.Fill = lg;
                c.Children.Add(body);

                for (int i = 0; i < 3; i++)
                {
                    var line = new Rectangle
                    {
                        Width = w - 14,
                        Height = 2,
                        Fill = B("#5A616A")
                    };
                    Canvas.SetLeft(line, 7);
                    Canvas.SetTop(line, (h - 10) * (i + 1) / 3.0);
                    c.Children.Add(line);

                    var handle = new Rectangle
                    {
                        Width = Math.Min(26, w - 30),
                        Height = 4,
                        Fill = B("#CBD2DA"),
                        RadiusX = 2,
                        RadiusY = 2
                    };
                    Canvas.SetLeft(handle, (w - Math.Min(26, w - 30)) / 2.0);
                    Canvas.SetTop(handle, (h - 10) * (i + 1) / 3.0 - 12);
                    c.Children.Add(handle);
                }

                var tag = new Rectangle
                {
                    Width = 28,
                    Height = 8,
                    Fill = B("#C0392B"),
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(tag, 8);
                Canvas.SetTop(tag, 8);
                c.Children.Add(tag);
            }

            return c;
        }

        internal static Canvas MakeBadge(BitmapSource icon, string kind)
        {
            double d = kind == "giant" ? 96 : 84;
            var c = new Canvas { Width = d, Height = d };

            var bg = new Ellipse { Width = d, Height = d };
            var rg = new RadialGradientBrush
            {
                Center = new Point(0.36, 0.3),
                GradientOrigin = new Point(0.36, 0.3),
                RadiusX = 0.75,
                RadiusY = 0.75
            };
            if (kind == "red")
            {
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF8F6E"), 0));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#E23A24"), 0.58));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8E1408"), 1));
            }
            else if (kind == "giant")
            {
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#8C9CB0"), 0));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#55606E"), 0.6));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2F3742"), 1));
            }
            else
            {
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#67768A"), 0));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#3E4956"), 0.62));
                rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#242B34"), 1));
            }
            bg.Fill = rg;
            c.Children.Add(bg);

            if (icon != null)
            {
                double isz = d * 0.5;
                var img = new Image
                {
                    Source = icon,
                    Width = isz,
                    Height = isz,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(img, (d - isz) / 2.0);
                Canvas.SetTop(img, (d - isz) / 2.0);
                c.Children.Add(img);
            }
            else
            {
                var q = new TextBlock
                {
                    Text = "?",
                    FontSize = d * 0.42,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI")
                };
                Canvas.SetLeft(q, d * 0.36);
                Canvas.SetTop(q, d * 0.16);
                c.Children.Add(q);
            }

            var ring = new Ellipse
            {
                Width = d,
                Height = d,
                StrokeThickness = kind == "red" ? 3.5 : (kind == "giant" ? 4 : 2),
                Stroke = kind == "red" ? B("#FFC7B4") : (kind == "giant" ? B("#C6D4E2") : B("#A7B6C6"))
            };
            c.Children.Add(ring);

            var glint = new Ellipse
            {
                Width = d * 0.3,
                Height = d * 0.16,
                Fill = BA("#FFFFFF", 0.35),
                RenderTransform = new RotateTransform(-28)
            };
            Canvas.SetLeft(glint, d * 0.16);
            Canvas.SetTop(glint, d * 0.16);
            c.Children.Add(glint);

            return c;
        }

        internal static Canvas DrawSling()
        {
            var c = new Canvas { Width = 220, Height = 260 };

            // 底座土坡（先画，被木体压住）
            var moundSh = new Ellipse { Width = 196, Height = 52, Fill = B("#33220F") };
            Canvas.SetLeft(moundSh, 12);
            Canvas.SetTop(moundSh, 216);
            c.Children.Add(moundSh);

            var mound = new Ellipse { Width = 178, Height = 44, Fill = B("#553A24") };
            Canvas.SetLeft(mound, 21);
            Canvas.SetTop(mound, 210);
            c.Children.Add(mound);

            var moundGrass = new Ellipse { Width = 160, Height = 18, Fill = BA("#8FA35C", 0.55) };
            Canvas.SetLeft(moundGrass, 30);
            Canvas.SetTop(moundGrass, 206);
            c.Children.Add(moundGrass);

            for (int i = 0; i < 9; i++)
            {
                var blade = new Line
                {
                    X1 = 36 + i * 17, Y1 = 214,
                    X2 = 40 + i * 17, Y2 = 196,
                    Stroke = BA("#A3B86C", 0.85),
                    StrokeThickness = 2.2
                };
                c.Children.Add(blade);
            }

            // 叉臂（圆头粗原木）
            var armL = new Line
            {
                X1 = 110, Y1 = 148, X2 = 48, Y2 = 26,
                Stroke = B("#7B5636"), StrokeThickness = 40,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            var armR = new Line
            {
                X1 = 110, Y1 = 148, X2 = 172, Y2 = 26,
                Stroke = B("#7B5636"), StrokeThickness = 40,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(armL);
            c.Children.Add(armR);

            // 叉臂受光面
            var hlL = new Line
            {
                X1 = 101, Y1 = 144, X2 = 42, Y2 = 30,
                Stroke = BA("#C09468", 0.92), StrokeThickness = 13,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            var hlR = new Line
            {
                X1 = 119, Y1 = 144, X2 = 178, Y2 = 30,
                Stroke = BA("#C09468", 0.92), StrokeThickness = 13,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(hlL);
            c.Children.Add(hlR);

            // 叉臂背光面
            var shL = new Line
            {
                X1 = 120, Y1 = 152, X2 = 56, Y2 = 34,
                Stroke = BA("#3D2716", 0.5), StrokeThickness = 12,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            var shR = new Line
            {
                X1 = 100, Y1 = 152, X2 = 164, Y2 = 34,
                Stroke = BA("#3D2716", 0.5), StrokeThickness = 12,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(shL);
            c.Children.Add(shR);

            // 主干（更粗，微微上宽下窄的木头感）
            var trunk = new Line
            {
                X1 = 110, Y1 = 130, X2 = 110, Y2 = 226,
                Stroke = B("#7B5636"), StrokeThickness = 50,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(trunk);

            var trunkHi = new Line
            {
                X1 = 98, Y1 = 134, X2 = 98, Y2 = 222,
                Stroke = BA("#C09468", 0.88), StrokeThickness = 14,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(trunkHi);

            var trunkSh = new Line
            {
                X1 = 122, Y1 = 134, X2 = 122, Y2 = 222,
                Stroke = BA("#3D2716", 0.42), StrokeThickness = 13,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            c.Children.Add(trunkSh);

            // 叉口木结（自然过渡，不是贴上去的圆）
            var knot = new Ellipse
            {
                Width = 76,
                Height = 70,
                Fill = B("#6E4B2E")
            };
            Canvas.SetLeft(knot, 72);
            Canvas.SetTop(knot, 112);
            c.Children.Add(knot);

            var knotHi = new Ellipse { Width = 34, Height = 28, Fill = BA("#A87B51", 0.5) };
            Canvas.SetLeft(knotHi, 82);
            Canvas.SetTop(knotHi, 118);
            c.Children.Add(knotHi);

            // 叉尖绑扎：皮筋在叉尖被缠住，这是弹弓最真实的外观特征
            for (int side = 0; side < 2; side++)
            {
                double tipX = side == 0 ? 48 : 172;
                double dirX = side == 0 ? 0.453 : -0.453;
                double dirY = 0.892;
                double perpX = 0.892;
                double perpY = side == 0 ? -0.453 : 0.453;
                for (int k = 0; k < 3; k++)
                {
                    double ox = tipX + dirX * (13 + k * 8);
                    double oy = 26 + dirY * (13 + k * 8);
                    var wrap = new Line
                    {
                        X1 = ox - perpX * 22, Y1 = oy - perpY * 22,
                        X2 = ox + perpX * 22, Y2 = oy + perpY * 22,
                        Stroke = B("#3A2A1C"), StrokeThickness = 6,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                    };
                    c.Children.Add(wrap);
                }
            }

            // 叉尖受光的小高光，让木头看起来是圆的
            var tipHiL = new Ellipse { Width = 13, Height = 9, Fill = BA("#D8AE7E", 0.55) };
            Canvas.SetLeft(tipHiL, 43);
            Canvas.SetTop(tipHiL, 18);
            c.Children.Add(tipHiL);
            var tipHiR = new Ellipse { Width = 13, Height = 9, Fill = BA("#D8AE7E", 0.55) };
            Canvas.SetLeft(tipHiR, 167);
            Canvas.SetTop(tipHiR, 18);
            c.Children.Add(tipHiR);

            // 树皮纹（稀疏几条，不抢戏）
            for (int i = 0; i < 4; i++)
            {
                var g = new Line
                {
                    X1 = 96, Y1 = 150 + i * 18,
                    X2 = 124, Y2 = 155 + i * 18,
                    Stroke = BA("#3D2716", 0.22), StrokeThickness = 2
                };
                c.Children.Add(g);
            }

            for (int i = 0; i < 5; i++)
            {
                double yy = 46 + i * 22;
                var gL = new Line
                {
                    X1 = 44 - i * 2.2, Y1 = yy,
                    X2 = 86 - i * 2.2, Y2 = yy + 13,
                    Stroke = BA("#5A3D24", 0.30), StrokeThickness = 2.2
                };
                var gR = new Line
                {
                    X1 = 176 + i * 2.2, Y1 = yy,
                    X2 = 134 + i * 2.2, Y2 = yy + 13,
                    Stroke = BA("#5A3D24", 0.30), StrokeThickness = 2.2
                };
                c.Children.Add(gL);
                c.Children.Add(gR);
            }

            return c;
        }

        private static Path QuadPath(Point a, Point ctrl, Point b, Brush stroke, double thick)
        {
            var fig = new PathFigure { StartPoint = a };
            fig.Segments.Add(new QuadraticBezierSegment(ctrl, b, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return new Path
            {
                Data = geo,
                Stroke = stroke,
                StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
        }

        // 照原版弹弓做的：自然分叉的树干、斜切的叉尖断面、红色粗皮筋绑带
        internal static Canvas DrawSling2()
        {
            var c = new Canvas { Width = 220, Height = 260 };

            // 地面阴影
            var gsh = new Ellipse { Width = 148, Height = 26, Fill = BA("#000000", 0.16) };
            Canvas.SetLeft(gsh, 36);
            Canvas.SetTop(gsh, 228);
            c.Children.Add(gsh);

            // 木头渐变：左亮右暗，看起来是圆的
            var wood = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            wood.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#A77C52"), 0));
            wood.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7E5738"), 0.42));
            wood.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#4A2F1C"), 1));

            // 主干：一直插到地面，下粗上细，微微后弯
            c.Children.Add(QuadPath(new Point(110, 292), new Point(100, 220), new Point(112, 148), wood, 56));

            // 两臂：从叉口向外弯到叉尖（弧度是原版弹弓的灵魂）
            c.Children.Add(QuadPath(new Point(108, 156), new Point(58, 106), new Point(42, 36), wood, 40));
            c.Children.Add(QuadPath(new Point(112, 156), new Point(162, 106), new Point(178, 36), wood, 40));

            // 叉口结节，让两臂与主干连成一体
            var knot = new Ellipse { Width = 68, Height = 62, Fill = wood };
            Canvas.SetLeft(knot, 76);
            Canvas.SetTop(knot, 122);
            c.Children.Add(knot);

            // 叉尖：斜切开的浅色木质断面 + 年轮
            for (int s = 0; s < 2; s++)
            {
                double tx = s == 0 ? 42 : 178;
                double ang = s == 0 ? -24 : 24;

                var cut = new Ellipse { Width = 44, Height = 26, Fill = B("#DCC29B"), Stroke = B("#8A6A45"), StrokeThickness = 1.7 };
                cut.RenderTransformOrigin = new Point(0.5, 0.5);
                cut.RenderTransform = new RotateTransform(ang);
                Canvas.SetLeft(cut, tx - 22);
                Canvas.SetTop(cut, 23);
                c.Children.Add(cut);

                var r2 = new Ellipse { Width = 27, Height = 15, Fill = B("#C6A276") };
                r2.RenderTransformOrigin = new Point(0.5, 0.5);
                r2.RenderTransform = new RotateTransform(ang);
                Canvas.SetLeft(r2, tx - 13.5);
                Canvas.SetTop(r2, 28.5);
                c.Children.Add(r2);

                var r3 = new Ellipse { Width = 13, Height = 7, Fill = B("#A98354") };
                r3.RenderTransformOrigin = new Point(0.5, 0.5);
                r3.RenderTransform = new RotateTransform(ang);
                Canvas.SetLeft(r3, tx - 6.5);
                Canvas.SetTop(r3, 32.5);
                c.Children.Add(r3);
            }

            // 红色粗皮筋绑带：缠在叉尖内侧，两圈
            for (int s = 0; s < 2; s++)
            {
                double bx = s == 0 ? 42 : 178;
                double dirX = s == 0 ? 0.482 : -0.482;
                double ang = s == 0 ? -29 : 29;
                for (int k = 0; k < 2; k++)
                {
                    double d = 17 + k * 13;
                    var band = new Ellipse { Width = 46, Height = 12, Fill = B("#B5342A"), Stroke = B("#7E1D14"), StrokeThickness = 1.8 };
                    band.RenderTransformOrigin = new Point(0.5, 0.5);
                    band.RenderTransform = new RotateTransform(ang);
                    Canvas.SetLeft(band, bx + dirX * d - 25);
                    Canvas.SetTop(band, 36 + 0.88 * d - 6.5);
                    c.Children.Add(band);
                }
            }

            // 树皮纹（只在主干上，短而淡）
            var bark = BA("#3A2415", 0.26);
            for (int i = 0; i < 4; i++)
            {
                c.Children.Add(QuadPath(
                    new Point(94, 268 - i * 30),
                    new Point(104, 258 - i * 30),
                    new Point(97, 244 - i * 30), bark, 2.0));
            }

            return c;
        }
    }

    internal class Ammo
    {
        internal BitmapSource Icon;
        internal string Kind;
    }

    internal class Bird
    {
        internal Canvas View;
        internal double X, Y, VX, VY, R;
        internal string Kind;
        internal bool Flying;
        internal bool Boosted;
        internal double Mass;
        internal bool Dead;
        internal int Kills;
        internal object PBody;
        internal double FlyT;
        internal double SlowT;
    }

    internal class Bubble
    {
        internal Canvas View;
        internal Pig Src;
        internal double X, Y, T0, Life;
    }

    internal class Pig
    {
        internal Canvas View;
        internal double X, Y, R, VW, VH;
        internal int Hp, MaxHp;
        internal bool Boss;
        internal bool Alive = true;
        internal Color Blood;
        internal double FallV;
        internal double CrushT = -99;
        internal double HitT = -99;
        internal object PBody;
        internal double BoxH;
        internal double SayT = -99;
        internal string Name = "";
    }

    internal class Cover
    {
        internal Canvas View;
        internal double X, Y, W, H;
        internal int Hp, MaxHp;
        internal string Mat;
        internal bool Alive = true;
        internal double FallV;
        internal bool Crushed;
        internal double VX, VY, Ang, AngV;
        internal bool Awake;
        internal RotateTransform Rot;
        internal object PBody;
        internal double HitT = -99;
    }

    internal class Particle
    {
        internal Ellipse View;
        internal double X, Y, VX, VY, Life, MaxLife;
    }

    internal class DustGhost
    {
        internal Canvas V;
        internal Rectangle Flash;
        internal ScaleTransform Scale;
        internal RotateTransform Rot;
        internal double T0;
        internal double Dir;
    }

    internal class PigDef
    {
        internal double X, Y;
        internal bool Boss;
        internal int Hp;
        internal int Variant;
    }

    internal class CoverDef
    {
        internal double X, Y;
        internal int W, H;
        internal string Mat;
        internal bool Slab;
    }

    internal class LevelDef
    {
        internal string Name;
        internal List<PigDef> Pigs = new List<PigDef>();
        internal List<CoverDef> Covers = new List<CoverDef>();
        internal int Star2;
        internal int Star3;
    }

    internal class GameWindow : Window
    {
        private double _W = 2560;
        private double _H = 1440;

        private Canvas _root;
        private Canvas _slingLayer;
        private Canvas _gameLayer;
        private Canvas _hudLayer;

        private Canvas _slingView;
        private Line _band1, _band2;
        private Canvas _slingAnchorDot;

        private TextBlock _levelText;
        private TextBlock _scoreText;
        private TextBlock _leftText;
        private TextBlock _dbg;
        private Canvas _ammoBar;
        private readonly List<Ellipse> _previewDots = new List<Ellipse>();

        private readonly List<Pig> _pigs = new List<Pig>();
        private readonly List<Cover> _covers = new List<Cover>();
        private readonly List<Particle> _particles = new List<Particle>();
        private readonly List<DustGhost> _dust = new List<DustGhost>();
        private readonly List<Bubble> _bubbles = new List<Bubble>();
        private readonly List<Ammo> _ammo = new List<Ammo>();
        private List<string> _deskFiles;
        private int _ammoSeq;

        private Bird _current;
        private bool _dragging;
        private double _score;
        private int _levelIndex;
        private bool _levelClear;
        private bool _finished;

        private double _anchorX, _anchorY;
        private double _forkLX, _forkLY, _forkRX, _forkRY;


        private Stopwatch _clock = new Stopwatch();
        private double _lastT;
        private double _freezeUntil;
        private double _shakeUntil;
        private double _lastStretchSfx;
        private double _lastStretchLen;
        private Ellipse _pouch;
        private ScaleTransform _slingScale;
        private TranslateTransform _slingShift;
        private Random _rng = new Random();


        private List<LevelDef> _levels;
        private int _startLevel;

        internal GameWindow(int startLevel)
        {
            _startLevel = startLevel;
            _W = Native.GetSystemMetrics(0);
            _H = Native.GetSystemMetrics(1);
            if (_W <= 0) _W = 1920;
            if (_H <= 0) _H = 1080;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Title = "愤怒的图标";
            Topmost = true;
            ShowInTaskbar = false;
            Background = Brushes.Black;
            Left = 0;
            Top = 0;
            Width = _W;
            Height = _H;

            BuildLevels();

            _root = new Canvas { Width = _W, Height = _H, Background = Brushes.Black };

            var bgImage = new Image { Width = _W, Height = _H, Stretch = Stretch.UniformToFill };
            var wp = Assets.LoadWallpaper();
            if (wp != null)
            {
                bgImage.Source = wp;
            }
            _root.Children.Add(bgImage);

            var tint = new Rectangle
            {
                Width = _W, Height = _H,
                Fill = new SolidColorBrush(Color.FromArgb(122, 4, 8, 16))
            };
            _root.Children.Add(tint);

            var vignette = new Rectangle { Width = _W, Height = _H };
            var vg = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.85,
                RadiusY = 0.85
            };
            vg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
            vg.GradientStops.Add(new GradientStop(Color.FromArgb(120, 0, 0, 0), 1));
            vignette.Fill = vg;
            _root.Children.Add(vignette);

            _slingLayer = new Canvas { Width = _W, Height = _H, IsHitTestVisible = false };
            _gameLayer = new Canvas { Width = _W, Height = _H, IsHitTestVisible = false };
            _hudLayer = new Canvas { Width = _W, Height = _H, IsHitTestVisible = false };
            _root.Children.Add(_slingLayer);
            _root.Children.Add(_gameLayer);
            _root.Children.Add(_hudLayer);

            Content = _root;

            _anchorX = _W * 0.13;            _anchorY = _H * 0.62;
            _forkLX = _anchorX - 88;
            _forkRX = _anchorX + 88;
            _forkLY = _anchorY - 133;
            _forkRY = _anchorY - 133;

            BuildSling();
            BuildHud();

            KeyDown += OnKeyDown;
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;

            _clock.Start();
            CompositionTarget.Rendering += OnFrame;

            Loaded += (s, e) => LoadLevel(_startLevel);

            DispatcherTimer hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            hintTimer.Tick += (s, e) =>
            {
                hintTimer.Stop();
                if (_hint != null) _hint.Opacity = 0.35;
            };
            hintTimer.Start();
        }

        private TextBlock _hint;

        private void BuildSling()
        {
            _slingView = Art.DrawSling2();
            Canvas.SetLeft(_slingView, _anchorX - 110);
            Canvas.SetTop(_slingView, _anchorY - 138);
            _slingView.RenderTransformOrigin = new Point(0.5, 0.5308);
            var tg = new TransformGroup();
            _slingScale = new ScaleTransform(1.3, 1.3);
            _slingShift = new TranslateTransform(0, 0);
            tg.Children.Add(_slingScale);
            tg.Children.Add(_slingShift);
            _slingView.RenderTransform = tg;
            _slingLayer.Children.Add(_slingView);

            _slingAnchorDot = new Canvas { Width = 10, Height = 10 };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Art.BA("#FFE08A", 0.6) };
            _slingAnchorDot.Children.Add(dot);
            Canvas.SetLeft(_slingAnchorDot, _anchorX - 5);
            Canvas.SetTop(_slingAnchorDot, _anchorY - 5);
            _slingLayer.Children.Add(_slingAnchorDot);

            _band1 = new Line { Stroke = Art.B("#A33224"), StrokeThickness = 18, X1 = _forkLX, Y1 = _forkLY, X2 = _anchorX, Y2 = _anchorY };
            _band2 = new Line { Stroke = Art.B("#A33224"), StrokeThickness = 18, X1 = _forkRX, Y1 = _forkRY, X2 = _anchorX, Y2 = _anchorY };
            _slingLayer.Children.Add(_band1);
            _slingLayer.Children.Add(_band2);

            _pouch = new Ellipse
            {
                Width = 44,
                Height = 22,
                Fill = Art.B("#5C2A22"),
                Stroke = Art.B("#3A1813"),
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _pouch.RenderTransformOrigin = new Point(0.5, 0.5);
            _slingLayer.Children.Add(_pouch);

            Panel.SetZIndex(_slingView, 10);
            Panel.SetZIndex(_slingAnchorDot, 20);
            Panel.SetZIndex(_band1, 40);
            Panel.SetZIndex(_band2, 40);
            Panel.SetZIndex(_pouch, 45);

            for (int i = 0; i < 46; i++)
            {
                var pdot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Art.BA("#FFE08A", 0.92),
                    Stroke = Art.BA("#8A5A18", 0.55),
                    StrokeThickness = 1.4,
                    Visibility = Visibility.Collapsed
                };
                _slingLayer.Children.Add(pdot);
                _previewDots.Add(pdot);
            }
        }

        private void UpdatePreview(double vx, double vy)
        {
            // 与 Box2D 完全相同的步长和积分顺序（先速度后位置），所以预览点和真实弹道是同一组数字
            const double dt = 1.0 / 60.0;
            const double g = 988.0;
            double px = (_current != null) ? _current.X : _anchorX;
            double py = (_current != null) ? _current.Y : _anchorY;
            double vX = vx, vY = vy;
            int shown = 0;

            for (int i = 0; i < _previewDots.Count; i++)
            {
                {
                    vY += g * dt;
                    px += vX * dt;
                    py += vY * dt;
                }
                if (px < -30 || px > _W + 30 || py > _H + 30)
                {
                    _previewDots[i].Visibility = Visibility.Collapsed;
                    continue;
                }
                double s = 8.0 - shown * 0.12;
                if (s < 3.0) s = 3.0;
                double op = 0.96 - shown * 0.014;
                if (op < 0.34) op = 0.34;
                _previewDots[i].Width = s;
                _previewDots[i].Height = s;
                _previewDots[i].Opacity = op;
                Canvas.SetLeft(_previewDots[i], px - s / 2);
                Canvas.SetTop(_previewDots[i], py - s / 2);
                _previewDots[i].Visibility = Visibility.Visible;
                shown++;
            }
        }

        private void HidePreview()
        {
            for (int i = 0; i < _previewDots.Count; i++)
                _previewDots[i].Visibility = Visibility.Collapsed;
        }

        private void BuildHud()
        {
            _levelText = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };
            Canvas.SetLeft(_levelText, 42);
            Canvas.SetTop(_levelText, 30);
            _hudLayer.Children.Add(_levelText);

            _scoreText = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 34,
                FontWeight = FontWeights.Bold,
                Foreground = Art.B("#FFD75E"),
                Text = "0"
            };
            Canvas.SetLeft(_scoreText, _W / 2 - 40);
            Canvas.SetTop(_scoreText, 26);
            _hudLayer.Children.Add(_scoreText);

            _leftText = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 20,
                Foreground = Art.BA("#E8EEF6", 0.8)
            };
            Canvas.SetLeft(_leftText, _W - 300);
            Canvas.SetTop(_leftText, 34);
            _hudLayer.Children.Add(_leftText);

            var esc = new TextBlock
            {
                Text = "ESC 退出",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 15,
                Foreground = Art.BA("#E8EEF6", 0.45)
            };
            Canvas.SetLeft(esc, _W - 110);
            Canvas.SetTop(esc, _H - 40);
            _hudLayer.Children.Add(esc);

            _hint = new TextBlock
            {
                Text = "按住鼠标向后再松手发射　·　红球碰到东西自动爆炸　·　巨型图标能砸穿两层",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 16,
                Foreground = Art.BA("#DCE6F2", 0.75)
            };
            Canvas.SetLeft(_hint, _W / 2 - 430);
            Canvas.SetTop(_hint, _H - 62);
            _hudLayer.Children.Add(_hint);

            _ammoBar = new Canvas { Width = 600, Height = 120 };
            Canvas.SetLeft(_ammoBar, 60);
            Canvas.SetTop(_ammoBar, _H - 130);
            _hudLayer.Children.Add(_ammoBar);
        }

        private void DrawAmmo()
        {
            _ammoBar.Children.Clear();
            for (int i = 0; i < 5; i++)
            {
                double x = i * 108;
                var slot = new Canvas { Width = 96, Height = 96 };
                Canvas.SetLeft(slot, x);
                Canvas.SetTop(slot, 8);

                if (i < _ammo.Count)
                {
                    var badge = Art.MakeBadge(_ammo[i].Icon, _ammo[i].Kind);
                    Canvas.SetLeft(badge, (96 - badge.Width) / 2.0);
                    Canvas.SetTop(badge, (96 - badge.Height) / 2.0);
                    slot.Children.Add(badge);
                }
                else
                {
                    var empty = new Ellipse
                    {
                        Width = 84, Height = 84,
                        Stroke = Art.BA("#8FA3B8", 0.28),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection(new double[] { 4, 5 })
                    };
                    Canvas.SetLeft(empty, 6);
                    Canvas.SetTop(empty, 6);
                    slot.Children.Add(empty);
                }

                _ammoBar.Children.Add(slot);
            }
        }

        private CoverDef Cv(double px, double py, int w, int h, string mat, bool slab)
        {
            return new CoverDef { X = px / _W, Y = py / _H, W = w, H = h, Mat = mat, Slab = slab };
        }

        private PigDef Pg(double px, double py, bool boss, int hp, int variant)
        {
            return new PigDef { X = px / _W, Y = py / _H, Boss = boss, Hp = hp, Variant = variant };
        }

        private void ColSeg(LevelDef l, double px, double cy, int w, int H, string mat, int segs)
        {
            double sh = (double)H / segs;
            for (int i = 0; i < segs; i++)
            {
                double yy = cy - H / 2.0 + sh * i + sh / 2.0;
                l.Covers.Add(Cv(px, yy, w, (int)(sh + 0.5), mat, true));
            }
        }

        private void RowSeg(LevelDef l, double cx, double py, int W, int h, string mat, int segs)
        {
            double sw = (double)W / segs;
            for (int i = 0; i < segs; i++)
            {
                double xx = cx - W / 2.0 + sw * i + sw / 2.0;
                l.Covers.Add(Cv(xx, py, (int)(sw + 0.5), h, mat, true));
            }
        }

        private void BuildLevels()
        {
            _levels = new List<LevelDef>();

            // ===== L1 工位初体验：一张办公桌 =====
            var l1 = new LevelDef { Name = "第 1 关　工位初体验", Star2 = 5200, Star3 = 7600 };
            ColSeg(l1, 1900, 980, 84, 300, "wood", 3);
            ColSeg(l1, 2170, 980, 84, 300, "wood", 3);
            l1.Covers.Add(Cv(2035, 805, 640, 56, "wood", true));
            l1.Pigs.Add(Pg(2035, 1039, false, 1, 0));
            l1.Pigs.Add(Pg(2035, 686, false, 1, 1));
            l1.Pigs.Add(Pg(2400, 1039, false, 1, 2));
            _levels.Add(l1);

            // ===== L2 茶水间：细高四层塔 =====
            var l2 = new LevelDef { Name = "第 2 关　茶水间", Star2 = 9000, Star3 = 13200 };
            ColSeg(l2, 1720, 980, 84, 300, "wood", 3);
            ColSeg(l2, 2080, 980, 84, 300, "wood", 3);
            RowSeg(l2, 1900, 807, 440, 46, "wood", 2);
            ColSeg(l2, 1780, 664, 76, 240, "metal", 2);
            ColSeg(l2, 2020, 664, 76, 240, "metal", 2);
            RowSeg(l2, 1900, 521, 340, 46, "metal", 2);
            ColSeg(l2, 1790, 398, 68, 200, "glass", 2);
            ColSeg(l2, 2010, 398, 68, 200, "glass", 2);
            RowSeg(l2, 1900, 275, 340, 44, "glass", 1);
            l2.Pigs.Add(Pg(1900, 1039, false, 1, 0));
            l2.Pigs.Add(Pg(1900, 693, false, 1, 1));
            l2.Pigs.Add(Pg(1900, 120, true, 2, 2));
            _levels.Add(l2);

            // ===== L3 会议室：左右双塔 + 悬空玻璃桥 =====
            var l3 = new LevelDef { Name = "第 3 关　会议室", Star2 = 12800, Star3 = 18600 };
            ColSeg(l3, 1520, 980, 84, 300, "wood", 3);
            ColSeg(l3, 1780, 980, 84, 300, "wood", 3);
            RowSeg(l3, 1650, 807, 320, 46, "wood", 2);
            ColSeg(l3, 2240, 980, 84, 300, "metal", 3);
            ColSeg(l3, 2480, 980, 84, 300, "metal", 3);
            RowSeg(l3, 2360, 807, 320, 46, "metal", 2);
            ColSeg(l3, 1950, 958, 84, 344, "metal", 3);
            RowSeg(l3, 1950, 762, 900, 48, "glass", 2);
            ColSeg(l3, 1800, 678, 100, 120, "glass", 1);
            ColSeg(l3, 2100, 678, 100, 120, "glass", 1);
            RowSeg(l3, 1950, 596, 410, 44, "glass", 1);
            l3.Pigs.Add(Pg(1650, 1039, false, 1, 0));
            l3.Pigs.Add(Pg(2360, 1039, false, 1, 1));
            l3.Pigs.Add(Pg(1710, 647, false, 1, 2));
            l3.Pigs.Add(Pg(2190, 647, false, 1, 2));
            l3.Pigs.Add(Pg(1950, 440, true, 2, 0));
            _levels.Add(l3);

            // ===== L4 加班区：四级阶梯金字塔 =====
            var l4 = new LevelDef { Name = "第 4 关　加班区", Star2 = 16400, Star3 = 23800 };
            ColSeg(l4, 1560, 1030, 200, 200, "wood", 2);
            ColSeg(l4, 1800, 980, 200, 300, "wood", 3);
            ColSeg(l4, 2040, 930, 200, 400, "metal", 4);
            ColSeg(l4, 2280, 880, 200, 500, "metal", 5);
            l4.Pigs.Add(Pg(1400, 1039, false, 1, 0));
            l4.Pigs.Add(Pg(1560, 839, false, 1, 0));
            l4.Pigs.Add(Pg(1800, 739, false, 1, 1));
            l4.Pigs.Add(Pg(2040, 639, false, 1, 2));
            l4.Pigs.Add(Pg(2280, 497, true, 2, 0));
            _levels.Add(l4);

            // ===== L5 总监办公室：单柱高塔 + 侧门框 =====
            var l5 = new LevelDef { Name = "第 5 关　总监办公室", Star2 = 21000, Star3 = 30000 };
            ColSeg(l5, 1440, 1030, 76, 200, "wood", 2);
            ColSeg(l5, 1620, 1030, 76, 200, "wood", 2);
            RowSeg(l5, 1530, 907, 280, 46, "wood", 2);
            ColSeg(l5, 1880, 980, 84, 300, "metal", 3);
            ColSeg(l5, 2100, 980, 84, 300, "metal", 3);
            RowSeg(l5, 1990, 807, 360, 46, "wood", 2);
            ColSeg(l5, 1920, 664, 96, 240, "glass", 2);
            ColSeg(l5, 2060, 664, 96, 240, "glass", 2);
            RowSeg(l5, 1990, 521, 280, 46, "glass", 2);
            ColSeg(l5, 1990, 398, 140, 200, "metal", 2);
            RowSeg(l5, 1990, 275, 180, 44, "glass", 1);
            l5.Pigs.Add(Pg(1530, 792, false, 1, 0));
            l5.Pigs.Add(Pg(1700, 1039, false, 1, 1));
            l5.Pigs.Add(Pg(1990, 120, true, 2, 2));
            l5.Pigs.Add(Pg(2300, 1039, false, 1, 0));
            l5.Pigs.Add(Pg(2450, 1039, false, 1, 1));
            _levels.Add(l5);
        }

        private void LoadLevel(int idx)
        {
            _levelIndex = idx;
            _levelClear = false;
            _pigs.Clear();
            _covers.Clear();
            _particles.Clear();
            _dust.Clear();
            _bubbles.Clear();
            _coverViews.Clear();
            _gameLayer.Children.Clear();
            _current = null;

            var def = _levels[idx];
            _levelText.Text = def.Name;

            double gy = 1130;

            Phys.Reset(_W, _H);

            var floorBg = new Rectangle { Width = _W, Height = 520 };
            var fg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 10, 18, 28), 0));
            fg.GradientStops.Add(new GradientStop(Color.FromArgb(56, 10, 18, 28), 0.2));
            fg.GradientStops.Add(new GradientStop(Color.FromArgb(152, 10, 18, 28), 0.46));
            fg.GradientStops.Add(new GradientStop(Color.FromArgb(198, 9, 15, 23), 0.72));
            fg.GradientStops.Add(new GradientStop(Color.FromArgb(206, 9, 15, 23), 1));
            floorBg.Fill = fg;
            Canvas.SetLeft(floorBg, 0);
            Canvas.SetTop(floorBg, gy);
            _gameLayer.Children.Add(floorBg);

            var floorEdge = new Rectangle { Width = _W, Height = 1, Fill = Art.BA("#A6BDD4", 0.22) };
            Canvas.SetLeft(floorEdge, 0);
            Canvas.SetTop(floorEdge, gy);
            _gameLayer.Children.Add(floorEdge);

            var floorGlow = new Rectangle { Width = _W, Height = 10, Fill = Art.BA("#C7DAEC", 0.09) };
            Canvas.SetLeft(floorGlow, 0);
            Canvas.SetTop(floorGlow, gy + 2);
            _gameLayer.Children.Add(floorGlow);


            // 身份贴纸按屏幕从左到右编号，看得更顺
            var tagFor = new Dictionary<PigDef, string>();
            var order = new List<PigDef>(def.Pigs);
            order.Sort((a, b) => a.X.CompareTo(b.X));
            int workerSeq = 0;
            foreach (var pd in order)
            {
                if (pd.Boss) tagFor[pd] = "老板";
                else { tagFor[pd] = "同事 " + (char)('A' + (workerSeq % 26)); workerSeq++; }
            }

            foreach (var pd in def.Pigs)
            {
                var view = Art.DrawGuy(pd.Boss, pd.Variant);
                var pig = new Pig
                {
                    View = view,
                    X = pd.X * _W,
                    Y = pd.Y * _H + (pd.Boss ? 267.0 : 183.0) / 2.0 - view.Height / 2.0,
                    R = pd.Boss ? 62 : 46,
                    Hp = pd.Hp,
                    MaxHp = pd.Hp,
                    Boss = pd.Boss,
                    VW = view.Width,
                    VH = view.Height,
                    Blood = pd.Boss ? (Color)ColorConverter.ConvertFromString("#C0392B") : (Color)ColorConverter.ConvertFromString("#E07A90")
                };
                Canvas.SetLeft(view, pig.X - pig.VW / 2);
                Canvas.SetTop(view, pig.Y - pig.VH / 2);
                view.RenderTransformOrigin = new Point(0.5, 0.5);

                // 身份贴纸：同事 A/B/C…、老板
                string tag = tagFor.ContainsKey(pd) ? tagFor[pd] : (pd.Boss ? "老板" : "同事");
                AddNameTag(pig, tag, pd.Boss);

                _gameLayer.Children.Add(view);

                double foot = pig.Y + pig.VH / 2.0;
                pig.BoxH = pig.VH * 0.62;
                pig.PBody = Phys.AddBox(pig.X, foot - pig.BoxH / 2.0,
                    pig.R * 1.34, pig.BoxH, 0.55f, 0.55f, pig);

                _pigs.Add(pig);
            }

            foreach (var cd in def.Covers)
            {
                int cw = (int)cd.W;
                int chh = (int)cd.H;
                var view = Art.DrawCover(cd.Mat, cw, chh, cd.Slab);
                var cov = new Cover
                {
                    View = view,
                    X = cd.X * _W - cw / 2.0,
                    Y = cd.Y * _H - chh / 2.0,
                    W = cw,
                    H = chh,
                    Mat = cd.Mat,
                    Hp = cd.Mat == "glass" ? 1 : (cd.Mat == "wood" ? 2 : 2),
                    MaxHp = cd.Mat == "glass" ? 1 : (cd.Mat == "wood" ? 2 : 2)
                };
                var rot = new RotateTransform(0, cw / 2.0, chh / 2.0);
                view.RenderTransform = rot;
                cov.Rot = rot;
                Canvas.SetLeft(view, cov.X);
                Canvas.SetTop(view, cov.Y);
                _gameLayer.Children.Add(view);
                RegisterCoverView(view);
                float dens = cd.Mat == "glass" ? 0.70f : (cd.Mat == "wood" ? 1.30f : 3.20f);
                float fric = cd.Mat == "glass" ? 0.45f : (cd.Mat == "wood" ? 0.72f : 0.60f);
                // 物理盒比视觉盒矮 2px（底边对齐、顶边留缝），避免堆叠时初始穿插被弹开导致失稳
                double gapH = 2.0;
                cov.PBody = Phys.AddBox(cov.X + cw / 2.0, cov.Y + chh / 2.0 + gapH / 2.0,
                    cw - 0.4, chh - gapH, dens, fric, cov);

                _covers.Add(cov);
            }

            _ammo.Clear();
            RefillAmmo();
            DrawAmmo();
            UpdateHud();
            PullNext();
        }

        private void RefillAmmo()
        {
            if (_deskFiles == null || _deskFiles.Count == 0) _deskFiles = Assets.DesktopItems();
            _ammo.Clear();
            _ammoSeq = 0;
            for (int i = 0; i < 6; i++)
            {
                var a = MakeAmmoItem();
                if (a == null) break;
                _ammo.Add(a);
            }
        }

        private Ammo MakeAmmoItem()
        {
            if (_deskFiles == null || _deskFiles.Count == 0) return null;
            int pick = _rng.Next(_deskFiles.Count);
            var icon = Assets.LoadIcon(_deskFiles[pick], 128);
            int m = _ammoSeq % 5;
            string kind = m == 1 ? "red" : m == 4 ? "giant" : "normal";
            _ammoSeq++;
            return new Ammo { Icon = icon, Kind = kind };
        }

        private static readonly string[] BossLines = new[]
        {
            "别打了，我给你加薪！",
            "奖金翻倍，行不行？",
            "年假随便你请！",
            "这个月 KPI 我给你 A+！",
            "我这就把 OKR 删了！",
            "下季度给你加两个 HC！",
            "预算我批，别砸了！",
            "汇报 PPT 署你名字！"
        };

        private static readonly string[] StaffLines = new[]
        {
            "我再也不抢你功劳了！",
            "这锅我背，我背还不行吗！",
            "周报我帮你写！",
            "下次汇报我提你名字！",
            "咖啡我请，一周！",
            "文档我马上改回来！",
            "会议室让给你！",
            "需求我接，我全接！"
        };

        private void AddNameTag(Pig p, string text, bool boss)
        {
            try
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = boss ? 12.5 : 11.0,
                    Foreground = Art.B(boss ? "#7A5A1E" : "#5A6675"),
                    FontWeight = boss ? FontWeights.SemiBold : FontWeights.Normal
                };

                var tag = new Border
                {
                    Child = tb,
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    Background = Art.BA(boss ? "#FBF0D2" : "#FFFFFF", 0.92),
                    BorderBrush = Art.BA(boss ? "#D8B45A" : "#C3D0E0", 0.82),
                    Padding = new Thickness(7, 2, 7, 2.5),
                    IsHitTestVisible = false
                };
                tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var sz = tag.DesiredSize;

                // 默认贴在头顶外侧；顶到屏幕了就挂到身体右侧，避免出屏
                bool side = (p.Y - p.VH / 2.0 - 22.0) < 4;
                double tx = side ? (p.VW - 8) : (p.VW / 2.0 - sz.Width / 2.0);
                double ty = side ? (p.VH * 0.30) : (-sz.Height - 3);

                tag.RenderTransformOrigin = new Point(0.5, 0.5);
                tag.RenderTransform = new RotateTransform(side ? 3.5 : -2.5);
                Canvas.SetLeft(tag, tx);
                Canvas.SetTop(tag, ty);
                p.View.Children.Add(tag);
                p.Name = text;
            }
            catch { }
        }

        // 被惊动时随机说一句话（白底对话气泡，上飘淡出）
        private void SayWord(Pig p)
        {
            if (p == null) return;
            try
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (now - p.SayT < 2.5) return;
                p.SayT = now;

                string text = p.Boss
                    ? BossLines[_rng.Next(BossLines.Length)]
                    : StaffLines[_rng.Next(StaffLines.Length)];

                var tb = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = 17,
                    Foreground = Art.B("#2F3A47")
                };

                var box = new Border
                {
                    Child = tb,
                    Background = Art.BA("#FFFFFF", 0.95),
                    BorderBrush = Art.BA("#B9C7D8", 0.88),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(12, 7, 12, 8),
                    IsHitTestVisible = false
                };
                box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = box.DesiredSize.Width;
                double h = box.DesiredSize.Height;

                var tail = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(w * 0.40, 0),
                        new Point(w * 0.60, 0),
                        new Point(w * 0.50, 9)
                    },
                    Fill = Art.BA("#FFFFFF", 0.95),
                    Stroke = Art.BA("#B9C7D8", 0.88),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };

                var holder = new Canvas { Width = w, Height = h + 10, Opacity = 0.0 };
                Canvas.SetLeft(box, 0);
                Canvas.SetTop(box, 0);
                holder.Children.Add(box);
                Canvas.SetLeft(tail, 0);
                Canvas.SetTop(tail, h - 1.5);
                holder.Children.Add(tail);

                holder.RenderTransformOrigin = new Point(0.5, 1.0);

                double bx = p.X - w / 2.0;
                double by = p.Y - p.VH / 2.0 - h - 24;
                if (by < 8) by = p.Y - p.VH / 2.0 + 10;

                Canvas.SetLeft(holder, bx);
                Canvas.SetTop(holder, by);
                // 挂到最上层，避免被飞散的碎块盖住
                _hudLayer.Children.Add(holder);

                _bubbles.Add(new Bubble
                {
                    View = holder,
                    Src = p,
                    X = bx,
                    Y = by,
                    T0 = now,
                    Life = 2.0
                });
            }
            catch { }
        }

        private void StepBubbles()
        {
            double now = _clock.Elapsed.TotalSeconds;
            for (int i = _bubbles.Count - 1; i >= 0; i--)
            {
                var bb = _bubbles[i];
                double el = now - bb.T0;
                if (el >= bb.Life)
                {
                    try { _hudLayer.Children.Remove(bb.View); } catch { }
                    _bubbles.RemoveAt(i);
                    continue;
                }

                double p = el / bb.Life;

                // 人还活着就跟着走，死了就停在最后位置
                if (bb.Src != null && bb.Src.Alive)
                {
                    bb.X = bb.Src.X - bb.View.Width / 2.0;
                    double by = bb.Src.Y - bb.Src.VH / 2.0 - bb.View.Height - 14;
                    if (by < 8) by = bb.Src.Y - bb.Src.VH / 2.0 + 10;
                    bb.Y = by;
                }

                Canvas.SetLeft(bb.View, bb.X);
                Canvas.SetTop(bb.View, bb.Y - 22.0 * p);

                double a;
                if (p < 0.10) a = p / 0.10;
                else if (p > 0.78) a = (1.0 - p) / 0.22;
                else a = 1.0;
                if (a < 0) a = 0;

                bb.View.Opacity = a;

                double s = p < 0.10 ? (0.88 + 0.12 * (p / 0.10)) : 1.0;
                bb.View.RenderTransform = new ScaleTransform(s, s);
            }
        }

        private void UpdateHud()
        {
            _scoreText.Text = _score.ToString("N0");
            int alive = 0;
            for (int i = 0; i < _pigs.Count; i++) if (_pigs[i].Alive) alive++;
            _leftText.Text = "剩余猪头 " + alive;
        }

        private Bird MakeBird(Ammo a)
        {
            var badge = Art.MakeBadge(a.Icon, a.Kind);
            // 物理半径贴近视觉半径（徽章画到 48，外面还有透明边），避免“看着碰到了却穿过去”
            double r = a.Kind == "giant" ? 52 : 44;
            var holder = new Canvas { Width = 96, Height = 96 };
            Canvas.SetLeft(badge, (96 - badge.Width) / 2.0);
            Canvas.SetTop(badge, (96 - badge.Height) / 2.0);
            holder.Children.Add(badge);

            double scale = r / (a.Kind == "giant" ? 48 : 42);
            holder.RenderTransform = new ScaleTransform(scale, scale);
            holder.RenderTransformOrigin = new Point(0.5, 0.5);

            var b = new Bird
            {
                View = holder,
                Kind = a.Kind,
                X = _anchorX,
                Y = _anchorY,
                R = r,
                Mass = a.Kind == "giant" ? 2.2 : 1.0
            };
            Canvas.SetLeft(holder, b.X - 48);
            Canvas.SetTop(holder, b.Y - 48);
            _gameLayer.Children.Add(holder);
            return b;
        }

        private void PullNext()
        {
            if (_current != null || _levelClear || _finished) return;
            if (_ammo.Count == 0)
            {
                RefillAmmo();
                DrawAmmo();
            }
            if (_ammo.Count == 0) return;

            var a = _ammo[0];
            _ammo.RemoveAt(0);
            var na = MakeAmmoItem();
            if (na != null) _ammo.Insert(Math.Min(_ammo.Count, 4), na);
            DrawAmmo();

            _current = MakeBird(a);
            _current.X = _anchorX;
            _current.Y = _anchorY;
            HidePreview();
            UpdateBand();
            UpdateHud();
        }

        private void UpdateBand()
        {
            double bx = _anchorX, by = _anchorY;
            bool held = (_current != null && !_current.Flying);
            if (held)
            {
                bx = _current.X;
                by = _current.Y;
            }

            // 皮筋拉长会变细，这是橡胶的真实表现
            double pull = Math.Sqrt((bx - _anchorX) * (bx - _anchorX) + (by - _anchorY) * (by - _anchorY));
            double thick = 18.0 - 6.5 * Math.Min(1.0, pull / 170.0);
            _band1.StrokeThickness = thick;
            _band2.StrokeThickness = thick;

            _band1.X2 = bx;
            _band1.Y2 = by;
            _band2.X2 = bx;
            _band2.Y2 = by;

            if (_pouch != null)
            {
                if (held && pull > 24)
                {
                    double side = Math.Sqrt((bx - _anchorX) * (bx - _anchorX) + (by - _anchorY) * (by - _anchorY));
                    if (side < 1) side = 1;
                    double nx = -(by - _anchorY) / side;
                    double ny = (bx - _anchorX) / side;
                    double w = 30;
                    _pouch.Width = 44;
                    _pouch.Height = 22;
                    _pouch.RenderTransform = new RotateTransform(
                        Math.Atan2(by - _anchorY, bx - _anchorX) * 180.0 / Math.PI, 22, 11);
                    Canvas.SetLeft(_pouch, bx - 22);
                    Canvas.SetTop(_pouch, by - 11);
                    _pouch.Visibility = Visibility.Visible;
                }
                else
                {
                    _pouch.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_levelClear || _finished) return;

            if (_current == null) PullNext();
            if (_current == null) return;

            if (!_current.Flying)
            {
                double dx = Mouse.GetPosition(_root).X - _current.X;
                double dy = Mouse.GetPosition(_root).Y - _current.Y;
                if (dx * dx + dy * dy < 420 * 420)
                {
                    _dragging = true;
                }
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _current == null || _current.Flying) return;

            var p = Mouse.GetPosition(_root);
            double dx = p.X - _anchorX;
            double dy = p.Y - _anchorY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double maxLen = 170;
            if (len > maxLen)
            {
                dx = dx / len * maxLen;
                dy = dy / len * maxLen;
            }

            _current.X = _anchorX + dx;
            _current.Y = _anchorY + dy;
            Canvas.SetLeft(_current.View, _current.X - 48);
            Canvas.SetTop(_current.View, _current.Y - 48);
            UpdateBand();

            double pw = _current.Kind == "giant" ? 17.0 : 20.0;
            UpdatePreview((_anchorX - _current.X) * pw, (_anchorY - _current.Y) * pw);

            // 皮筋音只在“真的又拉动了”时才响，且节流到 0.28s，避免连续刷屏式的刺啦声
            double now = _clock.Elapsed.TotalSeconds;
            if (len > 24 && Math.Abs(len - _lastStretchLen) > 15 && now - _lastStretchSfx > 0.28)
            {
                _lastStretchSfx = now;
                _lastStretchLen = len;
                Sfx.Play("stretch");
            }
            else if (Math.Abs(len - _lastStretchLen) > 6)
            {
                _lastStretchLen = len;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || _current == null || _current.Flying)
            {
                _dragging = false;
                return;
            }
            _dragging = false;

            double dx = _anchorX - _current.X;
            double dy = _anchorY - _current.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 14)
            {
                _current.X = _anchorX;
                _current.Y = _anchorY;
                Canvas.SetLeft(_current.View, _current.X - 48);
                Canvas.SetTop(_current.View, _current.Y - 48);
                UpdateBand();
                return;
            }

            double power = 20.0;
            if (_current.Kind == "giant") power = 17.0;
            _current.VX = dx * power;
            _current.VY = dy * power;
            _current.Flying = true;

            float dens = _current.Kind == "giant" ? 3.6f : 2.6f;
            _current.PBody = Phys.AddCircle(_current.X, _current.Y, _current.R, dens, true, _current);
            Phys.SetVel(_current.PBody, _current.VX, _current.VY);

            Sfx.Play("launch");
            HidePreview();
            UpdateBand();
            _lastStretchLen = 0;
            KickSling(dx, dy);
        }

        /// <summary>发射后弹弓的反作用抖动（衰减振荡）</summary>
        private void KickSling(double dirX, double dirY)
        {
            double len = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (len < 1 || _slingShift == null || _slingScale == null) return;
            double ux = dirX / len, uy = dirY / len;
            double t0 = _clock.Elapsed.TotalSeconds;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (s, e) =>
            {
                double el = _clock.Elapsed.TotalSeconds - t0;
                if (el >= 0.28)
                {
                    timer.Stop();
                    _slingShift.X = 0;
                    _slingShift.Y = 0;
                    _slingScale.ScaleX = 1.3;
                    _slingScale.ScaleY = 1.3;
                    return;
                }
                double amp = 12.0 * Math.Exp(-el * 13.0) * Math.Cos(el * 28.0);
                _slingShift.X = -ux * amp;
                _slingShift.Y = -uy * amp;
                _slingScale.ScaleX = 1.3 + amp * 0.007;
                _slingScale.ScaleY = 1.3 - amp * 0.007;
            };
            timer.Start();
        }

        // 红球能力：空中引爆。炸点半径内掩体按距离扣血并被向外抛飞，其中的猪直接被震死。
        private const double BlastR = 330.0;

        private void ExplodeBird(Bird b)
        {
            if (b == null || b.Dead || b.Boosted) return;
            b.Boosted = true;

            double cx = b.X, cy = b.Y;

            Sfx.Play("explode");
            // 爆炸音的头部不能被同时爆发的碎裂声打断
            Sfx.Mute(380, "breakWood", "breakGlass", "breakRock", "pop", "hurt", "hitWood", "hitGlass", "hitRock");
            Shake(0.42);
            BlastVisual(cx, cy);

            var covers = new List<Cover>();
            foreach (var cov in _covers) covers.Add(cov);
            foreach (var cov in covers)
            {
                if (!cov.Alive) continue;
                double dx = cov.X + cov.W / 2 - cx;
                double dy = cov.Y + cov.H / 2 - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > BlastR) continue;
                double f = 1.0 - d / BlastR;
                int dmg = f > 0.62 ? 4 : (f > 0.28 ? 2 : 1);
                BlastCover(cov, dmg, cx, cy);
            }

            var pigs = new List<Pig>();
            foreach (var p in _pigs) pigs.Add(p);
            foreach (var p in pigs)
            {
                if (!p.Alive) continue;
                double dx = p.X - cx;
                double dy = p.Y - cy;
                if (Math.Sqrt(dx * dx + dy * dy) > BlastR) continue;
                CrushPig(p);
            }

            b.Dead = true;
            if (b.PBody != null) { Phys.Destroy(b.PBody); b.PBody = null; }

            UpdateHud();
            CheckClear();
        }

        private Cover SpawnShard(double centerX, double centerY, double w, double h, string mat)
        {
            int cw = (int)Math.Max(16.0, w);
            int chh = (int)Math.Max(16.0, h);
            var view = Art.DrawCover(mat, cw, chh, false);
            var cov = new Cover
            {
                View = view,
                X = centerX - cw / 2.0,
                Y = centerY - chh / 2.0,
                W = cw,
                H = chh,
                Mat = mat,
                Hp = 1,
                MaxHp = 1
            };
            var rot = new RotateTransform(0, cw / 2.0, chh / 2.0);
            view.RenderTransform = rot;
            cov.Rot = rot;
            Canvas.SetLeft(view, cov.X);
            Canvas.SetTop(view, cov.Y);
            _gameLayer.Children.Add(view);
            RegisterCoverView(view);

            float dens = mat == "glass" ? 0.70f : (mat == "wood" ? 1.30f : 3.20f);
            float fric = mat == "glass" ? 0.45f : (mat == "wood" ? 0.72f : 0.60f);
            cov.PBody = Phys.AddBox(cov.X + cw / 2.0, cov.Y + chh / 2.0 + 1.0,
                cw - 0.4, chh - 2.0, dens, fric, cov);
            _covers.Add(cov);
            return cov;
        }

        // 炸成两段：沿长轴切开，两块各自带物理体往外飞
        private void ShatterCover(Cover cov, double cx, double cy)
        {
            Color pc = cov.Mat == "glass" ? (Color)ColorConverter.ConvertFromString("#8FE3F5")
                : cov.Mat == "wood" ? (Color)ColorConverter.ConvertFromString("#C99A5E")
                : (Color)ColorConverter.ConvertFromString("#AEB6C0");

            double mx = cov.X + cov.W / 2.0, my = cov.Y + cov.H / 2.0;
            var list = new List<Cover>();
            if (cov.W >= cov.H)
            {
                double cut = cov.W * (0.34 + _rng.NextDouble() * 0.32);
                list.Add(SpawnShard(cov.X + cut / 2.0, my, cut, cov.H, cov.Mat));
                list.Add(SpawnShard(cov.X + cut + (cov.W - cut) / 2.0, my, cov.W - cut, cov.H, cov.Mat));
            }
            else
            {
                double cut = cov.H * (0.34 + _rng.NextDouble() * 0.32);
                list.Add(SpawnShard(mx, cov.Y + cut / 2.0, cov.W, cut, cov.Mat));
                list.Add(SpawnShard(mx, cov.Y + cut + (cov.H - cut) / 2.0, cov.W, cov.H - cut, cov.Mat));
            }

            foreach (var sh in list)
            {
                double dx = sh.X + sh.W / 2.0 - cx;
                double dy = sh.Y + sh.H / 2.0 - cy;
                Phys.Kick(sh.PBody, dx, dy, 1500 + 500 * _rng.NextDouble());
            }
            for (int i = 0; i < 12; i++) SpawnParticle(mx, my, pc, 5);
        }

        // 炸没：白闪 + 整体缩小淡出 + 碎屑飞散
        private void DustCover(Cover cov, double cx, double cy)
        {
            Color pc = cov.Mat == "glass" ? (Color)ColorConverter.ConvertFromString("#8FE3F5")
                : cov.Mat == "wood" ? (Color)ColorConverter.ConvertFromString("#C99A5E")
                : (Color)ColorConverter.ConvertFromString("#AEB6C0");

            double mx = cov.X + cov.W / 2.0, my = cov.Y + cov.H / 2.0;

            for (int i = 0; i < 22; i++) SpawnParticle(mx, my, pc, 5);

            var v = cov.View;
            var flash = new Rectangle
            {
                Width = cov.W,
                Height = cov.H,
                Fill = new SolidColorBrush(Color.FromArgb(150, 255, 246, 214)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(flash, 0);
            Canvas.SetTop(flash, 0);
            v.Children.Add(flash);

            v.RenderTransformOrigin = new Point(0.5, 0.5);
            var st = new ScaleTransform(1, 1);
            var ro = new RotateTransform(0);
            var grp = new TransformGroup();
            grp.Children.Add(st);
            grp.Children.Add(ro);
            v.RenderTransform = grp;

            _dust.Add(new DustGhost
            {
                V = v,
                Flash = flash,
                Scale = st,
                Rot = ro,
                T0 = _clock.Elapsed.TotalSeconds,
                Dir = _rng.NextDouble() > 0.5 ? 1.0 : -1.0
            });

            // 兜底：就算主循环因为清场/暂停没跑到，也保证这层画面一定被摘掉
            var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            guard.Tick += (s2, e2) =>
            {
                guard.Stop();
                try { _gameLayer.Children.Remove(v); } catch { }
            };
            guard.Start();
        }

        private void StepDust()
        {
            double now = _clock.Elapsed.TotalSeconds;
            for (int i = _dust.Count - 1; i >= 0; i--)
            {
                var g = _dust[i];
                double el = now - g.T0;
                if (el >= 0.34)
                {
                    try { _gameLayer.Children.Remove(g.V); } catch { }
                    _dust.RemoveAt(i);
                    continue;
                }
                double p = el / 0.34;
                double k = 1.0 - 0.55 * p;
                g.Scale.ScaleX = k;
                g.Scale.ScaleY = k;
                g.Rot.Angle = 14.0 * p * g.Dir;
                g.V.Opacity = 1.0 - p * p;
                g.Flash.Opacity = 0.9 * (1.0 - p);
            }
        }

        private void BlastCover(Cover cov, int dmg, double cx, double cy)
        {
            cov.Hp -= dmg;
            Color c = cov.Mat == "glass" ? (Color)ColorConverter.ConvertFromString("#8FE3F5")
                : cov.Mat == "wood" ? (Color)ColorConverter.ConvertFromString("#C99A5E")
                : (Color)ColorConverter.ConvertFromString("#AEB6C0");

            if (cov.Hp <= 0)
            {
                bool split = cov.W * cov.H > 9000 && _rng.Next(100) < 45;

                cov.Alive = false;
                _covers.Remove(cov);
                if (cov.PBody != null) { Phys.Destroy(cov.PBody); cov.PBody = null; }
                _score += 500;

                if (cov.Mat == "glass") Sfx.Play("breakGlass");
                else if (cov.Mat == "wood") Sfx.Play("breakWood");
                else Sfx.Play("breakRock");

                if (split) ShatterCover(cov, cx, cy);
                else DustCover(cov, cx, cy);
            }
            else
            {
                if (cov.Mat == "glass") Sfx.Play("hitGlass");
                else if (cov.Mat == "wood") Sfx.Play("hitWood");
                else Sfx.Play("hitRock");
                AddCracks(cov);
                double dx = cov.X + cov.W / 2 - cx;
                double dy = cov.Y + cov.H / 2 - cy;
                Phys.Kick(cov.PBody, dx, dy, 1600);
            }
        }

        private void BlastVisual(double cx, double cy)
        {
            var fire = new Ellipse { Width = 40, Height = 40, IsHitTestVisible = false };
            var rg = new RadialGradientBrush();
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFDF0"), 0.0));
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFD34B"), 0.42));
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F2650F"), 0.76));
            rg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7A1404"), 1.0));
            fire.Fill = rg;
            Canvas.SetLeft(fire, cx - 20);
            Canvas.SetTop(fire, cy - 20);
            _gameLayer.Children.Add(fire);

            var ring = new Ellipse
            {
                Width = 30,
                Height = 30,
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 236, 186)),
                StrokeThickness = 11,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ring, cx - 15);
            Canvas.SetTop(ring, cy - 15);
            _gameLayer.Children.Add(ring);

            for (int i = 0; i < 34; i++)
            {
                Color pc = i % 3 == 0
                    ? (Color)ColorConverter.ConvertFromString("#FFE07A")
                    : (Color)ColorConverter.ConvertFromString("#F2701C");
                SpawnParticle(cx, cy, pc, 4);
            }

            double t0 = _clock.Elapsed.TotalSeconds;
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            tm.Tick += (s, e) =>
            {
                double el = _clock.Elapsed.TotalSeconds - t0;
                if (el >= 0.52)
                {
                    tm.Stop();
                    try { _gameLayer.Children.Remove(fire); } catch { }
                    try { _gameLayer.Children.Remove(ring); } catch { }
                    return;
                }
                double p1 = el / 0.30;
                if (p1 > 1.0) p1 = 1.0;
                double fw = 44 + BlastR * 1.15 * p1;
                fire.Width = fw;
                fire.Height = fw;
                Canvas.SetLeft(fire, cx - fw / 2);
                Canvas.SetTop(fire, cy - fw / 2);
                fire.Opacity = 1.0 - 0.92 * p1;

                double p2 = el / 0.52;
                double rw = 30 + BlastR * 2.1 * p2;
                ring.Width = rw;
                ring.Height = rw;
                Canvas.SetLeft(ring, cx - rw / 2);
                Canvas.SetTop(ring, cy - rw / 2);
                ring.StrokeThickness = 11 * (1.0 - p2) + 1.0;
                ring.Opacity = 0.9 * (1.0 - p2);
            };
            tm.Start();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (!_clock.IsRunning) _clock.Start();
            double t = _clock.Elapsed.TotalSeconds;
            if (_lastT == 0) { _lastT = t; return; }
            double dt = t - _lastT;
            _lastT = t;

            if (dt > 0.06) dt = 0.06;
            if (dt <= 0) return;

            if (t < _freezeUntil) return;

            Step(dt);

            if (t < _shakeUntil)
            {
                double amp = 5.0 * ((_shakeUntil - t) / 0.14);
                _root.RenderTransform = new TranslateTransform(
                    (_rng.NextDouble() * 2 - 1) * amp,
                    (_rng.NextDouble() * 2 - 1) * amp);
            }
            else if (_root.RenderTransform != null)
            {
                _root.RenderTransform = null;
            }
        }

        private double _sweepT = -99;
        // 所有出现过的掩体画面（含被炸出来的碎块），只用来回收掩体的残留
        private readonly List<Canvas> _coverViews = new List<Canvas>();

        private void RegisterCoverView(Canvas v)
        {
            if (v != null) _coverViews.Add(v);
        }

        // 只回收掩体的画面残留：球、猪、粒子、火光一概不碰
        private void SweepGhosts()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _sweepT < 0.4) return;
            _sweepT = now;

            // 1) 已死或没有物理体的掩体，从逻辑列表里摘掉
            for (int i = _covers.Count - 1; i >= 0; i--)
            {
                var c = _covers[i];
                if (c.Alive && c.PBody != null) continue;
                c.Alive = false;
                _covers.RemoveAt(i);
            }

            // 2) 逐个检查登记过的掩体画面：不在存活列表里、也不在播放炸没动画，就回收
            for (int i = _coverViews.Count - 1; i >= 0; i--)
            {
                var v = _coverViews[i];
                if (v == null) { _coverViews.RemoveAt(i); continue; }

                bool alive = false;
                for (int k = 0; k < _covers.Count; k++)
                {
                    if (_covers[k].View == v && _covers[k].Alive) { alive = true; break; }
                }

                bool dusting = false;
                for (int k = 0; k < _dust.Count; k++)
                {
                    if (_dust[k].V == v) { dusting = true; break; }
                }

                if (alive || dusting) continue;

                try { _gameLayer.Children.Remove(v); } catch { }
                _coverViews.RemoveAt(i);
            }
        }

        private void Step(double dt)
        {
            Phys.Step((float)dt);
            ResolveHits();
            StepFalling(dt);
            if (_current != null && _current.Flying) StepBird(_current, dt);
            StepParticles(dt);
            StepDust();
            StepBubbles();
            SweepGhosts();

            if (_current != null && _current.Dead)
            {
                Phys.Destroy(_current.PBody);
                _current.PBody = null;
                _gameLayer.Children.Remove(_current.View);
                _current = null;
                UpdateBand();
                UpdateHud();
                if (!_levelClear && !_finished) PullNext();
            }
        }

        private void StepFalling(double dt)
        {
            for (int i = 0; i < _covers.Count; i++)
            {
                var c = _covers[i];
                if (!c.Alive || c.PBody == null) continue;

                var pos = Phys.PosOf(c.PBody);
                c.X = Phys.SX(pos.X) - c.W / 2.0;
                c.Y = Phys.SY(pos.Y) - c.H / 2.0;
                c.Ang = Phys.AngleDeg(Phys.AngOf(c.PBody));

                Canvas.SetLeft(c.View, c.X);
                Canvas.SetTop(c.View, c.Y);
                c.Rot.Angle = c.Ang;
                c.Rot.CenterX = c.W / 2.0;
                c.Rot.CenterY = c.H / 2.0;
            }

            for (int i = 0; i < _pigs.Count; i++)
            {
                var p = _pigs[i];
                if (!p.Alive || p.PBody == null) continue;

                var pos = Phys.PosOf(p.PBody);
                double cy = Phys.SY(pos.Y);
                p.X = Phys.SX(pos.X);
                p.Y = cy + p.BoxH / 2.0 - p.VH / 2.0;

                Canvas.SetLeft(p.View, p.X - p.VW / 2.0);
                Canvas.SetTop(p.View, p.Y - p.VH / 2.0);
            }
        }

        private void ResolveHits()
        {
            var hits = Phys.Hits;
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                double hx = Phys.SX(h.At.X);
                double hy = Phys.SY(h.At.Y);
                double vrel = h.VRel * Phys.SC;

                Bird bird = _current;
                if (bird != null && bird.Flying && !bird.Dead && bird.PBody != null)
                {
                    double bx = hx - bird.X, by = hy - bird.Y;
                    if (Math.Sqrt(bx * bx + by * by) <= bird.R + 18.0)
                    {
                        // 红球：只要真正接触任何东西就引爆，不看材质和速度
                        if (bird.Kind == "red" && !bird.Boosted)
                        {
                            ExplodeBird(bird);
                            return;
                        }

                        Cover cov = PickCover(hx, hy, 12.0);
                        Pig pig = PickPig(hx, hy, 16.0);
                        double dc = cov == null ? 1e9 : DistToBox(cov.X, cov.Y, cov.W, cov.H, hx, hy);
                        double dp = pig == null ? 1e9 : DistToBox(pig.X - pig.R * 0.67, pig.Y - pig.VH / 2,
                            pig.R * 1.34, pig.VH, hx, hy);

                        if (cov != null && dc <= dp)
                        {
                            if (vrel > 900.0) HitCoverAt(bird, cov, vrel);
                        }
                        else if (pig != null)
                        {
                            if (vrel > 700.0) HitPig(bird, pig);
                        }
                        continue;
                    }
                }

                if (vrel > 620.0)
                {
                    for (int k = 0; k < _pigs.Count; k++)
                    {
                        var pg = _pigs[k];
                        if (!pg.Alive || pg.PBody == null) continue;
                        if (hx > pg.X - pg.R * 1.30 && hx < pg.X + pg.R * 1.30
                            && hy > pg.Y - pg.VH / 2 - 18 && hy < pg.Y + pg.VH / 2 + 18)
                        {
                            CrushPig(pg);
                            break;
                        }
                    }
                }
            }
        }

        private Cover PickCover(double x, double y, double pad)
        {
            Cover best = null;
            double bestD = 1e9;
            for (int i = 0; i < _covers.Count; i++)
            {
                var c = _covers[i];
                if (!c.Alive) continue;
                if (x < c.X - pad || x > c.X + c.W + pad) continue;
                if (y < c.Y - pad || y > c.Y + c.H + pad) continue;
                double d = DistToBox(c.X, c.Y, c.W, c.H, x, y);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private Pig PickPig(double x, double y, double pad)
        {
            Pig best = null;
            double bestD = 1e9;
            for (int i = 0; i < _pigs.Count; i++)
            {
                var p = _pigs[i];
                if (!p.Alive || p.PBody == null) continue;
                double bx = p.X - p.R * 0.67, by = p.Y - p.VH / 2;
                double bw = p.R * 1.34, bh = p.VH;
                if (x < bx - pad || x > bx + bw + pad) continue;
                if (y < by - pad || y > by + bh + pad) continue;
                double d = DistToBox(bx, by, bw, bh, x, y);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private static double DistToBox(double bx, double by, double bw, double bh, double x, double y)
        {
            double dx = 0, dy = 0;
            if (x < bx) dx = bx - x;
            else if (x > bx + bw) dx = x - (bx + bw);
            if (y < by) dy = by - y;
            else if (y > by + bh) dy = y - (by + bh);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        private void CrushPig(Pig p)
        {
            if (!p.Alive) return;

            double now = _clock.Elapsed.TotalSeconds;
            if (now - p.CrushT < 1.2) return;
            p.CrushT = now;

            p.Hp -= 1;
            Sfx.Play("hurt");
            SayWord(p);
            for (int i = 0; i < 12; i++) SpawnParticle(p.X, p.Y, p.Blood, 5);
            _score += 400;
            ShowFloat("+400", p.X, p.Y - p.R * 0.45, "#FFE9A8");
            Shake(0.1);

            var flash = new Ellipse
            {
                Width = p.VW,
                Height = p.VH,
                Fill = new SolidColorBrush(Color.FromArgb(108, 255, 70, 70))
            };
            p.View.Children.Add(flash);
            var ft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
            ft.Tick += (s, e) =>
            {
                ft.Stop();
                try { p.View.Children.Remove(flash); } catch { }
            };
            ft.Start();

            if (p.Hp <= 0)
            {
                p.Alive = false;
                _gameLayer.Children.Remove(p.View);
                Phys.Destroy(p.PBody);
                p.PBody = null;
                int baseScore = p.Boss ? 9000 : 3000;
                _score += baseScore;
                ShowFloat("+" + baseScore, p.X, p.Y - p.R * 0.6, p.Boss ? "#FFD75E" : "#FFE9A8");
                Sfx.Play("pop");
                Shake(0.14);
                for (int i = 0; i < 22; i++) SpawnParticle(p.X, p.Y, p.Blood, 6);
            }

            UpdateHud();
            CheckClear();
        }

        private static double RayAABB(double px, double py, double dx, double dy,
            double x0, double y0, double x1, double y1, out int side)
        {
            side = 0;
            double tmin = 0.0, tmax = 1.0;

            if (Math.Abs(dx) < 1e-9)
            {
                if (px < x0 || px > x1) return -1;
            }
            else
            {
                double t1 = (x0 - px) / dx;
                double t2 = (x1 - px) / dx;
                if (t1 > t2) { double tt = t1; t1 = t2; t2 = tt; }
                if (t1 > tmin) { tmin = t1; side = dx > 0 ? 0 : 1; }
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return -1;
            }

            if (Math.Abs(dy) < 1e-9)
            {
                if (py < y0 || py > y1) return -1;
            }
            else
            {
                double t1 = (y0 - py) / dy;
                double t2 = (y1 - py) / dy;
                if (t1 > t2) { double tt = t1; t1 = t2; t2 = tt; }
                if (t1 > tmin) { tmin = t1; side = dy > 0 ? 2 : 3; }
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return -1;
            }

            if (tmin < 0 || tmin > 1) return -1;
            return tmin;
        }

        private void StepBird(Bird b, double dt)
        {
            if (b.PBody == null) return;

            var pos = Phys.PosOf(b.PBody);
            b.X = Phys.SX(pos.X);
            b.Y = Phys.SY(pos.Y);

            Canvas.SetLeft(b.View, b.X - 48);
            Canvas.SetTop(b.View, b.Y - 48);

            // 红球：飞出去只要碰到东西就炸，不用点击
            if (b.Kind == "red" && !b.Boosted && b.FlyT > 0.10)
            {
                if (PickCover(b.X, b.Y, 24) != null) { ExplodeBird(b); return; }
                if (PickPig(b.X, b.Y, 26) != null) { ExplodeBird(b); return; }
                if (b.Y > 1130 - 54) { ExplodeBird(b); return; }
            }

            double spd = Phys.SpeedPx(b.PBody);
            b.VX = 0;
            b.VY = 0;
            b.FlyT += dt;
            if (spd < 90.0) b.SlowT += dt; else b.SlowT = 0;

            if (_rng.NextDouble() < 0.55)
            {
                Color c = b.Kind == "red"
                    ? (Color)ColorConverter.ConvertFromString("#FFD75E")
                    : (Color)ColorConverter.ConvertFromString("#C7D6E6");
                SpawnParticle(b.X, b.Y, c, 2);
            }

            // 一离开屏幕就收，不再等它慢慢滚出几百像素
            if (b.X < -60 || b.X > _W + 60 || b.Y > _H + 60 || b.Y < -_H)
            {
                b.Dead = true;
                return;
            }
            // 已经不可能再打到东西：速度掉到爬行并持续 0.5s，就结束这一发
            if (b.SlowT > 0.5)
            {
                b.Dead = true;
                return;
            }
            // 兜底：飞太久（掉进结构缝隙里打转）也强制结束
            if (b.FlyT > 6.0)
            {
                b.Dead = true;
                return;
            }
        }
        private bool HitCoverAt(Bird b, Cover cov, double vrel)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - cov.HitT < 0.10) return false;
            cov.HitT = now;

            int dmg = 1;
            if (b.Kind == "giant") dmg = 3;
            if (b.Kind == "red" && b.Boosted) dmg = cov.Mat == "wood" ? 3 : 2;
            // 高速撞上去的普通弹丸也能砸穿一层，避免“石头完全打不动”
            if (vrel > 2400.0 && dmg < 2) dmg = 2;

            cov.Hp -= dmg;
            bool broke = cov.Hp <= 0;

            double ringX = b.X, ringY = b.Y;
            var ring = new Ellipse
            {
                Width = 20,
                Height = 20,
                Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 245, 210)),
                StrokeThickness = 3,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ring, ringX - 10);
            Canvas.SetTop(ring, ringY - 10);
            _gameLayer.Children.Add(ring);
            double ringT0 = _clock.Elapsed.TotalSeconds;
            var ringTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            ringTimer.Tick += (s, e) =>
            {
                double el = _clock.Elapsed.TotalSeconds - ringT0;
                if (el >= 0.32)
                {
                    ringTimer.Stop();
                    try { _gameLayer.Children.Remove(ring); } catch { }
                    return;
                }
                double sc = 1.0 + el * 8.0;
                ring.Width = 20 * sc;
                ring.Height = 20 * sc;
                Canvas.SetLeft(ring, ringX - 10 * sc);
                Canvas.SetTop(ring, ringY - 10 * sc);
                ring.Opacity = 0.8 * (1.0 - el / 0.32);
            };
            ringTimer.Start();

            if (broke)
            {
                if (cov.Mat == "glass") Sfx.Play("breakGlass");
                else if (cov.Mat == "wood") Sfx.Play("breakWood");
                else Sfx.Play("breakRock");
            }
            else
            {
                if (cov.Mat == "glass") Sfx.Play("hitGlass");
                else if (cov.Mat == "wood") Sfx.Play(dmg >= 2 ? "damageWood" : "hitWood");
                else Sfx.Play(dmg >= 2 ? "damageRock" : "hitRock");
                AddCracks(cov);
            }

            Color c = cov.Mat == "glass" ? (Color)ColorConverter.ConvertFromString("#8FE3F5")
                : cov.Mat == "wood" ? (Color)ColorConverter.ConvertFromString("#C99A5E")
                : (Color)ColorConverter.ConvertFromString("#AEB6C0");
            for (int i = 0; i < 10; i++) SpawnParticle(b.X, b.Y, c, 4);

            _score += 200;

            if (broke)
            {
                cov.Alive = false;
                _covers.Remove(cov);
                _gameLayer.Children.Remove(cov.View);
                Phys.Destroy(cov.PBody);
                cov.PBody = null;
                _score += 500;
                Shake(0.12);
                for (int i = 0; i < 16; i++) SpawnParticle(cov.X + cov.W / 2, cov.Y + cov.H / 2, c, 5);
            }

            _freezeUntil = _clock.Elapsed.TotalSeconds + 0.05;
            UpdateHud();
            return broke;
        }
        private void AddCracks(Cover cov)
        {
            try
            {
                double w = cov.W, h = cov.H;
                if (w < 12 || h < 12) return;

                int lost = cov.MaxHp - cov.Hp;
                string ink = cov.Mat == "glass" ? "#FFFFFF" : (cov.Mat == "wood" ? "#2A1B10" : "#20262E");
                double alpha = cov.Mat == "glass" ? 0.80 : 0.72;
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ink));
                brush.Opacity = alpha;

                var rnd = new Random((int)(cov.X * 31 + cov.Y * 57 + lost * 13));
                int count = 1 + lost;
                for (int k = 0; k < count; k++)
                {
                    var crack = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = cov.Mat == "glass" ? 2.2 : 2.8,
                        StrokeLineJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    double px = w * (0.10 + rnd.NextDouble() * 0.80);
                    double py = h * (0.10 + rnd.NextDouble() * 0.45);
                    crack.Points.Add(new Point(px, py));
                    int segs = 3 + rnd.Next(3);
                    for (int s = 0; s < segs; s++)
                    {
                        px += (rnd.NextDouble() * 2 - 1) * w * 0.24;
                        py += (rnd.NextDouble() * 0.85 + 0.15) * h * 0.26;
                        if (px < 2) px = 2;
                        if (px > w - 2) px = w - 2;
                        if (py > h - 2) py = h - 2;
                        crack.Points.Add(new Point(px, py));
                    }
                    cov.View.Children.Add(crack);
                }

                if (lost == 1)
                {
                    var dim = new Rectangle
                    {
                        Width = w,
                        Height = h,
                        Fill = new SolidColorBrush(Color.FromArgb(cov.Mat == "glass" ? (byte)18 : (byte)22, 0, 0, 0)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(dim, 0);
                    Canvas.SetTop(dim, 0);
                    cov.View.Children.Insert(0, dim);
                }
            }
            catch { }
        }

        private void HitPig(Bird b, Pig p)
        {
            if (!p.Alive) return;

            double now = _clock.Elapsed.TotalSeconds;
            if (now - p.HitT < 0.30) return;
            p.HitT = now;

            int dmg = b.Kind == "giant" ? 2 : 1;
            if (b.Kind == "red" && b.Boosted) dmg = 2;

            p.Hp -= dmg;
            Sfx.Play("hurt");
            SayWord(p);

            for (int i = 0; i < 12; i++) SpawnParticle(b.X, b.Y, p.Blood, 4);

            p.View.RenderTransform = new RotateTransform(8);
            var jitter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            jitter.Tick += (s, e) =>
            {
                jitter.Stop();
                if (p.View != null) p.View.RenderTransform = null;
            };
            jitter.Start();

            _score += 1000;
            ShowFloat("+1000", p.X, p.Y - p.R * 0.45, "#FFE9A8");

            var flash = new Ellipse
            {
                Width = p.VW,
                Height = p.VH,
                Fill = new SolidColorBrush(Color.FromArgb(108, 255, 70, 70))
            };
            p.View.Children.Add(flash);
            var ft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
            ft.Tick += (s, e) =>
            {
                ft.Stop();
                try { p.View.Children.Remove(flash); } catch { }
            };
            ft.Start();

            if (p.Hp <= 0)
            {
                p.Alive = false;
                _gameLayer.Children.Remove(p.View);
                Phys.Destroy(p.PBody);
                p.PBody = null;
                b.Kills++;
                double mult = b.Kills >= 4 ? 3.0 : (b.Kills == 3 ? 2.0 : (b.Kills == 2 ? 1.5 : 1.0));
                int baseScore = p.Boss ? 9000 : 3000;
                int gained = (int)(baseScore * mult);
                _score += gained;
                ShowFloat("+" + gained, p.X, p.Y - p.R * 0.6, p.Boss ? "#FFD75E" : "#FFE9A8");
                if (b.Kills >= 2) ShowFloat("连击 ×" + b.Kills, p.X, p.Y - p.R * 0.6 - 42, "#FF9A4A");
                Sfx.Play("pop");
                Shake(0.14);
                for (int i = 0; i < 22; i++) SpawnParticle(p.X, p.Y, p.Blood, 6);
            }

            _freezeUntil = _clock.Elapsed.TotalSeconds + 0.07;
            UpdateHud();
            CheckClear();
        }

        private void CheckClear()
        {
            for (int i = 0; i < _pigs.Count; i++) if (_pigs[i].Alive) return;
            if (_levelClear) return;

            _levelClear = true;
            Sfx.Play("clear");
            ShowClearPanel();
        }

        private void ShowClearPanel()
        {
            var def = _levels[_levelIndex];
            int stars = _score >= def.Star3 ? 3 : (_score >= def.Star2 ? 2 : 1);

            var panel = new Canvas { Width = _W, Height = _H };
            var dim = new Rectangle
            {
                Width = _W, Height = _H,
                Fill = new SolidColorBrush(Color.FromArgb(120, 4, 8, 14))
            };
            panel.Children.Add(dim);

            var card = new Rectangle
            {
                Width = 520, Height = 300,
                Fill = Art.BA("#131A24", 0.94),
                Stroke = Art.BA("#FFD75E", 0.5),
                StrokeThickness = 2,
                RadiusX = 18, RadiusY = 18
            };
            Canvas.SetLeft(card, _W / 2 - 260);
            Canvas.SetTop(card, _H / 2 - 150);
            panel.Children.Add(card);

            bool last = _levelIndex >= _levels.Count - 1;
            var title = new TextBlock
            {
                Text = last ? "全部通关" : "过关",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 40,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            Canvas.SetLeft(title, _W / 2 - 90);
            Canvas.SetTop(title, _H / 2 - 116);
            panel.Children.Add(title);

            var st = new TextBlock
            {
                Text = new string('★', stars) + new string('☆', 3 - stars),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 46,
                Foreground = Art.B("#FFD75E")
            };
            Canvas.SetLeft(st, _W / 2 - 92);
            Canvas.SetTop(st, _H / 2 - 50);
            panel.Children.Add(st);

            var sc = new TextBlock
            {
                Text = "得分 " + _score.ToString("N0"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 24,
                Foreground = Art.BA("#DCE6F2", 0.85)
            };
            Canvas.SetLeft(sc, _W / 2 - 70);
            Canvas.SetTop(sc, _H / 2 + 30);
            panel.Children.Add(sc);

            var tip = new TextBlock
            {
                Text = last ? "按 ESC 返回桌面" : "正在进入下一关…",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 16,
                Foreground = Art.BA("#DCE6F2", 0.5)
            };
            Canvas.SetLeft(tip, _W / 2 - 80);
            Canvas.SetTop(tip, _H / 2 + 92);
            panel.Children.Add(tip);

            panel.Opacity = 0;
            _hudLayer.Children.Add(panel);

            var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            double t0 = _clock.Elapsed.TotalSeconds;
            fade.Tick += (s, e) =>
            {
                double k = (_clock.Elapsed.TotalSeconds - t0) / 0.24;
                if (k > 1) k = 1;
                panel.Opacity = k;
                if (k >= 1) fade.Stop();
            };
            fade.Start();

            if (last)
            {
                _finished = true;
                return;
            }

            var next = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            next.Tick += (s, e) =>
            {
                next.Stop();
                _hudLayer.Children.Remove(panel);
                LoadLevel(_levelIndex + 1);
            };
            next.Start();
        }

        private void SpawnParticle(double x, double y, Color color, double size)
        {
            if (_particles.Count > 260) return;
            double s = size + _rng.NextDouble() * size;
            var el = new Ellipse
            {
                Width = s,
                Height = s,
                Fill = new SolidColorBrush(color)
            };
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            _gameLayer.Children.Add(el);

            var p = new Particle
            {
                View = el,
                X = x,
                Y = y,
                VX = (_rng.NextDouble() * 2 - 1) * 260,
                VY = (_rng.NextDouble() * 2 - 1) * 260 - 60,
                Life = 0.0,
                MaxLife = 0.55 + _rng.NextDouble() * 0.5
            };
            _particles.Add(p);
        }

        private void StepParticles(double dt)
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life += dt;
                if (p.Life >= p.MaxLife)
                {
                    _gameLayer.Children.Remove(p.View);
                    _particles.RemoveAt(i);
                    continue;
                }
                p.VY += 700 * dt;
                p.X += p.VX * dt;
                p.Y += p.VY * dt;
                Canvas.SetLeft(p.View, p.X);
                Canvas.SetTop(p.View, p.Y);

                double k = 1 - p.Life / p.MaxLife;
                p.View.Opacity = k < 0 ? 0 : k;
                double sc = 0.4 + 0.6 * k;
                p.View.RenderTransform = new ScaleTransform(sc, sc);
                p.View.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }

        private void Shake(double dur)
        {
            _shakeUntil = _clock.Elapsed.TotalSeconds + dur;
        }

        private void ShowFloat(string text, double x, double y, string colorHex)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Art.B(colorHex)
            };
            Canvas.SetLeft(tb, x - 46);
            Canvas.SetTop(tb, y);
            _gameLayer.Children.Add(tb);

            var sw = Stopwatch.StartNew();
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            tm.Tick += (s, e) =>
            {
                double t = sw.Elapsed.TotalSeconds;
                if (t > 0.95)
                {
                    tm.Stop();
                    _gameLayer.Children.Remove(tb);
                    return;
                }
                Canvas.SetTop(tb, y - t * 78);
                tb.Opacity = 1 - t / 0.95;
            };
            tm.Start();
        }

        private static bool CircleRect(double cx, double cy, double r, double rx, double ry, double rw, double rh)
        {
            double nx = cx;
            if (nx < rx) nx = rx;
            if (nx > rx + rw) nx = rx + rw;
            double ny = cy;
            if (ny < ry) ny = ry;
            if (ny > ry + rh) ny = ry + rh;
            double dx = cx - nx;
            double dy = cy - ny;
            return dx * dx + dy * dy <= r * r;
        }
    }

    internal static class Program
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            try
            {
                int start = 0;
                if (args != null && args.Length > 0)
                {
                    int.TryParse(args[0], out start);
                }
                if (start < 0) start = 0;

                Sfx.Init();
                var app = new Application();
                app.DispatcherUnhandledException += (s, e) => { e.Handled = true; };
                var win = new GameWindow(start);
                app.Run(win);
            }
            catch (Exception ex)
            {
                try
                {
                    string p = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AngryDesktop_error.log");
                    System.IO.File.WriteAllText(p, ex.ToString());
                }
                catch { }
            }
        }
    }
}
