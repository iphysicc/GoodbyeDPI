using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace GoodbyeDPI.Panel
{
    /// <summary>
    /// Notification area icon: closing the window keeps the panel here.
    /// Left click opens the panel, right click shows open / start-stop / exit.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        readonly WinForms.NotifyIcon icon;
        readonly WinForms.ToolStripMenuItem toggleItem;

        public event Action OpenRequested;
        public event Action ToggleRequested;
        public event Action ExitRequested;

        public TrayIcon()
        {
            var menu = new WinForms.ContextMenuStrip
            {
                Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
                ShowImageMargin = false,
                BackColor = DarkColors.Surface,
                ForeColor = DarkColors.Text,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new WinForms.Padding(2, 4, 2, 4),
            };
            var open = new WinForms.ToolStripMenuItem("Paneli aç") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            open.Click += (s, e) => OpenRequested?.Invoke();
            toggleItem = new WinForms.ToolStripMenuItem("Başlat");
            toggleItem.Click += (s, e) => ToggleRequested?.Invoke();
            var exit = new WinForms.ToolStripMenuItem("Çıkış");
            exit.Click += (s, e) => ExitRequested?.Invoke();
            foreach (var item in new WinForms.ToolStripItem[] { open, toggleItem, new WinForms.ToolStripSeparator(), exit })
            {
                item.ForeColor = DarkColors.Text;
                item.Padding = new WinForms.Padding(4, 3, 16, 3);
                menu.Items.Add(item);
            }

            icon = new WinForms.NotifyIcon
            {
                Icon = offIcon,
                Text = "GoodbyeDPI",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
            };
        }

        bool running;
        string problem;

        /// <summary>Colored icon while protection is on, gray while it is off.</summary>
        public void SetState(bool on)
        {
            if (on != running || icon.Icon == null)
                icon.Icon = on ? onIcon : offIcon;
            running = on;
            toggleItem.Text = on ? "Durdur" : "Başlat";
            UpdateText();
        }

        /// <summary>A short problem shown in the tooltip; null clears it.</summary>
        public void SetProblem(string text)
        {
            problem = text;
            UpdateText();
        }

        void UpdateText()
        {
            string t = running ? "GoodbyeDPI: koruma açık" : "GoodbyeDPI: koruma kapalı";
            if (running && problem != null) t += ", " + problem;
            icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;   // NotifyIcon limit
        }

        public void SetBusy(bool busy) => toggleItem.Enabled = !busy;

        // ---------- icons ----------

        readonly Icon onIcon = MakeIcon(gray: false);
        readonly Icon offIcon = MakeIcon(gray: true);

        /// <summary>
        /// The app icon only has PNG frames, which System.Drawing on .NET Framework can't
        /// convert; decode the largest PNG frame ourselves and scale it to the tray size.
        /// </summary>
        static Icon MakeIcon(bool gray)
        {
            var size = WinForms.SystemInformation.SmallIconSize;
            using (var src = LargestFrame())
            using (var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    var rect = new Rectangle(0, 0, size.Width, size.Height);
                    if (!gray)
                    {
                        g.DrawImage(src, rect);
                    }
                    else
                    {
                        // gray and slightly faded: "off" at a glance
                        var m = new System.Drawing.Imaging.ColorMatrix(new[]
                        {
                            new[] { 0.3f, 0.3f, 0.3f, 0, 0 },
                            new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                            new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                            new[] { 0f, 0, 0, 0.75f, 0 },
                            new[] { 0f, 0, 0, 0, 1 },
                        });
                        using (var attr = new System.Drawing.Imaging.ImageAttributes())
                        {
                            attr.SetColorMatrix(m);
                            g.DrawImage(src, rect, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
                        }
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static Image LargestFrame()
        {
            byte[] ico;
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"));
            using (var s = res.Stream)
            using (var m = new System.IO.MemoryStream())
            {
                s.CopyTo(m);
                ico = m.ToArray();
            }
            int count = BitConverter.ToUInt16(ico, 4), best = -1, bestW = 0;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + 16 * i;
                int w = ico[e] == 0 ? 256 : ico[e];
                if (w > bestW) { bestW = w; best = e; }
            }
            int len = BitConverter.ToInt32(ico, best + 8), off = BitConverter.ToInt32(ico, best + 12);
            var frame = new System.IO.MemoryStream(ico, off, len);
            if (ico[off] == 0x89)   // PNG frame
                return Image.FromStream(frame);
            // classic BMP frame: let Icon decode it
            using (var whole = new System.IO.MemoryStream(ico))
            using (var icon = new Icon(whole, bestW, bestW))
                return icon.ToBitmap();
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        /// <summary>The panel's warm dark tones for the menu.</summary>
        sealed class DarkColors : WinForms.ProfessionalColorTable
        {
            public static readonly Color Surface = Color.FromArgb(0x1D, 0x1B, 0x14);
            public static readonly Color Hover = Color.FromArgb(0x35, 0x33, 0x2A);
            public static readonly Color Line = Color.FromArgb(0x2A, 0x28, 0x20);
            public static readonly Color Text = Color.FromArgb(0xED, 0xEA, 0xE2);

            public override Color ToolStripDropDownBackground => Surface;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
        }
    }
}
