// Ported from LedManager (LedManager.Setup/Input/GamepadReader.cs), deliberately and
// without rewriting: it is the reader that measured the CabinetButtons mapping in the
// first place, so reading presses ANY other way would risk resolving a button to a
// different identity than the one the cabinet was configured with.
//
// It lives here because a cabinet must be able to check its wiring WITHOUT LedManager
// installed - the whole point of the exercise. Two copies of this logic is the price;
// two DIFFERENT answers to "which button is this" would be the bug.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Reads gamepad presses the way RetroArch really resolves them: through the SAME
/// SDL2.dll RetroArch uses (emulators\retroarch\SDL2.dll) and the SAME
/// gamecontrollerdb.txt RetroBat ships (system\tools\gamecontrollerdb.txt).
///
/// Crucially, it parses that DB ITSELF (matching the device GUID) and reads the RAW
/// joystick buttons/axes - instead of trusting SDL's GameController layer, whose
/// built-in database can shadow RetroBat's entry and hand back a different button
/// order (this is what silently swapped X/Y for a DirectInput arcade encoder). By
/// applying RetroBat's own mapping to the raw inputs, the identity we measure is
/// exactly what RetroArch sees - so CabinetButtons drives both rmp and MAME cfg
/// correctly with no downstream change.
///
/// The SDL→RetroPad face swap (a→b, b→a, x→y, y→x, shoulders, triggers) is the fixed
/// sdl2 joypad-driver convention, universal to every controller - not a particularism.
/// </summary>
public sealed class CabinetInputReader : IDisposable
{
    // ---- SDL2 P/Invoke (only the handful we need) --------------------------

    private const string Lib = "SDL2";

