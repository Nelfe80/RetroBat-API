using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Keeps <c>&lt;system&gt;.MonitorIndex</c> in es_settings.cfg pointing at the RetroBat
/// screen so standalone emulators (MAME/FBNeo…) launch there. emulatorLauncher builds
/// the target monitor as <c>"\\.\DISPLAY" + MonitorIndex</c> (see Mame64.Generator.cs),
/// so the value must be the GDI device NUMBER of the RetroBat monitor (e.g.
/// <c>\\.\DISPLAY24</c> → 24) - a number the ES "Monitor Index" dropdown (0..10) cannot
/// reach and that Windows renumbers on display reconfig. Recomputed at startup and on
/// demand: Marquee Manager pushes the currently assigned game-screen device name when it
/// changes. Only writes when the value differs (the store backs up es_settings.cfg).
/// </summary>
public sealed class MonitorIndexSyncService : IHostedService
{
    private readonly IEsSettingsStore _settingsStore;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MonitorIndexSyncService> _logger;

    public MonitorIndexSyncService(
        IEsSettingsStore settingsStore,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MonitorIndexSyncService> logger)
    {
        _settingsStore = settingsStore;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Sync(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MonitorIndex startup sync failed.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Resolves the RetroBat monitor's GDI device number and writes it to
    /// <c>&lt;system&gt;.MonitorIndex</c> for every configured system. <paramref name="deviceName"/>
    /// (optional, e.g. <c>\\.\DISPLAY1</c>) comes from Marquee Manager (the currently
    /// assigned game screen); otherwise the live EmulationStation window's monitor, else
    /// the primary screen. Returns the applied index, or null when disabled/unresolved.</summary>
    public int? Sync(string? deviceName)
    {
        var opt = _options.CurrentValue.MonitorSync;
        if (!opt.Enabled)
        {
            return null;
        }

        var device = ResolveDeviceName(deviceName);
        var index = DeviceNumber(device);
        if (index == null)
        {
            _logger.LogWarning("MonitorIndex sync: could not resolve a \\.\\DISPLAYn device (got '{Device}').", device);
            return null;
        }

        var value = index.Value.ToString(CultureInfo.InvariantCulture);
        var systems = opt.Systems ?? Array.Empty<string>();

        var changed = _settingsStore.Update(document =>
        {
            var root = document.Root;
            if (root == null)
            {
                return false;
            }

            var updated = false;
            foreach (var system in systems)
            {
                if (string.IsNullOrWhiteSpace(system))
                {
                    continue;
                }

                var key = system.Trim().ToLowerInvariant() + ".MonitorIndex";
                var existing = root.Elements().FirstOrDefault(e =>
                    string.Equals(e.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    if (!string.Equals(existing.Attribute("value")?.Value ?? string.Empty, value, StringComparison.Ordinal))
                    {
                        existing.SetAttributeValue("value", value);
                        updated = true;
                    }
                }
                else
                {
                    root.Add(new XText(Environment.NewLine + "  "));
                    root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", value)));
                    updated = true;
                }
            }

            if (updated)
            {
                root.Add(new XText(Environment.NewLine));
            }

            return updated;
        }, CancellationToken.None);

        if (changed)
        {
            _logger.LogInformation("MonitorIndex synced to {Index} ({Device}) for {Count} system(s).",
                index, device, systems.Length);
        }

        return index;
    }

    private static string ResolveDeviceName(string? pushed)
    {
        if (!string.IsNullOrWhiteSpace(pushed) &&
            pushed.StartsWith(DisplayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return pushed;
        }

        return ResolveEmulationStationScreen()?.DeviceName
               ?? Screen.PrimaryScreen?.DeviceName
               ?? string.Empty;
    }

    private const string DisplayPrefix = @"\\.\DISPLAY";

    // \\.\DISPLAY24 -> 24
    private static int? DeviceNumber(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return null;
        }

        var i = deviceName.IndexOf(DisplayPrefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var digits = new string(deviceName[(i + DisplayPrefix.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static Screen? ResolveEmulationStationScreen()
    {
        var handle = FindWindowW(null, "EmulationStation");
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return null;
        }

        return Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
    }
}
