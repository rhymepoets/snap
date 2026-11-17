// Snap: Mac-style screenshots for Windows.
// Tap Ctrl+Shift (press both, let go, nothing else) = drag a box. Ctrl+Shift+4 = same. Ctrl+Shift+3 = whole screen.
// The shot goes to the clipboard and Pictures\Screenshots, and a thumbnail sits bottom-right for 5 s:
// drag it into any app, click it to open, right-click for more. Esc or right-click cancels a capture.
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WTimer = System.Windows.Forms.Timer;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }
}

static class App
{
    public static string Dir;
    public static Control Ui;
    public static Thumb Current;
    public static Overlay Active;
    static NotifyIcon tray;

    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new Mutex(true, "Local\\SnapMacShot", out created))
        {
            if (!created) return;
            try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { Active = null; };

            Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(Dir);
            Ui = new Control(); Ui.CreateControl(); var h = Ui.Handle;

            Hooks.Install();
            tray = new NotifyIcon { Icon = MakeIcon(), Text = "Snap: tap Ctrl+Shift to screenshot", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Capture area   (tap Ctrl+Shift)", null, (s, e) => Start(false));
            menu.Items.Add("Capture screen   (Ctrl+Shift+3)", null, (s, e) => Start(true));
            menu.Items.Add("Open Screenshots folder", null, (s, e) => Shell(Dir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit Snap", null, (s, e) => Application.Exit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Start(false); };
            tray.ShowBalloonTip(3000, "Snap is running", "Tap Ctrl+Shift to screenshot an area.", ToolTipIcon.None);

            Application.Run();
            Hooks.Uninstall();
            tray.Visible = false;
        }
    }

    public static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    public static float ScaleAt(Point p)
    {
        try
        {
            var mon = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, 2);
            uint x, y;
            if (Native.GetDpiForMonitor(mon, 0, out x, out y) == 0) return x / 96f;
        }
        catch { }
        return 1f;
    }

    static bool pending;

    public static void Start(bool full)
    {
        if (Active != null || pending) return;
        if (Current != null)
        {
            Current.CloseNow();
            pending = true;
            var t = new WTimer { Interval = 90 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); pending = false; Begin(full); };
            t.Start();
        }
        else Begin(full);
    }

    static void Begin(bool full)
    {
        if (Active != null) return;
        Rectangle vs = SystemInformation.VirtualScreen;
        var shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
        if (full)
        {
            var b = Screen.FromPoint(Cursor.Position).Bounds;
            var r = Rectangle.Intersect(new Rectangle(b.X - vs.X, b.Y - vs.Y, b.Width, b.Height), new Rectangle(Point.Empty, shot.Size));
            var crop = shot.Clone(r, PixelFormat.Format24bppRgb);
            shot.Dispose();
            Finish(crop);
            return;
        }
        Active = new Overlay(shot, vs);
        Active.Show();
        Active.Activate();
        Native.SetForegroundWindow(Active.Handle);
    }

    public static void Finish(Bitmap bmp)
    {
        string path = NewPath();
        try { bmp.Save(path, ImageFormat.Png); } catch { }
        Copy(bmp, path);
        Current = new Thumb(bmp, path);
        Current.ShowAnimated();
    }

    public static void Copy(Bitmap bmp, string path)
    {
        var data = new DataObject();
        data.SetImage(bmp);
        var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        if (File.Exists(path)) data.SetFileDropList(new StringCollection { path });
        try { Clipboard.SetDataObject(data, true, 3, 50); } catch { }
    }

    static string NewPath()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd 'at' h.mm.ss tt");
        string p = Path.Combine(Dir, "Screenshot " + stamp + ".png");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(Dir, "Screenshot " + stamp + " (" + i + ").png");
        return p;
    }

    static Icon MakeIcon()
    {
        var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.White, 3))
            {
                g.DrawLines(p, new[] { new Point(3, 11), new Point(3, 3), new Point(11, 3) });
                g.DrawLines(p, new[] { new Point(21, 3), new Point(29, 3), new Point(29, 11) });
                g.DrawLines(p, new[] { new Point(3, 21), new Point(3, 29), new Point(11, 29) });
                g.DrawLines(p, new[] { new Point(21, 29), new Point(29, 29), new Point(29, 21) });
            }
            g.FillEllipse(new SolidBrush(Color.FromArgb(10, 132, 255)), 10, 10, 12, 12);
        }
        return Icon.FromHandle(b.GetHicon());
    }
}

