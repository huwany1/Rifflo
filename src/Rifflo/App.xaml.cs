using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Rifflo.Settings;

namespace Rifflo;

public partial class App : Application
{
    private const string InstanceName = @"Local\Rifflo.ebb9f26fd18a4bf091d3933224eee542";
    private Mutex? _mutex;
    private EventWaitHandle? _showSettings;
    private RegisteredWaitHandle? _settingsWait;
    private AppController? _controller;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var (instanceName, trayName) = GetInstanceIdentity();
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Settings");
        _mutex = new Mutex(initiallyOwned: true, instanceName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try { _ownsMutex = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }

        if (!_ownsMutex)
        {
            _showSettings.Set();
            Shutdown();
            return;
        }

        var testMediaPrefix = instanceName == InstanceName ? null : Environment.GetEnvironmentVariable("RIFFLO_TEST_MEDIA_PREFIX")?.Trim();
        _controller = new AppController(this, trayName, testMediaPrefix);
        _settingsWait = ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.InvokeAsync(() => _controller?.ShowSettings());
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        _controller.Start();
    }

    private static (string InstanceName, string TrayName) GetInstanceIdentity()
    {
        var path = SettingsStore.CurrentUserPath;
        if (string.Equals(path, SettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase))
            return (InstanceName, "Rifflo");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return (InstanceName + "." + hash, $"Rifflo ({hash[..8]})");
    }

    protected override void OnExit(ExitEventArgs args)
    {
        _settingsWait?.Unregister(null);
        _controller?.Dispose();
        _controller = null;
        _showSettings?.Dispose();
        if (_ownsMutex)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(args);
    }
}
