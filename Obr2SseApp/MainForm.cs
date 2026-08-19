using System.ComponentModel;
using System.Runtime.InteropServices;
using Obr2Sse;

namespace Obr2SseApp;

/// The whole app in one window, dressed as an old-school keygen: a gray beveled panel, two game
/// folders to point at (found automatically where possible), a few options, and a Generate button
/// that sweeps the Oblivion weapons into a Skyrim mod.
public sealed class MainForm : Form
{
    private const int W = 452;
    private const int Pad = 16;
    private const int Inner = W - Pad * 2;

    private static readonly Color Face = Color.FromArgb(198, 198, 198);
    private static readonly Color Navy = Color.FromArgb(0, 0, 128);
    private static readonly Color Ink = Color.Black;
    private static readonly Color Good = Color.FromArgb(0, 100, 0);
    private static readonly Color Bad = Color.FromArgb(160, 0, 0);
    private static readonly Font Ui = new("MS Sans Serif", 8.25f);
    private static readonly Font UiBold = new("MS Sans Serif", 8.25f, FontStyle.Bold);

    private readonly TextBox _obr = Field();
    private readonly TextBox _skyrim = Field();
    private readonly TextBox _output = Field();
    private readonly TextBox _log = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.Fixed3D, BackColor = Color.White,
        Font = new Font("MS Sans Serif", 8.25f), Text = "- OBR2SSE -",
    };

    private readonly Panel _obrDot = Dot();
    private readonly Panel _skyrimDot = Dot();

    private readonly Panel _titleBar = new() { BackColor = Navy };
    private readonly Panel _modeGroup = new() { BackColor = Face };
    private readonly Panel _formatGroup = new() { BackColor = Face };
    private readonly RadioButton _standalone = Radio("Standalone", 0, true);
    private readonly RadioButton _replacer = Radio("Replacer", 120, false);
    private readonly RadioButton _zip = Radio("Zip archive", 0, true);
    private readonly RadioButton _loose = Radio("Loose files", 120, false);

    private readonly CheckBox _highPoly = new()
    {
        Text = "High poly", Checked = true, AutoSize = true, BackColor = Face,
        FlatStyle = FlatStyle.System, Font = Ui,
    };

    private readonly Label _hpWarn = new()
    {
        Text = "warning: lower quality, and some meshes will break", AutoSize = true, ForeColor = Bad,
        BackColor = Face, Font = Ui, Visible = false,
    };

    private readonly RetroButton _generate = new() { Text = "Generate", Size = new Size(150, 28) };
    private readonly RetroButton _about = new() { Text = "About", Size = new Size(120, 28) };
    private readonly RetroButton _exit = new() { Text = "Exit", Size = new Size(120, 28) };
    // Speaker / muted-speaker glyphs from Segoe MDL2 Assets (Win10+), clearer than a music note.
    private const string IconSound = "";
    private const string IconMuted = "";
    private readonly RetroButton _mute = new()
    {
        Text = IconSound, Size = new Size(24, 16), Font = new Font("Segoe MDL2 Assets", 9f),
    };
    private readonly RetroProgress _progress = new() { Size = new Size(Inner, 14), Visible = false };

    private readonly MusicPlayer _music = new(0.12f);
    private CancellationTokenSource? _cancel;
    private bool Busy => _cancel is not null;

    public MainForm()
    {
        Text = "OBR2SSE";
        Icon = LoadIcon();
        BackColor = Face;
        Font = Ui;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W, 344);
        DoubleBuffered = true;

        BuildUi();

        _generate.Click += (_, _) => OnGenerateOrCancel();
        _about.Click += (_, _) => ShowAbout();
        _exit.Click += (_, _) => Close();
        _mute.Click += (_, _) => ToggleMute();
        _highPoly.CheckedChanged += (_, _) => _hpWarn.Visible = !_highPoly.Checked;

        foreach (var box in new[] { _obr, _skyrim, _output })
            box.TextChanged += (_, _) => Revalidate();

        Load += async (_, _) => { _music.Play(); await Detect(); };
        FormClosing += (_, e) => { if (Busy) { _cancel!.Cancel(); e.Cancel = true; } };
        FormClosed += (_, _) => { _music.Dispose(); Conversion.CleanTemp(); };
    }

    private void BuildUi()
    {
        _titleBar.Location = new Point(4, 4);
        _titleBar.Size = new Size(W - 8, 20);
        var titleText = new Label
        {
            Text = "  OBR2SSE  -  Oblivion Remastered to Skyrim SE",
            ForeColor = Color.White, Font = UiBold, AutoSize = false,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        };
        _titleBar.Controls.Add(titleText);
        _mute.Location = new Point(W - 8 - 24, 2);
        _titleBar.Controls.Add(_mute);
        _mute.BringToFront();
        _titleBar.MouseDown += DragWindow;
        titleText.MouseDown += DragWindow;
        Controls.Add(_titleBar);

        int y = 34;
        y = PathRow("Oblivion Remastered :", _obr, _obrDot,
            () => BrowseFolder(_obr, "Select the Oblivion Remastered game folder"), y);
        y = PathRow("Skyrim Special Edition :", _skyrim, _skyrimDot,
            () => BrowseFolder(_skyrim, "Select the Skyrim Special Edition game folder"), y);
        y = PathRow("Save to :", _output, null,
            () => BrowseFolder(_output, "Where to save the mod"), y);

        OptionRow("Mode :", _modeGroup, _standalone, _replacer, y);
        OptionRow("Output :", _formatGroup, _zip, _loose, y + 22);
        _highPoly.Location = new Point(Pad, y + 46);
        _hpWarn.Location = new Point(Pad + 90, y + 47);
        Controls.Add(_highPoly);
        Controls.Add(_hpWarn);

        y += 76;
        Controls.Add(Caption("Log :", y));
        _log.Location = new Point(Pad, y + 16);
        _log.Size = new Size(Inner, 20);
        Controls.Add(_log);

        y += 44;
        _progress.Location = new Point(Pad, y);
        Controls.Add(_progress);

        y += 22;
        _generate.Location = new Point(Pad, y);
        _about.Location = new Point(Pad + 162, y);
        _exit.Location = new Point(W - Pad - _exit.Width, y);
        Controls.Add(_generate);
        Controls.Add(_about);
        Controls.Add(_exit);

        // ProcessPath, not BaseDirectory: the single-file build runs from a temp extract folder.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _output.Text = Path.Combine(exeDir, "OBR2SSE_OUTPUT");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ControlPaint.DrawBorder3D(e.Graphics, 0, 0, Width, Height, Border3DStyle.Raised);
    }

    private int PathRow(string label, TextBox box, Panel? dot, Action browse, int y)
    {
        Controls.Add(Caption(label, y));

        if (dot is not null)
        {
            // Small status square, sitting on the caption line at the right edge.
            dot.Location = new Point(Pad + Inner - 10, y + 2);
            Controls.Add(dot);
        }

        box.Location = new Point(Pad, y + 16);
        box.Size = new Size(Inner - 30, 20);

        var pick = new RetroButton { Text = "…", Size = new Size(26, 20), Location = new Point(Pad + Inner - 26, y + 15) };
        pick.Click += (_, _) => browse();

        Controls.Add(box);
        Controls.Add(pick);
        return y + 42;
    }

    private void OptionRow(string caption, Panel group, RadioButton first, RadioButton second, int y)
    {
        Controls.Add(Caption(caption, y + 2));
        group.Location = new Point(Pad + 76, y);
        group.Size = new Size(Inner - 76, 20);
        group.Controls.Add(first);
        group.Controls.Add(second);
        Controls.Add(group);
    }

    private static Label Caption(string text, int y) => new()
    {
        Text = text, ForeColor = Ink, BackColor = Face, AutoSize = true,
        Location = new Point(Pad, y), Font = Ui,
    };

    private static TextBox Field() => new()
    {
        BorderStyle = BorderStyle.Fixed3D, BackColor = Color.White,
        Font = new Font("MS Sans Serif", 8.25f),
    };

    private static Panel Dot() => new()
    {
        Size = new Size(9, 9),
        BackColor = Color.Gray,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static RadioButton Radio(string text, int x, bool @checked) => new()
    {
        Text = text, Checked = @checked, AutoSize = true, ForeColor = Ink, BackColor = Face,
        FlatStyle = FlatStyle.System, Font = Ui, Location = new Point(x, 0),
    };

    private async Task Detect()
    {
        Log("scanning for games...");
        var (obr, skyrim) = await Task.Run(() => (GameDetect.FindOblivion(), GameDetect.FindSkyrim()));

        if (obr is not null) _obr.Text = obr;
        if (skyrim is not null) _skyrim.Text = skyrim;

        Revalidate();
        Log(obr is not null && skyrim is not null ? "both games found" : "point to any game not found");
    }

    private void Revalidate()
    {
        bool obrOk = GameDetect.IsOblivion(_obr.Text);
        bool skyrimOk = GameDetect.IsSkyrim(_skyrim.Text);

        _obrDot.BackColor = obrOk ? Good : Bad;
        _skyrimDot.BackColor = skyrimOk ? Good : Bad;
        _generate.Enabled = Busy || (obrOk && skyrimOk && _output.Text.Length > 0);
    }

    private void BrowseFolder(TextBox target, string prompt)
    {
        using var dialog = new FolderBrowserDialog { Description = prompt, UseDescriptionForTitle = true };
        if (Directory.Exists(target.Text))
            dialog.SelectedPath = target.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private void ToggleMute()
    {
        _music.ToggleMute();
        _mute.Text = _music.Muted ? IconMuted : IconSound;
    }

    /// The app icon, embedded so the window and taskbar carry it too, not just the exe.
    private static Icon LoadIcon()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return new Icon(stream);
    }

    private void ShowAbout() => MessageBox.Show(this,
        "OBR2SSE\n" +
        "Oblivion Remastered to Skyrim Special Edition\n\n" +
        "by RoseEden30\n\n" +
        "Reads your own game installs. Ships no game assets.\n\n" +
        "CUE4Parse - reads Oblivion Remastered assets\n" +
        "nifly - NIF meshes\n" +
        "texconv - DDS textures\n" +
        "Mutagen - Skyrim plugin",
        "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void OnGenerateOrCancel()
    {
        if (Busy)
        {
            _cancel!.Cancel();
            _generate.Enabled = false;
            _generate.Text = "Cancelling";
            return;
        }
        Run();
    }

    private async void Run()
    {
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        EnterBusy();

        string obr = _obr.Text, skyrim = _skyrim.Text, output = _output.Text;
        var mode = _replacer.Checked ? ConversionMode.Replacer : ConversionMode.Standalone;
        var format = _zip.Checked ? OutputFormat.Zip : OutputFormat.Loose;
        var quality = _highPoly.Checked ? ObrMesh.MeshQuality.High : ObrMesh.MeshQuality.Balanced;

        try
        {
            var result = await Task.Run(() =>
                Conversion.Run(obr, skyrim, output, mode, format, quality, ReportProgress, token), token);

            Log(result.Message, result.Ok ? Good : Bad);
        }
        catch (OperationCanceledException)
        {
            Log("cancelled, nothing left behind");
        }
        catch (Exception e)
        {
            Log(e.Message, Bad);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            LeaveBusy();
            AlertDone();
        }
    }

    // When the run ends and the window is not the one in front, flash its taskbar button so the user
    // notices from another app. The flashing stops on its own once they bring the window forward.
    private void AlertDone()
    {
        if (GetForegroundWindow() == Handle)
            return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private void EnterBusy()
    {
        SetInputsEnabled(false);
        _progress.Visible = true;
        _progress.Value = 0;
        _generate.Text = "Cancel";
    }

    private void LeaveBusy()
    {
        SetInputsEnabled(true);
        _progress.Visible = false;
        _generate.Text = "Generate";
        Revalidate();
    }

    private void ReportProgress(int done, int total, string name)
    {
        BeginInvoke(() =>
        {
            if (total > 0)
            {
                _progress.Value = Math.Clamp(done * 100 / total, 0, 100);
                Log($"{done} / {total}   {name}");
            }
            else
            {
                Log(name);
            }
        });
    }

    // Locks the inputs while a conversion runs, but never the title bar (so the window still moves and
    // the sound still toggles) or Generate/About/Exit.
    private void SetInputsEnabled(bool on)
    {
        foreach (Control c in Controls)
        {
            if (c == _titleBar)
                continue;

            if (c is TextBox or CheckBox or Panel
                || (c is RetroButton b && b != _generate && b != _about && b != _exit))
                c.Enabled = on;
        }

        _log.Enabled = true;
    }

    private void Log(string text) => Log(text, Ink);

    private void Log(string text, Color color)
    {
        _log.ForeColor = color;
        _log.Text = text;
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_ALL = 0x3;
    private const uint FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    /// A classic Win9x raised/pushed button, drawn by hand so it keeps its bevel on any Windows theme.
    private sealed class RetroButton : Button
    {
        private bool _down;

        public RetroButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Face;
            Font = UiBold;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Face);
            ControlPaint.DrawButton(g, ClientRectangle, _down ? ButtonState.Pushed : ButtonState.Normal);

            var text = ClientRectangle;
            if (_down) text.Offset(1, 1);
            TextRenderer.DrawText(g, Text, Font, text, Enabled ? Ink : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// A sunken progress bar filled in navy blocks, in place of the modern themed one.
    private sealed class RetroProgress : Control
    {
        private int _value;

        public RetroProgress() => SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);

            int inner = (Width - 4) * _value / 100;
            using (var brush = new SolidBrush(Navy))
                g.FillRectangle(brush, 2, 2, inner, Height - 4);

            ControlPaint.DrawBorder3D(g, ClientRectangle, Border3DStyle.Sunken);
        }
    }
}
