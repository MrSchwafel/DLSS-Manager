using DLSSManager.Core;
using System.Runtime.InteropServices;
using System.Windows;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    private string catalogFilter = "Alle";
    private string catalogSearch = "";

    public string CatalogFilter
    {
        get => catalogFilter;
        set
        {
            if (catalogFilter == value) return;
            Set(ref catalogFilter, value);
            Notify(nameof(CatalogRows));
        }
    }

    public string CatalogSearch
    {
        get => catalogSearch;
        set
        {
            Set(ref catalogSearch, value);
            Notify(nameof(CatalogRows));
        }
    }

    public DllCatalogView.Row[] CatalogRows
    {
        get
        {
            var search = CatalogSearch.Trim();
            return CatalogEntries.Where(entry => CatalogFilter switch
                {
                    "SR" or "FG" => entry.Kind == CatalogFilter,
                    "Lokal" or "Online" => entry.Availability == CatalogFilter,
                    _ => true
                })
                .Where(entry => search.Length == 0 || entry.Version.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    entry.Sha256?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                    entry.DllMd5.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new DllCatalogView.Row(entry, settings.BatchTargets?.Any(target =>
                    target.Kind == entry.Kind && target.Version == entry.Version &&
                    target.CatalogHash == CatalogIdentity(entry)) == true))
                .ToArray();
        }
    }

    internal void CopyCatalogHash(string hash)
    {
        try
        {
            Clipboard.SetText(hash);
            Toast = Status = "SHA-256 kopiert.";
        }
        catch (ExternalException)
        {
            Toast = Status = "SHA-256 konnte nicht kopiert werden. Die Zwischenablage ist belegt.";
        }
    }

    public string CatalogCounts
    {
        get
        {
            var local = CatalogEntries.Count(entry => entry.Availability == "Lokal");
            return $"{local} lokal · {CatalogEntries.Length - local} online";
        }
    }

    public string UnlockerCacheLabel => $"FG & MFG Unlocker: {modReleaseTag ?? "kein Paket"}";
    internal Task OpenUnlockerReleasesAsync() =>
        RunAsync(_ => OpenShell("https://github.com/sdli1995/dlssg_for_sm86/releases"));

    private static string? CatalogIdentity(DllCatalogEntry entry) =>
        entry.LocalPath is null ? entry.DllMd5 : entry.Sha256;

    internal static bool CanPinCatalogEntry(DllCatalogEntry entry) =>
        entry.Availability == "Lokal" && entry.Kind is "SR" or "FG" or "RR" or "XeSS" or "FSR" &&
        !string.IsNullOrEmpty(CatalogIdentity(entry));

    internal Task PinCatalogEntryAsync(DllCatalogEntry entry) => RunAsync(async token =>
    {
        if (!CanPinCatalogEntry(entry)) return;
        var targets = (settings.BatchTargets ?? []).Where(target => target.Kind != entry.Kind);
        await SaveSettingsAsync(settings with
        {
            BatchTargets = [.. targets, new(entry.Kind, entry.Version, CatalogIdentity(entry)!)]
        }, token);
        Toast = Status = $"{entry.Kind} {entry.Version} als Batch-Ziel gespeichert.";
    });

    internal Task DownloadCatalogEntriesAsync(IReadOnlyList<DllCatalogEntry> entries) => RunAsync(async token =>
    {
        var pending = entries.Where(entry => entry.Availability == "Online").Distinct().ToArray();
        if (pending.Length == 0) return;
        var downloader = Downloader();
        var cached = new Dictionary<DllCatalogEntry, DllCatalogEntry>();
        var notes = new List<string>();
        try
        {
            for (var i = 0; i < pending.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = pending[i];
                Status = $"{i + 1}/{pending.Length} · {entry.Kind} {entry.Version} wird geladen und geprüft …";
                try
                {
                    var result = await Task.Run(async () =>
                    {
                        var path = await downloader.GetAsync(entry, token);
                        return (Path: path, DllDownloadService.VerifyCatalogFile(path, entry, token).Sha256);
                    }, token);
                    cached.Add(entry, entry with { Sha256 = result.Sha256 });
                    notes.Add(result.Path);
                }
                catch (Exception error) when (IsPackageFailure(error) ||
                    error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    notes.Add($"{entry.Kind} {entry.Version}: {error.Message}");
                }
                Percent = (i + 1) * 100d / pending.Length;
            }
            Status = $"{cached.Count}/{pending.Length} DLLs geprüft und lokal verfügbar." +
                (cached.Count < pending.Length ? " Details unter Hinweise und Messwerte." : "");
        }
        finally
        {
            if (cached.Count > 0)
                CatalogEntries = CatalogEntries.Select(entry => cached.GetValueOrDefault(entry, entry)).ToArray();
            Notes = [.. notes];
        }
    });
}