    [Flags]
    private enum InitFlags : uint
    {
        Joystick = 0x00000200,
        GameController = 0x00002000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlGuid
    {
        public ulong Lo;
        public ulong Hi;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_Init(InitFlags flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_Quit();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_NumJoysticks();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_JoystickOpen(int deviceIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickClose(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickUpdate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickNumButtons(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_JoystickNumAxes(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern short SDL_JoystickGetAxis(IntPtr joystick, int axis);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern SdlGuid SDL_JoystickGetGUID(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_JoystickGetGUIDString(SdlGuid guid, byte[] pszGUID, int cbGUID);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_JoystickName(IntPtr joystick);

    // ---- model -------------------------------------------------------------

    /// <summary>Result of one measurement: the RetroPad identity emitted, the device
    /// that emitted it, and the raw DirectInput button index (or -1 for an axis).</summary>
    public sealed record Press(string Identity, int DeviceIndex, int RawButton);

    /// <summary>An open device with RetroBat's mapping resolved onto its raw inputs.</summary>
    private sealed class Device
    {
        public required IntPtr Handle { get; init; }
        public required string Guid { get; init; }
        public required string Name { get; init; }
        public Dictionary<int, string> ButtonToIdentity { get; } = new();
        public List<(int Axis, int Sign, string Identity)> AxisToIdentity { get; } = new();
        public bool HasMapping => ButtonToIdentity.Count > 0 || AxisToIdentity.Count > 0;
    }

    /// <summary>SDL controller-name → RetroPad identity (fixed sdl2 face swap).</summary>
    private static readonly IReadOnlyDictionary<string, string> FaceSwap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "b", ["b"] = "a", ["x"] = "y", ["y"] = "x",
        ["leftshoulder"] = "l", ["rightshoulder"] = "r",
        ["lefttrigger"] = "l2", ["righttrigger"] = "r2",
        ["back"] = "select", ["start"] = "start",
        ["leftstick"] = "l3", ["rightstick"] = "r3",
    };

    /// <summary>Analog triggers/axes count as pressed past ~50% of the +32767 range.</summary>
    private const short AxisThreshold = 16000;

    private static bool _resolverInstalled;
    private readonly List<Device> _devices = new();
    private IReadOnlyList<string[]> _dbLines = Array.Empty<string[]>();
    private bool _initialized;

    // ---- lifecycle ---------------------------------------------------------

    public (bool Ok, string Message) Initialize(string retroBatRoot)
    {
        try
        {
            InstallResolver(Path.Combine(retroBatRoot, "emulators", "retroarch", "SDL2.dll"));

            // read joystick events without owning a focused SDL window (we are WPF)
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");

            if (SDL_Init(InitFlags.Joystick) != 0)
            {
                return (false, "SDL_Init a échoué.");
            }

            _initialized = true;

            var db = Path.Combine(retroBatRoot, "system", "tools", "gamecontrollerdb.txt");
            _dbLines = LoadDb(db);
            return (true, $"{_dbLines.Count} mappages chargés depuis gamecontrollerdb.txt");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Opens every joystick and resolves RetroBat's mapping onto its raw
    /// inputs. Returns the number of devices we can read an identity from.</summary>
    public int OpenControllers()
    {
        CloseDevices();
        var count = SDL_NumJoysticks();
        for (var i = 0; i < count; i++)
        {
            var handle = SDL_JoystickOpen(i);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            var name = DeviceName(handle);

            // A virtual pad is not a panel. vJoy, ViGEm and the like are created by
            // software - a mapping tool, a streaming client, a wheel driver - and they
            // report the same identities as a real pad. Left in, one would occupy a
            // panel slot the cabinet does not have, and its player numbering would push
            // the REAL panel to player 2.
            if (IsVirtual(name))
            {
                SDL_JoystickClose(handle);
                continue;
            }

            var device = new Device { Handle = handle, Guid = GuidString(handle), Name = name };
            ApplyMapping(device);
            _devices.Add(device);
        }

        return _devices.Count(d => d.HasMapping);
    }

    /// <summary>The names of the devices actually kept, in player order - what the log
    /// has to show for "player 2 lit up" to ever be explainable.</summary>
    public IReadOnlyList<string> DeviceNames => _devices.Select(d => d.Name).ToList();

    /// <summary>How many joysticks Windows currently shows, WITHOUT touching the ones
    /// already open. Asking this before reopening is what lets the watcher leave a
    /// working device alone.</summary>
    public static int AttachedCount()
    {
        try
        {
            SDL_JoystickUpdate();
            return SDL_NumJoysticks();
        }
        catch
        {
            return -1;
        }
    }

    private static readonly string[] VirtualMarkers = { "vjoy", "virtual", "vigem", "dummy", "emulated" };

    private static bool IsVirtual(string name) =>
        VirtualMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string DeviceName(IntPtr joystick)
    {
        var ptr = SDL_JoystickName(joystick);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    /// <summary>Waits for a FRESH press (edge) on any open device and returns the
    /// RetroPad identity + device + raw button. Inputs already held when the call
    /// starts are ignored. Returns null on cancellation.</summary>
    public async Task<Press?> WaitForPressAsync(CancellationToken token)
    {
        if (!_initialized || _devices.Count == 0)
        {
            return null;
        }

        var held = new HashSet<(int, string)>(Snapshot().Select(p => (p.DeviceIndex, p.Identity)));

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(16, CancellationToken.None).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                break;
            }

            var now = Snapshot();
            foreach (var pressed in now)
            {
                if (!held.Contains((pressed.DeviceIndex, pressed.Identity)))
                {
                    return pressed;
                }
            }

            var active = now.Select(p => (p.DeviceIndex, p.Identity)).ToHashSet();
            held.RemoveWhere(h => !active.Contains(h));
        }

        return null;
    }

    /// <summary>Everything held down right now. Public here: the watcher polls it and
    /// diffs, where the wizard only waited for the next new press.</summary>
    public List<Press> Snapshot()
    {
        SDL_JoystickUpdate();
        var result = new List<Press>();
        for (var d = 0; d < _devices.Count; d++)
        {
            var device = _devices[d];
            foreach (var (button, identity) in device.ButtonToIdentity)
            {
                if (SDL_JoystickGetButton(device.Handle, button) != 0)
                {
                    result.Add(new Press(identity, d, button));
                }
            }

            foreach (var (axis, sign, identity) in device.AxisToIdentity)
            {
                var value = SDL_JoystickGetAxis(device.Handle, axis);
                var pressed = sign < 0 ? value < -AxisThreshold : value > AxisThreshold;
                if (pressed)
                {
                    result.Add(new Press(identity, d, -1));
                }
            }
        }

        return result;
    }

    // ---- gamecontrollerdb ---------------------------------------------------

    /// <summary>Parses the DB into split token lines (GUID first, then name, then
    /// key:value pairs), keeping only Windows / platform-less entries.</summary>
    private static IReadOnlyList<string[]> LoadDb(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string[]>();
        }

        var lines = new List<string[]>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var tokens = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var platform = tokens.FirstOrDefault(t => t.StartsWith("platform:", StringComparison.OrdinalIgnoreCase));
            if (platform is not null && !platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(tokens);
        }

        return lines;
    }

    /// <summary>Resolves RetroBat's mapping for this device's GUID onto raw buttons/axes.</summary>
    private void ApplyMapping(Device device)
    {
        var entry = _dbLines.FirstOrDefault(t => string.Equals(t[0], device.Guid, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return; // unknown device: no identity resolved (caller warns)
        }

        foreach (var token in entry.Skip(2))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = token[..colon];
            if (!FaceSwap.TryGetValue(name, out var identity))
            {
                continue; // dpad / guide / misc - not a cabinet identity
            }

            var value = token[(colon + 1)..].TrimEnd('~'); // '~' = inverted axis
            var sign = 0;
            if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
            {
                sign = value[0] == '-' ? -1 : 1;
                value = value[1..];
            }

            if (value.Length < 2)
            {
                continue;
            }

            var kind = value[0];
            if (!int.TryParse(value[1..], out var index))
            {
                continue;
            }

            if (kind == 'b')
            {
                device.ButtonToIdentity[index] = identity;
            }
            else if (kind == 'a')
            {
                device.AxisToIdentity.Add((index, sign, identity));
            }
            // 'h' (hat) inputs are dpad directions - not cabinet identities
        }
    }

    private static string GuidString(IntPtr joystick)
    {
        var guid = SDL_JoystickGetGUID(joystick);
        var buffer = new byte[33];
        SDL_JoystickGetGUIDString(guid, buffer, buffer.Length);
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private void CloseDevices()
    {
        foreach (var device in _devices)
        {
            SDL_JoystickClose(device.Handle);
        }

        _devices.Clear();
    }

    private static void InstallResolver(string sdlPath)
    {
        if (_resolverInstalled)
        {
            return;
        }

        // route the "SDL2" DllImport to RetroArch's exact SDL2.dll
        NativeLibrary.SetDllImportResolver(typeof(CabinetInputReader).Assembly, (name, _, _) =>
            name == Lib && File.Exists(sdlPath) ? NativeLibrary.Load(sdlPath) : IntPtr.Zero);
        _resolverInstalled = true;
    }

    public void Dispose()
    {
        CloseDevices();
        if (_initialized)
        {
            SDL_Quit();
            _initialized = false;
        }
    }
}
