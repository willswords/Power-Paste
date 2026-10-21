using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

// PowerPaste - a stand-alone imitation of ClipMate's PowerPaste feature.
// Captures text clips from the clipboard into a list (newest at the top). When PowerPaste is
// active, the current clip (or fragment) is placed on the clipboard; each time Ctrl+V or
// Shift+Insert is pressed in another application, PowerPaste advances to the next item.

public class Clip
{
    public string Text;
    public DateTime Captured;
}

public class Settings
{
    public bool DirectionUp = true;
    public bool Explode = false;
    public bool Loop = false;
    public bool AlwaysOnTop = false;
    public bool SaveClips = true;
    public int MaxClips = 500;

    // How "Explode Into Fragments" splits a clip
    public bool SplitLines = true;
    public bool SplitCommas = true;
    public bool SplitTabs = true;
    public bool SplitSemicolons = false;
    public bool SplitSpaces = false;
    public string SplitCustom = "";
    public bool RespectQuotes = true;
    public bool TrimFragments = true;
    public bool SkipEmpty = true;

    public Settings Clone() { return (Settings)MemberwiseClone(); }
}

static class Native
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WH_KEYBOARD_LL = 13;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

    public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
}

static class Exploder
{
    // Splits text into fragments per the explode settings. Double-quoted sections (CSV style)
    // are kept together when RespectQuotes is on, and "" inside quotes becomes a literal quote.
    public static List<string> Explode(string s, Settings o)
    {
        var delims = new HashSet<char>();
        if (o.SplitLines) { delims.Add('\r'); delims.Add('\n'); }
        if (o.SplitCommas) delims.Add(',');
        if (o.SplitTabs) delims.Add('\t');
        if (o.SplitSemicolons) delims.Add(';');
        if (o.SplitSpaces) delims.Add(' ');
        foreach (char c in o.SplitCustom ?? "") delims.Add(c);

        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (o.RespectQuotes && c == '"')
            {
                if (inQuotes && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                continue;
            }
            // Treat CRLF as a single line break
            if (c == '\r' && o.SplitLines && i + 1 < s.Length && s[i + 1] == '\n') continue;
            if (!inQuotes && delims.Contains(c)) { Add(result, sb, o); continue; }
            sb.Append(c);
        }
        Add(result, sb, o);
        return result;
    }

    static void Add(List<string> result, StringBuilder sb, Settings o)
    {
        string t = o.TrimFragments ? sb.ToString().Trim() : sb.ToString();
        sb.Clear();
        if (o.SkipEmpty && t.Length == 0) return;
        result.Add(t);
    }
}

static class Icons
{
    // state: 0 = off (grey gear, no arrow), 1 = up, 2 = down
    public static Bitmap Draw(int size, int state)
    {
        var bmp = new Bitmap(size, size);
        float s = size / 32f;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color gearColor = state == 0 ? Color.FromArgb(130, 130, 130) : Color.FromArgb(40, 90, 165);
            float cx = 12 * s, cy = 17 * s, R = 11 * s, r = 8 * s, hole = 3.5f * s;
            int teeth = 8, n = teeth * 4;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double a = i * Math.PI * 2 / n;
                float rad = (i % 4 < 2) ? R : r;
                pts[i] = new PointF(cx + (float)(rad * Math.Cos(a)), cy + (float)(rad * Math.Sin(a)));
            }
            using (var path = new GraphicsPath(FillMode.Alternate))
            using (var brush = new SolidBrush(gearColor))
            {
                path.AddPolygon(pts);
                path.AddEllipse(cx - hole, cy - hole, hole * 2, hole * 2);
                g.FillPath(brush, path);
            }
            if (state != 0)
            {
                PointF[] head; RectangleF stem;
                if (state == 1)
                {
                    head = new[] { new PointF(20 * s, 13 * s), new PointF(31.5f * s, 13 * s), new PointF(25.75f * s, 2 * s) };
                    stem = new RectangleF(23.5f * s, 12 * s, 4.5f * s, 18 * s);
                }
                else
                {
                    head = new[] { new PointF(20 * s, 19 * s), new PointF(31.5f * s, 19 * s), new PointF(25.75f * s, 30 * s) };
                    stem = new RectangleF(23.5f * s, 2 * s, 4.5f * s, 18 * s);
                }
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(Color.FromArgb(0, 165, 70)))
                using (var outline = new Pen(Color.White, 1.5f * s))
                {
                    path.AddPolygon(head);
                    path.AddRectangle(stem);
                    path.FillMode = FillMode.Winding;
                    g.DrawPath(outline, path);
                    g.FillPath(brush, path);
                }
            }
        }
        return bmp;
    }
}

static class Store
{
    public static T Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var rs = new XmlReaderSettings { CheckCharacters = false };
            using (var r = XmlReader.Create(path, rs))
                return (T)new XmlSerializer(typeof(T)).Deserialize(r);
        }
        catch { return null; }
    }

    public static void Save<T>(string path, T obj)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            // Entitize keeps \r intact; CheckCharacters=false allows control chars in clips
            var ws = new XmlWriterSettings { CheckCharacters = false, NewLineHandling = NewLineHandling.Entitize, Indent = true };
            using (var w = XmlWriter.Create(tmp, ws))
                new XmlSerializer(typeof(T)).Serialize(w, obj);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { }
    }
}

