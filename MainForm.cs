using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskTodo;

internal sealed class MainForm : Form
{
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WsThickFrame = 0x00040000;
    private const int CsDropShadow = 0x00020000;
    private const int GwlExStyle = -20;
    private const int WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWcpRound = 2;
    private const int DwmSbtTransientWindow = 3;

    private const int WmImeStartComposition = 0x010D;
    private const int WmImeEndComposition = 0x010E;
    private const int WmImeSetContext = 0x0281;
    private const long IscShowUiComposition = 0x80000000;
    private const int WmImeComposition = 0x010F;
    private const int GcsCompStr = 0x0008;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint WdaNone = 0x00000000;
    private const int SmRemoteSession = 0x1000;

    private static readonly Color Accent = Color.FromArgb(104, 148, 255);
    private static readonly Color Danger = Color.FromArgb(255, 98, 98);
    private static readonly Color TextBright = Color.FromArgb(240, 240, 244);
    private static readonly Color TextDone = Color.FromArgb(168, 168, 178);
    private static readonly Color FieldFill = Color.FromArgb(30, 0, 0, 0);
    private static readonly Color FallbackBg = Color.FromArgb(26, 26, 31);
    private static readonly Color DebugBackdrop = Color.FromArgb(34, 34, 40);

    // 每一行、每一次敲击都从这串颜色里取一个，别老是同一个蓝
    private static readonly Color[] Palette =
    {
        Color.FromArgb(104, 148, 255),
        Color.FromArgb(160, 124, 255),
        Color.FromArgb(255, 108, 178),
        Color.FromArgb(255, 166, 66),
        Color.FromArgb(64, 214, 168),
        Color.FromArgb(64, 202, 255),
    };