static class Hooks
{
    // The hooks live on their own thread so a busy UI thread (clipboard, PNG encode) can never
    // make Windows time them out and silently unhook them.
    static Native.HookProc kbProc = Kb, msProc = Ms;
    static IntPtr kbHook, msHook;
    static Thread thread;
    static bool lc, rc, ls, rs, armed, tainted, comboHeld;
    static int armTick;
    static bool Ctrl { get { return lc || rc; } }
    static bool Shift { get { return ls || rs; } }

    public static void Install()
    {
        thread = new Thread(() =>
        {
            var mod = Native.GetModuleHandle(null);
            kbHook = Native.SetWindowsHookEx(13, kbProc, mod, 0);
            msHook = Native.SetWindowsHookEx(14, msProc, mod, 0);
            Application.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Uninstall()
    {
        if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
    }

    static void Fire(bool full)
    {
        try { App.Ui.BeginInvoke(new Action(() => App.Start(full))); } catch { }
    }

    static bool OtherModsOrButtons()
    {
        return Native.Down(0x12) || Native.Down(0x5B) || Native.Down(0x5C)   // Alt, Win
            || Native.Down(0x01) || Native.Down(0x02) || Native.Down(0x04)   // mouse buttons
            || Native.Down(0x05) || Native.Down(0x06);
    }

    static IntPtr Kb(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = (Native.KBD)Marshal.PtrToStructure(l, typeof(Native.KBD));
            int msg = (int)w;
            bool down = msg == 0x100 || msg == 0x104;
            bool up = msg == 0x101 || msg == 0x105;
            if (OnKey((int)k.vk, down, up)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(kbHook, code, w, l);
    }

    // Returns true to swallow the key.
    static bool OnKey(int vk, bool down, bool up)
    {
        var active = App.Active;
        if (vk == 0x1B && down && active != null)
        {
            try { App.Ui.BeginInvoke(new Action(() => active.Cancel())); } catch { }
            return true;
        }
        bool isCtrl = vk == 0xA2 || vk == 0xA3 || vk == 0x11;
        bool isShift = vk == 0xA0 || vk == 0xA1 || vk == 0x10;
        if (vk == 0x33 || vk == 0x34)
        {
            if (up && comboHeld) { comboHeld = false; return true; }
            if (down && comboHeld) return true; // key repeat: one shot per press
        }
        if (down)
        {
            if (isCtrl || isShift)
            {
                // Resync the other modifiers: key-ups can be missed (elevated windows, lock screen).
                lc = Native.Down(0xA2); rc = Native.Down(0xA3); ls = Native.Down(0xA0); rs = Native.Down(0xA1);
                if (vk == 0xA2 || vk == 0x11) lc = true;
                if (vk == 0xA3) rc = true;
                if (vk == 0xA0 || vk == 0x10) ls = true;
                if (vk == 0xA1) rs = true;
                if (Ctrl && Shift && !armed && !tainted)
                {
                    if (OtherModsOrButtons()) tainted = true;
                    else { armed = true; armTick = Environment.TickCount; }
                }
                return false;
            }
            if (Ctrl && Shift && (vk == 0x33 || vk == 0x34) && !Native.Down(0x12) && !Native.Down(0x5B) && !Native.Down(0x5C))
            {
                tainted = true;
                comboHeld = true;
                Fire(vk == 0x33);
                return true;
            }
            if (Ctrl || Shift) tainted = true;
            return false;
        }
        if (up && (isCtrl || isShift))
        {
            if (vk == 0xA2 || vk == 0x11) lc = false;
            if (vk == 0xA3) rc = false;
            if (vk == 0xA0 || vk == 0x10) ls = false;
            if (vk == 0xA1) rs = false;
            if (!Ctrl && !Shift)
            {
                if (armed && !tainted && unchecked(Environment.TickCount - armTick) < 1500 && !OtherModsOrButtons()) Fire(false);
                armed = false; tainted = false;
            }
        }
        return false;
    }

    static IntPtr Ms(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            // Ctrl/Shift + any click, release, or scroll is a normal shortcut, not a screenshot tap.
            bool button = msg >= 0x201 && msg <= 0x20E && msg != 0x200;
            if (button && (Ctrl || Shift)) tainted = true;
        }
        return Native.CallNextHookEx(msHook, code, w, l);
    }
}
class Overlay : Form
{
    Bitmap shot, dim;
    Point origin, a, b;
    bool dragging, done;
    Rectangle sel = Rectangle.Empty, lbl = Rectangle.Empty;
    Font font;
    float s;

    public Overlay(Bitmap shot, Rectangle vs)
    {
        this.shot = shot;
        origin = vs.Location;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = vs;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(dim))
        {
            g.DrawImageUnscaled(shot, 0, 0);
            using (var br = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(br, 0, 0, dim.Width, dim.Height);
        }
        s = App.ScaleAt(Cursor.Position);
        font = new Font("Segoe UI", 12 * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0) { m.Result = IntPtr.Zero; return; } // ignore DPI change: we draw in raw pixels
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var c = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(dim, c, c, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        if (sel.Width > 0 && sel.Height > 0)
        {
            var r = Rectangle.Intersect(sel, c);
            if (!r.IsEmpty) g.DrawImage(shot, r, r, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.None;
            using (var p = new Pen(Color.FromArgb(230, 255, 255, 255), 1)) g.DrawRectangle(p, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
        }
        if (!lbl.IsEmpty)
        {
            using (var br = new SolidBrush(Color.FromArgb(200, 20, 20, 22))) g.FillRectangle(br, lbl);
            TextRenderer.DrawText(g, Label(), font, lbl, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    string Label() { return sel.Width + " × " + sel.Height; }

    static Rectangle U(Rectangle x, Rectangle y)
    {
        if (x.IsEmpty) return y;
        if (y.IsEmpty) return x;
        return Rectangle.Union(x, y);
    }

    void Refresh2(Point cursor)
    {
        var old = U(sel, lbl);
        sel = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var sz = TextRenderer.MeasureText(Label(), font);
        int pad = (int)(6 * s);
        lbl = new Rectangle(cursor.X + (int)(14 * s), cursor.Y + (int)(14 * s), sz.Width + pad * 2, sz.Height + pad);
        if (lbl.Right > Width) lbl.X = cursor.X - lbl.Width - (int)(10 * s);
        if (lbl.Bottom > Height) lbl.Y = cursor.Y - lbl.Height - (int)(10 * s);
        if (!old.IsEmpty) { old.Inflate(2, 2); Invalidate(old); }
        var now = U(sel, lbl); now.Inflate(2, 2); Invalidate(now);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        a = b = e.Location;
        Refresh2(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        b = e.Location;
        Refresh2(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false;
        var r = sel;
        if (r.Width < 4 && r.Height < 4)
        {
            // A click with no drag takes the whole screen it was clicked on.
            var sb = Screen.FromPoint(new Point(e.X + origin.X, e.Y + origin.Y)).Bounds;
            r = new Rectangle(sb.X - origin.X, sb.Y - origin.Y, sb.Width, sb.Height);
        }
        r = Rectangle.Intersect(r, new Rectangle(Point.Empty, shot.Size));
        if (r.Width < 1 || r.Height < 1) { Cancel(); return; }
        var crop = shot.Clone(r, PixelFormat.Format24bppRgb);
        done = true;
        App.Active = null;
        Close();
        App.Finish(crop);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Cancel();
        base.OnKeyDown(e);
    }

    public void Cancel()
    {
        if (done) return;
        done = true;
        App.Active = null;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (App.Active == this) App.Active = null;
        shot.Dispose(); dim.Dispose(); font.Dispose();
        base.OnFormClosed(e);
    }
}

class Thumb : Form
{
    Bitmap img, pic;
    string path;
    Rectangle home;
    Size full;
    int remain = 5000, pad;
    bool hover, down, dragging, menuOpen, closing;
    Point downPt;
    WTimer life, anim, follow;
    float s;
    Rectangle work;

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // no-activate, tool window, topmost
            cp.ClassStyle |= 0x20000;              // drop shadow
            return cp;
        }
    }

    public Thumb(Bitmap img, string path)
    {
        this.img = img;
        this.path = path;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        DoubleBuffered = true;

        var at = Cursor.Position;
        s = App.ScaleAt(at);
        work = Screen.FromPoint(at).WorkingArea;
        int maxW = (int)(240 * s), maxH = (int)(170 * s), minSide = (int)(56 * s);
        pad = Math.Max(3, (int)(5 * s));
        double k = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
        int w = Math.Max(1, (int)Math.Round(img.Width * k)), h = Math.Max(1, (int)Math.Round(img.Height * k));
        pic = new Bitmap(w, h);
        using (var g = Graphics.FromImage(pic))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, 0, 0, w, h);
        }
        full = new Size(Math.Max(w, minSide) + pad * 2, Math.Max(h, minSide) + pad * 2);
        Size = full;
        int margin = (int)(22 * s);
        home = new Rectangle(work.Right - full.Width - margin, work.Bottom - full.Height - margin, full.Width, full.Height);
        Location = new Point(work.Right + 2, home.Y);

        life = new WTimer { Interval = 100 };
        life.Tick += (o, e) =>
        {
            if (hover || dragging || menuOpen || closing) return;
            remain -= 100;
            if (remain <= 0) Expire();
        };
        follow = new WTimer { Interval = 12 };
        follow.Tick += (o, e) => FollowCursor();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (o, e) => App.Copy(img, path));
        menu.Items.Add("Open", null, (o, e) => { App.Shell(path); CloseNow(); });
        menu.Items.Add("Show in folder", null, (o, e) =>
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
            CloseNow();
        });
        menu.Items.Add("Delete", null, (o, e) => { CloseNow(); try { File.Delete(path); } catch { } });
        menu.Items.Add("Close", null, (o, e) => CloseNow());
        menu.Opening += (o, e) => { if (closing || dragging) e.Cancel = true; else menuOpen = true; };
        menu.Closed += (o, e) => { menuOpen = false; remain = Math.Max(remain, 3000); };
        ContextMenuStrip = menu;
    }

    public void ShowAnimated()
    {
        Show();
        Animate(Location, home.Location, 220, null);
        life.Start();
    }

    void Animate(Point from, Point to, int ms, Action then)
    {
        if (anim != null) { anim.Stop(); anim.Dispose(); }
        var t = new WTimer { Interval = 10 };
        anim = t;
        int start = Environment.TickCount;
        t.Tick += (o, e) =>
        {
            double p = Math.Min(1.0, (Environment.TickCount - start) / (double)ms);
            double q = 1 - Math.Pow(1 - p, 3);
            Location = new Point(from.X + (int)((to.X - from.X) * q), from.Y + (int)((to.Y - from.Y) * q));
            if (p >= 1)
            {
                t.Stop();
                if (then != null) then();
            }
        };
        t.Start();
    }

    void Expire()
    {
        closing = true;
        Animate(Location, new Point(work.Right + 2, Location.Y), 200, CloseNow);
    }

    public void CloseNow()
    {
        closing = true;
        life.Stop(); follow.Stop();
        if (anim != null) anim.Stop();
        if (App.Current == this) App.Current = null;
        if (!IsDisposed) Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        double k = ClientSize.Width / (double)full.Width;
        int p = (int)Math.Round(pad * k);
        var inner = new Rectangle(p, p, ClientSize.Width - p * 2, ClientSize.Height - p * 2);
        // fit pic inside inner, centred
        double f = Math.Min(inner.Width / (double)pic.Width, inner.Height / (double)pic.Height);
        int w = (int)(pic.Width * f), h = (int)(pic.Height * f);
        g.DrawImage(pic, inner.X + (inner.Width - w) / 2, inner.Y + (inner.Height - h) / 2, w, h);
        using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; remain = Math.Max(remain, 3000); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { down = true; downPt = e.Location; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (down && !dragging)
        {
            var d = SystemInformation.DragSize;
            if (Math.Abs(e.X - downPt.X) > d.Width || Math.Abs(e.Y - downPt.Y) > d.Height) BeginDrag();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && down && !dragging)
        {
            down = false;
            App.Shell(path);
            CloseNow();
            return;
        }
        down = false;
        base.OnMouseUp(e);
    }

    void ClickThrough(bool on)
    {
        int ex = Native.GetWindowLong(Handle, -20);
        ex = on ? (ex | 0x20 | 0x80000) : (ex & ~0x20);
        Native.SetWindowLong(Handle, -20, ex);
    }

    void FollowCursor()
    {
        var c = Cursor.Position;
        Location = new Point(c.X + (int)(12 * s), c.Y + (int)(12 * s));
    }

    void BeginDrag()
    {
        dragging = true;
        down = false;
        if (anim != null) anim.Stop();
        var data = new DataObject();
        if (File.Exists(path)) data.SetFileDropList(new StringCollection { path });
        data.SetImage(img);

        Size = new Size(full.Width * 55 / 100, full.Height * 55 / 100);
        Opacity = 0.9;
        ClickThrough(true);
        FollowCursor();
        follow.Start();
        DragDropEffects eff = DragDropEffects.None;
        try { eff = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move); } catch { }
        follow.Stop();
        if (IsDisposed) return;
        ClickThrough(false);
        Opacity = 1;
        if (eff != DragDropEffects.None) { CloseNow(); return; }
        Size = full;
        dragging = false;
        closing = false;
        remain = 3000;
        Animate(Location, home.Location, 220, null);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (App.Current == this) App.Current = null;
        var m = ContextMenuStrip;
        ContextMenuStrip = null;
        if (m != null) { m.Close(); m.Dispose(); }
        life.Dispose(); follow.Dispose();
        if (anim != null) anim.Dispose();
        pic.Dispose(); img.Dispose();
        base.OnFormClosed(e);
    }
}
