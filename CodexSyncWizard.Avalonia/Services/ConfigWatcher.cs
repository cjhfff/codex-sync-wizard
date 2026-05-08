using System.Text.RegularExpressions;

namespace CodexSyncWizard.Services;

public class ProviderChangedEventArgs : EventArgs
{
    public string? OldProvider { get; init; }
    public string? NewProvider { get; init; }
    public string ConfigPath { get; init; } = "";
}

public class ConfigWatcher : IDisposable
{
    private readonly string _codexHome;
    private FileSystemWatcher? _watcher;
    private string? _lastProvider;
    private System.Timers.Timer? _debounce;
    private readonly object _lock = new();

    public event EventHandler<ProviderChangedEventArgs>? ProviderChanged;

    public ConfigWatcher(string codexHome)
    {
        _codexHome = codexHome;
        _lastProvider = ConfigService.ReadProvider(codexHome);
    }

    public void Start()
    {
        Stop();
        if (!Directory.Exists(_codexHome)) return;

        _watcher = new FileSystemWatcher(_codexHome, "config.toml")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            _debounce?.Stop();
            _debounce?.Dispose();
            _debounce = new System.Timers.Timer(800) { AutoReset = false };
            _debounce.Elapsed += (_, _) => CheckChange();
            _debounce.Start();
        }
    }

    private void CheckChange()
    {
        try
        {
            var current = ConfigService.ReadProvider(_codexHome);
            if (current == _lastProvider) return;
            var old = _lastProvider;
            _lastProvider = current;
            ProviderChanged?.Invoke(this, new ProviderChangedEventArgs
            {
                OldProvider = old,
                NewProvider = current,
                ConfigPath = Path.Combine(_codexHome, "config.toml")
            });
        }
        catch { }
    }

    public string? CurrentProvider => _lastProvider;

    public void Dispose() => Stop();
}
