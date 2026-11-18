using System.IO.Pipes;
using System.Threading;
using System.Windows;
using DeskBox.Services;

namespace DeskBox;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private BoxManager? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? sandboxDir = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--sandbox", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(e.Args[i + 1]))
            {
                sandboxDir = e.Args[i + 1];
                break;
            }
        }

        var mutexName = string.IsNullOrWhiteSpace(sandboxDir)
            ? "Local\\DeskBox_SingleInstance_Mutex"
            : "Local\\DeskBox_Sandbox_Mutex";

        _singleInstanceMutex = new Mutex(true, mutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (string.IsNullOrWhiteSpace(sandboxDir))
            {
                ForwardCommandToRunningInstance(e.Args);
            }
            Shutdown();
            return;
        }

        _manager = new BoxManager(Dispatcher, sandboxDir);
        _manager.Start();

        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            _manager.ShowSettingsWindow();
        }
        if (e.Args.Any(a => string.Equals(a, "--appearance", StringComparison.OrdinalIgnoreCase)))
        {
            _manager.ShowBoxAppearanceWindow();
        }
        if (e.Args.Any(a => string.Equals(a, "--new-box", StringComparison.OrdinalIgnoreCase)))
        {
            _manager.CreateNewBox();
        }
        if (e.Args.Any(a => string.Equals(a, "--toggle-tidy", StringComparison.OrdinalIgnoreCase)))
        {
            _manager.ToggleTidyPaused();
        }
    }

    private static void ForwardCommandToRunningInstance(string[] args)
    {
        int cmd = BoxManager.IpcCmdShowSettings;
        if (args.Any(a => string.Equals(a, "--new-box", StringComparison.OrdinalIgnoreCase)))
            cmd = BoxManager.IpcCmdNewBox;
        else if (args.Any(a => string.Equals(a, "--toggle-tidy", StringComparison.OrdinalIgnoreCase)))
            cmd = BoxManager.IpcCmdToggleTidy;
        else if (args.Any(a => string.Equals(a, "--appearance", StringComparison.OrdinalIgnoreCase)))
            cmd = BoxManager.IpcCmdShowAppearance;
        else if (args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            cmd = BoxManager.IpcCmdShowSettings;

        try
        {
            using var client = new NamedPipeClientStream(".", BoxManager.IpcPipeName, PipeDirection.Out);
            client.Connect(800);
            client.WriteByte((byte)cmd);
            client.Flush();
        }
        catch
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.Dispose();
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
