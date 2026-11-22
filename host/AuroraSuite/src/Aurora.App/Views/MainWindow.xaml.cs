using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Aurora.App.ViewModels;

namespace Aurora.App.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        var argv = Environment.GetCommandLineArgs();
        if (argv.Contains("--stream"))
        {
            string port = argv.Length > Array.IndexOf(argv, "--stream") + 1
                ? argv[Array.IndexOf(argv, "--stream") + 1] : "COM5";
            Loaded += async (_, _) =>
            {
                string result;
                try { result = await _vm.RunStreamTestAsync(port, 4); }
                catch (Exception ex) { result = "EXCEPTION: " + ex; }
                File.WriteAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "aurora-stream.txt"), result);
                Application.Current.Shutdown();
            };
        }

        var cmdArgs = Environment.GetCommandLineArgs();
        int ci = Array.IndexOf(cmdArgs, "--cmd");
        if (ci > 0 && ci + 1 < cmdArgs.Length)
        {
            string hex = cmdArgs[ci + 1];
            string? port = ci + 2 < cmdArgs.Length ? cmdArgs[ci + 2] : null;
            Loaded += (_, _) => RunOneShot(hex, port);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--preview"))
            Loaded += (_, _) => _vm.RenderDemo();
    }

    void RunOneShot(string hexCmd, string? port)
    {
        string log = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "aurora-cmd.txt");
        var sb = new System.Text.StringBuilder();
        Aurora.Core.Device.DeviceSession? session = null;
        try
        {
            ushort cmd = Convert.ToUInt16(hexCmd.Replace("0x", ""), 16);
            var ports = port != null ? new[] { port } : new[] { "COM5", "COM4", "COM6", "COM3" };

            foreach (var p in ports)
            {
                try
                {
                    session = Aurora.Core.Device.DeviceSession.Connect(p);
                    if (session.Probe(1200) == null) { session.Dispose(); session = null; continue; }
                    sb.AppendLine($"port={p} connected");
                    break;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"port={p} open failed: {ex.Message}");
                    session?.Dispose(); session = null;
                }
            }

            if (session == null) { sb.AppendLine("no device"); }
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (status, data) = session.Cmd(cmd, ReadOnlySpan<byte>.Empty, 3000, 0);
                sw.Stop();
                sb.AppendLine($"cmd=0x{cmd:X4} status={status} len={data.Length} time={sw.ElapsedMilliseconds}ms");
                sb.AppendLine("data=" + BitConverter.ToString(data));
                if (data.Length >= 20)
                {
                    uint magic = BitConverter.ToUInt32(data, 0);
                    uint wh = BitConverter.ToUInt32(data, 4);
                    uint len = BitConverter.ToUInt32(data, 12);
                    uint crc = BitConverter.ToUInt32(data, 16);
                    sb.AppendLine($"magic=0x{magic:X8} size={wh & 0xFFFF}x{wh >> 16} len={len} crc=0x{crc:X8}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
        finally { session?.Dispose(); System.IO.File.WriteAllText(log, sb.ToString()); }

        Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
    }

    Point _dragStart;
    MainViewModel.TextItem? _dragItem;

    void OnTextDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MainViewModel.TextItem item) return;
        _dragItem = item;
        _dragStart = e.GetPosition(this);
        (sender as UIElement)?.CaptureMouse();
        _vm.SelectedText = item;
        e.Handled = true;
    }

    void OnTextMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        _dragItem.X = Math.Clamp(_dragItem.X + (p.X - _dragStart.X), 0, 300);
        _dragItem.Y = Math.Clamp(_dragItem.Y + (p.Y - _dragStart.Y), 0, 160);
        _dragStart = p;
        _vm.PushSingle(_dragItem);      /* throttled by AutoPush inside the VM */
        e.Handled = true;
    }

    void OnTextUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem != null)
        {
            _vm.PushSingle(_dragItem, force: true);  /* final position, always sent */
            (sender as UIElement)?.ReleaseMouseCapture();
        }
        _dragItem = null;
        e.Handled = true;
    }

    void OnPickFirmware(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Intel HEX (*.hex)|*.hex|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog() == true) _vm.SetFirmware(dlg.FileName);
    }

    void OnPickBackground(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) _vm.SetBackground(dlg.FileName);
    }
}
