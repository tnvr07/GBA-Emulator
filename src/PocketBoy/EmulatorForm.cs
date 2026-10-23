using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PocketBoy;

internal sealed class EmulatorForm : Form
{
    private readonly GameScreen _screen = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private GameBoy? _gameBoy;
    private GameBoyAdvance? _gameBoyAdvance;
    private double _cycleBudget;
    private bool _paused;

    public EmulatorForm(string[] args)
    {
        Text = "PocketBoy";
        MinimumSize = new Size(640, 530);
        ClientSize = new Size(800, 680);
        BackColor = Color.FromArgb(22, 27, 35);
        ForeColor = Color.FromArgb(233, 237, 242);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 18, 24, 18),
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill };
        var brand = new Label
        {
            Text = "POCKETBOY",
            AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(150, 205, 170),
            Location = new Point(0, 5)
        };
        var openRom = new Button
        {
            Text = "Open ROM",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(57, 78, 68),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        openRom.FlatAppearance.BorderSize = 0;
        openRom.Location = new Point(Width - openRom.Width - 100, 8);
        openRom.Click += (_, _) => OpenRom();
        header.Resize += (_, _) => openRom.Location = new Point(header.ClientSize.Width - openRom.Width, 8);
        header.Controls.Add(brand);
        header.Controls.Add(openRom);

        _screen.Dock = DockStyle.Fill;
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_screen, 0, 1);
        _status.Text = "Open a .gb, .gbc, or .gba ROM to start.";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(165, 174, 187);
        _status.Font = new Font("Segoe UI", 9);
        layout.Controls.Add(_status, 0, 2);

        _timer.Tick += OnTimerTick;
        _timer.Start();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _screen.Dispose();
        };
        if (args.Length > 0)
            Shown += (_, _) => LoadRom(args[0]);
    }

    private void OpenRom()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Game Boy or Game Boy Advance ROM",
            Filter = "Supported ROMs (*.gb;*.gbc;*.gba)|*.gb;*.gbc;*.gba|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        LoadRom(dialog.FileName);
    }

    private void LoadRom(string path)
    {
        try
        {
            byte[] rom = File.ReadAllBytes(path);
            string extension = Path.GetExtension(path);
            string title;
            if (extension.Equals(".gba", StringComparison.OrdinalIgnoreCase))
            {
                GameBoyAdvance candidate = new(rom);
                _gameBoyAdvance = candidate;
                _gameBoy = null;
                title = candidate.Title;
            }
            else
            {
                GameBoy candidate = new(rom);
                _gameBoy = candidate;
                _gameBoyAdvance = null;
                title = candidate.Title;
            }

            _cycleBudget = 0;
            _paused = false;
            _clock.Restart();
            _status.Text = $"{title}  |  Arrows: move   Z: A   X: B   Q/E: L/R   Enter: Start   Backspace: Select   Esc: pause";
            Text = $"{title} - PocketBoy";
            _screen.ClearFrame();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "Unable to open ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if ((_gameBoy is null && _gameBoyAdvance is null) || _paused)
        {
            _clock.Restart();
            return;
        }

        double elapsed = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        bool gba = _gameBoyAdvance is not null;
        int cyclesPerFrame = gba ? 280_896 : 456 * 154;
        int cyclesPerSecond = gba ? 16_777_216 : 4_194_304;
        _cycleBudget = Math.Min(_cycleBudget + elapsed * cyclesPerSecond, cyclesPerFrame * 3);
        int frames = 0;
        while (_cycleBudget >= cyclesPerFrame && frames < 3)
        {
            if (_gameBoyAdvance is not null)
                _gameBoyAdvance.AdvanceFrame();
            else
                _gameBoy!.AdvanceFrame();
            _cycleBudget -= cyclesPerFrame;
            frames++;
        }
        if (frames > 0)
        {
            if (_gameBoyAdvance is not null)
                _screen.SetGbaPixels(_gameBoyAdvance.Ppu.Pixels);
            else
                _screen.SetGameBoyPixels(_gameBoy!.Ppu.Pixels);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && (_gameBoy is not null || _gameBoyAdvance is not null))
        {
            _paused = !_paused;
            string title = _gameBoyAdvance?.Title ?? _gameBoy!.Title;
            _status.Text = _paused ? "Paused  |  Press Esc to resume" : $"{title}  |  Arrows: move   Z: A   X: B   Q/E: L/R   Enter: Start   Backspace: Select   Esc: pause";
            _clock.Restart();
            e.Handled = true;
            return;
        }

        if (TryMapKey(e.KeyCode, out GameBoyButton button))
        {
            _gameBoy?.Joypad.SetButton(button, true);
            _gameBoyAdvance?.SetButton(button, true);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (TryMapKey(e.KeyCode, out GameBoyButton button))
        {
            _gameBoy?.Joypad.SetButton(button, false);
            _gameBoyAdvance?.SetButton(button, false);
            e.Handled = true;
        }
    }

    private static bool TryMapKey(Keys key, out GameBoyButton button)
    {
        button = key switch
        {
            Keys.Right or Keys.D => GameBoyButton.Right,
            Keys.Left or Keys.A => GameBoyButton.Left,
            Keys.Up or Keys.W => GameBoyButton.Up,
            Keys.Down or Keys.S => GameBoyButton.Down,
            Keys.Z => GameBoyButton.A,
            Keys.X => GameBoyButton.B,
            Keys.Q => GameBoyButton.L,
            Keys.E => GameBoyButton.R,
            Keys.Back => GameBoyButton.Select,
            Keys.Enter => GameBoyButton.Start,
            _ => (GameBoyButton)(-1)
        };
        return (int)button >= 0;
    }
}

internal sealed class GameScreen : Control
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(224, 248, 208),
        Color.FromArgb(136, 192, 112),
        Color.FromArgb(52, 104, 86),
        Color.FromArgb(8, 24, 32)
    ];
    private readonly Bitmap _bitmap = new(GbaPpu.Width, GbaPpu.Height, PixelFormat.Format32bppArgb);
    private readonly int[] _argb = new int[GbaPpu.Width * GbaPpu.Height];
    private bool _hasFrame;
    private int _frameWidth;
    private int _frameHeight;

    public GameScreen()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 15, 20);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void ClearFrame()
    {
        _hasFrame = false;
        Invalidate();
    }

    public void SetGameBoyPixels(byte[] pixels)
    {
        Array.Fill(_argb, Colors[0].ToArgb());
        for (int i = 0; i < pixels.Length; i++)
        {
            byte shade = pixels[i];
            int x = i % Ppu.Width;
            int y = i / Ppu.Width;
            _argb[y * GbaPpu.Width + x] = Colors[shade].ToArgb();
        }

        Present(Ppu.Width, Ppu.Height);
    }

    public void SetGbaPixels(byte[] pixels)
    {
        for (int i = 0; i < GbaPpu.Width * GbaPpu.Height; i++)
        {
            int color = pixels[i * 2] | (pixels[i * 2 + 1] << 8);
            int red = (color & 0x1F) * 255 / 31;
            int green = ((color >> 5) & 0x1F) * 255 / 31;
            int blue = ((color >> 10) & 0x1F) * 255 / 31;
            _argb[i] = Color.FromArgb(red, green, blue).ToArgb();
        }

        Present(GbaPpu.Width, GbaPpu.Height);
    }

    private void Present(int width, int height)
    {
        BitmapData data = _bitmap.LockBits(
            new Rectangle(0, 0, GbaPpu.Width, GbaPpu.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(_argb, 0, data.Scan0, _argb.Length);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        _frameWidth = width;
        _frameHeight = height;
        _hasFrame = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_hasFrame)
        {
            using var textBrush = new SolidBrush(Color.FromArgb(165, 174, 187));
            using var titleFont = new Font("Segoe UI", 15, FontStyle.Bold);
            using var hintFont = new Font("Segoe UI", 10);
            string title = "Your Game Boy, ready.";
            string hint = "Load a legally obtained .gb, .gbc, or .gba ROM to play.";
            SizeF titleSize = e.Graphics.MeasureString(title, titleFont);
            SizeF hintSize = e.Graphics.MeasureString(hint, hintFont);
            float centerY = ClientSize.Height / 2f;
            e.Graphics.DrawString(title, titleFont, textBrush, (ClientSize.Width - titleSize.Width) / 2, centerY - 32);
            e.Graphics.DrawString(hint, hintFont, textBrush, (ClientSize.Width - hintSize.Width) / 2, centerY + 6);
            return;
        }

        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        float scale = Math.Min((float)ClientSize.Width / _frameWidth, (float)ClientSize.Height / _frameHeight);
        int width = (int)(_frameWidth * scale);
        int height = (int)(_frameHeight * scale);
        e.Graphics.DrawImage(
            _bitmap,
            new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height),
            new Rectangle(0, 0, _frameWidth, _frameHeight),
            GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap.Dispose();
        base.Dispose(disposing);
    }
}
