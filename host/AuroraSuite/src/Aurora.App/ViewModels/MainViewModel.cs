using Aurora.Core.Device;
using Aurora.Core.Firmware;
using Aurora.Core.Imaging;
using Aurora.Core.Protocol;
using Aurora.Core.Serial;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Aurora.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // ---------- services ----------
    readonly DispatcherTimer _watchdog;
    DeviceSession? _session;
    BootloaderSession? _boot;
    AppSession? _app;

    public LogViewModel Log { get; } = new();

    readonly List<System.Windows.Input.ICommand> _stateCommands = new();

    void RegisterStateCommands()
    {
        foreach (var prop in GetType().GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (typeof(System.Windows.Input.ICommand).IsAssignableFrom(prop.PropertyType) &&
                prop.GetValue(this) is System.Windows.Input.ICommand cmd &&
                !_stateCommands.Contains(cmd))
            {
                _stateCommands.Add(cmd);
            }
        }
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraSuite", "settings.json");

    static string? LoadSavedPort()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("port", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    void SavePort()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                System.Text.Json.JsonSerializer.Serialize(new { port = SelectedPort, autoReconnect = AutoConnect }));
        }
        catch { }
    }

    // ---------- connection ----------
    public System.Collections.ObjectModel.ObservableCollection<string> Ports { get; } = new();
    string? _selectedPort;
    public string? SelectedPort
    {
        get => _selectedPort;
        set { if (Set(ref _selectedPort, value)) SavePort(); }
    }

    bool _autoConnect = false;
    public bool AutoConnect { get => _autoConnect; set { Set(ref _autoConnect, value); } }

    bool _connected;
    public bool IsConnected { get => _connected; private set { Set(ref _connected, value); RefreshStates(); } }

    string _modeText = "未连接";
    public string ModeText { get => _modeText; private set => Set(ref _modeText, value); }

    string _deviceText = "选择端口后自动连接";
    public string DeviceText { get => _deviceText; private set => Set(ref _deviceText, value); }

    string _statusLine = "就绪";
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    bool _busy;
    public bool Busy { get => _busy; private set { Set(ref _busy, value); RefreshStates(); } }

    double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    public bool IsBootloader => IsConnected && _session?.Mode == DeviceMode.Bootloader;
    public bool IsApp => IsConnected && _session?.Mode == DeviceMode.Application;

    // ---------- firmware ----------
    string _firmwarePath = "";
    public string FirmwarePath { get => _firmwarePath; private set => Set(ref _firmwarePath, value); }

    string _firmwareInfo = "未选择文件";
    public string FirmwareInfo { get => _firmwareInfo; private set => Set(ref _firmwareInfo, value); }

    // ---------- background ----------
    string _backgroundPath = "";
    public string BackgroundPath { get => _backgroundPath; private set => Set(ref _backgroundPath, value); }

    string _scaleMode = "拉伸（填满屏幕）";
    public string ScaleMode
    {
        get => _scaleMode;
        set { if (Set(ref _scaleMode, value)) { if (!string.IsNullOrEmpty(BackgroundPath)) SetBackground(BackgroundPath); } }
    }
    public string[] ScaleModes { get; } = { "等比（留黑边）", "拉伸（填满屏幕）", "裁剪（裁掉多余）" };

    string _backgroundInfo = "未选择图片";
    public string BackgroundInfo { get => _backgroundInfo; private set => Set(ref _backgroundInfo, value); }

    string _appImageInfo = "尚未读取";
    public string AppImageInfo { get => _appImageInfo; private set => Set(ref _appImageInfo, value); }

    ImageSource? _backgroundPreview;
    public ImageSource? BackgroundPreview { get => _backgroundPreview; private set => Set(ref _backgroundPreview, value); }

    // ---------- diagnostics ----------
    string _diagnostics = "尚未读取";
    public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }

    // ---------- fill color ----------
    string _fillColor = "#3CC8FF";
    public string FillColor { get => _fillColor; set => Set(ref _fillColor, value); }


    // ---------- debug frames ----------
    string _debugText = "Aurora LITE";
    public string DebugText { get => _debugText; set => Set(ref _debugText, value); }

    string _debugColor = "#3CC8FF";
    public string DebugColor { get => _debugColor; set => Set(ref _debugColor, value); }

    bool _streamBegan;

    /* ---- font debug canvas ---- */
    public sealed class TextItem : ObservableObject
    {
        double _x, _y;
        internal int LastX = -1, LastY = -1;

        int _previewSize = 16;
        /// <summary>Font size of the selected face, so the canvas matches the device width.</summary>
        public int PreviewSize { get => _previewSize; set => Set(ref _previewSize, value); }      /* where it was last drawn on the panel */
        string _text = "TEXT";
        int _face;
        string _color = "#3CC8FF";
        string _bg = "#0841";
        int _w, _h;

        public double X { get => _x; set { if (Set(ref _x, value)) OnPropertyChanged(nameof(Left)); } }
        public double Y { get => _y; set { if (Set(ref _y, value)) OnPropertyChanged(nameof(Top)); } }

        public double Left => _x;
        public double Top => _y;

        /// <summary>Cell size: when both > 0 the device clears this area first.</summary>
        public int W { get => _w; set => Set(ref _w, value); }
        public int H { get => _h; set => Set(ref _h, value); }

        /// <summary>Colour behind the text: alpha is blended against it on the device.</summary>
        public string BgHex { get => _bg; set => Set(ref _bg, value); }

        public string Text { get => _text; set => Set(ref _text, value); }
        public int Face
        {
            get => _face;
            set { if (Set(ref _face, value)) OnPropertyChanged(nameof(PreviewSize)); }
        }
        public string ColorHex
        {
            get => _color;
            set { if (Set(ref _color, value)) { OnPropertyChanged(nameof(Brush)); OnPropertyChanged(nameof(FontSize)); } }
        }

        public System.Windows.Media.Brush Brush
        {
            get
            {
                try { return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_color)!; }
                catch { return System.Windows.Media.Brushes.White; }
            }
        }

        public double FontSize => 20;
    }

    List<int> _faceSizes = new() { 12, 16, 20 };

    public System.Collections.ObjectModel.ObservableCollection<TextItem> TextItems { get; } = new()
    {
        new TextItem { X = 20, Y = 24, Text = "CPU 17%", ColorHex = "#3CC8FF" },
        new TextItem { X = 20, Y = 70, Text = "MEM 71%", ColorHex = "#E8EEF5" },
        new TextItem { X = 20, Y = 116, Text = "NET 64.3K", ColorHex = "#4ADE8C" },
    };

    TextItem? _selectedText;
    public TextItem? SelectedText { get => _selectedText; set => Set(ref _selectedText, value); }

    int _textCellW = 0;
    public int TextCellW { get => _textCellW; set => Set(ref _textCellW, value); }

    int _textCellH = 0;
    public int TextCellH { get => _textCellH; set => Set(ref _textCellH, value); }

    string _textBgHex = "#333333";
    public string TextBgHex { get => _textBgHex; set => Set(ref _textBgHex, value); }

    bool _autoPush = false;
    public bool AutoPush { get => _autoPush; set => Set(ref _autoPush, value); }

    string _fontDebugInfo = "拖动文字调整位置，松开鼠标即推送到屏幕";
    public string FontDebugInfo { get => _fontDebugInfo; private set => Set(ref _fontDebugInfo, value); }

    public AsyncCommand PushTextCommand { get; }
    public AsyncCommand PushTextsCommand { get; }
    public AsyncCommand ClearScreenCommand { get; }

    /* ---- font baking ---- */
    public System.Collections.ObjectModel.ObservableCollection<string> FontFamilies { get; } =
        new(System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(x => x));

    string _selectedFont = "Consolas";
    public string SelectedFont { get => _selectedFont; set => Set(ref _selectedFont, value); }

    public sealed class FaceItem : ObservableObject
    {
        int _index;
        string _family = "Consolas";
        int _size = 16;
        bool _bold;
        string _charset = "";

        public string Family { get => _family; set { if (Set(ref _family, value)) { OnPropertyChanged(nameof(Label)); } } }
        public int Size { get => _size; set { if (Set(ref _size, value)) { OnPropertyChanged(nameof(Label)); } } }
        public bool Bold { get => _bold; set { if (Set(ref _bold, value)) { OnPropertyChanged(nameof(Label)); } } }

        /// <summary>Characters baked for this face (filled with the default set on creation).</summary>
        public string Charset
        {
            get => _charset;
            set { if (Set(ref _charset, value)) OnPropertyChanged(nameof(Label)); }
        }

        public int Index
        {
            get => _index;
            set { if (Set(ref _index, value)) OnPropertyChanged(nameof(Label)); }
        }

        public string Label => $"[{_index}] {Family} {Size}px{(Bold ? " Bold" : "")} · {_charset.Distinct().Count()} 字";
    }

    public System.Collections.ObjectModel.ObservableCollection<FaceItem> Faces { get; } = new()
    {
        new FaceItem { Family = "Consolas", Size = 12, Charset = Rendering.GlyphBaker.DefaultCharset },
        new FaceItem { Family = "Consolas", Size = 16, Charset = Rendering.GlyphBaker.DefaultCharset },
        new FaceItem { Family = "Microsoft YaHei", Size = 20, Bold = true, Charset = Rendering.GlyphBaker.DefaultCharset },
    };

    void RenumberFaces()
    {
        for (int i = 0; i < Faces.Count; i++) Faces[i].Index = i;
    }

    FaceItem? _selectedFace;
    public FaceItem? SelectedFace
    {
        get => _selectedFace;
        set { if (Set(ref _selectedFace, value)) ShowFacePreview(); }
    }

    IReadOnlyList<ImageSource>? _facePreviews;

    void ShowFacePreview()
    {
        if (_facePreviews == null || SelectedFace == null) return;
        int i = Faces.IndexOf(SelectedFace);
        if (i >= 0 && i < _facePreviews.Count && _facePreviews.Count == Faces.Count)
            FontPreview = _facePreviews[i];
    }

    string _bakeCharset = Rendering.GlyphBaker.DefaultCharset;
    public string BakeCharset { get => _bakeCharset; set => Set(ref _bakeCharset, value); }

    int _bakeSize = 16;
    public int BakeSize { get => _bakeSize; set => Set(ref _bakeSize, value); }

    bool _bakeBold;
    public bool BakeBold { get => _bakeBold; set => Set(ref _bakeBold, value); }

    ImageSource? _fontPreview;
    public ImageSource? FontPreview { get => _fontPreview; private set => Set(ref _fontPreview, value); }

    string _fontInfo = "尚未烘焙";
    public string FontInfo { get => _fontInfo; private set => Set(ref _fontInfo, value); }

    byte[]? _lastFont;

    /* reuse everything in the hot path so a frame costs ~0 bytes of GC */
    readonly byte[] _prevFrame = new byte[320 * 172 * 2];
    bool _prevValid;

    readonly byte[] _rectBuf = new byte[320 * 172 * 2];
    readonly byte[] _rleBuf = new byte[320 * 172 * 3];
    readonly byte[] _frameBuf = new byte[96 * 1024];
    StreamPixelFormat _streamFormat = StreamPixelFormat.Rgb565Rle;

    bool _streaming;
    bool _projectScreen;
    /// <summary>When on, the desktop is captured and mirrored to the panel instead of the demo frames.</summary>
    public bool ProjectScreen { get => _projectScreen; set => Set(ref _projectScreen, value); }

    public const string ProjectionModeAuto = "自动（索引流优先）";
    public const string ProjectionModeRgb565 = "RGB565（高画质）";
    public const string ProjectionModeRgb332 = "RGB332 / 256 色（低带宽）";
    public IReadOnlyList<string> ProjectionColorModes { get; } =
        new[] { ProjectionModeAuto, ProjectionModeRgb565, ProjectionModeRgb332 };
    string _projectionColorMode = ProjectionModeAuto;
    public string ProjectionColorMode { get => _projectionColorMode; set => Set(ref _projectionColorMode, value); }

    readonly Aurora.App.Imaging.ScreenProjector _projector = new();
    readonly byte[] _projBuf = new byte[320 * 172 * 2];
    readonly byte[] _projIndexBuf = new byte[320 * 172];

    public bool Streaming
    {
        get => _streaming;
        private set { if (Set(ref _streaming, value) && _stateCommands.Count > 0) RefreshStates(); }
    }

    int _streamIntervalMs = 150;
    public int StreamIntervalMs { get => _streamIntervalMs; set => Set(ref _streamIntervalMs, value); }

    string _streamStats = "未开始";
    public string StreamStats { get => _streamStats; private set => Set(ref _streamStats, value); }

    ImageSource? _streamPreview;
    public ImageSource? StreamPreview { get => _streamPreview; private set => Set(ref _streamPreview, value); }

    CancellationTokenSource? _streamCts;

    ImageSource? _debugPreview;
    public ImageSource? DebugPreview { get => _debugPreview; private set => Set(ref _debugPreview, value); }

    string _debugInfo = "尚未生成画面";
    public string DebugInfo { get => _debugInfo; private set => Set(ref _debugInfo, value); }

    // ---------- commands ----------
    public System.Windows.Input.ICommand RefreshPortsCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public AsyncCommand FlashFirmwareCommand { get; }
    public AsyncCommand UploadBackgroundCommand { get; }
    public AsyncCommand UploadToAppCommand { get; }
    public AsyncCommand ReadAppImageCommand { get; }
    public AsyncCommand RedrawBackgroundCommand { get; }
    public AsyncCommand RunAppCommand { get; }
    public AsyncCommand SelfTestCommand { get; }
    public AsyncCommand ReadMetaCommand { get; }
    public AsyncCommand ToBootloaderCommand { get; }
    public AsyncCommand FillCommand { get; }
    public AsyncCommand CalibrationCommand { get; }
    public AsyncCommand ColorBarsCommand { get; }
    public AsyncCommand SendTextFrameCommand { get; }
    public AsyncCommand SendClockFrameCommand { get; }
    public AsyncCommand SendBarsCommand { get; }
    public AsyncCommand SendRowsCommand { get; }
    public AsyncCommand SendSolidCommand { get; }
    public AsyncCommand SendHalfCommand { get; }
    public RelayCommand AddTextCommand { get; }
    public RelayCommand RemoveTextCommand { get; }
    public RelayCommand AddFaceCommand { get; }
    public RelayCommand RemoveFaceCommand { get; }
    public AsyncCommand DrawCardCommand { get; }
    public AsyncCommand DrawRingCommand { get; }
    public AsyncCommand DrawLineCommand { get; }
    public AsyncCommand DrawFillRectCommand { get; }
    public AsyncCommand ApplyOrientationCommand { get; }
    public AsyncCommand ReadOrientationCommand { get; }
    public AsyncCommand BakeFontCommand { get; }
    public AsyncCommand SaveFontCommand { get; }
    public AsyncCommand UploadFontCommand { get; }
    public AsyncCommand StartStreamCommand { get; }
    public RelayCommand StopStreamCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public MainViewModel()
    {
        void OnErr(Exception ex) => Log.Error(ex.Message);

        RefreshPortsCommand = new AsyncCommand(RefreshPortsAsync, () => !Scanning, OnErr);
        ConnectCommand = new AsyncCommand(() => ConnectAsync(), () => !IsConnected, OnErr);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        FlashFirmwareCommand = new AsyncCommand(FlashFirmwareAsync, () => IsConnected && !Busy, OnErr);
        UploadBackgroundCommand = new AsyncCommand(UploadBackgroundAsync, () => IsConnected && !Busy, OnErr);
        UploadToAppCommand = new AsyncCommand(UploadToAppAsync, () => IsApp && !Busy, OnErr);
        ReadAppImageCommand = new AsyncCommand(ReadAppImageAsync, () => IsApp && !Busy, OnErr);
        RedrawBackgroundCommand = new AsyncCommand(async () =>
        {
            if (!IsApp) { Log.Warn("需要设备处于应用模式"); return; }
            await Task.Run(() => _app!.RedrawBackground());
            Log.Good("已触发重绘 APP 背景");
        }, () => IsApp && !Busy, OnErr);
        RunAppCommand = new AsyncCommand(RunAppAsync, () => IsBootloader && !Busy, OnErr);
        SelfTestCommand = new AsyncCommand(SelfTestAsync, () => IsBootloader && !Busy, OnErr);
        ReadMetaCommand = new AsyncCommand(ReadMetaAsync, () => IsBootloader && !Busy, OnErr);
        ToBootloaderCommand = new AsyncCommand(ToBootloaderAsync, () => !Busy, OnErr);
        FillCommand = new AsyncCommand(FillAsync, () => !Busy, OnErr);
        CalibrationCommand = new AsyncCommand(CalibrationAsync, () => !Busy, OnErr);
        ColorBarsCommand = new AsyncCommand(ColorBarsAsync, () => !Busy, OnErr);
        SendTextFrameCommand = new AsyncCommand(() => SendFrameAsync(() => Rendering.FrameRenderer.Text(DebugText, ParseColorBrush(DebugColor)), "文本帧"), () => !Busy, OnErr);
        SendClockFrameCommand = new AsyncCommand(() => SendFrameAsync(Rendering.FrameRenderer.Clock, "时钟"), () => !Busy, OnErr);
        SendBarsCommand = new AsyncCommand(() => SendFrameAsync(Rendering.FrameRenderer.Bars, "彩条"), () => !Busy, OnErr);
        SendRowsCommand = new AsyncCommand(() => SendFrameAsync(Rendering.FrameRenderer.Rows, "行序"), () => !Busy, OnErr);
        SendSolidCommand = new AsyncCommand(() => SendFrameAsync(() => Rendering.FrameRenderer.Solid(ParseColorBrush(DebugColor)), "纯色"), () => !Busy, OnErr);
        SendHalfCommand = new AsyncCommand(() => SendFrameAsync(Rendering.FrameRenderer.Half, "上下半分"), () => !Busy, OnErr);
        ClearLogCommand = new RelayCommand(Log.Clear);

        AddTextCommand = new RelayCommand(() =>
        {
            TextItems.Add(new TextItem { X = 20, Y = 20 + TextItems.Count * 24, Text = "NEW TEXT" });
            SelectedText = TextItems[^1];
        });
        RemoveTextCommand = new RelayCommand(() =>
        {
            if (SelectedText != null) TextItems.Remove(SelectedText);
        }, () => TextItems.Count > 0);
        PushTextCommand = new AsyncCommand(PushTextsAsync, () => IsApp && !Busy, OnErr);
        PushTextsCommand = new AsyncCommand(PushTextsAsync, () => IsApp && !Busy, OnErr);
        ClearScreenCommand = new AsyncCommand(async () =>
        {
            if (!IsApp) return;
            await Task.Run(() => _app!.Fill(0x0841));
        }, () => IsApp && !Busy, OnErr);

        AddFaceCommand = new RelayCommand(() =>
        {
            Faces.Add(new FaceItem
            {
                Family = SelectedFont,
                Size = Math.Clamp(BakeSize, 8, 48),
                Charset = Rendering.GlyphBaker.DefaultCharset
            });
            RenumberFaces();
            SelectedFace = Faces[^1];
        });
        RemoveFaceCommand = new RelayCommand(() =>
        {
            if (SelectedFace != null) Faces.Remove(SelectedFace);
            RenumberFaces();
        }, () => Faces.Count > 0);
        RenumberFaces();
        DrawCardCommand = new AsyncCommand(() => ShapesAsync("card"), () => IsApp && !Busy, OnErr);
        DrawRingCommand = new AsyncCommand(() => ShapesAsync("ring"), () => IsApp && !Busy, OnErr);
        DrawLineCommand = new AsyncCommand(() => ShapesAsync("line"), () => IsApp && !Busy, OnErr);
        DrawFillRectCommand = new AsyncCommand(() => ShapesAsync("rect"), () => IsApp && !Busy, OnErr);
        ApplyOrientationCommand = new AsyncCommand(ApplyOrientationAsync, () => IsConnected && !Busy, OnErr);
        ReadOrientationCommand = new AsyncCommand(ReadOrientationAsync, () => IsConnected && !Busy, OnErr);
        BakeFontCommand = new AsyncCommand(BakeFontAsync, () => !Busy, OnErr);
        SaveFontCommand = new AsyncCommand(SaveFontAsync, () => _lastFont != null, OnErr);
        UploadFontCommand = new AsyncCommand(UploadFontAsync, () => _lastFont != null && IsApp && !Busy, OnErr);
        StartStreamCommand = new AsyncCommand(StartStreamAsync, () => !Streaming, OnErr);
        StopStreamCommand = new RelayCommand(StopStream, () => Streaming);

        RegisterStateCommands();

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _watchdog.Tick += async (_, _) => await WatchdogAsync();
        _watchdog.Start();

        _ = RefreshPortsAsync();
        Log.Info("Aurora Suite 已启动（未自动连接，请选择端口后点连接）");
    }

    void RefreshStates()
    {
        OnPropertyChanged(nameof(IsBootloader));
        OnPropertyChanged(nameof(IsApp));

        foreach (var c in _stateCommands)
        {
            if (c is RelayCommand r) r.RaiseCanExecuteChanged();
            else if (c is AsyncCommand a) a.RaiseCanExecuteChanged();
        }
    }

    // ================= connection =================
    bool _scanning;
    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }

    public async Task RefreshPortsAsync()
    {
        if (Scanning) return;
        Scanning = true;
        StatusLine = "正在扫描串口…";
        try
        {
            var names = await Task.Run(() => PortFinder.List().Select(p => p.Name).ToList());
            var current = SelectedPort;

            Ports.Clear();
            foreach (var n in names) Ports.Add(n);

            var remembered = current ?? LoadSavedPort();
            if (remembered != null && names.Contains(remembered)) SelectedPort = remembered;
            else if (current == null && names.Count > 0 && Ports.Count > 0 && SelectedPort == null)
                SelectedPort = remembered;   // may be null: let the user choose

            StatusLine = names.Count == 0 ? "未发现串口设备" : $"发现 {names.Count} 个串口";
        }
        catch (Exception ex)
        {
            StatusLine = "扫描失败";
            Log.Error($"扫描串口失败: {ex.Message}");
        }
        finally
        {
            Scanning = false;
        }
    }

    async Task WatchdogAsync()
    {
        if (IsConnected || Busy || _connecting) return;
        if (!AutoConnect) return;

        if (SelectedPort == null) return;
        await ReconnectToModeAsync(null, 2, 80, silent: true);
        return;

        /* Legacy watchdog reconnect path kept disabled; ReconnectAsync owns lifecycle. */
        /*
        _connecting = true;
        try
        {
            var found = await Task.Run(() => ProbePorts(candidates));
            if (found.session == null) return;

            Bind(found.session, found.info!, found.port!);
            Log.Good($"自动重连 {found.port} → {found.info}");
        }
        finally { _connecting = false; }
    }

        */
    }

    bool _connecting;
    CancellationTokenSource? _reconnectCts;

    /// <summary>Probes every candidate port in parallel; the first device that answers wins.</summary>
    static (DeviceSession? session, DeviceInfo? info, string? port) ProbePorts(IEnumerable<string> ports)
    {
        var list = ports.Distinct().ToList();
        if (list.Count == 0) return (null, null, null);

        using var cts = new CancellationTokenSource();
        var tasks = list.Select(port => Task.Run(() =>
        {
            if (cts.IsCancellationRequested) return ((DeviceSession?)null, (DeviceInfo?)null, port);
            DeviceSession? session = null;
            try
            {
                session = DeviceSession.Connect(port);
                var info = session.Probe(150, 1);
                if (info != null) return (session, (DeviceInfo?)info, port);
                session.Dispose();
            }
            catch { session?.Dispose(); }
            return ((DeviceSession?)null, (DeviceInfo?)null, port);
        })).ToArray();

        Task.WaitAll(tasks, 800);         // fast manual/automatic connect probe

        foreach (var t in tasks)
        {
            if (t.IsCompletedSuccessfully && t.Result.Item1 != null)
            {
                cts.Cancel();             // other probes will close their ports
                return t.Result;
            }
        }

        cts.Cancel();
        return (null, null, null);
    }

    void Bind(DeviceSession session, DeviceInfo info, string port)
    {
        session.SetInfo(info);          // mode reporting depends on this
        _session = session;
        _boot = new BootloaderSession(session);
        _app = new AppSession(session);
        if (!Ports.Contains(port)) Ports.Add(port);
        SelectedPort = port;
        IsConnected = true;
        ModeText = info.Mode == DeviceMode.Bootloader ? "Bootloader 模式" : "应用模式";
        DeviceText = info.ToString();
        StatusLine = $"已连接 {port}";
        RefreshStates();
    }

    async Task ConnectAsync()
    {
        if (SelectedPort is null) { Log.Warn("没有可用串口，请先刷新"); return; }
        if (_connecting) return;

        _connecting = true;
        StatusLine = "正在连接…";
        try
        {
            string port = SelectedPort;
            var found = await Task.Run(() => OpenAndProbePort(port, 150));
            if (found.session == null || found.info == null)
            {
                StatusLine = "连接失败";
                Log.Error($"{port} 无响应（设备未连接或端口被占用）");
                return;
            }
            Bind(found.session, found.info, port);
            Log.Good($"已连接 {port} → {found.info}");
        }
        catch (Exception ex)
        {
            StatusLine = "连接失败";
            Log.Error($"连接失败: {ex.Message}");
        }
        finally { _connecting = false; }
    }

    void Disconnect()
    {
        _reconnectCts?.Cancel();
        ReleaseSession();
        StatusLine = "已断开";
        Log.Info("已断开连接");
    }

    void ReleaseSession()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _boot = null;
        _app = null;
        IsConnected = false;
        ModeText = "未连接";
        DeviceText = "选择端口后自动连接";
    }

    async Task<bool> EnsureBootloaderAsync()
    {
        if (IsBootloader) return true;

        Log.Info("切换到 Bootloader…");
        try { await Task.Run(() => _app?.RebootToBootloader()); } catch { }
        return await ReconnectToModeAsync(DeviceMode.Bootloader);
    }


    /// <summary>
    /// After a reboot the device may come back on a different COM number, so every
    /// attempt rescans the port list instead of probing only the previous name.
    /// </summary>
    /// <summary>
    /// The device keeps its COM number across a reboot, so reopening that same port is
    /// enough - no port re-enumeration and no probing of unrelated ports.
    /// </summary>
    /// <summary>
    /// Fast path: reopen the same COM port (the device usually keeps its number).
    /// Fallback: every few attempts the port list is refreshed, because a reboot
    /// occasionally re-enumerates as a different number.
    /// </summary>
    async Task<bool> ReconnectAsync(int attempts = 40, int delayMs = 150)
    {
        var port = SelectedPort;
        await Task.Delay(150);                     /* let the USB write drain before closing */
        Disconnect();
        await Task.Delay(200);                     /* let Windows release the handle */

        for (int i = 0; i < attempts; i++)
        {
            if (!string.IsNullOrEmpty(port))
            {
                var info = await Task.Run(() => TryOpenPort(port!));
                if (info != null)
                {
                    Log.Good($"已重连 {port} · {info}");
                    StatusLine = $"已连接 {port}";
                    return true;
                }
            }

            /* fallback: the device may have re-enumerated on another port */
            if (i > 0 && i % 6 == 0)
            {
                await RefreshPortsAsync();
                foreach (var other in Ports.ToList())
                {
                    if (other == port) continue;
                    var info2 = await Task.Run(() => TryOpenPort(other));
                    if (info2 != null)
                    {
                        Log.Warn($"设备端口已变更为 {other}（原 {port}）");
                        port = other;
                        StatusLine = $"已连接 {other}";
                        return true;
                    }
                }
            }

            await Task.Delay(delayMs);
        }

        StatusLine = "等待设备超时";
        Log.Warn("未检测到设备，请点『刷新』后手动连接");
        return false;
    }

    /// <summary>
    /// Owns the reboot connection lifecycle. Worker threads only open/probe a
    /// port; the UI thread binds the winning session after its mode is known.
    /// </summary>
    async Task<bool> ReconnectToModeAsync(DeviceMode? expectedMode, int attempts = 30,
                                          int delayMs = 80, bool silent = false)
    {
        if (_connecting) return false;

        _connecting = true;
        var previous = _reconnectCts;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var token = cts.Token;
        string? preferredPort = SelectedPort;
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(preferredPort)) candidates.Add(preferredPort);

        ReleaseSession();
        StatusLine = "正在等待设备重启…";

        try
        {
            for (int i = 0; i < attempts; i++)
            {
                token.ThrowIfCancellationRequested();

                foreach (var port in candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    var found = await Task.Run(() => OpenAndProbePort(port, 120), token);
                    if (found.session == null || found.info == null) continue;

                    if (expectedMode.HasValue && found.info.Mode != expectedMode.Value)
                    {
                        found.session.Dispose();
                        continue;
                    }

                    Bind(found.session, found.info, port);
                    if (!silent) Log.Good($"已重连 {port} · {found.info}");
                    return true;
                }

                /* COM numbers normally stay stable.  Only rescan periodically
                   so a mode switch is responsive without probing every COM port. */
                if ((i + 1) % 5 == 0)
                {
                    var ports = await Task.Run(() => PortFinder.List().Select(p => p.Name).ToList(), token);
                    foreach (var port in ports)
                        if (!candidates.Contains(port, StringComparer.OrdinalIgnoreCase)) candidates.Add(port);
                }

                await Task.Delay(delayMs, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_reconnectCts, cts)) _reconnectCts = null;
            cts.Dispose();
            _connecting = false;
        }

        if (!silent)
        {
            StatusLine = "等待设备超时";
            Log.Warn("未检测到目标模式设备");
        }
        return false;
    }

    static (DeviceSession? session, DeviceInfo? info) OpenAndProbePort(string port, int timeoutMs)
    {
        DeviceSession? session = null;
        try
        {
            session = DeviceSession.Connect(port);
            var info = session.Probe(timeoutMs, 2);
            if (info != null) return (session, info);
        }
        catch { }

        try { session?.Dispose(); } catch { }
        return (null, null);
    }

    /// <summary>Opens one port and binds it when the device answers; null on failure.</summary>
    DeviceInfo? TryOpenPort(string port)
    {
        DeviceSession? session = null;
        try
        {
            session = DeviceSession.Connect(port);
            var info = session.Probe(400, 2);
            if (info == null) { session.Dispose(); return null; }
            Bind(session, info, port);
            return info;
        }
        catch
        {
            try { session?.Dispose(); } catch { }
            return null;
        }
    }


    void DrawTextItem(TextItem it)
    {
        ushort bg = ParseColor(TextBgHex);
        int cw = TextCellW is > 0 and <= 320 ? TextCellW : 0;
        int ch = TextCellH is > 0 and <= 172 ? TextCellH : 0;
        int x = (int)it.X, y = (int)it.Y;

        /* cells may overlap, so the old one must go first */
        if (it.LastX >= 0 && (it.LastX != x || it.LastY != y))
        {
            bool overlap = Math.Abs(it.LastX - x) < TextCellW && Math.Abs(it.LastY - y) < TextCellH;
            if (overlap)
            {
                try
                {
                    _app!.Text(it.Face, it.LastX, it.LastY, cw, ch, ParseColor(it.ColorHex), " ", bg);
                }
                catch { }
            }
        }

        _app!.Text(it.Face, x, y, cw, ch, ParseColor(it.ColorHex), it.Text ?? "", bg);
        it.LastX = x;
        it.LastY = y;
    }

    // ================= font debug canvas =================
    public async Task PushTextsAsync()
    {
        if (!IsApp) { Log.Warn("需要设备处于应用模式"); return; }
        var items = TextItems.ToList();
        if (items.Count == 0) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var it in items) DrawTextItem(it);
            });
            var summary = string.Join(", ", items.Select(x => $"{x.Text}@({(int)x.X},{(int)x.Y})"));
            FontDebugInfo = $"已推送 {items.Count} 条 · 单元格 {TextCellW}×{TextCellH} · 背景 {TextBgHex} · {summary}";
        }
        catch (Exception ex) { FontDebugInfo = "推送失败: " + ex.Message; Log.Error(ex.Message); }
    }

    TextItem? _pendingPush;
    int _pushBusy;

    /// <summary>Queues a text update; only one command is in flight and stale moves are dropped.</summary>
    public void PushSingle(TextItem item, bool force = false)
    {
        if (!IsApp) return;
        if (!force && !AutoPush) return;

        _pendingPush = item;
        if (Interlocked.Exchange(ref _pushBusy, 1) == 1) return;   /* a pump is already running */

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var it = _pendingPush;
                    _pendingPush = null;
                    if (it == null) break;
                    try { _app!.Text(it.Face, (int)it.X, (int)it.Y, ParseColor(it.ColorHex), it.Text ?? ""); }
                    catch { }
                }
            }
            finally { Volatile.Write(ref _pushBusy, 0); }
        });
    }

    public string[] OrientationModes { get; } = { "0°", "90°", "180°", "270°" };

    string _selectedOrientation = "0°";
    public string SelectedOrientation { get => _selectedOrientation; set => Set(ref _selectedOrientation, value); }

    string _orientationInfo = "未读取";
    public string OrientationInfo { get => _orientationInfo; private set => Set(ref _orientationInfo, value); }

    // ================= shape demos =================
    async Task ShapesAsync(string kind)
    {
        if (!IsApp) { Log.Warn("需要应用模式"); return; }
        try
        {
            await Task.Run(() =>
            {
                switch (kind)
                {
                    case "card":
                        _app!.DrawRect(8, 8, 304, 74, 0x1A2C, 0x3E5F, true, true, 10, 2);
                        _app!.DrawRect(16, 16, 140, 58, 0x2145, 0x4ADE, true, true, 6, 1);
                        break;
                    case "ring":
                        _app!.DrawEllipse(120, 30, 100, 100, 0x0841, 0x3CC8, false, true, 6);
                        _app!.DrawEllipse(146, 56, 48, 48, 0x3CC8, 0x3CC8, true, false, 1);
                        break;
                    case "line":
                        _app!.DrawLine(10, 160, 310, 20, 0xFD20, 4);
                        _app!.DrawLine(10, 20, 310, 160, 0x4ADE, 2);
                        break;
                    default:
                        _app!.DrawRect(40, 40, 240, 90, 0xF800, 0xFFFF, true, true, 0, 3);
                        break;
                }
            });
            Log.Good("已发送图元: " + kind);
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    // ================= display orientation =================
    async Task ReadOrientationAsync()
    {
        if (_session == null) { Log.Warn("未连接设备"); return; }
        var (rot, w, h) = await Task.Run(() => _app!.GetDisplayRotation());
        OrientationInfo = $"当前 {rot * 90}° · 逻辑画布 {w}×{h}";
        SelectedOrientation = OrientationModes[Math.Clamp((int)rot, 0, 3)];
        Log.Info("屏幕方向: " + OrientationInfo);
    }

    async Task ApplyOrientationAsync()
    {
        if (_session == null) { Log.Warn("未连接设备"); return; }
        int idx = Array.IndexOf(OrientationModes, SelectedOrientation);
        if (idx < 0) return;
        Busy = true;
        try
        {
            await Task.Run(() => _app!.SetDisplayRotation((byte)idx));
            OrientationInfo = $"已写入 {OrientationModes[idx]} · 断电重启后生效";
            Log.Good("屏幕方向已写入: " + OrientationModes[idx]);
        }
        catch (Exception ex) { Log.Error("设置方向失败: " + ex.Message); }
        finally { Busy = false; }
    }

    // ================= font baking =================
    async Task BakeFontAsync()
    {
        if (string.IsNullOrEmpty(FontsSafe(SelectedFont))) return;
        Busy = true;
        try
        {
            int size = Math.Clamp(BakeSize, 8, 48);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string chars = string.IsNullOrEmpty(BakeCharset) ? Rendering.GlyphBaker.DefaultCharset : BakeCharset;
            var reqs = Faces.Count > 0
                ? Faces.Select(f => new Rendering.GlyphBaker.FaceRequest(f.Family, f.Size, f.Bold, f.Charset)).ToList()
                : new List<Rendering.GlyphBaker.FaceRequest> { new(SelectedFont, size, BakeBold, null) };
            var baked = await Task.Run(() => Rendering.GlyphBaker.BakeWithMetrics(reqs, chars));
            var result = baked.Package;
            _faceSizes = baked.Metrics.Select(m => m.Size).ToList();
            foreach (var it in TextItems)
                it.PreviewSize = (it.Face >= 0 && it.Face < _faceSizes.Count) ? _faceSizes[it.Face] : 16;
            sw.Stop();

            _lastFont = result.Package;
            _facePreviews = result.Previews;
            FontPreview = result.Previews.Count > 0 ? result.Previews[0] : null;
            FontInfo = $"{result.Faces} 个字面 · {result.Glyphs} 字形 · {result.Bytes:N0} 字节 · {sw.ElapsedMilliseconds} ms";
            Log.Good("字库烘焙完成: " + FontInfo);
            RefreshStates();
        }
        catch (Exception ex) { Log.Error("字库烘焙失败: " + ex.Message); }
        finally { Busy = false; }
    }

    async Task UploadFontAsync()
    {
        if (_lastFont == null) { Log.Warn("请先烘焙字库"); return; }
        if (!IsApp) { Log.Warn("需要设备处于应用模式"); return; }

        Busy = true;
        try
        {
            Progress = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (glyphs, lh) = await Task.Run(() => _app!.UploadFont(_lastFont, (done, total) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Progress = total == 0 ? 0 : (double)done / total;
                    ProgressText = $"{done}/{total} 页";
                });
            }));
            sw.Stop();

            FontInfo = $"已上传设备 · {glyphs} 字形 · 行高 {lh}px · {_lastFont.Length:N0} 字节 · {sw.ElapsedMilliseconds} ms";
            Log.Good("字库已上传到设备 0x040000: " + FontInfo);
        }
        catch (Exception ex) { Log.Error("字库上传失败: " + ex.Message); }
        finally { Busy = false; Progress = 0; ProgressText = ""; }
    }

    static string FontsSafe(string name) => string.IsNullOrWhiteSpace(name) ? "" : name;

    async Task SaveFontAsync()
    {
        if (_lastFont == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Aurora font (*.afont)|*.afont",
            FileName = $"{SelectedFont}_{BakeSize}px.afont".Replace(" ", "_")
        };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllBytesAsync(dlg.FileName, _lastFont);
        Log.Good($"字库已保存: {dlg.FileName} ({_lastFont.Length:N0} 字节)");
    }

    // ================= streaming demo (dirty-rect diffing) =================
    const int TileW = 16, TileH = 16;
    const int TilesX = (320 + TileW - 1) / TileW, TilesY = (172 + TileH - 1) / TileH;
    const int LargeChangePixels = 320 * 172 / 3;

    async Task StartStreamAsync()
    {
        Log.Info($"点击开始推送：IsApp={IsApp}  IsConnected={IsConnected}  Busy={Busy}  Streaming={Streaming}  port={SelectedPort}");
        if (!IsApp) { Log.Warn("需要设备处于应用模式，未启动"); return; }
        if (_app == null) { Log.Warn("设备会话为空，未启动"); return; }
        if (Streaming) return;

        Streaming = true;
        RefreshStates();
        _streamBegan = false;
        _streamFormat = StreamPixelFormat.Rgb565Rle;
        _prevValid = false;
        bool supportsIndex8 = _session?.Info?.SupportsIndex8Stream == true;
        if (ProjectScreen && ProjectionColorMode != ProjectionModeRgb565 && !supportsIndex8)
            Log.Warn("设备固件不支持 RGB332 索引流，投影将回退 RGB565；请升级至 App v1.1。");
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;

        int tick = 0;
        Log.Info("流式推送开始：首帧整屏，之后只推送变化的矩形");

        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[] cur;
                bool projectScreen = ProjectScreen;
                bool useIndexMode = ProjectionColorMode != ProjectionModeRgb565;
                bool indexedProjection = projectScreen && useIndexMode && supportsIndex8;
                if (projectScreen)
                {
                    _projector.Capture(_projBuf);
                    if (supportsIndex8) Rgb565ToRgb332(_projBuf, _projIndexBuf);
                    cur = _projBuf;
                }
                else
                {
                    var frame = Rendering.FrameRenderer.Stream(tick);
                    if (tick % 10 == 0) StreamPreview = frame.Preview;
                    cur = frame.Rgb565;
                }

                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (!_prevValid)
                {
                    await Task.Run(() =>
                    {
                        if (indexedProjection)
                        {
                            _app!.BeginStream(StreamPixelFormat.Index8Raw);
                            _streamBegan = true;
                            _streamFormat = StreamPixelFormat.Index8Raw;
                            int len = AppendRect(_frameBuf, 0, 0, 0, 320, 172, _projIndexBuf, _projIndexBuf.Length);
                            uint crc = _app.SendPackedFrameReliable(_frameBuf, len);
                            _app.CommitStream(crc);
                        }
                        else
                        {
                            var rle = Aurora.Core.Imaging.RleCodec.EncodeRgb565(cur);
                            _app!.SendRle(rle);
                        }
                    }, token);
                    Buffer.BlockCopy(cur, 0, _prevFrame, 0, cur.Length);
                    _prevValid = true;
                    sw.Stop();
                    StreamStats = $"首帧整屏 · {sw.ElapsedMilliseconds} ms";
                }
                else
                {
                    var rects = DiffRects(_prevFrame, cur);
                    int bytes = 0, count = rects.Count, batches = 0;
                    int dirtyPixels = 0;
                    foreach (var r in rects) dirtyPixels += r.w * r.h;

                    await Task.Run(() =>
                    {
                        StreamPixelFormat wantedFormat = indexedProjection
                            ? StreamPixelFormat.Index8Raw
                            : StreamPixelFormat.Rgb565Rle;

                        if (_streamBegan && _streamFormat != wantedFormat)
                        {
                            _app!.EndStream();
                            _streamBegan = false;
                        }

                        /* Desktop/video frames often change most pixels.  Such
                           a frame is poorly suited to fire-and-forget dirty
                           rect packets: use the ACKed full-frame path instead. */
                        if (dirtyPixels >= LargeChangePixels)
                        {
                            if (indexedProjection)
                            {
                                if (!_streamBegan)
                                {
                                    _app!.BeginStream(StreamPixelFormat.Index8Raw);
                                    _streamBegan = true;
                                    _streamFormat = StreamPixelFormat.Index8Raw;
                                }
                                int fullLen = AppendRect(_frameBuf, 0, 0, 0, 320, 172,
                                    _projIndexBuf, _projIndexBuf.Length);
                                uint crc = _app.SendPackedFrameReliable(_frameBuf, fullLen);
                                _app.CommitStream(crc);
                                bytes = fullLen;
                                batches = 1;
                            }
                            else
                            {
                                if (_streamBegan)
                                {
                                    _app!.EndStream();
                                    _streamBegan = false;
                                }
                                var rle = Aurora.Core.Imaging.RleCodec.EncodeRgb565(cur);
                                _app!.SendRle(rle);
                                bytes = rle.Length;
                                batches = 1;
                            }
                            return;
                        }

                        if (!_streamBegan)
                        {
                            _app!.BeginStream(wantedFormat);
                            _streamBegan = true;
                            _streamFormat = wantedFormat;
                        }

                        int len = 0;

                        void Flush()
                        {
                            if (len == 0) return;
                            uint c = indexedProjection
                                ? _app!.SendPackedFrameReliable(_frameBuf, len)
                                : _app!.SendPackedFrame(_frameBuf, len);
                            _app.CommitStream(c);
                            bytes += len;
                            len = 0;
                            batches++;
                            /* the device ring buffer is small: give it time to drain */
                            System.Threading.Thread.Sleep(2);
                        }

                        foreach (var r in rects)
                        {
                            int n;
                            if (indexedProjection)
                            {
                                CropIndexInto(_projIndexBuf, r.x, r.y, r.w, r.h, _rleBuf);
                                n = r.w * r.h;
                            }
                            else
                            {
                                CropInto(cur, r.x, r.y, r.w, r.h, _rectBuf);
                                n = Aurora.Core.Imaging.RleCodec.EncodeRgb565Into(
                                    _rectBuf.AsSpan(0, r.w * r.h * 2), _rleBuf);
                            }

                            if (len + n + 8 > 2040 && len > 0) Flush();

                            len = AppendRect(_frameBuf, len, r.x, r.y, r.w, r.h, _rleBuf, n);
                        }
                        Flush();
                    }, token);
                    sw.Stop();
                    long gc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                    string mode = indexedProjection ? "RGB332 索引流" :
                        dirtyPixels >= LargeChangePixels ? "全帧 ACK" : "差分流";
                    StreamStats = $"帧 {tick,5} · {mode} · {count} 块 / {batches} 批 · {bytes,5} B · {sw.ElapsedMilliseconds,3} ms · GC {gc} B";
                }

                Buffer.BlockCopy(cur, 0, _prevFrame, 0, cur.Length);
                tick++;
                await Task.Delay(Math.Max(20, StreamIntervalMs), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"流式推送中断: {ex.Message}"); }
        finally
        {
            try { if (_streamBegan) _app?.EndStream(); } catch { }
            _streamBegan = false;
            Streaming = false;
            RefreshStates();
            StreamStats = "已停止 · " + StreamStats;
            Log.Info("流式推送结束");
        }
    }

    /// <summary>Headless test hook: connect, stream for a few seconds, return the last stats line.</summary>
    public async Task<string> RunStreamTestAsync(string port, int seconds)
    {
        var found = await Task.Run(() => ProbePorts(new[] { port }));
        if (found.session == null || found.info == null) return "connect failed";
        Bind(found.session, found.info, port);

        var run = StartStreamAsync();
        var marks = new List<string>();
        for (int i = 0; i < seconds * 4; i++)
        {
            await Task.Delay(250);
            marks.Add(StreamStats);
        }
        StopStream();
        try { await run; } catch { }
        return string.Join(" | ", marks.Distinct());
    }

    void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts = null;
    }

    /// <summary>Compares two RGB565 frames tile by tile and merges changed tiles into rectangles.</summary>
    static List<(int x, int y, int w, int h)> DiffRects(byte[] prev, byte[] cur)
    {
        var dirty = new bool[TilesX, TilesY];
        for (int ty = 0; ty < TilesY; ty++)
            for (int tx = 0; tx < TilesX; tx++)
                dirty[tx, ty] = TileChanged(prev, cur, tx, ty);

        var rects = new List<(int, int, int, int)>();

        for (int ty = 0; ty < TilesY; ty++)
        {
            int tx = 0;
            while (tx < TilesX)
            {
                if (!dirty[tx, ty]) { tx++; continue; }

                int startX = tx;
                while (tx < TilesX && dirty[tx, ty]) tx++;
                int cols = tx - startX;

                int rows = 1;
                while (ty + rows < TilesY && RowAllDirty(dirty, startX, cols, ty + rows)) rows++;

                for (int r = 0; r < rows; r++)
                    for (int k = 0; k < cols; k++)
                        dirty[startX + k, ty + r] = false;

                int x = startX * TileW;
                int y = ty * TileH;
                rects.Add((x, y, Math.Min(cols * TileW, 320 - x), Math.Min(rows * TileH, 172 - y)));
            }
        }
        return rects;
    }

    static bool TileChanged(byte[] prev, byte[] cur, int tx, int ty)
    {
        int x = tx * TileW;
        int y0 = ty * TileH;
        int w = Math.Min(TileW, 320 - x);
        int h = Math.Min(TileH, 172 - y0);
        for (int row = 0; row < h; row++)
        {
            int y = y0 + row;
            int offset = (y * 320 + x) * 2;
            for (int i = 0; i < w * 2; i++)
                if (prev[offset + i] != cur[offset + i]) return true;
        }
        return false;
    }

    static bool RowAllDirty(bool[,] dirty, int startX, int cols, int ty)
    {
        for (int k = 0; k < cols; k++)
            if (!dirty[startX + k, ty]) return false;
        return true;
    }

    /// <summary>Converts a big-endian RGB565 frame to the fixed RGB332 index palette.</summary>
    static void Rgb565ToRgb332(byte[] rgb565, byte[] indices)
    {
        for (int p = 0, i = 0; p < indices.Length; p++, i += 2)
        {
            byte hi = rgb565[i], lo = rgb565[i + 1];
            indices[p] = (byte)((hi & 0xE0) | ((hi & 0x07) << 2) | ((lo & 0x18) >> 3));
        }
    }

    static int AppendRect(byte[] frame, int offset, int x, int y, int w, int h,
                          byte[] pixels, int length)
    {
        frame[offset++] = (byte)(x & 0xFF); frame[offset++] = (byte)(x >> 8);
        frame[offset++] = (byte)(y & 0xFF); frame[offset++] = (byte)(y >> 8);
        frame[offset++] = (byte)(w & 0xFF); frame[offset++] = (byte)(w >> 8);
        frame[offset++] = (byte)(h & 0xFF); frame[offset++] = (byte)(h >> 8);
        Array.Copy(pixels, 0, frame, offset, length);
        return offset + length;
    }

    static byte[] CropRgb565(byte[] full, int x, int y, int w, int h)
    {
        var outp = new byte[w * h * 2];
        CropInto(full, x, y, w, h, outp);
        return outp;
    }

    static void CropInto(byte[] full, int x, int y, int w, int h, byte[] dst)
    {
        for (int row = 0; row < h; row++)
            Array.Copy(full, ((y + row) * 320 + x) * 2, dst, row * w * 2, w * 2);
    }

    static void CropIndexInto(byte[] full, int x, int y, int w, int h, byte[] dst)
    {
        for (int row = 0; row < h; row++)
            Array.Copy(full, (y + row) * 320 + x, dst, row * w, w);
    }

    // ================= bootloader =================
    public async Task FlashFirmwareAsync()
    {
        if (string.IsNullOrEmpty(FirmwarePath)) { Log.Warn("请先选择固件 .hex 文件"); return; }

        Busy = true;
        ProgressText = "";
        try
        {
            var image = await Task.Run(() => FirmwareImage.FromHexFile(FirmwarePath));
            Log.Info($"固件 {image.Length} 字节，起始 0x{image.BaseAddress:X4}");

            if (!await EnsureBootloaderAsync()) return;

            var info = _session!.Info!;
            if (image.BaseAddress != info.AppStart)
                throw new InvalidOperationException(
                    $"固件起始地址 0x{image.BaseAddress:X4} 与设备 App 区 0x{info.AppStart:X4} 不一致");

            var padded = image.PadTo256();
            uint crc = Crc32.Compute(padded);

            Progress = 0;
            StatusLine = "擦除中…";
            Log.Info("擦除应用区…");
            await Task.Run(() => _boot!.Erase());

            StatusLine = "写入中…";
            await Task.Run(() => _boot!.WriteFirmware(padded, info.AppStart, (done, total) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Progress = total == 0 ? 0 : (double)done / total;
                    ProgressText = $"{done}/{total} 页";
                });
            }));

            StatusLine = "校验中…";
            await Task.Run(() => _boot!.Finish(padded.Length, crc));

            Log.Good("固件写入并校验通过，启动新固件…");
            await Task.Run(() =>
            {
                _boot!.Run();
            });
            await ReconnectToModeAsync(DeviceMode.Application);
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            StatusLine = "烧录失败";
        }
        finally
        {
            Busy = false;
            Progress = 0;
            ProgressText = "";
        }
    }

    public async Task RunAppAsync()
    {
        /* the device may jump before it can acknowledge, so the ACK is optional */
        try
        {
            await Task.Run(() => _boot!.Run());
            Log.Good("已发送 RUN 命令");
        }
        catch (Exception ex)
        {
            Log.Warn("RUN 无应答（设备可能已跳转）: " + ex.Message);
        }

        Log.Info("等待设备重新枚举…");
        bool ok = await ReconnectToModeAsync(DeviceMode.Application);
        StatusLine = ok ? "已切换到应用模式" : "未检测到应用，请点『刷新』后重连";
    }

    public async Task SelfTestAsync()
    {
        try
        {
            var (ok, msg) = await Task.Run(() => _boot!.SelfTest());
            if (ok) Log.Good($"Flash 自检通过: {msg}");
            else Log.Error($"Flash 自检失败: {msg}");
            Diagnostics = $"自检: {(ok ? "通过" : "失败")}  {msg}";
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    public async Task ReadMetaAsync()
    {
        if (_boot == null) { Log.Warn("未连接 Bootloader"); return; }
        try
        {
            var (meta, bg) = await Task.Run(() => _boot.Diagnostics());
            var sb = new System.Text.StringBuilder();
            if (meta != null)
            {
                sb.AppendLine($"元数据 magic=0x{meta.Magic:X8} size={meta.Size} crc=0x{meta.Crc:X8}");
                sb.AppendLine($"升级标志=0x{meta.Flag:X2}   App 首字=0x{meta.AppWord0:X8} ({(meta.AppLooksValid ? "JAL ✓" : "异常")})");
            }
            if (bg != null)
                sb.AppendLine($"背景 magic=0x{bg.Magic:X8} {bg.Width}x{bg.Height} len={bg.Length} crc=0x{bg.Crc:X8} ({(bg.IsValid ? "有效" : "无效")})");

            Diagnostics = sb.ToString().TrimEnd();
            Log.Info("已读取设备元数据");
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    public async Task UploadBackgroundAsync()
    {
        if (string.IsNullOrEmpty(BackgroundPath)) { Log.Warn("请先选择背景图片"); return; }

        Busy = true;
        try
        {
            var encoded = await Task.Run(() => BuildBackground332(BackgroundPath, ScaleMode));
            Log.Info($"背景编码完成: {encoded.Length} 字节（{encoded.Length / 256} 页）");

            if (!await EnsureBootloaderAsync()) return;

            Progress = 0;
            StatusLine = "写入背景…";
            await Task.Run(() => _boot!.UploadBackground(encoded, (done, total) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Progress = total == 0 ? 0 : (double)done / total;
                    ProgressText = $"{done}/{total} 页";
                });
            }));

            Log.Good("背景写入成功（断电或 RUN 后生效）");
            StatusLine = "背景完成";
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            StatusLine = "背景写入失败";
        }
        finally
        {
            Busy = false;
            Progress = 0;
            ProgressText = "";
        }
    }

    /// <summary>Writes the background through the running app: stored, shown immediately and on next boot.</summary>
    public async Task UploadToAppAsync()
    {
        if (string.IsNullOrEmpty(BackgroundPath)) { Log.Warn("请先选择背景图片"); return; }
        if (!IsApp) { Log.Warn("需要设备处于应用模式"); return; }

        Busy = true;
        try
        {
            var encoded = await Task.Run(() => BuildBackground(BackgroundPath, ScaleMode));
            Log.Info($"图片编码完成: {encoded.Length} 字节（{encoded.Length / 256} 页）");

            Progress = 0;
            StatusLine = "写入图片…";
            await Task.Run(() => _app!.WriteBackground(encoded, (done, total) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Progress = total == 0 ? 0 : (double)done / total;
                    ProgressText = $"{done}/{total} 页";
                });
            }));

            Log.Good("图片已写入设备并显示，下次开机会自动加载");
            StatusLine = "图片完成";
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            StatusLine = "图片写入失败";
        }
        finally { Busy = false; Progress = 0; ProgressText = ""; }
    }

    async Task ReadAppImageAsync()
    {
        if (!IsApp) { Log.Warn("需要设备处于应用模式"); return; }
        var d = await Task.Run(() => _app!.ReadImageHeader());
        uint magic = d.Length >= 4 ? BitConverter.ToUInt32(d, 0) : 0;
        uint wh = d.Length >= 8 ? BitConverter.ToUInt32(d, 4) : 0;
        uint len = d.Length >= 16 ? BitConverter.ToUInt32(d, 12) : 0;
        uint crc = d.Length >= 20 ? BitConverter.ToUInt32(d, 16) : 0;

        AppImageInfo = magic == 0x4732534D
            ? $"有效图  {wh & 0xFFFF}×{wh >> 16}  {len} 字节  CRC 0x{crc:X8}"
            : $"空（magic 0x{magic:X8}）";
        Log.Info("APP 图头: " + AppImageInfo);
    }

    public async Task ToBootloaderAsync()
    {
        try
        {
            if (IsBootloader)
            {
                await Task.Run(() => _boot!.Restart());
                Log.Info("已请求重启 Bootloader，等待设备重新枚举…");
                await ReconnectToModeAsync(DeviceMode.Bootloader);
                return;
            }

            if (!IsApp)
            {
                Log.Warn("设备未处于可切换的应用模式");
                return;
            }

            await Task.Run(() => _app!.RebootToBootloader());
            Log.Info("已请求进入 Bootloader，等待设备重新枚举…");
            await ReconnectToModeAsync(DeviceMode.Bootloader);
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    // ================= app display tests =================
    public async Task FillAsync()
    {
        try
        {
            var color = ParseColor(FillColor);
            await Task.Run(() => _app!.Fill(color));
            Log.Good($"整屏填充 {FillColor}");
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    public async Task CalibrationAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _app!.Rect(0, 0, 24, 24, 0xF800);
                _app.Rect(296, 0, 24, 24, 0x07E0);
                _app.Rect(0, 148, 24, 24, 0x001F);
                _app.Rect(296, 148, 24, 24, 0xFFE0);
                _app.Rect(0, 0, 320, 1, 0xFFFF);
                _app.Rect(0, 171, 320, 1, 0xFFFF);
                _app.Rect(0, 0, 1, 172, 0xFFFF);
                _app.Rect(319, 0, 1, 172, 0xFFFF);
                _app.Rect(120, 60, 80, 40, 0xF81F);
            });
            Log.Good("校准图案已发送：四角红/绿/蓝/黄，中央品红");
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    public async Task ColorBarsAsync()
    {
        try
        {
            // Send the bars as one full frame.  Eight synchronous DISP_RECT commands
            // made each band visible before the next command was acknowledged.
            var frame = Rendering.FrameRenderer.Bars();
            await Task.Run(() =>
            {
                var rle = Aurora.Core.Imaging.RleCodec.EncodeRgb565(frame.Rgb565);
                _app!.SendRle(rle);
            });
            Log.Good("彩条已发送");
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }


    /// <summary>Render + RLE encode + push a full frame, then show it in the preview.</summary>
    async Task SendFrameAsync(Func<Rendering.FrameRenderer.Rendered> render, string label)
    {
        Busy = true;
        try
        {
            // always render first so the preview works even without a device
            var frame = render();
            DebugPreview = frame.Preview;

            if (!IsApp)
            {
                DebugInfo = $"{label} · 已渲染 {frame.Rgb565.Length}B（设备不在应用模式，未发送）";
                Log.Warn($"{label} 已渲染，但设备不在应用模式，未发送");
                StatusLine = "未发送（需要应用模式）";
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var transfer = await Task.Run(() =>
            {
                var encoded = Aurora.Core.Imaging.RleCodec.EncodeRgb565(frame.Rgb565);
                bool fast = _app!.SendFullFrameRleFast(encoded);
                if (!fast) _app.SendRle(encoded);
                return (Length: encoded.Length, Fast: fast);
            });
            sw.Stop();

            string mode = transfer.Fast ? "无 ACK 单帧" : "分块 ACK";
            DebugInfo = $"{label} · RLE {transfer.Length} B · {mode} · {sw.ElapsedMilliseconds} ms · 校验通过";
            Log.Good($"{label} 已发送  RLE {transfer.Length} B  {mode}  {sw.ElapsedMilliseconds} ms");
            StatusLine = $"{label} 完成";
        }
        catch (Exception ex)
        {
            Log.Error($"{label} 失败: {ex.Message}");
            StatusLine = $"{label} 失败";
        }
        finally { Busy = false; }
    }

    static Color ParseColorBrush(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return c;
        }
        catch { return Color.FromRgb(0x3C, 0xC8, 0xFF); }
    }

    /// <summary>Used by --preview to populate the frame preview without a device.</summary>
    public void RenderDemo() => _ = SendFrameAsync(Rendering.FrameRenderer.Bars, "彩条");

    // ================= helpers =================
    public void SetFirmware(string path)
    {
        try
        {
            var img = FirmwareImage.FromHexFile(path);
            FirmwarePath = path;
            FirmwareInfo = $"{Path.GetFileName(path)}  ·  {img.Length} 字节  ·  0x{img.BaseAddress:X4}  ·  {(img.PaddedLength + 255) / 256} 页";
            Log.Info($"已载入固件 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error($"固件解析失败: {ex.Message}");
        }
    }

    public void SetBackground(string path)
    {
        try
        {
            var bmp = LoadBackgroundBitmap(path, ScaleMode);
            BackgroundPath = path;
            BackgroundPreview = QuantizePreview(bmp);
            BackgroundInfo = $"{Path.GetFileName(path)}  ·  320×172  ·  3-3-2 调色板 + RLE";
            Log.Info($"已载入背景 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error($"图片载入失败: {ex.Message}");
        }
    }

    /// <summary>Reproduces exactly what the panel will show: 3-3-2 palette -> RGB565.</summary>
    public static BitmapSource QuantizePreview(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];

        var conv = src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        conv.CopyPixels(px, stride, 0);

        for (int i = 0; i < px.Length; i += 4)
        {
            ushort c = (ushort)(((px[i + 2] >> 3) << 11) | ((px[i + 1] >> 2) << 5) | (px[i] >> 3));
            int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
            px[i + 2] = (byte)((r << 3) | (r >> 2));
            px[i + 1] = (byte)((g << 2) | (g >> 4));
            px[i] = (byte)((b << 3) | (b >> 2));
            px[i + 3] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    static ushort ColorFromHex(string hex) => ParseColor(hex);

    static ushort ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) s = "3CC8FF";
        int r = Convert.ToInt32(s.Substring(0, 2), 16);
        int g = Convert.ToInt32(s.Substring(2, 2), 16);
        int b = Convert.ToInt32(s.Substring(4, 2), 16);
        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }

    /// <summary>Cover-fit the image into 320x172 and return a frozen BitmapSource.</summary>
    public static BitmapSource LoadBackgroundBitmap(string path, string scaleMode = "等比（留黑边）")
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();
        src.Freeze();

        bool stretch = scaleMode != null && scaleMode.StartsWith("拉伸");
        bool cover = scaleMode != null && scaleMode.StartsWith("裁剪");
        double scale = stretch ? 0
                     : cover ? Math.Max(320.0 / src.PixelWidth, 172.0 / src.PixelHeight)
                             : Math.Min(320.0 / src.PixelWidth, 172.0 / src.PixelHeight);
        double w = stretch ? 320 : src.PixelWidth * scale;
        double h = stretch ? 172 : src.PixelHeight * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 320, 172));
            dc.DrawImage(src, new Rect((320 - w) / 2, (172 - h) / 2, w, h));
        }

        var rtb = new RenderTargetBitmap(320, 172, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    static byte[] BuildBackground332(string path, string scaleMode)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();
        src.Freeze();

        bool stretch = scaleMode != null && scaleMode.StartsWith("拉伸");
        bool cover = scaleMode != null && scaleMode.StartsWith("裁剪");
        double scale = stretch ? 0
                     : cover ? Math.Max(320.0 / src.PixelWidth, 172.0 / src.PixelHeight)
                             : Math.Min(320.0 / src.PixelWidth, 172.0 / src.PixelHeight);
        double w = stretch ? 320 : src.PixelWidth * scale;
        double h = stretch ? 172 : src.PixelHeight * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 320, 172));
            dc.DrawImage(src, new Rect((320 - w) / 2, (172 - h) / 2, w, h));
        }

        var rtb = new RenderTargetBitmap(320, 172, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = 320 * 4;
        var pixels = new byte[stride * 172];
        rtb.CopyPixels(pixels, stride, 0);
        return Aurora.Core.Imaging.BackgroundBuilder.Build332(pixels, stride, 24576);
    }

    static byte[] BuildBackground(string path, string scaleMode = "等比（留黑边）")
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();
        src.Freeze();

        bool stretch = scaleMode != null && scaleMode.StartsWith("拉伸");
        bool cover = scaleMode != null && scaleMode.StartsWith("裁剪");
        double scale = stretch ? 0
                     : cover ? Math.Max(320.0 / src.PixelWidth, 172.0 / src.PixelHeight)
                             : Math.Min(320.0 / src.PixelWidth, 172.0 / src.PixelHeight);
        double w = stretch ? 320 : src.PixelWidth * scale;
        double h = stretch ? 172 : src.PixelHeight * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 320, 172));
            dc.DrawImage(src, new Rect((320 - w) / 2, (172 - h) / 2, w, h));
        }

        var rtb = new RenderTargetBitmap(320, 172, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = 320 * 4;
        var pixels = new byte[stride * 172];
        rtb.CopyPixels(pixels, stride, 0);
        return BackgroundBuilder.Build(pixels, stride);
    }
}
