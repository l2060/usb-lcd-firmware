using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aurora.App.Rendering;

namespace Aurora.App;

public partial class App : Application
{
    static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "aurora-crash.txt");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0] == "--diagnose")
        {
            RunDiagnose(e.Args[1]);
            Shutdown(0);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            RunSelfTest();
            Shutdown(0);
            return;
        }
        base.OnStartup(e);
    }

    void RunDiagnose(string port)
    {
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                  "aurora-diagnose.txt");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"port={port}  {DateTime.Now:HH:mm:ss}");

        Aurora.Core.Device.DeviceSession? session = null;
        try
        {
            session = Aurora.Core.Device.DeviceSession.Connect(port);
            sb.AppendLine("open: OK");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var info = session.Probe(2000);
            sw.Stop();

            if (info == null)
            {
                sb.AppendLine($"INFO: no answer after {sw.ElapsedMilliseconds}ms");
                var raw = new byte[512];
                int n = 0;
                try
                {
                    var port2 = typeof(Aurora.Core.Device.DeviceSession);
                }
                catch { }
                sb.AppendLine($"raw bytes read: {n}");
            }
            else
            {
                sb.AppendLine($"INFO: {info}  ({sw.ElapsedMilliseconds}ms)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            session?.Dispose();
        }

        File.WriteAllText(log, sb.ToString());
    }

    void RunSelfTest()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "aurora-frames");
        Directory.CreateDirectory(dir);

        (string name, Func<FrameRenderer.Rendered> fn)[] cases =
        {
            ("text",  () => FrameRenderer.Text("Aurora LITE", System.Windows.Media.Colors.Cyan)),
            ("clock", FrameRenderer.Clock),
            ("bars",  FrameRenderer.Bars),
            ("rows",  FrameRenderer.Rows),
            ("solid", () => FrameRenderer.Solid(System.Windows.Media.Colors.Cyan)),
            ("half",  FrameRenderer.Half),
        };

        foreach (var (name, fn) in cases)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = fn();
                sw.Stop();

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(r.Preview));
                using (var fs = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(fs);

                int rle = 0;
                try
                {
                    rle = AzureRle(r.Rgb565);
                }
                catch (Exception ex)
                {
                    rle = -1;
                    Write("rle", ex);
                }

                File.AppendAllText(Path.Combine(dir, "result.txt"),
                    $"{name,-6} render={sw.ElapsedMilliseconds}ms  rgb565={r.Rgb565.Length}B  rle={rle}B{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Write("render:" + name, ex);
                File.AppendAllText(Path.Combine(dir, "result.txt"),
                    $"{name,-6} FAILED: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }

    static int AzureRle(byte[] rgb565) =>
        Aurora.Core.Imaging.RleCodec.EncodeRgb565(rgb565).Length;

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("Dispatcher", e.Exception);
        e.Handled = true;
    }

    static void Write(string tag, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLog, $"[{DateTime.Now:HH:mm:ss}] {tag}: {ex}\n\n");
        }
        catch { }
    }
}
