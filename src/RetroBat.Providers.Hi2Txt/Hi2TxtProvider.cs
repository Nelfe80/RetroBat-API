using System.IO;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Providers.Hi2Txt;

public class Hi2TxtProvider : IProvider
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<Hi2TxtProvider>? _logger;
    private FileSystemWatcher? _hiWatcher;
    private FileSystemWatcher? _nvramWatcher;

    public Hi2TxtProvider(IEventBus eventBus, ILogger<Hi2TxtProvider>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // For demonstration, paths should be read from configuration
        string retrobatDir = @"C:\RetroBat"; // default fallback path
        string hiPath = Path.Combine(retrobatDir, "roms", "mame", "hi");
        string nvramPath = Path.Combine(retrobatDir, "roms", "mame", "nvram"); // Adjust according to emulator paths

        SetupWatcher(ref _hiWatcher, hiPath);
        SetupWatcher(ref _nvramWatcher, nvramPath);

        _logger?.LogInformation("Hi2TxtProvider watchers started");
        
        return Task.CompletedTask;
    }

    private void SetupWatcher(ref FileSystemWatcher? watcher, string path)
    {
        if (Directory.Exists(path))
        {
            watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = "*.*",
                EnableRaisingEvents = true
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
        }
        else
        {
            _logger?.LogWarning($"Hi2Txt directory not found: {path}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Simple debounce could be added here
        var gameId = Path.GetFileNameWithoutExtension(e.Name);
        
        // Log event
        _logger?.LogInformation($"Hi/Nvram file changed: {e.FullPath} for game {gameId}");

        // TODO: Call actual hi2txt executable to extract and normalize the score
        
        // Mock broadcasting of the event
        var evt = new EventEnvelope
        {
            Type = "hiscore.updated",
            Payload = new
            {
                GameId = gameId,
                Source = "hi2txt",
                Timestamp = DateTime.UtcNow
            }
        };
        
        _eventBus.PublishAsync(evt);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopWatcher(_hiWatcher);
        StopWatcher(_nvramWatcher);
        _logger?.LogInformation("Hi2TxtProvider watchers stopped");
        
        return Task.CompletedTask;
    }

    private void StopWatcher(FileSystemWatcher? watcher)
    {
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Dispose();
        }
    }

    public bool IsHealthy() => true;
}