class MainForm : Form
{
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerPaste");
    string SettingsPath { get { return Path.Combine(dataDir, "settings.xml"); } }
    string ClipsPath { get { return Path.Combine(dataDir, "clips.xml"); } }

    Settings settings = new Settings();
    List<Clip> clips = new List<Clip>();

    ListView list;
    ToolStripButton ppButton, explodeButton, loopButton;
    ToolStripStatusLabel status;
    ToolStripMenuItem miUp, miDown, miStop, miExplode, miLoop, miTop;
    NotifyIcon tray;
    ToolStripMenuItem tiUp, tiDown, tiStop, tiExplode, tiLoop;
    Image imgOff, imgUp, imgDown;
    Icon icoOff, icoUp, icoDown;
    Font boldFont;

    // PowerPaste state
    bool active, dirUp, pastedSinceOn, flippedSinceOn;
    List<Clip> seq = new List<Clip>();
    int seqPos;
    List<string> frags = new List<string>();
    int fragPos;
    string lastSetText;
    Clip startFragClip;     // fragment picked in the fragment pane, and the clip it belongs to
    int startFrag;

    SplitContainer split;
    Label fragHeader;
    ListBox fragList;
    Clip fragShownClip;
    string fragShownKey;
    bool suppressFrag;

    // Paste detection
    Native.LowLevelKeyboardProc hookProc;
    IntPtr hook = IntPtr.Zero;
    bool pastePending;
    int pasteKey;
    DateTime pasteDownAt;

    System.Windows.Forms.Timer advanceTimer, saveTimer, reselectTimer, beepTimer;
    int beepsLeft;
    bool exiting, suppressSelection, dirty;

    public MainForm()
    {
        var loaded = Store.Load<Settings>(SettingsPath);
        if (loaded != null) settings = loaded;
        if (settings.SaveClips)
        {
            var c = Store.Load<List<Clip>>(ClipsPath);
            if (c != null) clips = c;
        }
        dirUp = settings.DirectionUp;

        Text = "PowerPaste";
        Font = SystemFonts.MessageBoxFont;
        Size = new Size(620, 480);
        MinimumSize = new Size(380, 260);
        StartPosition = FormStartPosition.CenterScreen;
        boldFont = new Font(Font, FontStyle.Bold);

        imgOff = Icons.Draw(24, 0); imgUp = Icons.Draw(24, 1); imgDown = Icons.Draw(24, 2);
        icoOff = Icon.FromHandle(Icons.Draw(32, 0).GetHicon());
        icoUp = Icon.FromHandle(Icons.Draw(32, 1).GetHicon());
        icoDown = Icon.FromHandle(Icons.Draw(32, 2).GetHicon());
        Icon = icoUp;

        BuildMenu();
        BuildToolbar();
        BuildList();
        BuildStatus();
        BuildTray();

        BuildFragmentPane();

        // Add in reverse dock order so the split fills the remaining space
        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(menu);
        Controls.Add(statusStrip);
        MainMenuStrip = menu;

        advanceTimer = new System.Windows.Forms.Timer();
        advanceTimer.Tick += delegate { advanceTimer.Stop(); if (pastePending) { pastePending = false; OnPasted(); } };
        saveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        saveTimer.Tick += delegate { saveTimer.Stop(); SaveAll(); };
        reselectTimer = new System.Windows.Forms.Timer { Interval = 120 };
        reselectTimer.Tick += delegate { reselectTimer.Stop(); if (active) RestartFromSelection(); else RefreshFragments(); };
        beepTimer = new System.Windows.Forms.Timer { Interval = 300 };
        beepTimer.Tick += delegate { SystemSounds.Asterisk.Play(); if (--beepsLeft <= 0) beepTimer.Stop(); };

        TopMost = settings.AlwaysOnTop;
        RefreshList();
        UpdateUi();
    }

    // ---------------------------------------------------------------- UI construction

    MenuStrip menu;
    ToolStrip toolbar;
    StatusStrip statusStrip;

