using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using Microsoft.Win32;

namespace AIUsageBar
{
    public class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardHover, Border, Text, Sub, Faint, Track, Good, Warn, Danger, Unknown, Accent, CodexTint, ClaudeTint, Selected, SelectedText, Link;

        public static bool SystemUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static Theme Create(bool dark)
        {
            var t = new Theme { Dark = dark };
            if (dark)
            {
                t.Bg = Hex("#1C1D20"); t.Card = Hex("#27282C"); t.CardHover = Hex("#2E3034"); t.Border = Hex("#36383D");
                t.Text = Hex("#F2F2F4"); t.Sub = Hex("#A7A8AE"); t.Faint = Hex("#7C7E85"); t.Track = Hex("#3A3C42");
                t.Good = Hex("#46C27A"); t.Warn = Hex("#F2A23A"); t.Danger = Hex("#F06560"); t.Unknown = Hex("#80828A");
                t.Accent = Hex("#5AB8F0"); t.CodexTint = Hex("#2EC4B6"); t.ClaudeTint = Hex("#F08A5D");
                t.Selected = Hex("#3B3E45"); t.SelectedText = Hex("#FFFFFF"); t.Link = Hex("#6CB8FF");
            }
            else
            {
                t.Bg = Hex("#F6F7FA"); t.Card = Hex("#FFFFFF"); t.CardHover = Hex("#FAFBFD"); t.Border = Hex("#E9ECF2");
                t.Text = Hex("#20242B"); t.Sub = Hex("#5F6875"); t.Faint = Hex("#98A0AD"); t.Track = Hex("#EAEDF3");
                t.Good = Hex("#16A384"); t.Warn = Hex("#D9820A"); t.Danger = Hex("#D6473A"); t.Unknown = Hex("#98A0AD");
                t.Accent = Hex("#4F63D6"); t.CodexTint = Hex("#0F9D82"); t.ClaudeTint = Hex("#D66338");
                t.Selected = Hex("#FFFFFF"); t.SelectedText = Hex("#20242B"); t.Link = Hex("#3B57C8");
            }
            return t;
        }

        public Color LevelColor(Level l)
        {
            switch (l)
            {
                case Level.Good: return Good;
                case Level.Warn: return Warn;
                case Level.Danger: return Danger;
                default: return Unknown;
            }
        }

        public static Color Hex(string s)
        {
            s = s.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static string uiFamily;
        /// <summary>検査用：使えないフォントを指定して、代わりのフォントが選ばれるかを確かめる</summary>
        public static string[] ExcludeFontsForTests;
        public static void ResetFontForTests() { uiFamily = null; }

        public static string UiFontFamily
        {
            get
            {
                if (uiFamily != null) return uiFamily;
                var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
                if (ExcludeFontsForTests != null) installed = installed.Where(n => !ExcludeFontsForTests.Contains(n)).ToList();
                foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS UI Gothic" })
                    if (installed.Contains(name)) { uiFamily = name; return name; }
                uiFamily = SystemFonts.MessageBoxFont.FontFamily.Name;
                return uiFamily;
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
