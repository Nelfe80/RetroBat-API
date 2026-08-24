using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Sur quoi cette machine fait tourner les jeux.
///
/// Le Passeport de jouabilite se remplissait a la main : quelqu'un decrivait
/// un environnement, et cette description valait ce que vaut une declaration.
/// La machine, elle, SAIT - elle connait ses versions et son materiel, et les
/// dire ne lui coute rien. Ce qui suit est donc un releve, pas un formulaire.
///
/// Deux precautions dans ce qui est retenu.
///
/// LES VERSIONS SONT VOLONTAIREMENT GROSSIERES. « Windows 11 » plutot que
/// « 10.0.26200 », « 1.22.2 » plutot que le hachage git : le serveur regroupe
/// les machines par configuration et n'en publie un resultat qu'a partir de
/// trois. Une precision inutile eclaterait chaque configuration en exemplaires
/// uniques, et aucune n'atteindrait jamais ce seuil.
///
/// LE RELEVE NE COUTE RIEN AU JOUEUR. Il est calcule UNE fois par demarrage :
/// ces valeurs ne changent pas pendant une session, et interroger RetroArch a
/// chaque partie lancerait un processus au moment precis ou la machine est
/// occupee a demarrer un jeu.
/// </summary>
public static class NelfePlayEnvironment
{
    private static readonly Lazy<Snapshot> Value = new(Resolve, isThreadSafe: true);

    public static Snapshot Current => Value.Value;

    public sealed record Snapshot(
        string RetroBat,
        string RetroArch,
        string EmulationStation,
        string Windows,
        string Cpu,
        string Gpu,
        int RamMb);

    private static Snapshot Resolve() => new(
        ReadRetroBatVersion(),
        ReadRetroArchVersion(),
        ReadEmulationStationVersion(),
        ReadWindowsVersion(),
        ReadRegistryValue(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString"),
        ReadRegistryValue(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000",
            "DriverDesc"),
        ReadRamMegabytes());

    /// <summary>RetroBat depose sa version dans un fichier : rien a lancer.</summary>
    private static string ReadRetroBatVersion()
    {
        try
        {
            var path = Path.Combine(RetroBatPaths.RetroBatRoot, "system", "version.info");

            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// RetroArch repond a <c>--version</c> et ecrit « Version: 1.22.2 (Git …) ».
    /// On ne garde que le numero : le hachage git changerait a chaque nightly
    /// et separerait des machines qui font pourtant tourner la meme chose.
    /// </summary>
    private static string ReadRetroArchVersion()
    {
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var output = RunForOutput(exe, "--version");
        var match = Regex.Match(output, @"Version:\s*([0-9][0-9A-Za-z.\-]*)");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// EmulationStation n'a PAS de <c>--version</c>.
    ///
    /// L'essayer ne renvoie rien d'exploitable et, pire, la fait creer son
    /// dossier de configuration dans le profil de l'utilisateur : une sonde qui
    /// modifie la machine qu'elle observe n'est pas une sonde. La version est
    /// donc lue dans les ressources de l'executable, ce qui ne demarre rien.
    ///
    /// On retient la version PRODUIT (« 42 ») et non la version fichier
    /// (« 42.2026.0411.0857 ») : RetroBat livre une EmulationStation par
    /// version, et l'horodatage de compilation n'apprendrait rien de plus sur
    /// la compatibilite d'un jeu.
    /// </summary>
    private static string ReadEmulationStationVersion()
    {
        try
        {
            var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "emulationstation.exe");
            if (!File.Exists(exe))
            {
                return string.Empty;
            }

            var info = FileVersionInfo.GetVersionInfo(exe);

            return (info.ProductVersion ?? info.FileVersion ?? string.Empty).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// « 11 » ou « 10 », rien de plus.
    ///
    /// Le numero de build distingue deux machines a jour a quinze jours d'ecart,
    /// ce qui n'a jamais explique qu'un jeu se lance ou non - mais suffirait a
    /// les empecher de compter ensemble.
    /// </summary>
    private static string ReadWindowsVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        var version = Environment.OSVersion.Version;

        return version.Major >= 10 && version.Build >= 22000
            ? "11"
            : version.Major.ToString();
    }

    private static string ReadRegistryValue(string subKey, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);

            return (key?.GetValue(name) as string ?? string.Empty).Trim();
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static int ReadRamMegabytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

            return GlobalMemoryStatusEx(ref status)
                ? (int)(status.TotalPhys / (1024 * 1024))
                : 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Lance un utilitaire pour lire ce qu'il ecrit, et l'abandonne s'il traine.
    ///
    /// Sans delai maximum, un executable qui attendrait une touche bloquerait le
    /// releve pour toujours - et avec lui le service qui l'a demande.
    /// </summary>
    private static string RunForOutput(string exePath, string arguments)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                return string.Empty;
            }

            using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
            });

            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Deja termine entre-temps.
                }
            }

            return output;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