    private readonly AppData _data;
    private readonly List<TodoItem> _items;
    private readonly TextBox _input = new();
    private ImeContextHook? _imeHook;
    private readonly System.Windows.Forms.Timer _pinTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 530 };
    private readonly System.Windows.Forms.Timer _blurTimer = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _fxTimer = new() { Interval = 15 };
    private readonly List<Burst> _bursts = new();
    private readonly Random _rng = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private ImeFilter? _imeFilter;
    private float _lastFx;

    private Bitmap? _backdrop;
    private Bitmap? _rawShot;
    private Bitmap? _smallShot;
    private Bitmap? _entryBmp;
    private Bitmap? _wipeBmp;
    private float _backdropLum;
    private int _hueTick;
    private float _glyphSide = 1f;
    private Color _fg = TextBright;
    private Color _fgDone = TextDone;
    private Color _fgSoft = Color.FromArgb(165, 235, 235, 245);
    private Color _fgEdge = Color.FromArgb(46, 255, 255, 255);
    private bool _capturing;
    private bool _remoteMode;
    private uint _appliedAffinity = uint.MaxValue;

    private Font _fItem = null!;
    private Font _fBig = null!;

    private string _entry = string.Empty;
    private int _caret;
    private string _composition = string.Empty;
    private bool _caretOn = true;
    private float _scale = 1f;
    private int _fieldScroll;
    private int _scroll;
    private int _hoverRow = -1;
    private bool _closeHover;
    private bool _scrollDragging;
    private int _scrollDragY;
    private int _scrollDragStart;

    // 长按蓄力 + 画面抖动
    private TodoItem? _pressItem;
    private PressKind _pressKind;
    private bool _pressHeld;
    private float _press;

    // 长按行中间拖动排序
    private TodoItem? _dragItem;
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragDown;
    private int _dragY;
    private float _dragHold;
    private float _shake;
    private bool _winHeld;
    private bool _winMoving;
    private Point _winBase;
    private float _punch;
    private bool _shaking;
    private float _shakeDx;
    private float _shakeDy;
    private float _shakeZoom = 1f;
    private int _glyphChar = -1;
    private int _glyphPos;

    public MainForm()
    {
        _data = Store.Load();
        _items = _data.Items;
        foreach (TodoItem item in _items)
        {
            item.Strike = item.Done ? 1f : 0f;
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = FallbackBg;
        TopMost = true;
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Text = "桌面清单";
        MinimumSize = new Size(200, 130);
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // 取不到图标就用默认图标
        }

        BuildUi();
        HookEvents();
    }

    // 字体优先找更粗更方的现代黑体，找不到才退回雅黑
    private static readonly string FontFamily = PickFamily(
        "HarmonyOS Sans SC", "MiSans", "Alibaba PuHuiTi 3.0", "思源黑体 CN",
        "Source Han Sans SC", "DengXian", "Microsoft YaHei UI");

    private static string PickFamily(params string[] candidates)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var collection = new InstalledFontCollection();
            foreach (FontFamily family in collection.Families)
            {
                installed.Add(family.Name);
            }
        }
        catch
        {
            return "Microsoft YaHei UI";
        }

        foreach (string name in candidates)
        {
            if (installed.Contains(name))
            {
                return name;
            }
        }

        return "Microsoft YaHei UI";
    }

    private int HeaderH => (int)Math.Round(30 * _scale);
    private int FooterH => (int)Math.Round(52 * _scale);
    private int RowH => (int)Math.Round(38 * _scale);
    private int Pad => (int)Math.Round(16 * _scale);
    private int ContentTop => HeaderH;
    private int ContentHeight => Math.Max(0, ClientSize.Height - HeaderH - FooterH);

    // 只有第一条特别大，其余全是小而灰的次级信息
    private float RowScale(int index) => index == 0 ? 1.42f : 0.80f;

    private int RowHeightFor(int index) => (int)Math.Round(RowH * RowScale(index));

    // 第 index 行之前的累计高度（不含滚动偏移）
    private int RowOffset(int index)
    {
        int n = Math.Min(index, _items.Count);
        int y = 0;
        for (int i = 0; i < n; i++)
        {
            y += RowHeightFor(i);
        }

        return y;
    }

    private int TotalHeight
    {
        get
        {
            int y = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                y += RowHeightFor(i);
            }

            return y;
        }
    }

    private int MaxScroll => Math.Max(0, TotalHeight - ContentHeight);

    private Rectangle CloseRect()
    {
        int size = (int)Math.Round(26 * _scale);
        return new Rectangle(ClientSize.Width - size - (int)Math.Round(10 * _scale),
            (HeaderH - size) / 2, size, size);
    }

    private Rectangle InputPill()
    {
        int margin = (int)Math.Round(10 * _scale);
        int gap = (int)Math.Round(12 * _scale);
        return new Rectangle(margin, ClientSize.Height - FooterH + gap,
            ClientSize.Width - margin * 2, FooterH - gap * 2);
    }

    private void BuildUi()
    {
        // 一个 2 像素宽的原生编辑框藏在光标处：输入法、上屏、光标位置都交给它，
        // 画面上一个字都不显示，文字全由窗口自己画
        _input.BorderStyle = BorderStyle.None;
        // TextBox 不接受半透明底色，这里给它一个不透明的近似色
        _input.BackColor = Color.FromArgb(60, 60, 68);
        _input.ForeColor = TextBright;
        _input.TabStop = false;
        _input.TextChanged += (_, _) =>
        {
            string next = _input.Text;
            TrackGlyph(next);
            _entry = next;
            _caret = Math.Max(0, Math.Min(_input.SelectionStart, _entry.Length));
            // 上屏就代表组词结束了，这里必须清掉，否则拼音会一直浮在输入框里
            _composition = string.Empty;
            AfterEdit();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddFromInput();
            }
        };
        Controls.Add(_input);

        // 让编辑框全透明：它得占住真实的光标位置，输入法才能算出候选框该跟着哪儿走，
        // 但一个字都不能露出来，画面上的字全由窗口自己画
        _input.HandleCreated += (_, _) => HideInputChrome();

        Application.AddMessageFilter(_imeFilter = new ImeFilter(this));
    }

    private void HideInputChrome()
    {
        if (!_input.IsHandleCreated)
        {
            return;
        }

        // 编辑框只当输入法用的锚点：真正的位置和插入点交给它，画面一个字都不让它画。
        // 用空区域把它的绘制整个裁掉，比调透明度可靠（子窗口设不了 WS_EX_LAYERED）
        if (_input.Region == null)
        {
            _input.Region = new Region(Rectangle.Empty);
        }

        if (_imeHook == null)
        {
            _imeHook = new ImeContextHook();
            _imeHook.AssignHandle(_input.Handle);
        }
    }

    // 输入法会给本线程塞一个 MSCTFIME Composition 白底窗自己数组词，
    // 我们已经在卡片里自己画了，这个窗必须按下去，不然它永远糊在输入框上
    private void HideImeCompositionWindow()
    {
        _ = EnumThreadWindows(GetCurrentThreadId(), (hwnd, lParam) =>
        {
            var cls = new System.Text.StringBuilder(64);
            _ = GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() == "MSCTFIME Composition")
            {
                // 只在显示的时候按下去不够，它下一次组词又会自己冒出来，
                // 直接把它的窗口区域清空，之后怎么 ShowWindow 都画不出东西
                IntPtr empty = CreateRectRgn(0, 0, 0, 0);
                _ = SetWindowRgn(hwnd, empty, true);
                _ = ShowWindow(hwnd, SwHide);
            }

            return true;
        }, IntPtr.Zero);
    }

    internal bool HandleImeMessage(ref Message m)
    {
        if (m.HWnd != _input.Handle)
        {
            return false;
        }

        switch (m.Msg)
        {
            case WmImeSetContext:
                // 组词的字由窗口自己画，输入法那个白底组词窗必须关掉，不然会糊在卡片上
                m.LParam = (IntPtr)(m.LParam.ToInt64() & ~IscShowUiComposition);
                break;
            case WmImeStartComposition:
                _composition = ReadImeComposition();
                UpdateImePosition();
                HideImeCompositionWindow();
                _shake = Math.Min(1.2f, _shake + 0.35f);
                _punch = Math.Min(1.1f, _punch + 0.25f);
                KickFx();
                Invalidate();
                break;

            case WmImeEndComposition:
                _composition = string.Empty;
                Invalidate();
                break;

            case WmImeComposition:
                // 直接问输入法要当前组词，别自己攒状态，攒了就会留下删不掉的幽灵拼音
                {
                    string next = ReadImeComposition();
                    if (next.Length > _composition.Length && next.Length > 0)
                    {
                        // 打中文也一样要抖：每多一个字就砸一下
                        SpawnGlyph(next[next.Length - 1], EntryCaretPoint().X, EntryCaretPoint().Y,
                            ColorFor(_hueTick + 2), 1.35f);
                    }

                    _composition = next;
                    UpdateImePosition();
                    HideImeCompositionWindow();
                    Invalidate();
                }

                break;
        }

        return false;
    }

    private string ReadImeComposition()
    {
        if (!_input.IsHandleCreated)
        {
            return string.Empty;
        }

        IntPtr himc = ImmGetContext(_input.Handle);
        if (himc == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            int bytes = ImmGetCompositionStringW(himc, GcsCompStr, null!, 0);
            if (bytes <= 0)
            {
                return string.Empty;
            }

            var buffer = new System.Text.StringBuilder(bytes / 2 + 1);
            _ = ImmGetCompositionStringW(himc, GcsCompStr, buffer, bytes);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            _ = ImmReleaseContext(_input.Handle, himc);
        }
    }

    // 组词状态以输入法为准：它说没有就一定是没有，防止残留
    private void RefreshComposition()
    {
        if (_composition.Length == 0)
        {
            return;
        }

        _composition = ReadImeComposition();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style |= WsThickFrame;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subIdList);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool HideCaret(IntPtr hwnd);

    private delegate bool EnumThreadProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadProc callback, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buffer, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr himc, ref CompositionForm form);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionFont(IntPtr himc, ref LogFont lf);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCandidateWindow(IntPtr himc, ref CandidateForm form);

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        public int Index;
        public int Style;
        public int X;
        public int Y;
        public int AreaLeft;
        public int AreaTop;
        public int AreaRight;
        public int AreaBottom;
    }

    [DllImport("imm32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(IntPtr himc, int index, System.Text.StringBuilder buffer, int length);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionForm
    {
        public int Style;
        public int CurrentPosX;
        public int CurrentPosY;
        public int AreaLeft;
        public int AreaTop;
        public int AreaRight;
        public int AreaBottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyGlass();
        HideInputChrome();
    }

    private void ApplyGlass()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, 4);

            int corner = DwmWcpRound;
            _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, 4);
        }
        catch
        {
            // 圆角只是外观，拿不到就算了
        }
    }

    private void HookEvents()
    {
        Load += (_, _) =>
        {
            ApplyScale();
            ApplySavedBounds();
            _input.Focus();
            Invalidate();
        };

        Shown += (_, _) =>
        {
            ApplyGlass();

            if (Build.DebugUi)
            {
                // 调试版：谁都拍得到，底子是实心色，不掺任何抓屏
                _ = SetWindowDisplayAffinity(Handle, WdaNone);
                BuildSolidBackdrop();
                PinTopMost();
                _pinTimer.Start();
                Invalidate();
                return;
            }

            CaptureBackdrop();
            _blurTimer.Start();
            PinTopMost();
            _pinTimer.Start();
            Invalidate();
        };

        Activated += (_, _) =>
        {
            PinTopMost();
            _caretOn = true;
            _caretTimer.Start();
            Invalidate();
        };

        Deactivate += (_, _) =>
        {
            _caretTimer.Stop();
            _caretOn = false;
            Invalidate();
        };

        _caretTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            if (_input.IsHandleCreated)
            {
                _ = HideCaret(_input.Handle);
            }

            Invalidate(FieldRect());
        };

        _blurTimer.Tick += (_, _) => CaptureBackdrop();
        _fxTimer.Tick += (_, _) => OnFxTick();

        _pinTimer.Tick += (_, _) =>
        {
            // 组词期间不能再往上顶，一顶就把输入法的候选框压到背后去了
            if (_composition.Length == 0 && (GetWindowLong(Handle, GwlExStyle) & WsExTopMost) == 0)
            {
                PinTopMost();
            }

            SyncRemoteMode();
        };

        Move += (_, _) =>
        {
            // 抖动时窗口每帧都在挪，这时候抓背景既慢又没意义
            if (_winMoving)
            {
                return;
            }

            _winHeld = false;
            _winBase = Location;
            CaptureBackdrop();
        };
        Resize += (_, _) => RecalcLayout();
        DpiChanged += (_, _) =>
        {
            ApplyScale();
            RecalcLayout();
        };

        FormClosing += (_, _) =>
        {
            _pinTimer.Stop();
            _blurTimer.Stop();
            _fxTimer.Stop();
            SaveData();
        };
    }

    private void PinTopMost()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _ = SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    // 每行字号不一样，字体会一直新建，按字号缓存住
    private readonly Dictionary<int, Font> _rowFonts = new();

    private Font RowFont(int index)
    {
        int px = Math.Max(9, (int)Math.Round(16 * _scale * RowScale(index)));
        if (!_rowFonts.TryGetValue(px, out Font? font))
        {
            font = new Font(FontFamily, px, FontStyle.Bold, GraphicsUnit.Pixel);
            _rowFonts[px] = font;
        }

        return font;
    }

    private void ApplyScale()
    {
        _scale = DeviceDpi / 96f;
        float s = _scale;

        _fItem?.Dispose();
        _fBig?.Dispose();

        foreach (Font cached in _rowFonts.Values)
        {
            cached.Dispose();
        }

        _rowFonts.Clear();

        // 字要够粗够壮，细体在这块玻璃上根本看不清
        _fItem = new Font(FontFamily, 16f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _fBig = new Font(FontFamily, 36f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        _input.Font = _fItem;
        HideInputChrome();

        MinimumSize = new Size((int)Math.Round(200 * s), (int)Math.Round(130 * s));
        RecalcLayout();
    }

    private void RecalcLayout()
    {
        if (_fItem == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        ClampScroll();
        SyncCaretScroll();

        if (Build.DebugUi)
        {
            BuildSolidBackdrop();
        }
        PositionInput();
        UpdateImePosition();
        Invalidate();
    }

    private void PositionInput()
    {
        if (_fItem == null || !IsHandleCreated)
        {
            return;
        }

        Rectangle field = FieldRect();

        // 这个编辑框只当输入法锚点用：它宽要够，输入法才能算出真实的插入点，候选框才跟着走；
        // 它高要极薄，输入法自带的那个白底组词窗才不会糊在卡片上
        _input.SetBounds(field.Left, field.Bottom - 3, Math.Max(1, field.Width), 3);

        int caret = Math.Max(0, Math.Min(_caret, _input.TextLength));
        if (_input.SelectionStart != caret)
        {
            _input.SelectionStart = caret;
        }
    }

    private Rectangle FieldRect()
    {
        Rectangle pill = InputPill();
        int inset = (int)Math.Round(14 * _scale);
        return new Rectangle(pill.X + inset, pill.Y,
            Math.Max(10, pill.Width - inset * 2), pill.Height);
    }

    private int CaretX()
    {
        if (_caret <= 0 || _fItem == null)
        {
            return 0;
        }

        string head = _entry.Substring(0, Math.Min(_caret, _entry.Length));
        return TextRenderer.MeasureText(head, _fItem,
            new Size(int.MaxValue, 0),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
    }

    // 输入法的组词、候选框都要钉在这个点上，不能让它自己往屏幕左上角跑
    private PointF EntryCaretPoint()
    {
        Rectangle field = FieldRect();
        float x = field.Left + CaretX() - Math.Max(0, _fieldScroll);
        if (_fItem != null && _composition.Length > 0)
        {
            x += MeasureWidth(_composition, _fItem);
        }

        x = Math.Min(field.Right - 2, Math.Max(field.Left, x));
        return new PointF(x, field.Top + field.Height / 2f);
    }

    private void SyncCaretScroll()
    {
        if (_fItem == null || FieldRect().Width <= 0)
        {
            return;
        }

        int width = FieldRect().Width - (int)Math.Round(4 * _scale);
        int caret = CaretX();
        if (caret - _fieldScroll > width)
        {
            _fieldScroll = caret - width;
        }
        else if (caret < _fieldScroll)
        {
            _fieldScroll = Math.Max(0, caret);
        }

        if (_fieldScroll < 0)
        {
            _fieldScroll = 0;
        }
    }

    private void UpdateImePosition()
    {
        if (!IsHandleCreated || _fItem == null || !_input.IsHandleCreated)
        {
            return;
        }

        PointF caret = EntryCaretPoint();
        var pill = InputPill();

        // 输入法窗口必须跟着光标走，否则它会跑到屏幕左上角去显示那个古老的小框
        foreach (IntPtr hwnd in new[] { _input.Handle, Handle })
        {
            IntPtr himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                Point origin = hwnd == Handle
                    ? Point.Empty
                    : new Point(_input.Left, _input.Top);
                int cx = Math.Max(0, (int)caret.X - origin.X);
                int cy = Math.Max(0, (int)caret.Y - origin.Y);

                var form = new CompositionForm
                {
                    // CFS_RECT | CFS_POINT | CFS_FORCE_POSITION
                    Style = 0x0023,
                    CurrentPosX = cx,
                    CurrentPosY = cy,
                    AreaLeft = pill.Left - origin.X,
                    AreaTop = pill.Top - origin.Y,
                    AreaRight = pill.Right - origin.X,
                    AreaBottom = pill.Bottom - origin.Y,
                };
                _ = ImmSetCompositionWindow(himc, ref form);

                // 候选字窗贴着输入框下沿，跟着光标横着走；exclude 区把整条输入框挖掉，
                // 输入法就不会反过来盖在字上面
                int candY = pill.Bottom + (int)Math.Round(6 * _scale) - origin.Y;
                for (int i = 0; i < 4; i++)
                {
                    var candidate = new CandidateForm
                    {
                        Index = i,
                        // CFS_CANDIDATEPOS | CFS_EXCLUDE
                        Style = 0x0040 | 0x0080,
                        X = cx,
                        Y = Math.Max(0, candY),
                        AreaLeft = pill.Left - origin.X - 2,
                        AreaTop = pill.Top - origin.Y - 2,
                        AreaRight = pill.Right - origin.X + 2,
                        AreaBottom = pill.Bottom - origin.Y + 2,
                    };
                    _ = ImmSetCandidateWindow(himc, ref candidate);
                }

                var logFont = new LogFont
                {
                    Height = -_fItem.Height,
                    Weight = 700,
                    CharSet = 1,
                    FaceName = FontFamily,
                };
                _ = ImmSetCompositionFont(himc, ref logFont);
            }
            finally
            {
                _ = ImmReleaseContext(hwnd, himc);
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 不画背景，让系统亚克力材质透出来
    }

    // 沿边上取几个点平均，免得边上一根白线直接穿进玻璃里
    private static int SampleRing(int x, int y, bool horizontal, int w, int h, Bitmap src)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int i = -2; i <= 2; i++)
        {
            int sx = horizontal ? Math.Max(0, Math.Min(w - 1, x + i)) : Math.Max(0, Math.Min(w - 1, x));
            int sy = horizontal ? Math.Max(0, Math.Min(h - 1, y)) : Math.Max(0, Math.Min(h - 1, y + i));
            Color c = src.GetPixel(sx, sy);
            r += c.R;
            g += c.G;
            b += c.B;
            n++;
        }

        return Color.FromArgb(r / n, g / n, b / n).ToArgb();
    }

    private static void BoxBlur(Bitmap bmp, int radius)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        var src = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                src[y * w + x] = bmp.GetPixel(x, y).ToArgb();
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            var dst = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = Math.Max(0, Math.Min(h - 1, y + dy));
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = Math.Max(0, Math.Min(w - 1, x + dx));
                            Color c = Color.FromArgb(src[yy * w + xx]);
                            r += c.R;
                            g += c.G;
                            b += c.B;
                            n++;
                        }
                    }

                    dst[y * w + x] = Color.FromArgb(r / n, g / n, b / n).ToArgb();
                }
            }

            Array.Copy(dst, src, dst.Length);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bmp.SetPixel(x, y, Color.FromArgb(src[y * w + x]));
            }
        }
    }

    private void ClampScroll()
    {
        _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll));
        if (_hoverRow >= _items.Count)
        {
            _hoverRow = -1;
        }
    }

    // 本地：窗口排除在抓屏之外，抓到的就是它背后真实的画面，一点不改。
    // 远控：那边得看见这张卡，不能排除，于是把卡占的那块用四周颜色补掉。
    private void CaptureBackdrop()
    {
        if (Build.DebugUi)
        {
            return;
        }

        if (!IsHandleCreated || _capturing || !Visible || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        Rectangle client = ClientRectangle;
        if (client.Width < 8 || client.Height < 8)
        {
            return;
        }

        ApplyCaptureAffinity();

        int sw = Math.Max(8, client.Width / 18);
        int sh = Math.Max(8, client.Height / 18);

        _capturing = true;
        try
        {
            if (_smallShot == null || _smallShot.Width != sw || _smallShot.Height != sh)
            {
                _smallShot?.Dispose();
                _smallShot = new Bitmap(sw, sh);
            }

            if (_remoteMode)
            {
                FillSmear(client, sw, sh);
            }
            else
            {
                FillBehind(client);
            }

            // 缩小再放大就是模糊，比逐像素卷积快得多
            var blur = new Bitmap(client.Width, client.Height);
            using (Graphics g = Graphics.FromImage(blur))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_smallShot, 0, 0, blur.Width, blur.Height);
            }

            Bitmap? old = _backdrop;
            _backdrop = blur;
            old?.Dispose();
            _backdropLum = AverageLuminance(_smallShot);
            ApplyForeground();
            Invalidate();
        }
        catch
        {
            // 抓屏失败就沿用上一帧
        }
        finally
        {
            _capturing = false;
        }
    }

    // 调试版的实心底：照样给出一张「底图」和一份小图，
    // 行的配色、前景亮度这些逻辑还能照常跑起来
    private void BuildSolidBackdrop()
    {
        Rectangle client = ClientRectangle;
        if (client.Width < 8 || client.Height < 8 || !IsHandleCreated)
        {
            return;
        }

        int sw = Math.Max(8, client.Width / 18);
        int sh = Math.Max(8, client.Height / 18);

        _smallShot?.Dispose();
        _smallShot = new Bitmap(sw, sh);
        using (Graphics g = Graphics.FromImage(_smallShot))
        {
            using var solid = new SolidBrush(DebugBackdrop);
            g.FillRectangle(solid, 0, 0, sw, sh);
        }

        var blur = new Bitmap(client.Width, client.Height);
        using (Graphics g = Graphics.FromImage(blur))
        {
            using var solid = new SolidBrush(DebugBackdrop);
            g.FillRectangle(solid, 0, 0, blur.Width, blur.Height);
        }

        Bitmap? old = _backdrop;
        _backdrop = blur;
        old?.Dispose();
        _backdropLum = AverageLuminance(_smallShot);
        ApplyForeground();
        Invalidate();
    }

    // 被排除在抓屏之外，抓到的就是卡背后真实的画面
    private void FillBehind(Rectangle client)
    {
        if (_rawShot == null || _rawShot.Width != client.Width || _rawShot.Height != client.Height)
        {
            _rawShot?.Dispose();
            _rawShot = new Bitmap(client.Width, client.Height);
        }

        Point origin = PointToScreen(Point.Empty);
        using (Graphics g = Graphics.FromImage(_rawShot))
        {
            g.CopyFromScreen(origin.X, origin.Y, 0, 0, _rawShot.Size, CopyPixelOperation.SourceCopy);
        }

        using Graphics small = Graphics.FromImage(_smallShot!);
        small.InterpolationMode = InterpolationMode.HighQualityBilinear;
        small.PixelOffsetMode = PixelOffsetMode.Half;
        small.DrawImage(_rawShot, 0, 0, _smallShot!.Width, _smallShot.Height);
    }

    // 卡也在画面里，中间那块拿四周四条边插值补掉，不然后面就是自己的鬼影
    private void FillSmear(Rectangle client, int sw, int sh)
    {
        int margin = Math.Max(24, (int)Math.Round(44 * _scale));
        int rw = client.Width + margin * 2;
        int rh = client.Height + margin * 2;

        if (_rawShot == null || _rawShot.Width != rw || _rawShot.Height != rh)
        {
            _rawShot?.Dispose();
            _rawShot = new Bitmap(rw, rh);
        }

        // 屏幕边上顺着裁一下，省得抓出黑边
        Point origin = PointToScreen(Point.Empty);
        Rectangle screen = SystemInformation.VirtualScreen;
        int cx = Math.Max(screen.Left, Math.Min(screen.Right - rw, origin.X - margin));
        int cy = Math.Max(screen.Top, Math.Min(screen.Bottom - rh, origin.Y - margin));
        using (Graphics g = Graphics.FromImage(_rawShot))
        {
            g.CopyFromScreen(cx, cy, 0, 0, _rawShot.Size, CopyPixelOperation.SourceCopy);
        }

        int offX = Math.Max(0, Math.Min(rw - client.Width, origin.X - cx));
        int offY = Math.Max(0, Math.Min(rh - client.Height, origin.Y - cy));

        var left = new int[sh];
        var right = new int[sh];
        var top = new int[sw];
        var bottom = new int[sw];
        int ringX = Math.Max(1, offX / 2);
        int ringY = Math.Max(1, offY / 2);
        int ringR = Math.Max(1, (rw - offX - client.Width) / 2);
        int ringB = Math.Max(1, (rh - offY - client.Height) / 2);

        for (int y = 0; y < sh; y++)
        {
            int py = Math.Min(rh - 1, offY + (int)((y + 0.5f) * client.Height / sh));
            left[y] = SampleRing(ringX, py, true, rw, rh, _rawShot);
            right[y] = SampleRing(rw - 1 - ringR, py, true, rw, rh, _rawShot);
        }

        for (int x = 0; x < sw; x++)
        {
            int px = Math.Min(rw - 1, offX + (int)((x + 0.5f) * client.Width / sw));
            top[x] = SampleRing(px, ringY, false, rw, rh, _rawShot);
            bottom[x] = SampleRing(px, rh - 1 - ringB, false, rw, rh, _rawShot);
        }

        for (int y = 0; y < sh; y++)
        {
            Color l = Color.FromArgb(left[y]);
            Color r = Color.FromArgb(right[y]);
            float v = (y + 0.5f) / sh;
            for (int x = 0; x < sw; x++)
            {
                Color t = Color.FromArgb(top[x]);
                Color b = Color.FromArgb(bottom[x]);
                float u = (x + 0.5f) / sw;
                _smallShot!.SetPixel(x, y, Color.FromArgb(
                    (int)((l.R * (1f - u) + r.R * u + t.R * (1f - v) + b.R * v) / 2f),
                    (int)((l.G * (1f - u) + r.G * u + t.G * (1f - v) + b.G * v) / 2f),
                    (int)((l.B * (1f - u) + r.B * u + t.B * (1f - v) + b.B * v) / 2f)));
            }
        }

        // 边上跨过的颜色是硬跳的，抹两道才像糊过
        BoxBlur(_smallShot!, 2);
    }

    private void ApplyCaptureAffinity()
    {
        uint mode = _remoteMode ? WdaNone : WdaExcludeFromCapture;
        if (_appliedAffinity == mode)
        {
            return;
        }

        _appliedAffinity = mode;
        _ = SetWindowDisplayAffinity(Handle, mode);
    }

    private void SyncRemoteMode()
    {
        bool remote = RemoteViewerRunning();
        if (remote == _remoteMode)
        {
            return;
        }

        _remoteMode = remote;
        CaptureBackdrop();
    }

    private static bool RemoteViewerRunning()
    {
        try
        {
            // 只有真正的远程桌面会话一定在抓屏；本机常驻的远控客户端不算，
            // 不然装个 UU远程 就永远看不到背后的画面了
            return GetSystemMetrics(SmRemoteSession) != 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_fItem == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        RenderAll(e.Graphics);
    }

    // 背景亮就换深色字，背景暗就用浅色字，玻璃本身不上任何色
    private void ApplyForeground()
    {
        if (_backdropLum > 0.55f)
        {
            _fg = Color.FromArgb(24, 24, 28);
            _fgDone = Color.FromArgb(98, 98, 106);
            _fgSoft = Color.FromArgb(150, 40, 40, 48);
            _fgEdge = Color.FromArgb(34, 0, 0, 0);
        }
        else
        {
            _fg = TextBright;
            _fgDone = TextDone;
            _fgSoft = Color.FromArgb(165, 235, 235, 245);
            _fgEdge = Color.FromArgb(46, 255, 255, 255);
        }
    }

    // 背景亮就换深色字，背景暗就用浅色字
    private static float AverageLuminance(Bitmap bmp)
    {
        float sum = 0;
        int count = 0;
        for (int y = 0; y < bmp.Height; y += 2)
        {
            for (int x = 0; x < bmp.Width; x += 2)
            {
                Color c = bmp.GetPixel(x, y);
                sum += (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 255f;
                count++;
            }
        }

        return count == 0 ? 0f : sum / count;
    }

    // 某块区域背后的平均色，用来算字该发什么颜色
    private Color RegionColor(Rectangle region)
    {
        if (_smallShot == null)
        {
            return Color.FromArgb(128, 128, 128);
        }

        const int shrink = 18;
        int x0 = Math.Max(0, region.Left / shrink);
        int y0 = Math.Max(0, region.Top / shrink);
        int x1 = Math.Min(_smallShot.Width - 1, region.Right / shrink);
        int y1 = Math.Min(_smallShot.Height - 1, region.Bottom / shrink);
        if (x1 < x0 || y1 < y0)
        {
            return Color.FromArgb(128, 128, 128);
        }

        long r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Color c = _smallShot.GetPixel(x, y);
                r += c.R;
                g += c.G;
                b += c.B;
                n++;
            }
        }

        return n == 0 ? Color.FromArgb(128, 128, 128) : Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static float Lum(Color c)
    {
        return (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 255f;
    }

    // 背景色的对面色相 + 拉满饱和度 + 亮度往反方向推：数学上离背景最远、又还认得出的颜色
    private static Color MakeVivid(Color hueSource, Color under)
    {
        float bgLum = Lum(under);
        float hue = hueSource.GetHue() / 360f;

        // 背景有颜色就绕着它的对面上色，色相还是让它摆得开，不至于每个字都一个色
        if (under.GetSaturation() > 0.12f)
        {
            float bgHue = under.GetHue() / 360f;
            hue = (bgHue + 0.5f + (hue - 0.5f) * 0.65f + 1f) % 1f;
        }

        float l = bgLum > 0.62f ? 0.28f : bgLum < 0.34f ? 0.76f : 0.56f;
        return FromHsl(hue, 1f, l);
    }

    private static Color FromHsl(float h, float s, float l)
    {
        if (s <= 0f)
        {
            int v = (int)Math.Round(l * 255);
            return Color.FromArgb(v, v, v);
        }

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        return Color.FromArgb(
            (int)Math.Round(Hue(p, q, h + 1f / 3f) * 255),
            (int)Math.Round(Hue(p, q, h) * 255),
            (int)Math.Round(Hue(p, q, h - 1f / 3f) * 255));
    }

    private static float Hue(float p, float q, float t)
    {
        if (t < 0f)
        {
            t += 1f;
        }

        if (t > 1f)
        {
            t -= 1f;
        }

        if (t < 1f / 6f)
        {
            return p + (q - p) * 6f * t;
        }

        if (t < 0.5f)
        {
            return q;
        }

        return t < 2f / 3f ? p + (q - p) * (2f / 3f - t) * 6f : p;
    }

    private void RenderAll(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Rectangle client = ClientRectangle;

        // 背景先画一份不抖的，抖动的重心全靠它兜底，边上不会露空
        if (_backdrop != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_backdrop, client);
        }
        else
        {
            using var solid = new SolidBrush(FallbackBg);
            g.FillRectangle(solid, client);
        }

        float dx = 0f;
        float dy = 0f;
        bool transformed = false;
        _shakeZoom = 1f;
        if (_shake > 0.01f || _punch > 0.01f)
        {
            float amp = 13f * _scale * _shake;
            dx = (float)(_rng.NextDouble() * 2 - 1) * amp;
            dy = (float)(_rng.NextDouble() * 2 - 1) * amp * 0.9f;

            // 整体再顶一下，像被砸得往里一缩
            float zoom = 1f + 0.05f * _punch;
            g.TranslateTransform(client.Width / 2f + dx, client.Height / 2f + dy);
            g.ScaleTransform(zoom, zoom);
            g.TranslateTransform(-client.Width / 2f, -client.Height / 2f);
            transformed = true;

            // GDI 画的字不吃 Graphics 的变换，得照同一位移自己挪一遍
            _shakeDx = dx;
            _shakeDy = dy;
            _shakeZoom = zoom;
            _shaking = true;

            if (_backdrop != null)
            {
                g.DrawImage(_backdrop, client);
            }
        }
        else
        {
            _shaking = false;
        }

        PaintHeader(g);
        PaintRows(g);
        PaintFooter(g);
        PaintBursts(g);

        if (transformed)
        {
            g.ResetTransform();
        }

        // 边框不跟着抖，玻璃的边始终钉在原处
        if (_backdrop != null)
        {
            using GraphicsPath edge = Rounded(
                new RectangleF(0.5f, 0.5f, client.Width - 1, client.Height - 1), 11 * _scale);
            using var line = new Pen(_fgEdge, 1f);
            g.DrawPath(line, edge);
        }
    }

    private void PaintHeader(Graphics g)
    {
        float s = _scale;
        Rectangle close = CloseRect();

        if (_closeHover)
        {
            using var hover = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
            using GraphicsPath path = Rounded(Inflate(close, -2), 5 * s);
            g.FillPath(hover, path);
        }

        float cx = close.Left + close.Width / 2f;
        float cy = close.Top + close.Height / 2f;
        float d = 4.6f * s;
        using (var pen = new Pen(_fgSoft, Math.Max(1.3f, 1.4f * s)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
        }
    }

    private void PaintRows(Graphics g)
    {
        float s = _scale;
        int top = ContentTop;
        int bottom = top + ContentHeight;

        if (_items.Count == 0)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        int[] tops = new int[_items.Count + 1];
        for (int i = 0; i < _items.Count; i++)
        {
            tops[i + 1] = tops[i] + RowHeightFor(i);
        }

        // 每行高度不同，视口里第一行要按累计高度二分找，不能直接除
        int lo = 0, hi = _items.Count - 1, firstIdx = _items.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (tops[mid + 1] > _scroll)
            {
                firstIdx = Math.Min(firstIdx, mid);
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        int dragIndex = _dragging && _dragItem != null ? _items.IndexOf(_dragItem) : -1;

        // 分两遍画：静止的行先画，浮起来的那一行最后画，永远压在最上层
        for (int pass = 0; pass < 2; pass++)
        for (int i = firstIdx; i < _items.Count; i++)
        {
            bool dragging = i == dragIndex;
            if ((pass == 0) == dragging)
            {
                continue;
            }

            TodoItem item = _items[i];
            int rowHeight = tops[i + 1] - tops[i];
            int rowTop = top + tops[i] - _scroll;

            if (rowTop > bottom)
            {
                break;
            }

            if (rowTop + rowHeight < top)
            {
                continue;
            }

            // 蓄力时整行左右痉挛
            float jitter = 0f;
            if (_pressHeld && ReferenceEquals(item, _pressItem))
            {
                float amp = (_pressKind == PressKind.Delete ? 3.4f : 2.6f) * s * _press;
                jitter = (float)Math.Sin(now * 58) * amp;
            }

            // 拖着排序的那一行：真的浮起来跟着手走，落到哪儿一眼就能看出来
            int drawTop = rowTop;
            if (dragging)
            {
                // 抬离列表 6px，跟槽位错开，一眼能看出是"拿在手上"的
                int lift = (int)Math.Round(6 * s);
                drawTop = Math.Max(top, Math.Min(bottom - rowHeight, _dragY - rowHeight / 2 - lift));
            }

            var rowRect = new Rectangle((int)jitter, drawTop, ClientSize.Width, rowHeight);

            if (dragging)
            {
                Color accent = ColorFor(Math.Max(0, _items.IndexOf(item)));

                // 卡片上下各多探出 4px，把贴着的邻居压住，不然会露出半截字
                int pad2 = (int)Math.Round(4 * s);
                using GraphicsPath liftPath = Rounded(
                    new RectangleF(4 * s, drawTop - pad2, ClientSize.Width - 8 * s,
                        rowHeight + pad2 * 2),
                    9 * s);

                // 影子
                using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                using (GraphicsPath shadowPath = Rounded(
                    new RectangleF(7 * s, drawTop + 6 * s, ClientSize.Width - 14 * s, rowHeight - 2 * s),
                    9 * s))
                {
                    g.FillPath(shadow, shadowPath);
                }

                // 实心底：盖住下面那行，看着才像拿在手上的一张卡，而不是透过去的字
                using (var solid = new SolidBrush(Color.FromArgb(252, 28, 28, 36)))
                {
                    g.FillPath(solid, liftPath);
                }

                using (var lift = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                {
                    g.FillPath(lift, liftPath);
                }

                using var edge = new Pen(Color.FromArgb(190, accent), Math.Max(1.4f, 1.9f * s));
                g.DrawPath(edge, liftPath);

                // 落点指示：槽位顶上一条粗线，松手就插这儿
                using var mark = new Pen(Color.FromArgb(240, accent), Math.Max(2f, 3f * s));
                mark.StartCap = LineCap.Round;
                mark.EndCap = LineCap.Round;
                g.DrawLine(mark, 6 * s, rowTop + 1 * s, ClientSize.Width - 6 * s, rowTop + 1 * s);
            }
            else if (i == _hoverRow && item.Wipe <= 0f)
            {
                using var hover = new SolidBrush(Color.FromArgb(24, 255, 255, 255));
                using GraphicsPath path = Rounded(
                    new RectangleF((int)(6 * s) + rowRect.X, drawTop + (int)(1 * s),
                        ClientSize.Width - (int)(12 * s), rowHeight - (int)(2 * s)), 7 * s);
                g.FillPath(hover, path);
            }

            GraphicsState rowState = g.Save();
            float wipe = item.Wipe;
            if (wipe > 0f)
            {
                int wipeX = (int)(ClientSize.Width * wipe);
                g.SetClip(new Rectangle(wipeX, drawTop - 2, ClientSize.Width - wipeX, rowHeight + 4),
                    CombineMode.Intersect);
            }

            bool charging = _pressHeld && ReferenceEquals(item, _pressItem);
            RectangleF box = CheckboxRect(rowRect);
            bool chargingDone = charging && _pressKind == PressKind.Done;
            if (chargingDone)
            {
                float grow = 1f + 0.22f * _press;
                box = new RectangleF(
                    box.X + box.Width * (1 - grow) / 2f,
                    box.Y + box.Height * (1 - grow) / 2f,
                    box.Width * grow, box.Height * grow);
            }

            Color rowColor = ColorFor(i);

            // 长按的蓄力直接画在空框里：从底往上填满，不绕圈
            DrawCheckbox(g, box, item.Done, item.Strike, rowColor, chargingDone ? _press : 0f);

            float textLeft = box.Right + 11 * s;
            int deleteZone = (int)Math.Round(30 * s);
            var textRect = new Rectangle((int)textLeft, drawTop,
                Math.Max(0, ClientSize.Width - (int)textLeft - Pad - deleteZone), rowHeight);

            // 扫除时把文字按剩余宽度截断，TextRenderer 不认 Graphics 的裁剪区
            Color textColor = RowTextColor(textRect, i, item.Strike);
            if (wipe > 0f)
            {
                // 整行字先画进一张小位图，再只贴出扫帚扫过之后剩下的那截，
                // 这样字是被真切开的，不会半个字挂在原地不动
                int wipeX = (int)(ClientSize.Width * wipe);
                int cut = wipeX - textRect.Left;
                if (textRect.Width > 0 && cut < textRect.Width)
                {
                    int bh = Math.Max(1, textRect.Height);
                    if (_wipeBmp == null || _wipeBmp.Width != textRect.Width || _wipeBmp.Height != bh)
                    {
                        _wipeBmp?.Dispose();
                        _wipeBmp = new Bitmap(textRect.Width, bh);
                    }

                    using (Graphics layer = Graphics.FromImage(_wipeBmp))
                    {
                        layer.Clear(Color.Transparent);
                        layer.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        DrawRowText(layer, item.Text, textColor, item.Strike,
                            new Rectangle(0, 0, _wipeBmp.Width, bh), true, false, RowFont(i));
                    }

                    int sx = Math.Max(0, cut);
                    int sw2 = _wipeBmp.Width - sx;
                    if (sw2 > 0)
                    {
                        g.DrawImage(_wipeBmp,
                            new Rectangle(textRect.Left + sx, textRect.Top, sw2, bh),
                            new Rectangle(sx, 0, sw2, bh),
                            GraphicsUnit.Pixel);
                    }
                }
            }
            else
            {
                DrawRowText(g, item.Text, textColor, item.Strike, textRect, false, true, RowFont(i));
            }

            g.Restore(rowState);

            DrawDeleteGlyph(g, rowRect, item, charging && _pressKind == PressKind.Delete);

            if (wipe > 0f && wipe < 1f)
            {
                float x = ClientSize.Width * wipe;
                using var bar = new SolidBrush(Color.FromArgb((int)(200 * (1 - wipe)), rowColor));
                g.FillRectangle(bar, x - 2 * s, drawTop + 4 * s, 3 * s, rowHeight - 8 * s);
            }
        }

        if (MaxScroll > 0)
        {
            float trackTop = top + 4 * s;
            float trackHeight = ContentHeight - 8 * s;
            float thumbHeight = Math.Max(24 * s, trackHeight * (ContentHeight / (float)TotalHeight));
            float thumbTop = trackTop + (trackHeight - thumbHeight) * (_scroll / (float)MaxScroll);
            float x = ClientSize.Width - 7 * s;
            using var thumb = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
            using GraphicsPath path = Rounded(new RectangleF(x, thumbTop, 3.5f * s, thumbHeight), 2 * s);
            g.FillPath(thumb, path);
        }
    }

    private void DrawDeleteGlyph(Graphics g, Rectangle rowRect, TodoItem item, bool charging)
    {
        float s = _scale;
        float size = 18 * s * Math.Min(1.16f, Math.Max(0.86f, rowRect.Height / Math.Max(1f, RowH)));
        float cx = ClientSize.Width - Pad - size / 2f - 4 * s;
        float cy = rowRect.Top + rowRect.Height / 2f;
        float d = (3.6f + (charging ? 1.4f * _press : 0f)) * s;

        int alpha;
        if (item.Wipe > 0f)
        {
            alpha = 0;
        }
        else if (charging)
        {
            alpha = 255;
        }
        else
        {
            alpha = ReferenceEquals(HoverItem(), item) ? 150 : 80;
        }

        if (alpha <= 0)
        {
            return;
        }

        Color color = charging ? Danger : _fgSoft;
        using (var pen = new Pen(Color.FromArgb(Math.Min(255, alpha), color), Math.Max(1.2f, 1.35f * s)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
        }

        if (charging)
        {
            var ring = new RectangleF(cx - size * 0.72f, cy - size * 0.72f, size * 1.44f, size * 1.44f);
            using var pen = new Pen(Color.FromArgb((int)(70 + 185 * _press), Danger), Math.Max(1.6f, 2.2f * s));
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, ring, -90f, 360f * Math.Min(1f, _press));
        }
    }

    private RectangleF CheckboxRect(Rectangle rowRect)
    {
        float s = _scale;

        // 字号往上变大的时候，勾选框也跟着长一点，不然大字的行配个小框很别扭
        float k = Math.Min(1.16f, Math.Max(0.86f, rowRect.Height / Math.Max(1f, RowH)));
        float size = 18 * s * k;
        return new RectangleF(Pad, rowRect.Top + (rowRect.Height - size) / 2f, size, size);
    }

    // 行被删掉以后 _hoverRow 会指到后面去，别再直接拿它当下标
    private TodoItem? HoverItem()
    {
        if (_hoverRow < 0 || _hoverRow >= _items.Count)
        {
            return null;
        }

        return _items[_hoverRow];
    }

    private void DrawCheckbox(Graphics g, RectangleF box, bool done, float strike, Color accent,
        float charge = 0f)
    {
        float s = _scale;
        using GraphicsPath path = Rounded(box, 5 * s);
        float t = Math.Min(1f, Math.Max(0f, strike));
        float ch = Math.Min(1f, Math.Max(0f, charge));

        if (t > 0.02f)
        {
            int alpha = (int)(255 * Math.Min(1f, t * 1.6f));
            using var fill = new SolidBrush(Color.FromArgb(alpha, accent));
            g.FillPath(fill, path);

            if (t > 0.35f)
            {
                float p = (t - 0.35f) / 0.65f;
                using var pen = new Pen(Color.FromArgb((int)(255 * Math.Min(1f, p * 3f)), Color.White), 2f * s)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                PointF a = new(box.Left + 4.4f * s, box.Top + 9.2f * s);
                PointF b = new(box.Left + 7.7f * s, box.Top + 12.6f * s);
                PointF c = new(box.Left + 13.7f * s, box.Top + 5.6f * s);

                if (p < 0.5f)
                {
                    float k = p * 2f;
                    g.DrawLine(pen, a, new PointF(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k));
                }
                else
                {
                    float k = (p - 0.5f) * 2f;
                    g.DrawLine(pen, a, b);
                    g.DrawLine(pen, b, new PointF(b.X + (c.X - b.X) * k, b.Y + (c.Y - b.Y) * k));
                }
            }
        }

        if (t < 0.35f)
        {
            // 长按蓄力：空框从底下往上被灌满，灌满那一下才打勾
            if (ch > 0.005f)
            {
                GraphicsState state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                float filled = box.Height * ch;
                using var wash = new SolidBrush(Color.FromArgb((int)(70 + 185 * ch), accent));
                g.FillRectangle(wash, box.X, box.Bottom - filled, box.Width, filled);
                g.Restore(state);
            }

            using var pen = new Pen(_fgSoft, Math.Max(1.2f, 1.35f * s));
            g.DrawPath(pen, path);

            if (ch > 0.005f)
            {
                using var hot = new Pen(Color.FromArgb((int)(255 * ch), accent),
                    Math.Max(1.4f, 1.7f * s));
                g.DrawPath(hot, path);
            }
        }
    }

    private void PaintFooter(Graphics g)
    {
        float s = _scale;
        Rectangle pill = InputPill();

        using (var pen = new Pen(Color.FromArgb(16, 255, 255, 255)))
        {
            g.DrawLine(pen, Pad, ClientSize.Height - FooterH + 0.5f,
                ClientSize.Width - Pad, ClientSize.Height - FooterH + 0.5f);
        }

        using (GraphicsPath path = Rounded(pill, 8 * s))
        {
            using var fill = new SolidBrush(FieldFill);
            g.FillPath(fill, path);
        }

        PaintEntry(g);
    }

    private void PaintEntry(Graphics g)
    {
        RefreshComposition();

        Rectangle field = FieldRect();
        int width = field.Width - (int)Math.Round(2 * _scale);
        if (field.Width <= 0 || width <= 0)
        {
            return;
        }

        string display = _entry.Substring(0, Math.Min(_caret, _entry.Length))
            + _composition
            + _entry.Substring(Math.Min(_caret, _entry.Length));

        // TextRenderer 根本不理 Graphics 的裁剪区，先画进和输入框一样大的小位图再贴上去，
        // 这样字打多了也绝不会溢出到框外去；关掉抗锯齿，笔画像像素块
        const int half = 1;
        int lw = Math.Max(1, width / half);
        int lh = Math.Max(1, field.Height / half);
        if (_entryBmp == null || _entryBmp.Width != lw || _entryBmp.Height != lh)
        {
            _entryBmp?.Dispose();
            _entryBmp = new Bitmap(lw, lh);
        }

        using (Graphics layer = Graphics.FromImage(_entryBmp))
        {
            layer.Clear(Color.Transparent);
            layer.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            layer.SmoothingMode = SmoothingMode.None;
            layer.PixelOffsetMode = PixelOffsetMode.Half;

            Font font = _fItem;
            // 输入框背后那块的平均色决定这里用什么颜色：反差最大的那个
            Color entryColor = MakeVivid(ColorFor(_hueTick), RegionColor(field));
            int x = -_fieldScroll / half;
            if (display.Length == 0)
            {
                if (_caretOn)
                {
                    DrawCaretAt(layer, x, (lh - font.Height) / 2f, font);
                }
            }
            else
            {
                // 字就画自己，不再套一圈描边
                DrawTextFlat(layer, display, font, entryColor,
                    new Rectangle(x, 0, MeasureWidth(display, font) + 4, lh),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                if (_composition.Length > 0)
                {
                    string head = display.Substring(0, Math.Max(0, _caret));
                    int start = x + MeasureWidth(head, font);
                    int end = start + MeasureWidth(_composition, font);
                    int y = lh - (int)Math.Round(4 * _scale);
                    using var pen = new Pen(entryColor, Math.Max(1.5f, _scale * 0.9f));
                    layer.DrawLine(pen, start, y, end, y);
                }

                if (_caretOn)
                {
                    DrawCaretAt(layer, x + MeasureWidth(
                        display.Substring(0, Math.Max(0, _caret) + _composition.Length), font),
                        (lh - font.Height) / 2f, font);
                }
            }
        }

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_entryBmp, new Rectangle(field.Left, field.Top, lw * half, lh * half));
    }

    private int MeasureWidth(string text)
    {
        return MeasureWidth(text, _fItem);
    }

    private int MeasureWidth(string text, Font? font)
    {
        if (text.Length == 0 || font == null)
        {
            return 0;
        }

        return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 0),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
    }

    private void DrawCaret(Graphics g, float x)
    {
        Rectangle field = FieldRect();
        DrawCaretAt(g, x, (field.Height - (_fItem?.Height ?? 16)) / 2f, _fItem);
    }

    private void DrawCaretAt(Graphics g, float x, float top, Font? font)
    {
        float height = font?.Height ?? 16;
        using var pen = new Pen(_fg, Math.Max(1f, 1.4f * _scale));
        g.DrawLine(pen, x, top, x, top + height);
    }

    // 每一行的字都按这行背后那块的平均色，配一个最扎眼的彩色
    private Color RowTextColor(Rectangle sampleRect, int index, float strike)
    {
        Color under = RegionColor(sampleRect);

        // 第一条是主角，颜色最艳；后面全部退成灰，只当好认的次级信息
        Color color = index == 0
            ? MakeVivid(ColorFor(0), under)
            : Lum(under) > 0.5f
                ? Color.FromArgb(96, 96, 104)
                : Color.FromArgb(148, 148, 158);

        float t = Math.Min(1f, Math.Max(0f, strike));
        if (t <= 0.01f)
        {
            return color;
        }

        // 划掉的字不留颜色：先褪成死灰，再按划掉的程度往背景里化，看着就是透明度掉下去
        Color dead = Blend(_fgDone, under, 0.35f);
        return Blend(color, dead, t);
    }

    private void DrawRowText(Graphics g, string text, Color color, float strike, Rectangle rect,
        bool cropped, bool shift = true, Font? font = null)
    {
        float t = Math.Min(1f, Math.Max(0f, strike));
        Font useFont = font ?? _fItem;

        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
        if (!cropped)
        {
            flags |= TextFormatFlags.EndEllipsis;
        }

        // 不描边了：亮背景上那圈白边会变成怪光晕，字就画自己
        DrawTextFlat(g, text, useFont, color, shift ? ShiftByShake(rect) : rect, flags);

        if (t <= 0.01f)
        {
            return;
        }

        // 划线长度跟着文字实际宽度走，别划到空白里
        int width = MeasureWidth(text, useFont);
        if (cropped)
        {
            width = Math.Min(width, rect.Width);
        }

        int y = rect.Top + rect.Height / 2;
        using var pen = new Pen(Color.FromArgb(230, color), Math.Max(1f, _scale * 1.4f));
        g.DrawLine(pen, rect.Left, y, rect.Left + width * t, y);
    }

    // 抖动时字要跟卡片一起晃，但 GDI 字不吃变换，只能自己算位移
    private Rectangle ShiftByShake(Rectangle rect)
    {
        if (!_shaking)
        {
            return rect;
        }

        float cx = ClientSize.Width / 2f;
        float cy = ClientSize.Height / 2f;
        return new Rectangle(
            (int)Math.Round(cx + (rect.X - cx) * _shakeZoom + _shakeDx),
            (int)Math.Round(cy + (rect.Y - cy) * _shakeZoom + _shakeDy),
            rect.Width,
            rect.Height);
    }

    private static Color Blend(Color a, Color b, float t)
    {
        t = Math.Min(1f, Math.Max(0f, t));
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // 字只画自己：不描边、不投影。白边压在亮背景上会变成一圈怪光晕
    private static void DrawTextFlat(Graphics g, string text, Font font, Color color,
        Rectangle rect, TextFormatFlags flags)
    {
        TextFormatFlags final = flags | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, text, font, rect, color, final);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static RectangleF Inflate(Rectangle rect, float amount)
    {
        return new RectangleF(rect.X + amount, rect.Y + amount,
            Math.Max(1, rect.Width - amount * 2), Math.Max(1, rect.Height - amount * 2));
    }

    private enum PressKind
    {
        None,
        Done,
        Delete,
    }

    private sealed class Burst
    {
        public char Glyph;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Life;
        public float Max = 0.52f;
        public float Scale0 = 1.9f;
        public float Scale1 = 0.85f;
        public float Rot;
        public Color Color;
        public bool Particle;
        public bool Ring;
        public float Size;
        public float Jitter;
        public float Wobble;
        public float Spin;
        public float FadeStart = 0.55f;
    }

    private static Color ColorFor(int index)
    {
        return Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];
    }

    // 像素大字的字号是按帧变的，缓存起来别每次都 new Font
    private readonly Dictionary<int, Font> _glyphFonts = new();

    private Font GlyphFont(float pixelSize)
    {
        int key = Math.Max(16, (int)(Math.Round(pixelSize / 4f) * 4));
        if (!_glyphFonts.TryGetValue(key, out Font? font))
        {
            font = new Font(FontFamily, key, FontStyle.Bold, GraphicsUnit.Pixel);
            _glyphFonts[key] = font;
        }

        return font;
    }

    private Color RowColor(TodoItem item)
    {
        int index = _items.IndexOf(item);
        return ColorFor(index < 0 ? 0 : index);
    }

    // 像素大字：字先画进一张小 1 位图当模具，再烤成带黑边的彩色版，最后整数倍最近邻放大
    private readonly Dictionary<char, Bitmap> _glyphCache = new();
    private readonly Dictionary<long, Bitmap> _tintCache = new();

    private Bitmap GlyphSprite(char c)
    {
        if (_glyphCache.TryGetValue(c, out Bitmap? cached))
        {
            return cached;
        }

        // 汉字笔画密，格子给大、少叠几次，不然糊成一坨方块
        bool cjk = c >= 0x2E80;
        int box = cjk ? 30 : 24;
        var bmp = new Bitmap(box, box);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var font = new Font(FontFamily, cjk ? box - 3 : box - 4, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            g.DrawString(c.ToString(), font, brush, new RectangleF(0, 0, box, box), format);

            // 拉丁字母笔画少，同一个字补画几遍把笔画撑粗，放大之后才够块
            if (!cjk)
            {
                foreach (PointF offset in new[]
                {
                    new PointF(-0.8f, 0f), new PointF(0.8f, 0f),
                    new PointF(0f, -0.8f), new PointF(0f, 0.8f),
                })
                {
                    g.DrawString(c.ToString(), font, brush,
                        new RectangleF(offset.X, offset.Y, box, box), format);
                }
            }
        }

        if (_glyphCache.Count > 256)
        {
            foreach (Bitmap old in _glyphCache.Values)
            {
                old.Dispose();
            }

            _glyphCache.Clear();
        }

        _glyphCache[c] = bmp;
        return bmp;
    }

    // 模具 -> 带黑描边、已经染好色和透明度的成品，动画里直接贴，不再算颜色矩阵
    private Bitmap TintedGlyph(char c, Color color, int alphaStep)
    {
        long key = ((long)c << 32) | ((uint)color.ToArgb() & 0xFFFFFF) | ((long)alphaStep << 40);
        if (_tintCache.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap mask = GlyphSprite(c);
        int pad = 1;
        int size = mask.Width + pad * 2;
        var bmp = new Bitmap(size, size);
        int alpha = (int)Math.Round(255 * alphaStep / 7f);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var src = new Rectangle(0, 0, mask.Width, mask.Height);

            for (int oy = -pad; oy <= pad; oy++)
            {
                for (int ox = -pad; ox <= pad; ox++)
                {
                    if (ox == 0 && oy == 0)
                    {
                        continue;
                    }

                    DrawTinted(g, mask, new Rectangle(pad + ox, pad + oy, mask.Width, mask.Height),
                        src, Color.Black, (int)(alpha * 0.92f));
                }
            }

            DrawTinted(g, mask, new Rectangle(pad, pad, mask.Width, mask.Height), src, color, alpha);
        }

        if (_tintCache.Count > 600)
        {
            foreach (Bitmap old in _tintCache.Values)
            {
                old.Dispose();
            }

            _tintCache.Clear();
        }

        _tintCache[key] = bmp;
        return bmp;
    }

    private static void DrawTinted(Graphics g, Bitmap mask, Rectangle dest, Rectangle src, Color color, int alpha)
    {
        using var brushed = new Bitmap(mask.Width, mask.Height);
        for (int y = 0; y < mask.Height; y++)
        {
            for (int x = 0; x < mask.Width; x++)
            {
                int a = mask.GetPixel(x, y).A;
                brushed.SetPixel(x, y, a == 0
                    ? Color.Transparent
                    : Color.FromArgb(Math.Min(255, a * alpha / 255), color));
            }
        }

        g.DrawImage(brushed, dest, src, GraphicsUnit.Pixel);
    }

    private void KickFx()
    {
        if (_fxTimer.Enabled)
        {
            return;
        }

        _lastFx = (float)_clock.Elapsed.TotalSeconds;
        _fxTimer.Start();
    }

    private void OnFxTick()
    {
        float now = (float)_clock.Elapsed.TotalSeconds;
        float dt = Math.Min(0.05f, Math.Max(0.001f, now - _lastFx));
        _lastFx = now;
        bool alive = false;

        if (_pressHeld && _pressItem != null)
        {
            float dur = _pressKind == PressKind.Delete ? 0.70f : 0.52f;
            _press = Math.Min(1f, _press + dt / dur);
            alive = true;
            if (_press >= 1f)
            {
                FirePress();
            }
        }
        else if (_press > 0f)
        {
            _press = Math.Max(0f, _press - dt / 0.12f);
            if (_press > 0f)
            {
                alive = true;
            }
        }

        foreach (TodoItem item in _items)
        {
            float target = item.Done ? 1f : 0f;
            if (Math.Abs(item.Strike - target) > 0.004f)
            {
                item.Strike += (target - item.Strike) * Math.Min(1f, dt * 17f);
                if (Math.Abs(item.Strike - target) <= 0.012f)
                {
                    item.Strike = target;
                }

                alive = true;
            }

            if (item.Wipe > 0f)
            {
                item.Wipe = Math.Min(1f, item.Wipe + dt / 0.15f);
                alive = true;
            }
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Wipe >= 1f)
            {
                _items.RemoveAt(i);
                SaveData();
                ClampScroll();
            }
        }

        if (_shake > 0.005f)
        {
            // 抖得久一点，字飞出去的整段路都还在晃
            _shake *= (float)Math.Pow(0.03f, dt);
            alive = true;
        }
        else
        {
            _shake = 0f;
        }

        // 按住不动攒够时间才进入拖动排序
        if (_dragArmed && !_dragging)
        {
            _dragHold += dt;
            alive = true;
            if (_dragHold >= 0.32f)
            {
                _dragging = true;
                _shake = Math.Min(1.35f, _shake + 0.75f);
                _punch = Math.Min(1.2f, _punch + 0.5f);
                SpawnShock(ClientSize.Width / 2f, _dragY, ColorFor(Math.Max(0, _items.IndexOf(_dragItem!))), 0.9f, 1.2f);
                Invalidate();
            }
        }

        // 拖到上下边缘自己滚
        if (_dragging)
        {
            alive = true;
            int edge = (int)Math.Round(26 * _scale);
            int viewTop = ContentTop;
            int viewBottom = ContentTop + ContentHeight;
            if (_dragY < viewTop + edge)
            {
                _scroll = Math.Max(0, _scroll - (int)Math.Round(dt * 620 * _scale));
            }
            else if (_dragY > viewBottom - edge)
            {
                _scroll = Math.Min(MaxScroll, _scroll + (int)Math.Round(dt * 620 * _scale));
            }
        }

        ShakeWindow();

        if (_punch > 0.005f)
        {
            _punch *= (float)Math.Pow(0.012f, dt);
            alive = true;
        }
        else
        {
            _punch = 0f;
        }

        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            Burst b = _bursts[i];
            b.Life += dt;
            if (b.Life >= b.Max)
            {
                _bursts.RemoveAt(i);
            }
            else
            {
                alive = true;
            }
        }

        Invalidate();
        if (!alive)
        {
            _fxTimer.Stop();
        }
    }

    // 窗口本身也跟着抖：按当前抖幅整像素地甩出去，抖完再放回原位
    private void ShakeWindow()
    {
        // 拖着排序的时候窗口别晃，不然手上那行会跟着飘
        if (!IsHandleCreated || WindowState != FormWindowState.Normal || _dragging)
        {
            return;
        }

        if (_shake <= 0.02f)
        {
            if (_winHeld)
            {
                _winMoving = true;
                Location = _winBase;
                _winMoving = false;
                _winHeld = false;
                CaptureBackdrop();
            }

            return;
        }

        if (!_winHeld)
        {
            _winBase = Location;
            _winHeld = true;
        }

        int amp = (int)Math.Round(6 * _scale * Math.Min(1.2f, _shake));
        if (amp < 1)
        {
            // 抖到只剩亚像素了就别让它歪着不动，立刻回原位
            if (_winHeld)
            {
                _winMoving = true;
                Location = _winBase;
                _winMoving = false;
                _winHeld = false;
                CaptureBackdrop();
            }

            return;
        }

        _winMoving = true;
        Location = new Point(
            _winBase.X + _rng.Next(-amp, amp + 1),
            _winBase.Y + _rng.Next(-amp, amp + 1));
        _winMoving = false;
    }

    private void SpawnGlyph(char c, float x, float y, Color color, float power,
        float size = 1f, float drift = 1f, float endRatio = 0f)
    {
        if (char.IsWhiteSpace(c))
        {
            return;
        }

        float s = _scale;
        float margin = 70 * s;
        x = Math.Max(margin, Math.Min(ClientSize.Width - margin, x));
        _hueTick++;

        // 起跳点背后是什么颜色，就用数学上离它最远的颜色，永远扎眼
        Color under = RegionColor(new Rectangle((int)x - 40, (int)y - 30, 80, 60));
        Color glyphColor = MakeVivid(color, under);

        _bursts.Add(new Burst
        {
            Glyph = c,
            // 起跳点本身也撒开一点，连打的时候才不是一摞字叠在一起
            X = x + (float)(_rng.NextDouble() * 16 - 8) * s,
            Y = y + (float)(_rng.NextDouble() * 8 - 4) * s,
            // 左右交替往两边甩，一个笔画一个方向，飞起来是扇面不是串糖葫芦
            Vx = _glyphSide * (52 + (float)_rng.NextDouble() * 78) * s * drift,
            Vy = -(96 + (float)_rng.NextDouble() * 86) * s * drift,
            Max = (0.58f + (float)_rng.NextDouble() * 0.22f) * (1f + (size - 1f) * 0.35f)
                * (drift > 0f ? 1f : 0.55f),
            Scale0 = (1.58f + (float)_rng.NextDouble() * 0.3f) * size,
            Scale1 = endRatio > 0f
                ? (1.58f + 0.15f) * size * endRatio
                : (0.82f + (float)_rng.NextDouble() * 0.12f) * (1f + (size - 1f) * 0.5f),
            Rot = (float)(_rng.NextDouble() * 24 - 12),
            Color = glyphColor,
            // 飞出去的一路自己也在抖，不是只在起跳那一下才晃
            Jitter = (4f + (float)_rng.NextDouble() * 4f) * s * Math.Min(1.4f, power) * (1f + (size - 1f) * 0.6f),
            Wobble = 11f + (float)_rng.NextDouble() * 9f,
            Spin = (float)(_rng.NextDouble() * 2 - 1) * 90f * drift,
            // 原地不动的（勾和叉）先亮一下再淡，淡的那段只占三成，衰减够快
            FadeStart = drift > 0f ? 0.55f : 0.68f,
        });

        _glyphSide = -_glyphSide;

        // 打太快的时候只留最近几个字，不然满屏都是字看不清清单
        int glyphs = 0;
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            if (_bursts[i].Particle || _bursts[i].Ring)
            {
                continue;
            }

            glyphs++;
            if (glyphs > 4)
            {
                _bursts.RemoveAt(i);
            }
        }

        SpawnShockAt(x, y, under, glyphColor, power, size);
    }

    // 冲击波本体：两圈环 + 一圈粒子 + 抖一下。
    // 往输入框打一个字是它，字落进清单也是它，只是后者不放飞字
    private void SpawnShock(float x, float y, Color color, float power, float size = 1f)
    {
        float s = _scale;
        float margin = 70 * s;
        x = Math.Max(margin, Math.Min(ClientSize.Width - margin, x));
        _hueTick++;

        Color under = RegionColor(new Rectangle((int)x - 40, (int)y - 30, 80, 60));
        SpawnShockAt(x, y, under, MakeVivid(color, under), power, size);
    }

    private void SpawnShockAt(float x, float y, Color under, Color glyphColor, float power, float size)
    {
        float s = _scale;

        // 两圈冲击波，一粗一细，砸下去才有"咚"的回响
        float ringScale = 1f + (size - 1f) * 0.45f;
        _bursts.Add(new Burst
        {
            Ring = true,
            X = x,
            Y = y,
            Max = 0.36f,
            Scale0 = 0.3f * ringScale,
            Scale1 = 2.6f * ringScale,
            Color = glyphColor,
        });

        _bursts.Add(new Burst
        {
            Ring = true,
            X = x,
            Y = y,
            Max = 0.26f,
            Scale0 = 0.18f * ringScale,
            Scale1 = 1.4f * ringScale,
            Color = Color.White,
        });

        for (int i = 0; i < 26; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2;
            double speed = (170 + _rng.NextDouble() * 380) * s * (1f + (size - 1f) * 0.6f);
            _bursts.Add(new Burst
            {
                Particle = true,
                X = x,
                Y = y,
                Vx = (float)Math.Cos(angle) * (float)speed,
                Vy = (float)Math.Sin(angle) * (float)speed - 80 * s,
                Max = (float)(0.36 + _rng.NextDouble() * 0.34),
                Size = (float)(3.2 + _rng.NextDouble() * 5.6) * s * (1f + (size - 1f) * 0.7f),
                Color = _rng.Next(3) == 0 ? Color.White : MakeVivid(ColorFor(_hueTick + i), under),
            });
        }

        _shake = Math.Min(1.35f, _shake + power * 1.9f);
        _punch = Math.Min(1.2f, _punch + power * 1.4f);

        KickFx();
    }

    private void PaintBursts(Graphics g)
    {
        if (_bursts.Count == 0)
        {
            return;
        }

        foreach (Burst b in _bursts)
        {
            float k = Math.Min(1f, b.Life / b.Max);
            float ease = 1f - (1f - k) * (1f - k);
            float x = b.X + b.Vx * k;
            float y = b.Y + b.Vy * k;

            if (b.Particle)
            {
                y += 260 * k * k;
                int pa = (int)(235 * (1f - ease));
                if (pa <= 3)
                {
                    continue;
                }

                float sz = b.Size * (1f - ease * 0.45f);
                using var brush = new SolidBrush(Color.FromArgb(pa, b.Color));
                g.FillEllipse(brush, x - sz / 2f, y - sz / 2f, sz, sz);
                continue;
            }

            if (b.Ring)
            {
                float radius = (b.Scale0 + (b.Scale1 - b.Scale0) * ease) * 42 * _scale;
                int ra = (int)(225 * (1f - ease));
                if (ra <= 3)
                {
                    continue;
                }

                // 环越冲越细，尾巴拖出去像气浪
                float thick = b.Color == Color.White
                    ? 4.2f * _scale * (1f - ease * 0.7f)
                    : 9f * _scale * (1f - ease * 0.75f);
                using var pen = new Pen(Color.FromArgb(ra, b.Color), Math.Max(2f, thick));
                g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
                continue;
            }

            float scale = b.Scale0 + (b.Scale1 - b.Scale0) * ease;

            // 前半段全不透明，后半段才淡出，看着才砸得实
            float fs = Math.Min(0.9f, Math.Max(0f, b.FadeStart));
            float fade = k < fs ? 1f : 1f - (k - fs) / Math.Max(0.05f, 1f - fs);
            int a = (int)(255 * fade);
            if (a <= 4)
            {
                continue;
            }

            // 字自己也在高频抖，越往上飞抖得越凶
            if (b.Jitter > 0.01f)
            {
                float j = b.Jitter * (0.45f + k * 0.9f);
                x += (float)(_rng.NextDouble() * 2 - 1) * j;
                y += (float)(_rng.NextDouble() * 2 - 1) * j;
            }

            float angle = b.Rot + b.Wobble * (float)Math.Sin(b.Life * 52.0) + b.Spin * k;

            // 整数倍放大，方块才是方块；缩放动画落到格子上反而更有跳动感
            // 透明度不再量化成几档，直接按 alpha 矩阵连续衰减，渐隐才顺
            Bitmap sprite = TintedGlyph(b.Glyph, b.Color, 7);
            float unit = sprite.Width;
            int cells = Math.Max(2, (int)Math.Round(30 * _scale * scale / unit));
            float size = unit * cells;

            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(angle);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;

            float left = -size / 2f;
            float top = -size / 2f;
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = a / 255f });
            g.DrawImage(sprite, Quad(left, top, size),
                new RectangleF(0, 0, unit, unit), GraphicsUnit.Pixel, attrs);

            g.Restore(state);
        }
    }

    private static PointF[] Quad(float left, float top, float size)
    {
        return new[]
        {
            new PointF(left, top),
            new PointF(left + size, top),
            new PointF(left, top + size),
        };
    }

    private Rectangle RowRectFor(int index)
    {
        int height = RowHeightFor(index);
        return new Rectangle(0, ContentTop - _scroll + RowOffset(index), ClientSize.Width, height);
    }

    private Rectangle DeleteRect(int index)
    {
        Rectangle row = RowRectFor(index);
        int width = (int)Math.Round(34 * _scale);
        return new Rectangle(ClientSize.Width - Pad - width, row.Top, width, row.Height);
    }

    // 勾选框可点范围比画出来的框略大一圈，手指才好按
    private Rectangle CheckboxTouchRect(Rectangle rowRect)
    {
        RectangleF box = CheckboxRect(rowRect);
        RectangleF padded = RectangleF.Inflate(box, 4 * _scale, 6 * _scale);
        return Rectangle.Round(padded);
    }

    private void ArmDrag(TodoItem item, Point at)
    {
        if (item.Wipe > 0f)
        {
            return;
        }

        _dragItem = item;
        _dragArmed = true;
        _dragging = false;
        _dragDown = at;
        _dragY = at.Y;
        _dragHold = 0f;
        Capture = true;

        // 计时靠 FX 循环跑，得先把它叫醒，不然长按永远攒不满
        KickFx();
        Invalidate();
    }

    private void CancelDrag()
    {
        if (!_dragArmed && !_dragging)
        {
            return;
        }

        _dragArmed = false;
        _dragging = false;
        _dragItem = null;
        _dragHold = 0f;
        Capture = false;
        Invalidate();
    }

    // 把被拖的行挪到目标索引，其它行跟着让位
    private void MoveDragTo(int target)
    {
        if (_dragItem == null)
        {
            return;
        }

        int from = _items.IndexOf(_dragItem);
        if (from < 0)
        {
            CancelDrag();
            return;
        }

        target = Math.Max(0, Math.Min(_items.Count - 1, target));
        if (target == from)
        {
            return;
        }

        _items.RemoveAt(from);
        _items.Insert(target, _dragItem);
        SaveData();
        Invalidate();
    }

    private void StartPress(TodoItem item, PressKind kind)
    {
        if (item.Wipe > 0f)
        {
            return;
        }

        _pressItem = item;
        _pressKind = kind;
        _pressHeld = true;
        _press = 0f;
        Capture = true;
        KickFx();
        Invalidate();
    }

    private void ReleasePress()
    {
        if (!_pressHeld)
        {
            return;
        }

        _pressHeld = false;
        Capture = false;
        KickFx();
        Invalidate();
    }

    private void FirePress()
    {
        TodoItem? item = _pressItem;
        PressKind kind = _pressKind;
        _pressHeld = false;
        _press = 0f;
        _pressKind = PressKind.None;
        Capture = false;

        if (item == null)
        {
            KickFx();
            return;
        }

        int index = _items.IndexOf(item);
        if (index < 0)
        {
            KickFx();
            return;
        }

        if (kind == PressKind.Done)
        {
            item.Done = !item.Done;
            SaveData();

            // 勾和叉就钉在画面正中，抖一下原地淡掉，不往外飘
            SpawnGlyph(item.Done ? '✓' : '↺',
                ClientSize.Width / 2f, ClientSize.Height / 2f, ColorFor(index), 1.7f, 2.6f, 0f, 1.08f);
        }
        else if (kind == PressKind.Delete)
        {
            item.Wipe = 0.01f;
            SpawnGlyph('✕',
                ClientSize.Width / 2f, ClientSize.Height / 2f, Danger, 1.9f, 2.6f, 0f, 1.08f);
        }

        _pressItem = null;
        KickFx();
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmImeSetContext)
        {
            // 组词窗整条掐掉：字我们自己画，输入法那个白底窗只会糊在卡片上
            m.LParam = (IntPtr)(m.LParam.ToInt64() & ~IscShowUiComposition);
        }

        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
        {
            // 客户区铺满整个窗口，边框只留给缩放
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WmNcHitTest)
        {
            int lp = m.LParam.ToInt32();
            Point screenPoint = new((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            Point p = PointToClient(screenPoint);
            int grip = Math.Max(6, (int)Math.Round(8 * _scale));

            bool left = p.X <= grip;
            bool right = p.X >= ClientSize.Width - grip;
            bool top = p.Y <= grip;
            bool bottom = p.Y >= ClientSize.Height - grip;

            if (top && left) { m.Result = (IntPtr)13; return; }
            if (top && right) { m.Result = (IntPtr)14; return; }
            if (bottom && left) { m.Result = (IntPtr)16; return; }
            if (bottom && right) { m.Result = (IntPtr)17; return; }
            if (left) { m.Result = (IntPtr)10; return; }
            if (right) { m.Result = (IntPtr)11; return; }
            if (top) { m.Result = (IntPtr)12; return; }
            if (bottom) { m.Result = (IntPtr)15; return; }
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            Rectangle close = CloseRect();
            if (close.Contains(e.Location))
            {
                Close();
                return;
            }

            if (e.Y < HeaderH)
            {
                ReleaseCapture();
                _ = SendMessage(Handle, 0x00A1, (IntPtr)0x0002, IntPtr.Zero);
                return;
            }

            if (e.Y < ClientSize.Height - FooterH)
            {
                if (MaxScroll > 0 && e.X > ClientSize.Width - (int)(14 * _scale))
                {
                    _scrollDragging = true;
                    _scrollDragY = e.Y;
                    _scrollDragStart = _scroll;
                    return;
                }

                int index = RowAt(e.Y);
                if (index >= 0)
                {
                    TodoItem hit = _items[index];
                    Rectangle rowRect = RowRectFor(index);

                    // 勾选框长按打勾、✕ 长按删除，中间那块空白和文字长按是拖动排序
                    if (DeleteRect(index).Contains(e.Location))
                    {
                        StartPress(hit, PressKind.Delete);
                    }
                    else if (CheckboxTouchRect(rowRect).Contains(e.Location))
                    {
                        StartPress(hit, PressKind.Done);
                    }
                    else
                    {
                        ArmDrag(hit, e.Location);
                    }
                }

                _input.Focus();
                Invalidate();
            }
            else
            {
                PlaceCaretAt(e.X);
                UpdateImePosition();
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pressHeld)
        {
            int rowIndex = RowAt(e.Y);
            bool sameRow = rowIndex >= 0 && ReferenceEquals(_items[rowIndex], _pressItem);
            bool sameKind = rowIndex >= 0 && DeleteRect(rowIndex).Contains(e.Location) == (_pressKind == PressKind.Delete);
            if (!sameRow || !sameKind)
            {
                ReleasePress();
            }
        }

        bool closeHover = CloseRect().Contains(e.Location);
        if (closeHover != _closeHover)
        {
            _closeHover = closeHover;
            Invalidate();
        }

        int hover = e.Y >= ContentTop && e.Y < ClientSize.Height - FooterH ? RowAt(e.Y) : -1;
        if (hover != _hoverRow)
        {
            _hoverRow = hover;
            Invalidate();
        }

        if (_dragArmed)
        {
            int dx = Math.Abs(e.X - _dragDown.X);
            int dy = Math.Abs(e.Y - _dragDown.Y);

            // 还没到长按时长就挪开了，说明只是滑过，不当拖拽
            if (!_dragging && (dx > 10 || dy > 30))
            {
                CancelDrag();
            }
            else if (_dragging)
            {
                _dragY = e.Y;
                int target = RowAt(e.Y);
                if (target < 0 && _dragItem != null)
                {
                    int offset = e.Y - ContentTop + _scroll;
                    target = offset >= TotalHeight ? _items.Count - 1 : 0;
                }

                if (target >= 0)
                {
                    MoveDragTo(target);
                }
            }
        }

        if (_scrollDragging)
        {
            int view = Math.Max(1, ContentHeight);
            int delta = e.Y - _scrollDragY;
            int target = _scrollDragStart + (int)(delta * (TotalHeight / (float)view));
            _scroll = Math.Max(0, Math.Min(target, MaxScroll));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _scrollDragging = false;
        ReleasePress();

        if (_dragArmed)
        {
            if (_dragging)
            {
                // 落位时砸一下，顺手抖一抖
                SpawnShock(ClientSize.Width / 2f, e.Y, ColorFor(Math.Max(0, _items.IndexOf(_dragItem!))), 1.1f, 1.4f);
            }

            CancelDrag();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1 || _closeHover)
        {
            _hoverRow = -1;
            _closeHover = false;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxScroll <= 0)
        {
            return;
        }

        _scroll -= Math.Sign(e.Delta) * RowH;
        ClampScroll();
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (_dragArmed || _dragging)
            {
                CancelDrag();
                return true;
            }

            ClearEntry();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void AfterEdit()
    {
        // 原生输入框只用来收字，它自己的光标不能露出来
        if (_input.IsHandleCreated)
        {
            _ = HideCaret(_input.Handle);
        }

        _caretOn = true;
        _caretTimer.Stop();
        _caretTimer.Start();
        SyncCaretScroll();
        PositionInput();
        UpdateImePosition();

        if (_glyphChar >= 0)
        {
            char c = (char)_glyphChar;
            int pos = Math.Min(_glyphPos, _entry.Length);
            _glyphChar = -1;

            Rectangle field = FieldRect();
            float x = field.Left - _fieldScroll + MeasureWidth(_entry.Substring(0, pos));
            float y = field.Top - 10 * _scale;
            SpawnGlyph(c, x, y, ColorFor(_hueTick + 1), 1.15f);
        }

        Invalidate();
    }

    private void TrackGlyph(string next)
    {
        _glyphChar = -1;
        if (next.Length <= _entry.Length || !next.StartsWith(_entry, StringComparison.Ordinal))
        {
            // 删字、换行、整体替换都不放烟花
            return;
        }

        _glyphPos = _entry.Length;
        _glyphChar = next[_glyphPos];
    }

    private void PlaceCaretAt(int mouseX)
    {
        if (!_input.Focused)
        {
            _input.Focus();
        }

        Rectangle field = FieldRect();
        int x = mouseX - field.Left;
        int width = MeasureWidth(_entry);

        // 鼠标位置换算成字符索引：交给原生输入框算，省得自己量字形
        int index = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i <= _entry.Length; i++)
        {
            int distance = Math.Abs(MeasureWidth(_entry.Substring(0, i)) - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                index = i;
            }
        }

        _ = width;
        _input.SelectionStart = Math.Min(index, _input.TextLength);
        AfterEdit();
    }

    private int RowAt(int y)
    {
        int offset = y - ContentTop + _scroll;
        if (offset < 0)
        {
            return -1;
        }

        int acc = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            int h = RowHeightFor(i);
            if (offset < acc + h)
            {
                return i;
            }

            acc += h;
        }

        return -1;
    }

    private void AddFromInput()
    {
        string text = _entry.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _items.Add(new TodoItem { Text = text, Done = false, Strike = 0f });
        ClearEntry();
        SaveData();
        _scroll = MaxScroll;

        // 字砸进清单：和往输入框打字同款的冲击波，只是字本身不飞
        int index = _items.Count - 1;
        Rectangle row = RowRectFor(index);
        RectangleF box = CheckboxRect(row);
        float textLeft = box.Right + 11 * _scale;
        float midY = row.Top + row.Height / 2f;
        int hits = Math.Min(3, text.Length);
        for (int i = 0; i < hits; i++)
        {
            SpawnShock(textLeft + i * 15 * _scale, midY, ColorFor(index + i), 1.15f);
        }

        Invalidate();
    }

    private void ClearEntry()
    {
        _entry = string.Empty;
        _caret = 0;
        _composition = string.Empty;
        _fieldScroll = 0;
        _glyphChar = -1;
        _input.Clear();
        PositionInput();
        UpdateImePosition();
        Invalidate();
    }

    private void SaveData()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        // 抖动时窗口是被临时挪开的，存位置要存没抖的那个原点
        if (_winHeld)
        {
            bounds.X = _winBase.X;
            bounds.Y = _winBase.Y;
        }

        _data.X = bounds.X;
        _data.Y = bounds.Y;
        _data.W = bounds.Width;
        _data.H = bounds.Height;

        _data.Items = _items;
        Store.Save(_data);
    }

    private void ApplySavedBounds()
    {
        float s = _scale;
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;

        int? x = _data.X, y = _data.Y, w = _data.W, h = _data.H;
        bool valid = w is > 200 && h is > 160;
        Rectangle bounds = new(0, 0, (int)Math.Round(300 * s), (int)Math.Round(212 * s));

        if (valid)
        {
            bounds = new Rectangle(x ?? 0, y ?? 0, w!.Value, h!.Value);
        }
        else
        {
            bounds.X = area.Right - bounds.Width - (int)Math.Round(56 * s);
            bounds.Y = area.Top + (int)Math.Round(64 * s);
        }

        bool onScreen = false;
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (visible.Width >= 80 && visible.Height >= 80)
            {
                onScreen = true;
                break;
            }
        }

        if (!onScreen)
        {
            bounds.X = area.Right - bounds.Width - (int)Math.Round(56 * s);
            bounds.Y = area.Top + (int)Math.Round(64 * s);
        }

        bounds.Width = Math.Min(bounds.Width, area.Width);
        bounds.Height = Math.Min(bounds.Height, area.Height);
        Bounds = bounds;

        RecalcLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_imeFilter != null)
            {
                Application.RemoveMessageFilter(_imeFilter);
            }
            _pinTimer.Dispose();
            _caretTimer.Dispose();
            _blurTimer.Dispose();
            _fxTimer.Dispose();

            _backdrop?.Dispose();
            _rawShot?.Dispose();
            _smallShot?.Dispose();

            _fItem?.Dispose();
            _fBig?.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class ImeFilter : IMessageFilter
    {
        private readonly MainForm _owner;

        public ImeFilter(MainForm owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            return _owner.HandleImeMessage(ref m);
        }
    }

    // 输入法往编辑框发 WM_IME_SETCONTEXT 时，把"画自带组词窗"这一位掐掉：
    // 组词的字由窗口自己画，输入法那个白底小窗只会在卡片上糊一块
    private sealed class ImeContextHook : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmImeSetContext)
            {
                m.LParam = (IntPtr)(m.LParam.ToInt64() & ~IscShowUiComposition);
            }

            base.WndProc(ref m);
        }
    }
}
