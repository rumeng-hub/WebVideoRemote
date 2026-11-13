// 视觉主题层：配色、绘制工具、自绘控件（无边框窗口 / 卡片 / 按钮 / 开关 / 标签页）
// 不依赖任何第三方库，全部用 GDI+ 绘制。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebVideoRemote
{
    enum UiTheme { Furina = 0, Firefly = 1 }

    // ---------------- 配色 ----------------
    // 芙宁娜：水色青蓝 + 香槟金；流萤：薄荷青绿 + 微光紫。
    // 取值都在绘制时实时读取，切换主题后重绘即可全局生效。
    static class Palette
    {
        public static UiTheme Current = UiTheme.Furina;

        public static Color Accent
        {
            get { return Current == UiTheme.Furina ? Color.FromArgb(118, 214, 255) : Color.FromArgb(128, 255, 206); }
        }
        public static Color Accent2
        {
            get { return Current == UiTheme.Furina ? Color.FromArgb(158, 142, 255) : Color.FromArgb(92, 214, 190); }
        }
        public static Color Gold { get { return Color.FromArgb(244, 213, 141); } }
        public static Color BgTop { get { return Color.FromArgb(11, 17, 35); } }
        public static Color BgBottom { get { return Color.FromArgb(7, 12, 24); } }
        public static Color Card { get { return Current == UiTheme.Furina ? Color.FromArgb(18, 27, 51) : Color.FromArgb(15, 29, 42); } }
        public static Color CardRaise { get { return Current == UiTheme.Furina ? Color.FromArgb(25, 37, 66) : Color.FromArgb(21, 40, 55); } }
        public static Color CardBorder { get { return Color.FromArgb(52, 74, 116); } }
        public static Color TextMain { get { return Color.FromArgb(235, 243, 255); } }
        public static Color TextSub { get { return Color.FromArgb(158, 176, 206); } }
        public static Color TextDim { get { return Color.FromArgb(112, 130, 162); } }
        public static Color Ok { get { return Color.FromArgb(112, 228, 165); } }
        public static Color Warn { get { return Color.FromArgb(255, 194, 112); } }
        public static Color Error { get { return Color.FromArgb(255, 124, 142); } }
        public static Color Ink { get { return Color.FromArgb(7, 16, 30); } }
    }

    // ---------------- 绘制工具 ----------------
    static class Draw
    {
        public static Font Px(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void FillRoundGradient(Graphics g, Rectangle r, int radius, Color a, Color b, float angle)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, radius))
            using (var br = new LinearGradientBrush(r, a, b, angle))
                g.FillPath(br, p);
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            using (var p = Round(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        // 柔和外发光：中心亮、边缘透明的椭圆
        public static void Glow(Graphics g, Rectangle r, Color c, int alpha)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(r);
                using (var br = new PathGradientBrush(p))
                {
                    br.CenterColor = Color.FromArgb(alpha, c);
                    br.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillPath(br, p);
                }
            }
        }

        // 四角星 / 菱形装饰
        public static void Sparkle(Graphics g, float x, float y, float r, Color c)
        {
            using (var pen = new Pen(c, 1f))
            {
                g.DrawLine(pen, x - r, y, x + r, y);
                g.DrawLine(pen, x, y - r, x, y + r);
            }
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, x - 1.2f, y - 1.2f, 2.4f, 2.4f);
        }

        public static void Diamond(Graphics g, PointF c, float r, Color fill, Color edge)
        {
            var pts = new[]
            {
                new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y),
                new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y)
            };
            using (var b = new SolidBrush(fill)) g.FillPolygon(b, pts);
            if (edge.A > 0)
                using (var pen = new Pen(edge, 1f)) g.DrawPolygon(pen, pts);
        }
    }

    // ---------------- 素材 ----------------
    // 立绘/主视觉放在安装目录的 assets 子目录里（安装时由安装程序释放），
    // 不再打进 exe：exe 只保留运行必需的代码与图标。缺失时界面自动降级为纯色背景。
    static class Assets
    {
        static Image _heroF, _heroL, _charF, _charL;
        static bool _heroFTried, _heroLTried, _charFTried, _charLTried;
        static Icon _icon;

        public static string ArtDir { get { return Path.Combine(Log.AppDir, "assets"); } }

        public static Image HeroFurina { get { if (!_heroFTried) { _heroFTried = true; _heroF = Load("hero_furina_band.png"); } return _heroF; } }
        public static Image HeroFirefly { get { if (!_heroLTried) { _heroLTried = true; _heroL = Load("hero_firefly_band.png"); } return _heroL; } }
        public static Image CharFurina { get { if (!_charFTried) { _charFTried = true; _charF = Load("char_furina_card.png"); } return _charF; } }
        public static Image CharFirefly { get { if (!_charLTried) { _charLTried = true; _charL = Load("char_firefly_card.png"); } return _charL; } }

        public static Icon AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    try
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        foreach (var n in asm.GetManifestResourceNames())
                            if (n.EndsWith(".app.ico", StringComparison.OrdinalIgnoreCase))
                                using (var s = asm.GetManifestResourceStream(n))
                                {
                                    _icon = new Icon(s);
                                    break;
                                }
                    }
                    catch { }
                    if (_icon == null) _icon = SystemIcons.Application;
                }
                return _icon;
            }
        }

        static Image Load(string file)
        {
            try
            {
                string path = Path.Combine(ArtDir, file);
                if (File.Exists(path))
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                    using (var tmp = Image.FromStream(ms))
                        return new Bitmap(tmp);
                }

                // 安装程序把同一份素材内嵌在自己身上（安装时释放到 assets 目录），
                // 这样安装向导也能显示主视觉；主程序里没有内嵌资源，这里会直接跳过。
                var asm = Assembly.GetExecutingAssembly();
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase))
                        using (var s = asm.GetManifestResourceStream(n))
                        using (var tmp = Image.FromStream(s))
                            return new Bitmap(tmp);
                return null;
            }
            catch (Exception e)
            {
                Log.Write("读取素材失败 " + file + ": " + e.Message);
                return null;
            }
        }
    }

    // ---------------- 背景合成 ----------------
    static class UiBackground
    {
        public static Bitmap Build(int w, int h, UiTheme theme)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Palette.BgTop, Palette.BgBottom, 90f))
                    g.FillRectangle(bg, 0, 0, w, h);

                Draw.Glow(g, new Rectangle(w - 300, 10, 460, 460), Palette.Accent, 34);
                Draw.Glow(g, new Rectangle(-160, h - 380, 420, 420), Palette.Accent2, 30);

                int heroY = 40, heroH = 148;
                var art = theme == UiTheme.Furina ? Assets.HeroFurina : Assets.HeroFirefly;
                if (art != null)
                    g.DrawImage(art, new Rectangle(0, heroY, w, heroH));
                else
                    using (var lg = new LinearGradientBrush(new Rectangle(0, heroY, w, heroH),
                        Color.FromArgb(40, Palette.Accent), Color.FromArgb(8, Palette.Accent2), 0f))
                        g.FillRectangle(lg, 0, heroY, w, heroH);

                // 左侧压暗，保证标题可读
                using (var lg = new LinearGradientBrush(new Rectangle(0, heroY, 430, heroH),
                    Color.FromArgb(220, 8, 13, 28), Color.FromArgb(0, 8, 13, 28), 0f))
                    g.FillRectangle(lg, 0, heroY, 430, heroH);

                // 主视觉底部融入页面背景
                using (var lg = new LinearGradientBrush(new Rectangle(0, heroY + 76, w, 112),
                    Color.FromArgb(0, 8, 13, 28), Color.FromArgb(255, 8, 13, 28), 90f))
                    g.FillRectangle(lg, 0, heroY + 76, w, 112);

                // 顶部与标题栏融合
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, 64),
                    Color.FromArgb(130, 6, 10, 22), Color.FromArgb(0, 6, 10, 22), 90f))
                    g.FillRectangle(lg, 0, 0, w, 64);

                // 主视觉与页签之间的细分隔线：青线 + 金线
                using (var pen = new Pen(Color.FromArgb(80, Palette.Accent), 1f))
                    g.DrawLine(pen, 18, heroY + heroH, w - 18, heroY + heroH);
                using (var pen = new Pen(Color.FromArgb(36, Palette.Gold), 1f))
                    g.DrawLine(pen, 18, heroY + heroH + 1, w - 18, heroY + heroH + 1);

                // 点缀星芒（固定位置，每次重绘一致）
                Draw.Sparkle(g, w - 96, 60, 4f, Color.FromArgb(150, Palette.Gold));
                Draw.Sparkle(g, w - 132, 96, 2.5f, Color.FromArgb(120, Palette.Accent));
                Draw.Sparkle(g, 464, 208, 3f, Color.FromArgb(110, Palette.Gold));
            }
            return bmp;
        }
    }

    // ---------------- 自绘小控件 ----------------
    class ChromeButton : Control
    {
        public bool IsClose;
        bool _hover;

        public ChromeButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(34, 26);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover)
                Draw.FillRound(g, new Rectangle(0, 0, Width, Height), 7,
                    IsClose ? Color.FromArgb(90, 255, 90, 110) : Color.FromArgb(30, 255, 255, 255));

            using (var pen = new Pen(_hover && IsClose ? Color.White : Palette.TextSub, 1.5f))
            {
                if (IsClose)
                {
                    int cx = Width / 2, cy = Height / 2, r = 4;
                    g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                    g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                }
                else
                {
                    int cx = Width / 2, cy = Height / 2;
                    g.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
                }
            }
        }
    }

    class NeoCard : Panel
    {
        public string Title = "";
        public int Radius = 14;
        public bool DrawBorder = true;
        Font _titleFont;

        public NeoCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Card;
            _titleFont = Draw.Px(10f, FontStyle.Bold);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (var p = Draw.Round(new Rectangle(0, 0, Width, Height), Radius))
                    Region = new Region(p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Draw.Round(r, Radius))
            using (var b = new SolidBrush(Palette.Card))
                g.FillPath(b, p);
            if (DrawBorder)
                Draw.StrokeRound(g, r, Radius, Color.FromArgb(70, Palette.Accent), 1f);

            if (!string.IsNullOrEmpty(Title))
            {
                Draw.Diamond(g, new PointF(17, 18), 4.5f, Palette.Accent, Color.FromArgb(0, Palette.Accent));
                TextRenderer.DrawText(g, Title, _titleFont, new Rectangle(27, 5, Width - 40, 28), Palette.TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    enum NeoButtonStyle { Primary, Secondary, Ghost, Danger }

    // 注意：这里不继承 Button / CheckBox。
    // ButtonBase 会用自己的逻辑重绘背景，配合自绘容器会出现「上一行文字残影」。
    // 继承 Control 后绘制完全由自己掌控，点击语义由 Control 自带。
    class NeoButton : Control
    {
        public NeoButtonStyle Style = NeoButtonStyle.Secondary;
        bool _hover, _down;

        public NeoButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
            Font = Draw.Px(13f, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 自绘实色底，避免残留上一帧内容
            if (BackColor.A == 255)
                using (var b = new SolidBrush(BackColor))
                    g.FillRectangle(b, ClientRectangle);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);

            if (Style == NeoButtonStyle.Primary)
            {
                Draw.Glow(g, new Rectangle(-8, -6, Width + 16, Height + 16), Palette.Accent, _hover ? 90 : 55);
                Color a = Palette.Accent, b = Palette.Accent2;
                if (_down) { a = Darken(a, 0.82f); b = Darken(b, 0.82f); }
                else if (_hover) { a = Lighten(a, 0.16f); b = Lighten(b, 0.10f); }
                Draw.FillRoundGradient(g, r, 11, a, b, 0f);
                Draw.StrokeRound(g, r, 11, Color.FromArgb(_hover ? 180 : 120, 255, 255, 255), 1f);
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Palette.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else if (Style == NeoButtonStyle.Danger)
            {
                if (_hover) Draw.FillRound(g, r, 11, Color.FromArgb(36, 255, 110, 130));
                Draw.StrokeRound(g, r, 11, Color.FromArgb(_hover ? 140 : 80, Palette.Error), 1f);
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Palette.Error,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else if (Style == NeoButtonStyle.Ghost)
            {
                if (_hover) Draw.FillRound(g, r, 10, Color.FromArgb(22, 255, 255, 255));
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, _hover ? Palette.TextMain : Palette.TextSub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                Color fill = _hover ? Palette.CardRaise : Color.FromArgb(18, 255, 255, 255);
                if (BackColor.A == 255 && !_hover) fill = Blend(BackColor, Color.FromArgb(18, 255, 255, 255));
                Draw.FillRound(g, r, 11, fill);
                Draw.StrokeRound(g, r, 11, Color.FromArgb(_hover ? 130 : 70, Palette.Accent), 1f);
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Palette.TextMain,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        static Color Blend(Color bg, Color fg)
        {
            float a = fg.A / 255f;
            return Color.FromArgb(bg.A,
                (int)(bg.R * (1 - a) + fg.R * a),
                (int)(bg.G * (1 - a) + fg.G * a),
                (int)(bg.B * (1 - a) + fg.B * a));
        }

        static Color Lighten(Color c, float k)
        {
            return Color.FromArgb(c.A,
                (int)Math.Min(255, c.R + (255 - c.R) * k),
                (int)Math.Min(255, c.G + (255 - c.G) * k),
                (int)Math.Min(255, c.B + (255 - c.B) * k));
        }

        static Color Darken(Color c, float k)
        {
            return Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }
    }

    class NeoSwitch : Control
    {
        bool _checked, _hover, _down;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public NeoSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = 38;
            Font = Draw.Px(12f, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            _down = false;
            if (fire) Checked = !Checked;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (BackColor.A == 255)
                using (var b = new SolidBrush(BackColor))
                    g.FillRectangle(b, ClientRectangle);

            if (_hover)
                Draw.FillRound(g, new Rectangle(2, 2, Width - 4, Height - 4), 8, Color.FromArgb(16, 255, 255, 255));

            int trackW = 42, trackH = 22;
            int tx = Width - trackW - 12, ty = (Height - trackH) / 2;
            var track = new Rectangle(tx, ty, trackW, trackH);

            if (_checked)
            {
                Draw.Glow(g, new Rectangle(tx - 10, ty - 9, trackW + 20, trackH + 18), Palette.Accent, 70);
                Draw.FillRoundGradient(g, track, trackH / 2, Palette.Accent, Palette.Accent2, 0f);
                Draw.StrokeRound(g, track, trackH / 2, Color.FromArgb(160, 255, 255, 255), 1f);
            }
            else
            {
                Draw.FillRound(g, track, trackH / 2, Color.FromArgb(46, 60, 86));
                Draw.StrokeRound(g, track, trackH / 2, Color.FromArgb(90, 110, 145), 1f);
            }

            int knob = 16;
            int kx = _checked ? track.Right - knob - 3 : track.X + 3;
            using (var b = new SolidBrush(_checked ? Color.White : Color.FromArgb(190, 206, 230)))
                g.FillEllipse(b, kx, ty + (trackH - knob) / 2, knob, knob);

            var textRect = new Rectangle(14, 0, Math.Max(10, Width - trackW - 40), Height);
            TextRenderer.DrawText(g, Text, Font, textRect,
                _checked ? Palette.TextMain : Palette.TextSub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    class NeoSegment : Control
    {
        int _sel, _hover = -1;
        public string[] Items = new string[0];
        public event Action<int> SelectedChanged;

        public int SelectedIndex
        {
            get { return _sel; }
            set { _sel = Math.Max(0, Math.Min(Items.Length - 1, value)); Invalidate(); }
        }

        public NeoSegment()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 32;
            Cursor = Cursors.Hand;
            Font = Draw.Px(11.5f, FontStyle.Regular);
        }

        int HitTest(Point p)
        {
            if (Items.Length == 0) return -1;
            int w = (Width - 8) / Items.Length;
            int i = (p.X - 4) / Math.Max(1, w);
            return i >= 0 && i < Items.Length ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0 && h != _sel)
            {
                _sel = h;
                Invalidate();
                var ev = SelectedChanged;
                if (ev != null) ev(h);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(g, all, Height / 2, Color.FromArgb(20, 255, 255, 255));
            Draw.StrokeRound(g, all, Height / 2, Color.FromArgb(60, 255, 255, 255), 1f);

            if (Items.Length == 0) return;
            int seg = (Width - 8) / Items.Length;
            for (int i = 0; i < Items.Length; i++)
            {
                var r = new Rectangle(4 + i * seg, 4, seg - 2, Height - 8);
                bool on = i == _sel;
                if (on)
                {
                    Draw.FillRoundGradient(g, r, r.Height / 2, Color.FromArgb(60, Palette.Accent), Color.FromArgb(40, Palette.Accent2), 0f);
                    Draw.StrokeRound(g, r, r.Height / 2, Color.FromArgb(150, Palette.Accent), 1f);
                }
                else if (i == _hover)
                {
                    Draw.FillRound(g, r, r.Height / 2, Color.FromArgb(14, 255, 255, 255));
                }
                TextRenderer.DrawText(g, Items[i], Font, r, on ? Palette.TextMain : Palette.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    class NeoTabBar : Control
    {
        int _sel, _hover = -1;
        public string[] Items = new string[0];
        public event Action<int> SelectedChanged;

        public int SelectedIndex
        {
            get { return _sel; }
            set { _sel = Math.Max(0, Math.Min(Items.Length - 1, value)); Invalidate(); }
        }

        public NeoTabBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 46;
            Cursor = Cursors.Hand;
            Font = Draw.Px(12.5f, FontStyle.Regular);
        }

        Rectangle RectOf(Graphics g, int i)
        {
            int x = 22;
            for (int k = 0; k < i; k++)
                x += TextRenderer.MeasureText(g, Items[k], Font, Size.Empty, TextFormatFlags.NoPadding).Width + 34;
            int w = TextRenderer.MeasureText(g, Items[i], Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            return new Rectangle(x - 10, 0, w + 20, Height);
        }

        int HitTest(Point p)
        {
            using (var g = CreateGraphics())
                for (int i = 0; i < Items.Length; i++)
                    if (RectOf(g, i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0 && h != _sel)
            {
                _sel = h;
                Invalidate();
                var ev = SelectedChanged;
                if (ev != null) ev(h);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Items.Length; i++)
            {
                var r = RectOf(g, i);
                bool on = i == _sel;
                TextRenderer.DrawText(g, Items[i], Font, r,
                    on ? Palette.TextMain : (i == _hover ? Palette.TextSub : Palette.TextDim),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                if (on)
                {
                    int uw = Math.Min(34, r.Width - 16);
                    var line = new Rectangle(r.X + (r.Width - uw) / 2, Height - 12, uw, 3);
                    Draw.FillRoundGradient(g, line, 2, Palette.Accent, Palette.Accent2, 0f);
                    Draw.Glow(g, new Rectangle(line.X - 8, line.Y - 6, line.Width + 16, line.Height + 12), Palette.Accent, 70);
                }
                else if (i == _hover)
                {
                    int uw = 16;
                    var line = new Rectangle(r.X + (r.Width - uw) / 2, Height - 11, uw, 2);
                    Draw.FillRound(g, line, 1, Color.FromArgb(70, Palette.Accent));
                }
            }
        }
    }

    class NeoProgress : Control
    {
        double _v;
        public double Value
        {
            get { return _v; }
            set
            {
                double nv = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(nv - _v) < 0.0005) return;
                _v = nv;
                Invalidate();
            }
        }

        public NeoProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 6;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(g, track, Height / 2, Color.FromArgb(34, 52, 80));
            if (_v <= 0.001) return;
            int w = Math.Max(Height, (int)(track.Width * _v));
            var fill = new Rectangle(0, 0, w, track.Height);
            Draw.FillRoundGradient(g, fill, Height / 2, Palette.Accent, Palette.Accent2, 0f);
            Draw.Glow(g, new Rectangle(fill.Right - 8, -6, 20, Height + 12), Palette.Accent, 90);
        }
    }

    class NeoDot : Control
    {
        Color _c = Palette.TextDim;
        public Color DotColor
        {
            get { return _c; }
            set { if (_c != value) { _c = value; Invalidate(); } }
        }

        public NeoDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(10, 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw.Glow(g, new Rectangle(-6, -6, Width + 12, Height + 12), _c, 160);
            using (var b = new SolidBrush(_c))
                g.FillEllipse(b, 1, 1, Width - 2, Height - 2);
        }
    }

    class NeoBadge : Control
    {
        public Color Tint = Palette.Accent;
        public bool ShowDot = true;

        public NeoBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 24;
            Font = Draw.Px(10.5f, FontStyle.Regular);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(g, r, Height / 2, Color.FromArgb(46, 8, 14, 28));
            Draw.StrokeRound(g, r, Height / 2, Color.FromArgb(110, Tint), 1f);
            int x = 10;
            if (ShowDot)
            {
                using (var b = new SolidBrush(Tint)) g.FillEllipse(b, 10, Height / 2 - 2.5f, 5, 5);
                x = 21;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - 8, Height), Tint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    class NeoTextBox : TextBox
    {
        public NeoTextBox()
        {
            BorderStyle = BorderStyle.None;
            BackColor = Color.FromArgb(11, 19, 36);
            ForeColor = Color.FromArgb(224, 236, 255);
            Font = Draw.Px(12f, FontStyle.Regular);
        }
    }

    // 自绘下拉：系统的 ComboBox 在深色界面里会留下白色箭头和边框，这里用菜单模拟
    class NeoSelect : Control
    {
        public string[] Items = new string[0];
        int _sel, _hover;
        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return _sel; }
            set
            {
                int v = Math.Max(0, Math.Min(Items.Length - 1, value));
                if (v == _sel) return;
                _sel = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public NeoSelect()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.FromArgb(20, 31, 56);
            Cursor = Cursors.Hand;
            Height = 30;
            Font = Draw.Px(11.5f, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = 1; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = 0; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            OpenMenu();
            base.OnMouseDown(e);
        }

        void OpenMenu()
        {
            if (Items.Length == 0) return;
            var m = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(16, 26, 48),
                ForeColor = Palette.TextMain,
                Renderer = new NeoMenuRenderer(),
                ShowImageMargin = false,
                Font = Draw.Px(12f, FontStyle.Regular)
            };
            for (int i = 0; i < Items.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Items[i]) { Checked = i == _sel };
                it.Click += (a, b) => { SelectedIndex = idx; };
                m.Items.Add(it);
            }
            m.Closed += (a, b) => m.Dispose();
            m.Show(this, new Point(0, Height + 3));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            Draw.FillRound(g, r, 8, Color.FromArgb(24, 36, 62));
            Draw.StrokeRound(g, r, 8, Color.FromArgb(_hover == 1 ? 150 : 70, Palette.Accent), 1f);

            if (Items.Length > 0)
                TextRenderer.DrawText(g, Items[Math.Max(0, Math.Min(Items.Length - 1, _sel))], Font,
                    new Rectangle(11, 0, Width - 34, Height), Palette.TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            using (var pen = new Pen(Palette.TextSub, 1.2f))
            {
                int cx = Width - 17, cy = Height / 2;
                g.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
            }
        }
    }

    // ---------------- 无边框窗口基类 ----------------
    class WvrForm : Form
    {
        public string Caption = "网页视频遥控器";
        public string ChromeSubtitle = "";
        public bool ShowMinimize = true;
        public bool ShowClose = true;
        protected int ChromeH = 40;

        ChromeButton _min, _close;
        Font _capsFont;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public WvrForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.BgTop;
            Font = Draw.Px(12f, FontStyle.Regular);
            DoubleBuffered = true;
            _capsFont = Draw.Px(9f, FontStyle.Bold);
            MouseDown += DragWindow;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BuildChrome();
        }

        void BuildChrome()
        {
            if (ShowMinimize)
            {
                _min = new ChromeButton { IsClose = false };
                _min.Click += (a, b) => WindowState = FormWindowState.Minimized;
                Controls.Add(_min);
            }
            if (ShowClose)
            {
                _close = new ChromeButton { IsClose = true };
                _close.Click += (a, b) => Close();
                Controls.Add(_close);
                _close.BringToFront();
            }
            if (_min != null) _min.BringToFront();
            LayoutChrome();
        }

        void LayoutChrome()
        {
            int y = (ChromeH - 26) / 2;
            if (_close != null) _close.Location = new Point(Width - 44, y);
            if (_min != null) _min.Location = new Point(Width - 80, y);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (var p = Draw.Round(new Rectangle(0, 0, Width, Height), 16))
                    Region = new Region(p);
            LayoutChrome();
        }

        void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y > ChromeH) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 顶部标题条：压暗 + 左侧品牌标记
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Width, ChromeH),
                Color.FromArgb(200, 7, 12, 25), Color.FromArgb(70, 7, 12, 25), 90f))
                g.FillRectangle(lg, 0, 0, Width, ChromeH);

            Draw.Diamond(g, new PointF(26, ChromeH / 2f), 8f, Color.FromArgb(26, Palette.Accent), Color.FromArgb(120, Palette.Accent));
            using (var b = new SolidBrush(Palette.Accent))
                g.FillPolygon(b, new[]
                {
                    new PointF(24, ChromeH / 2f - 3.2f),
                    new PointF(24, ChromeH / 2f + 3.2f),
                    new PointF(29.5f, ChromeH / 2f)
                });

            float x = 42;
            string caps = "WEB VIDEO REMOTE";
            foreach (char ch in caps)
            {
                string s = ch.ToString();
                TextRenderer.DrawText(g, s, _capsFont, new Point((int)x, ChromeH / 2 - 6), Palette.TextDim,
                    TextFormatFlags.NoPadding);
                x += TextRenderer.MeasureText(g, s, _capsFont, Size.Empty, TextFormatFlags.NoPadding).Width + 1.6f;
            }

            if (!string.IsNullOrEmpty(ChromeSubtitle))
            {
                using (var f = Draw.Px(11f, FontStyle.Regular))
                    TextRenderer.DrawText(g, "· " + ChromeSubtitle, f,
                        new Rectangle((int)x + 12, 0, Width - (int)x - 100, ChromeH), Palette.TextSub,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            using (var pen = new Pen(Color.FromArgb(34, Palette.Accent), 1f))
                g.DrawLine(pen, 18, ChromeH - 1, Width - 18, ChromeH - 1);
        }
    }

    // ---------------- 首次启动封面 ----------------
    class SplashForm : Form
    {
        readonly Timer _timer;
        int _t;

        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 420);
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = 0;
            _timer = new Timer { Interval = 16 };
            _timer.Tick += (a, b) => Tick();
            Click += (a, b) => Close();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (var p = Draw.Round(new Rectangle(0, 0, Width, Height), 20))
                    Region = new Region(p);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
        }

        void Tick()
        {
            _t += 16;
            if (_t < 260) Opacity = _t / 260.0;
            else if (_t < 1500) Opacity = 1;
            else if (_t < 1850) Opacity = Math.Max(0, (1850 - _t) / 350.0);
            else { _timer.Stop(); Close(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, Height),
                Color.FromArgb(12, 19, 38), Color.FromArgb(6, 11, 23), 90f))
                g.FillRectangle(bg, 0, 0, Width, Height);

            Draw.Glow(g, new Rectangle(280, -60, 260, 260), Palette.Accent, 40);
            Draw.Glow(g, new Rectangle(240, 240, 300, 260), Palette.Accent2, 30);

            if (Assets.CharFurina != null) g.DrawImage(Assets.CharFurina, new Rectangle(8, 0, 260, 420));
            if (Assets.CharFirefly != null) g.DrawImage(Assets.CharFirefly, new Rectangle(Width - 268, 0, 260, 420));

            // 两侧压暗，让中间文字更聚焦
            using (var lg = new LinearGradientBrush(new Rectangle(240, 0, 100, Height),
                Color.FromArgb(0, 8, 13, 28), Color.FromArgb(200, 8, 13, 28), 0f))
                g.FillRectangle(lg, 240, 0, 100, Height);
            using (var lg = new LinearGradientBrush(new Rectangle(Width - 340, 0, 100, Height),
                Color.FromArgb(200, 8, 13, 28), Color.FromArgb(0, 8, 13, 28), 0f))
                g.FillRectangle(lg, Width - 340, 0, 100, Height);

            int cx = Width / 2;
            Draw.Diamond(g, new PointF(cx, 108), 10f, Color.FromArgb(30, Palette.Accent), Color.FromArgb(160, Palette.Accent));
            using (var b = new SolidBrush(Palette.Accent))
                g.FillPolygon(b, new[]
                {
                    new PointF(cx - 3, 103), new PointF(cx - 3, 113), new PointF(cx + 5, 108)
                });

            using (var f = Draw.Px(22f, FontStyle.Bold))
                TextRenderer.DrawText(g, "网页视频遥控器", f, new Rectangle(cx - 150, 132, 300, 34), Palette.TextMain,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            using (var f = Draw.Px(10.5f, FontStyle.Regular))
                TextRenderer.DrawText(g, "在游戏里后台遥控 B站 / 抖音", f, new Rectangle(cx - 150, 172, 300, 20), Palette.TextSub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            using (var pen = new Pen(Color.FromArgb(90, Palette.Gold), 1f))
            {
                g.DrawLine(pen, cx - 56, 200, cx - 20, 200);
                g.DrawLine(pen, cx + 20, 200, cx + 56, 200);
            }
            Draw.Diamond(g, new PointF(cx, 200), 3f, Palette.Gold, Color.FromArgb(0, Palette.Gold));

            // 载入进度条
            int barW = 200, barX = cx - barW / 2, barY = 300;
            Draw.FillRound(g, new Rectangle(barX, barY, barW, 3), 1, Color.FromArgb(40, 60, 90));
            int done = (int)(barW * Math.Min(1.0, _t / 1400.0));
            if (done > 0)
                Draw.FillRoundGradient(g, new Rectangle(barX, barY, done, 3), 1, Palette.Accent, Palette.Accent2, 0f);

            using (var f = Draw.Px(9f, FontStyle.Regular))
                TextRenderer.DrawText(g, "正在启动…  v2.0", f, new Rectangle(cx - 100, 318, 200, 18), Palette.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            Draw.Sparkle(g, 300, 90, 3, Color.FromArgb(150, Palette.Gold));
            Draw.Sparkle(g, 462, 250, 2.5f, Color.FromArgb(130, Palette.Accent));
            Draw.Sparkle(g, 330, 350, 2f, Color.FromArgb(100, Palette.Accent2));
        }
    }

    // 托盘菜单的深色渲染
    class NeoMenuRenderer : ToolStripProfessionalRenderer
    {
        public NeoMenuRenderer() : base(new NeoMenuColors()) { RoundedEdges = false; }
    }

    class NeoMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(16, 26, 48); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(16, 26, 48); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(16, 26, 48); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(16, 26, 48); } }
        public override Color MenuBorder { get { return Color.FromArgb(44, 66, 112); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(70, Palette.Accent); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(28, 44, 74); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(28, 44, 74); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(28, 44, 74); } }
        public override Color SeparatorDark { get { return Color.FromArgb(38, 56, 90); } }
        public override Color SeparatorLight { get { return Color.FromArgb(38, 56, 90); } }
        public override Color CheckBackground { get { return Color.FromArgb(40, Palette.Accent); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(70, Palette.Accent); } }
        public override Color CheckPressedBackground { get { return Color.FromArgb(70, Palette.Accent); } }
    }
}
