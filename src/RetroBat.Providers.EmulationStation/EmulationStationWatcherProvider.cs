using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Providers.EmulationStation;

public class EmulationStationWatcherProvider : IProvider
{
    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly ILogger<EmulationStationWatcherProvider>? _logger;
    private FileSystemWatcher? _watcher;
    private readonly string _eventsIniPath = @"C:\RetroBat\plugins\APIExpose\events.ini";
    private readonly string _eventsIniDir;
    private readonly HttpClient _httpClient;

    public EmulationStationWatcherProvider(IEventBus eventBus, ApiContext context, ILogger<EmulationStationWatcherProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _logger = logger;
        _eventsIniDir = Path.GetDirectoryName(_eventsIniPath)!;
        _httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1234") };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_eventsIniDir))
        {
            Directory.CreateDirectory(_eventsIniDir);
        }

        if (!File.Exists(_eventsIniPath))
        {
            File.WriteAllText(_eventsIniPath, "");
        }

        _watcher = new FileSystemWatcher(_eventsIniDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = Path.GetFileName(_eventsIniPath),
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        
        _logger?.LogInformation($"EmulationStationWatcherProvider watching {_eventsIniPath}");

        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed) return;

        try
        {
            Thread.Sleep(50);
            
            var lines = File.ReadAllLines(_eventsIniPath);
            if (lines.Length == 0) return;

            var evt = lines[0].Trim();
            if (evt.StartsWith("event="))
            {
                var eventName = evt.Substring(6).Trim();
                // Process on thread pool to not block watcher
                _ = Task.Run(() => ProcessEventAsync(eventName, lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()));
            }
        }
        catch (IOException)
        {
            // Ignore temporary file lock
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing events.ini");
        }
    }

    private async Task ProcessEventAsync(string eventName, string[] args)
    {
        _logger?.LogInformation($"ES Event received: {eventName}");
        var eventType = "ui.event";

        if (eventName == "game-selected")
        {
            eventType = "ui.game.selected";
            _context.Ui.State = "browsing";
            
            if (args.Length > 0)
            {
                ParseGameSelected(args[0], out string systemId, out string path, out string name);
                
                var systemDetails = await FetchSystemDetailsAsync(systemId);
                _context.Ui.SelectedSystem = systemDetails ?? new SystemDetails { Name = systemId };
                
                var gameDetails = await FetchGameDetailsAsync(systemId, path);
                var gameId = gameDetails?.Id ?? gameDetails?.Md5 ?? "";
                
                _context.Ui.Selected = new GameReference 
                { 
                    SystemId = systemId, 
                    GamePath = path, 
                    GameName = name,
                    GameId = gameId,
                    Details = gameDetails
                };
            }
        }
        else if (eventName == "system-selected")
        {
            eventType = "ui.system.selected";
            _context.Ui.State = "browsing";
            if (args.Length > 0)
            {
                var sysId = args[0].Trim();
                var systemDetails = await FetchSystemDetailsAsync(sysId);
                _context.Ui.SelectedSystem = systemDetails ?? new SystemDetails { Name = sysId };
            }
        }
        else if (eventName == "game-start")
        {
            eventType = "ui.game.started";
            _context.Ui.State = "playing";
            
            if (args.Length > 0)
            {
                ParseGameStart(args[0], out string path, out string longName, out string shortName);
                
                var sysId = _context.Ui.SelectedSystem?.Name ?? "unknown";
                var gameDetails = await FetchGameDetailsAsync(sysId, path);
                var gameId = gameDetails?.Id ?? gameDetails?.Md5 ?? "";

                _context.Ui.Running = new GameReference 
                { 
                    SystemId = sysId, 
                    GamePath = path, 
                    GameName = shortName,
                    GameId = gameId,
                    Details = gameDetails
                };
            }
        }
        else if (eventName == "game-end")
        {
            eventType = "ui.game.ended";
            _context.Ui.State = "browsing";
            _context.Ui.Running = null;
        }
        
        var envelope = new EventEnvelope
        {
            Type = eventType,
            Payload = new { EventName = eventName, RawArgs = args, Context = _context.Ui }
        };
        
        await _eventBus.PublishAsync(envelope);
    }
    
