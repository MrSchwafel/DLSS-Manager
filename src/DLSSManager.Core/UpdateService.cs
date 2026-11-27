using System.Net;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DLSSManager.Core;

public enum ReleaseChannel { Stable, Beta }

public sealed record ReleaseCandidate(string Tag, bool Draft, bool Prerelease);

public static partial class UpdateService
{
    [GeneratedRegex(@"\Av?(?<number>[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:-(?<type>beta|rc)(?:\.(?<sequence>[0-9]+))?)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    private static (Version Number, int Rank, long Sequence)? Parse(string tag)
    {
        var match = ReleaseTag().Match(tag);
        if (!match.Success || DllVersionService.Parse(match.Groups["number"].Value) is not { } number)
            return null;

        var rank = match.Groups["type"].Value.ToLowerInvariant() switch
        {
            "beta" => 0,
            "rc" => 1,
            _ => 2
        };
        long sequence = 0;
        if (match.Groups["sequence"].Success && !long.TryParse(match.Groups["sequence"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
            return null;
        return (number, rank, sequence);
    }

    public static async Task<ReleaseCandidate?> CheckManagerAsync(HttpClient client, string installed,
        ReleaseChannel channel, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var releases = new List<ReleaseCandidate>();
        for (var page = 1; page <= 20; page++)
        {
            var uri = new Uri($"https://api.github.com/repos/MrSchwafel/DLSS-Manager/releases?per_page=100&page={page}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DLSSManager-CSharp");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uri)
                throw new IOException("Unerwartete Weiterleitung bei der GitHub-Abfrage.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
            {
                var retry = response.Headers.RetryAfter?.ToString();
                throw new IOException(retry is null ? "GitHub-Anfragelimit erreicht. Später erneut versuchen." :
                    $"GitHub-Anfragelimit erreicht. Retry-After: {retry}.");
            }
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(source, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 100)
                throw new IOException("GitHub lieferte eine ungültige Release-Liste.");

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !item.TryGetProperty("prerelease", out var preview) || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    continue;
                releases.Add(new(tag.GetString()!, draft.GetBoolean(), preview.GetBoolean()));
            }

            if (root.GetArrayLength() < 100) return SelectUpdate(installed, channel, releases);
        }

        throw new IOException("Die GitHub-Release-Liste konnte nicht vollständig ausgewertet werden.");
    }

    public static ReleaseCandidate? SelectUpdate(string installed, ReleaseChannel channel, IEnumerable<ReleaseCandidate> releases)
    {
        var current = Parse(installed) ?? throw new ArgumentException("Installierte Release-Version ist ungültig.", nameof(installed));
        ReleaseCandidate? selected = null;
        foreach (var release in releases)
        {
            if (release.Draft || Parse(release.Tag) is not { } version || release.Prerelease != (version.Rank < 2) ||
                (channel == ReleaseChannel.Stable && release.Prerelease) || version.CompareTo(current) <= 0)
                continue;

            current = version;
            selected = release;
        }
        return selected;
    }
}