    void BuildMenu()
    {
        menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Options...", null, delegate { ShowOptions(); });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, delegate { ExitApp(); });

        var edit = new ToolStripMenuItem("&Edit");
        miUp = new ToolStripMenuItem("PowerPaste &Up", imgUp, delegate { MenuDirection(true); });
        miDown = new ToolStripMenuItem("PowerPaste &Down", imgDown, delegate { MenuDirection(false); });
        miStop = new ToolStripMenuItem("&Stop PowerPaste", null, delegate { StopPowerPaste(); });
        miExplode = new ToolStripMenuItem("&Explode Into Fragments", null, delegate { ToggleExplode(); });
        miLoop = new ToolStripMenuItem("PowerPaste &Loop", null, delegate { ToggleLoop(); });
        var miEdit = new ToolStripMenuItem("Edi&t Clip...", null, delegate { EditSelected(); }) { ShortcutKeys = Keys.F2 };
        var miNew = new ToolStripMenuItem("&New Clip...", null, delegate { NewClip(); }) { ShortcutKeys = Keys.Control | Keys.N };
        var miCopy = new ToolStripMenuItem("&Copy Clip to Clipboard", null, delegate { CopySelected(); }) { ShortcutKeyDisplayString = "Enter" };
        var miDelete = new ToolStripMenuItem("&Delete Selected Clips", null, delegate { DeleteSelected(); }) { ShortcutKeyDisplayString = "Del" };
        var miSelectAll = new ToolStripMenuItem("Select &All", null, delegate { SelectAll(); }) { ShortcutKeys = Keys.Control | Keys.A };
        var miClear = new ToolStripMenuItem("Clear All Clips...", null, delegate { ClearAll(); });
        edit.DropDownItems.AddRange(new ToolStripItem[] {
            miUp, miDown, miStop, new ToolStripSeparator(), miExplode, miLoop, new ToolStripSeparator(),
            miEdit, miNew, miCopy, miDelete, miSelectAll, miClear });

        var view = new ToolStripMenuItem("&View");
        miTop = new ToolStripMenuItem("Always on &Top", null, delegate
        {
            settings.AlwaysOnTop = !settings.AlwaysOnTop; TopMost = settings.AlwaysOnTop; MarkDirty(); UpdateUi();
        });
        view.DropDownItems.Add(miTop);

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view });
    }

    void BuildToolbar()
    {
        toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(24, 24) };
        ppButton = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Text = "PowerPaste" };
        ppButton.Click += delegate { PowerPasteButton(); };
        explodeButton = new ToolStripButton("Explode") { CheckOnClick = false };
        explodeButton.ToolTipText = "Explode Into Fragments: paste each line / field of a clip separately";
        explodeButton.Click += delegate { ToggleExplode(); };
        loopButton = new ToolStripButton("Loop");
        loopButton.ToolTipText = "PowerPaste Loop: start over after the last item";
        loopButton.Click += delegate { ToggleLoop(); };
        var edit = new ToolStripButton("Edit") { ToolTipText = "Edit the selected clip (F2 or double-click)" };
        edit.Click += delegate { EditSelected(); };
        var del = new ToolStripButton("Delete") { ToolTipText = "Delete selected clips" };
        del.Click += delegate { DeleteSelected(); };
        var opts = new ToolStripButton("Options") { ToolTipText = "Explode and general options" };
        opts.Click += delegate { ShowOptions(); };
        toolbar.Items.AddRange(new ToolStripItem[] { ppButton, new ToolStripSeparator(), explodeButton, loopButton,
            new ToolStripSeparator(), edit, del, opts });
    }

    void BuildList()
    {
        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true,
            HideSelection = false, GridLines = false
        };
        list.Columns.Add("Clip", 420);
        list.Columns.Add("Chars", 60, HorizontalAlignment.Right);
        list.Columns.Add("Captured", 90);
        list.Resize += delegate { FitColumns(); };
        // Debounced: while active, picking a clip restarts PowerPaste there; otherwise it just
        // refreshes the fragment pane to show where PowerPaste would start.
        list.ItemSelectionChanged += delegate
        {
            if (!suppressSelection) { reselectTimer.Stop(); reselectTimer.Start(); }
        };
        list.DoubleClick += delegate { EditSelected(); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !active) { CopySelected(); e.Handled = true; }
        };

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("PowerPaste Up from here", imgUp, delegate { pastedSinceOn = flippedSinceOn = false; StartPowerPaste(true); });
        ctx.Items.Add("PowerPaste Down from here", imgDown, delegate { pastedSinceOn = flippedSinceOn = false; StartPowerPaste(false); });
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Edit Clip...", null, delegate { EditSelected(); });
        ctx.Items.Add("Copy to Clipboard", null, delegate { CopySelected(); });
        ctx.Items.Add("Delete", null, delegate { DeleteSelected(); });
        list.ContextMenuStrip = ctx;
    }

    // Lower pane (shown while Explode is on): the fragments of the clip PowerPaste is on, or
    // would start on. Clicking a fragment makes it the starting / next paste.
    void BuildFragmentPane()
    {
        split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 80, Panel2MinSize = 70
        };
        split.Panel1.Controls.Add(list);
        fragHeader = new Label { Dock = DockStyle.Top, AutoSize = false, Height = Font.Height + 8,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), BackColor = SystemColors.ControlLight };
        fragList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
        fragList.SelectedIndexChanged += delegate { if (!suppressFrag) PickFragment(fragList.SelectedIndex, false); };
        fragList.DoubleClick += delegate { PickFragment(fragList.SelectedIndex, true); };
        split.Panel2.Controls.Add(fragList);
        split.Panel2.Controls.Add(fragHeader);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try { split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Height - 170); } catch { }
    }

    // Which clip PowerPaste would start on if turned on now, in the given direction.
    Clip StartClip(bool up)
    {
        var order = SequenceOrder(up);
        return order.Count > 0 ? clips[order[0]] : null;
    }

    void RefreshFragments()
    {
        split.Panel2Collapsed = !settings.Explode;
        if (!settings.Explode) return;

        Clip c;
        List<string> f;
        int sel;
        if (active)
        {
            c = seq[seqPos]; f = frags; sel = fragPos;
            fragHeader.Text = "Fragments of the current clip — click one to make it the next paste";
        }
        else
        {
            c = StartClip(settings.DirectionUp);
            f = c == null ? new List<string>() : ExplodeClip(c.Text);
            sel = (c != null && c == startFragClip && startFrag < f.Count) ? startFrag : 0;
            fragHeader.Text = c == null ? "Fragments — select a clip"
                : "Start PowerPaste at fragment — click to choose, double-click to start now";
        }

        string key = c == null ? "" : c.GetHashCode() + "\u0001" + string.Join("\u0001", f);
        suppressFrag = true;
        if (key != fragShownKey)
        {
            fragShownKey = key;
            fragShownClip = c;
            fragList.BeginUpdate();
            fragList.Items.Clear();
            for (int i = 0; i < f.Count; i++) fragList.Items.Add((i + 1) + ":   " + Preview(f[i], 200));
            fragList.EndUpdate();
        }
        if (sel < fragList.Items.Count) fragList.SelectedIndex = sel;
        suppressFrag = false;
    }

    void PickFragment(int i, bool startNow)
    {
        if (i < 0 || fragShownClip == null) return;
        startFragClip = fragShownClip;
        startFrag = i;
        if (active)
        {
            if (seq[seqPos] == fragShownClip && i < frags.Count) { fragPos = i; PutCurrent(); }
        }
        else if (startNow)
        {
            pastedSinceOn = flippedSinceOn = false;
            StartPowerPaste(settings.DirectionUp);
        }
        else UpdateUi();
    }

    List<string> ExplodeClip(string text)
    {
        var f = settings.Explode ? Exploder.Explode(text, settings) : new List<string> { text };
        if (f.Count == 0) f.Add(text);
        return f;
    }

    void FitColumns()
    {
        int w = list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width - 4;
        if (w > 100) list.Columns[0].Width = w;
    }

    void BuildStatus()
    {
        statusStrip = new StatusStrip();
        status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(status);
    }

    void BuildTray()
    {
        var ctx = new ContextMenuStrip();
        tiUp = new ToolStripMenuItem("PowerPaste Up", imgUp, delegate { MenuDirection(true); });
        tiDown = new ToolStripMenuItem("PowerPaste Down", imgDown, delegate { MenuDirection(false); });
        tiStop = new ToolStripMenuItem("Stop PowerPaste", null, delegate { StopPowerPaste(); });
        tiExplode = new ToolStripMenuItem("Explode Into Fragments", null, delegate { ToggleExplode(); });
        tiLoop = new ToolStripMenuItem("PowerPaste Loop", null, delegate { ToggleLoop(); });
        var show = new ToolStripMenuItem("Show Clip List", null, delegate { ShowMain(); }) { Font = new Font(Font, FontStyle.Bold) };
        ctx.Items.AddRange(new ToolStripItem[] { show, new ToolStripSeparator(), tiUp, tiDown, tiStop,
            new ToolStripSeparator(), tiExplode, tiLoop, new ToolStripSeparator() });
        ctx.Items.Add("Exit", null, delegate { ExitApp(); });

        tray = new NotifyIcon { Icon = icoOff, Text = "PowerPaste", ContextMenuStrip = ctx, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
    }

    void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- Clip list

    void RefreshList()
    {
        var selected = new HashSet<Clip>(list.SelectedItems.Cast<ListViewItem>().Select(i => (Clip)i.Tag));
        suppressSelection = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var c in clips)
        {
            var item = new ListViewItem(Preview(c.Text, 300)) { Tag = c };
            item.SubItems.Add(c.Text.Length.ToString());
            item.SubItems.Add(c.Captured.Date == DateTime.Today ? c.Captured.ToString("h:mm:ss tt") : c.Captured.ToString("MMM d"));
            item.ToolTipText = Preview(c.Text, 1000);
            if (selected.Contains(c)) item.Selected = true;
            list.Items.Add(item);
        }
        list.EndUpdate();
        suppressSelection = false;
        FitColumns();
        UpdateHighlight();
    }

    void UpdateHighlight()
    {
        Clip current = active ? seq[seqPos] : null;
        var remaining = active ? new HashSet<Clip>(seq.Skip(seqPos)) : new HashSet<Clip>();
        foreach (ListViewItem item in list.Items)
        {
            var c = (Clip)item.Tag;
            if (c == current) { item.BackColor = Color.FromArgb(255, 236, 140); item.Font = boldFont; }
            else if (remaining.Contains(c)) { item.BackColor = Color.FromArgb(232, 242, 255); item.Font = list.Font; }
            else { item.BackColor = list.BackColor; item.Font = list.Font; }
        }
        if (current != null)
        {
            int i = clips.IndexOf(current);
            if (i >= 0) list.EnsureVisible(i);
        }
    }

    static string Preview(string t, int max)
    {
        string p = t.Replace("\r\n", " ⏎ ").Replace('\n', '⏎').Replace('\r', '⏎').Replace("\t", " → ");
        return p.Length > max ? p.Substring(0, max) + "…" : p;
    }

    void OnClipboardUpdate()
    {
        try { if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return; } catch { }
        string t = ReadClipboardText();
        if (string.IsNullOrEmpty(t)) return;
        if (t == lastSetText) return;                         // our own PowerPaste / copy
        if (clips.Count > 0 && clips[0].Text == t) return;    // duplicate of newest clip
        clips.Insert(0, new Clip { Text = t, Captured = DateTime.Now });
        if (clips.Count > settings.MaxClips)
            RemoveClips(new HashSet<Clip>(clips.Skip(settings.MaxClips)));
        else
        {
            RefreshList();
            UpdateUi();
        }
        MarkDirty();
    }

    static string ReadClipboardText()
    {
        for (int i = 0; i < 6; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return null;
    }

    void SetClipboard(string t)
    {
        lastSetText = t;
        try
        {
            var d = new DataObject();
            d.SetData(DataFormats.UnicodeText, t);
            d.SetData(DataFormats.Text, t);
            Clipboard.SetDataObject(d, true, 10, 50);
        }
        catch (Exception ex) { status.Text = "Could not set clipboard: " + ex.Message; }
    }

    void CopySelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var c = (Clip)list.SelectedItems[0].Tag;
        SetClipboard(c.Text);
        status.Text = "Copied to clipboard: " + Preview(c.Text, 80);
    }

    void EditSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var item = list.FocusedItem != null && list.FocusedItem.Selected ? list.FocusedItem : list.SelectedItems[0];
        var c = (Clip)item.Tag;
        using (var f = new EditClipForm("Edit Clip", c.Text, settings))
        {
            f.TopMost = TopMost;
            if (f.ShowDialog(this) != DialogResult.OK || f.ClipText == c.Text) return;
            c.Text = f.ClipText;
        }
        if (startFragClip == c && !active) startFragClip = null;
        if (active && seq[seqPos] == c)
        {
            int keep = fragPos;
            LoadFragments();
            fragPos = Math.Min(keep, frags.Count - 1);
            PutCurrent();
        }
        RefreshList();
        UpdateUi();
        MarkDirty();
    }

    void NewClip()
    {
        using (var f = new EditClipForm("New Clip", "", settings))
        {
            f.TopMost = TopMost;
            if (f.ShowDialog(this) != DialogResult.OK) return;
            clips.Insert(0, new Clip { Text = f.ClipText, Captured = DateTime.Now });
        }
        if (clips.Count > settings.MaxClips) RemoveClips(new HashSet<Clip>(clips.Skip(settings.MaxClips)));
        else { RefreshList(); UpdateUi(); }
        MarkDirty();
    }

    void SelectAll()
    {
        suppressSelection = true;
        foreach (ListViewItem i in list.Items) i.Selected = true;
        suppressSelection = false;
        if (active) RestartFromSelection();
    }

    void DeleteSelected()
    {
        var del = new HashSet<Clip>(list.SelectedItems.Cast<ListViewItem>().Select(i => (Clip)i.Tag));
        if (del.Count > 0) { RemoveClips(del); MarkDirty(); }
    }

    void ClearAll()
    {
        if (clips.Count == 0) return;
        if (MessageBox.Show(this, "Delete all " + clips.Count + " clips?", "PowerPaste",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        RemoveClips(new HashSet<Clip>(clips));
        MarkDirty();
    }

    void RemoveClips(HashSet<Clip> removed)
    {
        if (active)
        {
            Clip current = seq[seqPos];
            int before = seq.Take(seqPos).Count(removed.Contains);
            seq.RemoveAll(removed.Contains);
            seqPos -= before;
            if (seqPos >= seq.Count) { clips.RemoveAll(removed.Contains); StopPowerPaste(); RefreshList(); return; }
            if (removed.Contains(current)) { LoadFragments(); PutCurrent(); }
        }
        clips.RemoveAll(removed.Contains);
        RefreshList();
        UpdateUi();
    }

    // ---------------------------------------------------------------- PowerPaste

    // Toolbar button: off -> on (last direction); on & nothing pasted -> flip (once); otherwise -> off.
    void PowerPasteButton()
    {
        if (!active) { pastedSinceOn = false; flippedSinceOn = false; StartPowerPaste(settings.DirectionUp); }
        else if (!pastedSinceOn && !flippedSinceOn) { flippedSinceOn = true; StartPowerPaste(!dirUp); }
        else StopPowerPaste();
    }

    void MenuDirection(bool up)
    {
        if (active && dirUp == up) { StopPowerPaste(); return; }
        pastedSinceOn = false; flippedSinceOn = false;
        StartPowerPaste(up);
    }

    void StartPowerPaste(bool up)
    {
        dirUp = up;
        if (settings.DirectionUp != up) { settings.DirectionUp = up; MarkDirty(); }
        if (!BuildSequence())
        {
            Beep(1);
            status.Text = "Nothing to PowerPaste - copy some clips first.";
            StopPowerPaste();
            return;
        }
        active = true;
        LoadStartFragments();
        PutCurrent();
        InstallHook();
    }

    void RestartFromSelection()
    {
        if (!active) return;
        if (!BuildSequence()) { StopPowerPaste(); return; }
        LoadStartFragments();
        PutCurrent();
    }

    // Loads the first clip of the sequence, starting at the fragment picked in the fragment pane
    // if it belongs to that clip.
    void LoadStartFragments()
    {
        LoadFragments();
        if (seq[0] == startFragClip && startFrag < frags.Count) fragPos = startFrag;
    }

    bool BuildSequence()
    {
        seq = SequenceOrder(dirUp).Select(i => clips[i]).ToList();
        seqPos = 0;
        return seq.Count > 0;
    }

    // Selected clips (extended selection) or the selected clip to the end of the list in the
    // direction of travel. Index 0 is the top (newest); "Up" walks toward index 0.
    List<int> SequenceOrder(bool up)
    {
        var order = new List<int>();
        if (clips.Count == 0) return order;
        var idx = list.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (idx.Count > 1)
        {
            order = idx;
            if (up) order.Reverse();
        }
        else
        {
            int start = idx.Count == 1 ? idx[0] : (up ? clips.Count - 1 : 0);
            if (up) for (int i = start; i >= 0; i--) order.Add(i);
            else for (int i = start; i < clips.Count; i++) order.Add(i);
        }
        return order;
    }

    void LoadFragments()
    {
        frags = ExplodeClip(seq[seqPos].Text);
        fragPos = 0;
    }

    void PutCurrent()
    {
        SetClipboard(frags[fragPos]);
        UpdateUi();
    }

    void OnPasted()
    {
        if (!active) return;
        pastedSinceOn = true;
        if (++fragPos < frags.Count) { PutCurrent(); return; }
        if (++seqPos < seq.Count) { LoadFragments(); PutCurrent(); return; }
        if (settings.Loop)
        {
            Beep(2);
            seqPos = 0;
            LoadFragments();
            PutCurrent();
            return;
        }
        Beep(1);
        StopPowerPaste();
        status.Text = "PowerPaste complete.";
    }

    void StopPowerPaste()
    {
        active = false;
        seq = new List<Clip>();
        seqPos = 0;
        startFragClip = null;
        pastePending = false;
        advanceTimer.Stop();
        reselectTimer.Stop();
        RemoveHook();
        UpdateUi();
    }

    void ToggleExplode()
    {
        settings.Explode = !settings.Explode;
        MarkDirty();
        if (active) { LoadFragments(); PutCurrent(); } else UpdateUi();
    }

    void ToggleLoop()
    {
        settings.Loop = !settings.Loop;
        MarkDirty();
        UpdateUi();
    }

    void Beep(int times)
    {
        SystemSounds.Asterisk.Play();
        beepsLeft = times - 1;
        if (beepsLeft > 0) beepTimer.Start();
    }

    void UpdateUi()
    {
        string arrow = dirUp ? "▲ Up" : "▼ Down";
        ppButton.Image = !active ? imgOff : (dirUp ? imgUp : imgDown);
        ppButton.Checked = active;
        ppButton.Text = active ? "PowerPaste " + arrow : "PowerPaste";
        ppButton.ToolTipText = active
            ? "PowerPaste is ON (" + arrow + "). Click to " + (!pastedSinceOn && !flippedSinceOn ? "reverse direction." : "turn off.")
            : "PowerPaste is off. Click to start (" + (settings.DirectionUp ? "▲ Up" : "▼ Down") + ").";

        miUp.Checked = tiUp.Checked = active && dirUp;
        miDown.Checked = tiDown.Checked = active && !dirUp;
        miStop.Enabled = tiStop.Enabled = active;
        miExplode.Checked = tiExplode.Checked = explodeButton.Checked = settings.Explode;
        miLoop.Checked = tiLoop.Checked = loopButton.Checked = settings.Loop;
        miTop.Checked = settings.AlwaysOnTop;

        tray.Icon = !active ? icoOff : (dirUp ? icoUp : icoDown);
        Icon = tray.Icon;

        if (active)
        {
            string frag = frags.Count > 1 ? string.Format(", fragment {0} of {1}", fragPos + 1, frags.Count) : "";
            status.Text = string.Format("PowerPaste {0}: clip {1} of {2}{3}.  Next paste: {4}",
                arrow, seqPos + 1, seq.Count, frag, Preview(frags[fragPos], 80));
            string tip = "PowerPaste " + arrow + " - next: " + Preview(frags[fragPos], 30);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }
        else
        {
            status.Text = clips.Count + " clip" + (clips.Count == 1 ? "" : "s") +
                ". Select a starting clip (or several), then click PowerPaste.";
            tray.Text = "PowerPaste (off)";
        }
        UpdateHighlight();
        RefreshFragments();
    }

    // ---------------------------------------------------------------- Paste detection

    void InstallHook()
    {
        if (hook != IntPtr.Zero) return;
        hookProc = HookCallback;
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc,
            Native.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0);
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show(this, "Could not install the keyboard hook needed to detect pastes.", "PowerPaste",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            StopPowerPaste();
        }
    }

    void RemoveHook()
    {
        if (hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    // Watches for Ctrl+V / Shift+Insert in other applications. The clipboard already holds the
    // current item when the key goes down; we advance shortly after the key is released, giving
    // the target application time to read the clipboard.
    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && active)
        {
            int vk = Marshal.ReadInt32(lParam);
            int msg = wParam.ToInt32();
            bool down = msg == 0x100 || msg == 0x104;
            bool up = msg == 0x101 || msg == 0x105;
            if (down && !pastePending && IsPasteChord(vk) && !OurWindowIsForeground())
            {
                pastePending = true;
                pasteKey = vk;
                pasteDownAt = DateTime.UtcNow;
                advanceTimer.Stop();
                advanceTimer.Interval = 1500;   // fallback if the key-up is never seen
                advanceTimer.Start();
            }
            else if (up && pastePending && vk == pasteKey)
            {
                int elapsed = (int)(DateTime.UtcNow - pasteDownAt).TotalMilliseconds;
                advanceTimer.Stop();
                advanceTimer.Interval = Math.Max(40, 200 - elapsed);
                advanceTimer.Start();
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    static bool IsPasteChord(int vk)
    {
        bool ctrl = Native.KeyDown(0x11), shift = Native.KeyDown(0x10), alt = Native.KeyDown(0x12);
        if (vk == 0x56) return ctrl && !alt;            // Ctrl+V (also Ctrl+Shift+V)
        if (vk == 0x2D) return shift && !ctrl && !alt;  // Shift+Insert
        return false;
    }

    static bool OurWindowIsForeground()
    {
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        return pid == (uint)Process.GetCurrentProcess().Id;
    }

    // ---------------------------------------------------------------- Options, persistence, lifetime

    void ShowOptions()
    {
        using (var f = new OptionsForm(settings))
        {
            f.TopMost = TopMost;
            if (f.ShowDialog(this) != DialogResult.OK) return;
            bool wasSaving = settings.SaveClips;
            settings = f.Result;
            if (wasSaving && !settings.SaveClips) try { File.Delete(ClipsPath); } catch { }
            if (clips.Count > settings.MaxClips) RemoveClips(new HashSet<Clip>(clips.Skip(settings.MaxClips)));
            if (active) { LoadFragments(); PutCurrent(); } else UpdateUi();
            MarkDirty();
        }
    }

    void MarkDirty() { dirty = true; saveTimer.Stop(); saveTimer.Start(); }

    void SaveAll()
    {
        if (!dirty) return;
        dirty = false;
        Store.Save(SettingsPath, settings);
        if (settings.SaveClips) Store.Save(ClipsPath, clips);
    }

    void ExitApp()
    {
        exiting = true;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnClipboardUpdate();
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RemoveHook();
        Native.RemoveClipboardFormatListener(Handle);
        dirty = true;
        SaveAll();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}

class EditClipForm : Form
{
    readonly TextBox box;
    readonly Label info;
    readonly Button ok;
    readonly Settings settings;

    public string ClipText { get { return box.Text; } }

    public EditClipForm(string title, string text, Settings s)
    {
        settings = s;
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(580, 420);
        MinimumSize = new Size(340, 220);
        ShowInTaskbar = false;
        MinimizeBox = false;
        ShowIcon = false;
        KeyPreview = true;

        box = new TextBox
        {
            Multiline = true, AcceptsReturn = true, AcceptsTab = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Text = text
        };
        box.TextChanged += delegate { UpdateInfo(); };

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var wrap = new CheckBox { Text = "Word wrap", AutoSize = true, Anchor = AnchorStyles.Left };
        wrap.CheckedChanged += delegate
        {
            box.WordWrap = wrap.Checked;
            box.ScrollBars = wrap.Checked ? ScrollBars.Vertical : ScrollBars.Both;
        };
        info = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 0, 0, 0) };
        ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        new ToolTip().SetToolTip(ok, "Save (Ctrl+Enter)");
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(wrap, 0, 0);
        bottom.Controls.Add(info, 1, 0);
        bottom.Controls.Add(ok, 2, 0);
        bottom.Controls.Add(cancel, 3, 0);
        CancelButton = cancel;

        Controls.Add(box);
        Controls.Add(bottom);

        // Enter inserts a line break in the editor; Ctrl+Enter saves
        KeyDown += (o, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control && ok.Enabled) { DialogResult = DialogResult.OK; e.SuppressKeyPress = true; }
        };
        Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; box.Focus(); };
        UpdateInfo();
    }

    void UpdateInfo()
    {
        string t = box.Text;
        int lines = t.Length == 0 ? 0 : t.Split('\n').Length;
        string s = t.Length + " chars, " + lines + " line" + (lines == 1 ? "" : "s");
        if (settings.Explode)
        {
            int n = Exploder.Explode(t, settings).Count;
            s += ", " + n + " fragment" + (n == 1 ? "" : "s") + " when exploded";
        }
        info.Text = s;
        ok.Enabled = t.Length > 0;
    }
}