    // Enrichissement depuis l'API locale ES (127.0.0.1:1234)
    private async Task<SystemDetails?> FetchSystemDetailsAsync(string systemId)
    {
        try
        {
            var url = $"/systems/{systemId}";
            _logger?.LogInformation($"[FetchSystemDetailsAsync] API Request to {url}");
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var details = JsonSerializer.Deserialize<SystemDetails>(content, options);
                
                if (details != null)
                {
                    ConsolidateWithEsConfigs(details);
                    return details;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to fetch system details for {systemId}: {ex.Message}");
        }
        return null;
    }

    private async Task<GameDetails?> FetchGameDetailsAsync(string systemId, string path)
    {
        try
        {
            var url = $"/systems/{systemId}/games";
            _logger?.LogInformation($"[FetchGameDetailsAsync] API Request to {url}");
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<EsGameApiData>>(content, options);
                
                if (games != null)
                {
                    // Normalize slashes for comparison
                    var normalizedPath = path.Replace("\\", "/").ToLowerInvariant();
                    // Match path
                    var match = games.FirstOrDefault(g => (g.Path ?? "").Replace("\\", "/").ToLowerInvariant() == normalizedPath);
                    if (match != null)
                    {
                        // Some endpoints map the API "id" differently than Md5, let's make sure Md5 is exposed
                        if (string.IsNullOrEmpty(match.Md5) && !string.IsNullOrEmpty(match.Id))
                            match.Md5 = match.Id;
                            
                        // Enrichir avec infos du gamelist.xml local pour compléter d'éventuels champs (scrap name, ScrapDate, emulator, etc.)
                        ConsolidateWithGamelist(match, systemId);

                        return match;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to fetch game details for {path}: {ex.Message}");
        }
        return null;
    }

    // Helper classes matching the API structure for internal deserialization from ES API
    private class EsGameApiData : GameDetails 
    {
        public string Path { get; set; } = string.Empty;
    }

    private void ConsolidateWithGamelist(EsGameApiData details, string systemId)
    {
        var gamelistPath = $@"C:\RetroBat\roms\{systemId}\gamelist.xml";
        _logger?.LogInformation($"[ConsolidateWithGamelist] Reading data from {gamelistPath}");
        if (!File.Exists(gamelistPath))
            return;

        try
        {
            var targetMd5 = details.Md5;
            var targetPath = Path.GetFileName(details.Path); 
            var xDoc = XDocument.Load(gamelistPath);

            var gameNodes = xDoc.Descendants("game").ToList();
            var targetGame = gameNodes.FirstOrDefault(g => 
                (g.Element("md5")?.Value == targetMd5 && !string.IsNullOrEmpty(targetMd5)) || 
                (g.Element("path")?.Value ?? "").EndsWith(targetPath)
            );

            if (targetGame != null)
            {
                // Consolidate Fields only if not already populated or if we want to overwrite 
                // E.g. we fetch all possible fields into 'details'
                
                // Extract possible custom tags
                var scrapElem = targetGame.Element("scrap");
                if (scrapElem != null)
                {
                    details.ScrapName = scrapElem.Attribute("name")?.Value ?? details.ScrapName;
                    details.ScrapDate = scrapElem.Attribute("date")?.Value ?? details.ScrapDate;
                }

                if (string.IsNullOrEmpty(details.SystemName)) details.SystemName = systemId;
                if (string.IsNullOrEmpty(details.Emulator)) details.Emulator = targetGame.Element("emulator")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Family)) details.Family = targetGame.Element("family")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Arcadesystemname)) details.Arcadesystemname = targetGame.Element("arcadesystemname")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Players)) details.Players = targetGame.Element("players")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Favorite)) details.Favorite = targetGame.Element("favorite")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Hidden)) details.Hidden = targetGame.Element("hidden")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Kidgame)) details.Kidgame = targetGame.Element("kidgame")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Playcount)) details.Playcount = targetGame.Element("playcount")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Lastplayed)) details.Lastplayed = targetGame.Element("lastplayed")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Gametime)) details.Gametime = targetGame.Element("gametime")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Lang)) details.Lang = targetGame.Element("lang")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Region)) details.Region = targetGame.Element("region")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Releasedate)) details.Releasedate = targetGame.Element("releasedate")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Genres)) details.Genres = targetGame.Element("genres")?.Value ?? targetGame.Element("genreId")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Manual)) details.Manual = targetGame.Element("manual")?.Value ?? "";
                
                // Mettre à jour / enrichir les médias de gamelist si jamais ceux de l'api étaient vides
                if (string.IsNullOrEmpty(details.Boxback)) details.Boxback = targetGame.Element("boxback")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Bezel)) details.Bezel = targetGame.Element("bezel")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Fanart)) details.Fanart = targetGame.Element("fanart")?.Value ?? "";
                
                // Consolidation générique pour tous les autres tags potentiels rajoutés manuellement ou par des thèmes/scrappeurs (non exhaustifs)
                if (details.Extras == null) details.Extras = new Dictionary<string, string>();
                
                var knownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "id", "path", "name", "desc", "image", "video", "marquee", "thumbnail",
                    "developer", "publisher", "genre", "rating", "md5", "emulator", "fanart", 
                    "bezel", "boxback", "manual", "releasedate", "family", "genres", "genreid", 
                    "arcadesystemname", "players", "favorite", "hidden", "kidgame", "playcount", 
                    "lastplayed", "gametime", "lang", "region", "scrap", "core"
                };

                foreach (var elem in targetGame.Elements())
                {
                    var name = elem.Name.LocalName;
                    var val = elem.Value;
                    
                    if (!string.IsNullOrEmpty(name) && !knownTags.Contains(name))
                    {
                        // On stocke la valeur principale du noeud
                        if (!string.IsNullOrEmpty(val))
                        {
                            // On la met dans le dico Extras
                            details.Extras[name] = val;
                        }

                        // Et si le noeud a des attributs exotiques
                        if (elem.HasAttributes)
                        {
                            foreach (var attr in elem.Attributes())
                            {
                                details.Extras[$"{name}_{attr.Name.LocalName}"] = attr.Value;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to consolidate gamelist.xml for {systemId}: {ex.Message}");
        }
    }

    private void ConsolidateWithEsConfigs(SystemDetails details)
    {
        var systemName = details.Name;
        if (string.IsNullOrEmpty(systemName)) return;

        // Parse es_systems.cfg
        var esSystemsPath = @"C:\RetroBat\emulationstation\.emulationstation\es_systems.cfg";
        if (File.Exists(esSystemsPath))
        {
            try
            {
                var xDoc = XDocument.Load(esSystemsPath);
                var systemNodes = xDoc.Descendants("system").ToList();
                var targetSystem = systemNodes.FirstOrDefault(s => s.Element("name")?.Value == systemName);
                
                if (targetSystem != null)
                {
                    details.Fullname = targetSystem.Element("fullname")?.Value ?? details.Fullname;
                    details.Manufacturer = targetSystem.Element("manufacturer")?.Value ?? details.Manufacturer;
                    details.Release = targetSystem.Element("release")?.Value ?? details.Release;
                    details.Hardware = targetSystem.Element("hardware")?.Value ?? details.Hardware;
                    details.Path = targetSystem.Element("path")?.Value ?? details.Path;
                    details.Extension = targetSystem.Element("extension")?.Value ?? details.Extension;
                    details.Command = targetSystem.Element("command")?.Value ?? details.Command;
                    details.Platform = targetSystem.Element("platform")?.Value ?? details.Platform;
                    details.Theme = targetSystem.Element("theme")?.Value ?? details.Theme;
                    details.Group = targetSystem.Element("group")?.Value ?? details.Group;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to consolidate es_systems.cfg for {systemName}: {ex.Message}");
            }
        }

        // Parse es_settings.cfg
        var esSettingsPath = @"C:\RetroBat\emulationstation\.emulationstation\es_settings.cfg";
        if (File.Exists(esSettingsPath))
        {
            try
            {
                var xDoc = XDocument.Load(esSettingsPath);
                var configNodes = xDoc.Descendants().Where(e => e.Parent?.Name == "config").ToList();
                
                foreach (var node in configNodes)
                {
                    var nameAttr = node.Attribute("name")?.Value;
                    var valueAttr = node.Attribute("value")?.Value;

                    if (!string.IsNullOrEmpty(nameAttr) && nameAttr.StartsWith($"{systemName}."))
                    {
                        var key = nameAttr.Substring(systemName.Length + 1); // remove "{systemName}."
                        details.Settings[key] = valueAttr ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to consolidate es_settings.cfg for {systemName}: {ex.Message}");
            }
        }
    }

    private void ParseGameSelected(string line, out string systemId, out string path, out string name)
    {
        systemId = "";
        path = "";
        name = "";
        
        var args = ParseArguments(line);
        if (args.Count > 0) systemId = args[0];
        if (args.Count > 1) path = args[1];
        if (args.Count > 2) name = args[2];
        else if (args.Count == 2) name = Path.GetFileNameWithoutExtension(path);
    }

    private void ParseGameStart(string line, out string path, out string longName, out string shortName)
    {
        path = "";
        longName = "";
        shortName = "";
        
        var args = ParseArguments(line);
        if (args.Count > 0) path = args[0];
        if (args.Count > 1) longName = args[1];
        if (args.Count > 2) shortName = args[2];
        else if (args.Count == 1) shortName = Path.GetFileNameWithoutExtension(path);
    }

    private List<string> ParseArguments(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new System.Text.StringBuilder();
        bool inQuotes = false;
        
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
            }
            else
            {
                currentArg.Append(c);
            }
        }
        
        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }
        
        return args;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
        }
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;
}