class OptionsForm : Form
{
    public Settings Result;
    readonly Settings work;
    CheckBox cLines, cCommas, cTabs, cSemis, cSpaces, cQuotes, cTrim, cSkip, cSave;
    TextBox tCustom, tSample;
    ListBox lPreview;
    Label lCount;
    NumericUpDown nMax;

    public OptionsForm(Settings s)
    {
        work = s.Clone();
        Text = "PowerPaste Options";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var root = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };

        // Explode settings
        var explode = Group("Explode Into Fragments - split at");
        var ef = Flow();
        cLines = Check(ef, "Line breaks", work.SplitLines);
        cCommas = Check(ef, "Commas", work.SplitCommas);
        cTabs = Check(ef, "Tabs", work.SplitTabs);
        cSemis = Check(ef, "Semicolons", work.SplitSemicolons);
        cSpaces = Check(ef, "Spaces", work.SplitSpaces);
        var customRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        customRow.Controls.Add(new Label { Text = "Other characters:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        tCustom = new TextBox { Text = work.SplitCustom, Width = 80 };
        tCustom.TextChanged += delegate { UpdatePreview(); };
        customRow.Controls.Add(tCustom);
        ef.Controls.Add(customRow);
        ef.Controls.Add(new Label { Text = "", Height = 6 });
        cQuotes = Check(ef, "Keep \"quoted, text\" together (CSV)", work.RespectQuotes);
        cTrim = Check(ef, "Strip leading/trailing spaces", work.TrimFragments);
        cSkip = Check(ef, "Skip empty fragments", work.SkipEmpty);
        explode.Controls.Add(ef);

        // Live preview
        var preview = Group("Preview");
        var pf = Flow();
        pf.Controls.Add(new Label { Text = "Sample clip:", AutoSize = true });
        tSample = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, AcceptsTab = true,
            Width = 260, Height = 110,
            Text = "January, 31\r\nFebruary, 28\r\nMarch, 31\r\nApril, 30\r\nMay, 31\r\nJune, 30"
        };
        tSample.TextChanged += delegate { UpdatePreview(); };
        pf.Controls.Add(tSample);
        lCount = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        pf.Controls.Add(lCount);
        lPreview = new ListBox { Width = 260, Height = 130, IntegralHeight = false };
        pf.Controls.Add(lPreview);
        preview.Controls.Add(pf);

        // General
        var general = Group("General");
        var gf = Flow();
        var maxRow = new FlowLayoutPanel { AutoSize = true };
        maxRow.Controls.Add(new Label { Text = "Keep at most", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        nMax = new NumericUpDown { Minimum = 10, Maximum = 10000, Value = Math.Max(10, Math.Min(10000, work.MaxClips)), Width = 70 };
        maxRow.Controls.Add(nMax);
        maxRow.Controls.Add(new Label { Text = "clips", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        gf.Controls.Add(maxRow);
        cSave = Check(gf, "Save clips between sessions", work.SaveClips);
        general.Controls.Add(gf);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        ok.Click += delegate { Commit(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        root.Controls.Add(explode, 0, 0);
        root.Controls.Add(preview, 1, 0);
        root.Controls.Add(general, 0, 1);
        root.Controls.Add(buttons, 1, 1);
        Controls.Add(root);

        foreach (var c in new[] { cLines, cCommas, cTabs, cSemis, cSpaces, cQuotes, cTrim, cSkip })
            c.CheckedChanged += delegate { UpdatePreview(); };
        UpdatePreview();
    }

    static GroupBox Group(string text)
    {
        return new GroupBox { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8), Margin = new Padding(4), Dock = DockStyle.Fill };
    }

    static FlowLayoutPanel Flow()
    {
        return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
    }

    static CheckBox Check(Control parent, string text, bool value)
    {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true };
        parent.Controls.Add(c);
        return c;
    }

    void ReadControls(Settings s)
    {
        s.SplitLines = cLines.Checked;
        s.SplitCommas = cCommas.Checked;
        s.SplitTabs = cTabs.Checked;
        s.SplitSemicolons = cSemis.Checked;
        s.SplitSpaces = cSpaces.Checked;
        s.SplitCustom = tCustom.Text;
        s.RespectQuotes = cQuotes.Checked;
        s.TrimFragments = cTrim.Checked;
        s.SkipEmpty = cSkip.Checked;
    }

    void UpdatePreview()
    {
        if (lPreview == null) return;
        var s = work.Clone();
        ReadControls(s);
        var frags = Exploder.Explode(tSample.Text, s);
        lPreview.BeginUpdate();
        lPreview.Items.Clear();
        for (int i = 0; i < frags.Count; i++) lPreview.Items.Add((i + 1) + ":  " + frags[i]);
        lPreview.EndUpdate();
        lCount.Text = frags.Count + " paste" + (frags.Count == 1 ? "" : "s") + ":";
    }

    void Commit()
    {
        ReadControls(work);
        work.MaxClips = (int)nMax.Value;
        work.SaveClips = cSave.Checked;
        Result = work;
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\PowerPaste_SingleInstance", out created))
        {
            if (!created)
            {
                MessageBox.Show("PowerPaste is already running. Look for its icon in the system tray.", "PowerPaste");
                return;
            }
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
