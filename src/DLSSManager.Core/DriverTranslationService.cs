using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DLSSManager.Core;

public static class ClientLanguageProvider
{
    public static string GetLanguageCode(string? preferredLanguage = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage) &&
            !preferredLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = preferredLanguage.Trim();
            var dash = trimmed.IndexOfAny(['-', '_']);
            return (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();
        }

        var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.IsNullOrWhiteSpace(iso) ? "en" : iso.ToLowerInvariant();
    }

    public static bool CanTranslateFromEnglish(string? languageCode) =>
        !string.IsNullOrWhiteSpace(languageCode) &&
        !languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) &&
        !languageCode.Equals("iv", StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string languageCode) =>
        languageCode.ToLowerInvariant() switch
        {
            "de" => "Deutsch",
            "fr" => "Französisch",
            "es" => "Spanisch",
            "it" => "Italienisch",
            "pt" => "Portugiesisch",
            "nl" => "Niederländisch",
            "pl" => "Polnisch",
            "ja" => "Japanisch",
            "ko" => "Koreanisch",
            "zh" => "Chinesisch",
            _ => TryNativeName(languageCode)
        };

    private static string TryNativeName(string languageCode)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageCode).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return languageCode.ToUpperInvariant();
        }
    }
}

public sealed class DriverSummaryTranslator
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(10);

    private readonly HttpClient client;
    private readonly string? cacheFilePath;
    private readonly Func<string?>? apiKeyProvider;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, string> memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim ioLock = new(1, 1);
    private bool cacheLoaded;
    private DateTimeOffset? blockedUntilUtc;

    public DriverSummaryTranslator(
        HttpClient client,
        string? cacheFilePath = null,
        Func<string?>? apiKeyProvider = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.client = client;
        this.cacheFilePath = cacheFilePath;
        this.apiKeyProvider = apiKeyProvider;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<string?> TranslateAsync(
        string driverVersion,
        string? cleanedEnglishText,
        string targetLanguage,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cleanedEnglishText) ||
            !ClientLanguageProvider.CanTranslateFromEnglish(targetLanguage))
        {
            return cleanedEnglishText;
        }

        var normalizedLang = ClientLanguageProvider.GetLanguageCode(targetLanguage);
        var key = $"{driverVersion.Trim()}|{normalizedLang}";

        await EnsureCacheLoadedAsync(token).ConfigureAwait(false);
        if (memoryCache.TryGetValue(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var now = clock();
        if (blockedUntilUtc is { } blocked && blocked > now)
            return cleanedEnglishText;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(RequestTimeout);

            var apiKey = apiKeyProvider?.Invoke()?.Trim();
            var translated = !string.IsNullOrWhiteSpace(apiKey)
                ? await TranslateWithCloudApiAsync(cleanedEnglishText, normalizedLang, apiKey, now, timeoutCts.Token).ConfigureAwait(false)
                : await TranslateWithPublicEndpointAsync(cleanedEnglishText, normalizedLang, now, timeoutCts.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(translated))
                return cleanedEnglishText;

            memoryCache[key] = translated;
            await PersistCacheAsync(token).ConfigureAwait(false);
            return translated;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException ||
                                  (e is OperationCanceledException && !token.IsCancellationRequested))
        {
            return cleanedEnglishText;
        }
    }

    private async Task<string?> TranslateWithPublicEndpointAsync(
        string text,
        string targetLanguage,
        DateTimeOffset now,
        CancellationToken token)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en" +
            $"&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", HotfixFeedScanner.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", HotfixFeedScanner.AcceptLanguage);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            blockedUntilUtc = now + RateLimitCooldown;
            return null;
        }
        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var segments = doc.RootElement[0];
        if (segments.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array &&
                segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(segment[0].GetString());
            }
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async Task<string?> TranslateWithCloudApiAsync(
        string text,
        string targetLanguage,
        string apiKey,
        DateTimeOffset now,
        CancellationToken token)
    {
        var url = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(apiKey)}";
        var body = JsonSerializer.Serialize(new
        {
            q = text,
            source = "en",
            target = targetLanguage,
            format = "text"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            blockedUntilUtc = now + RateLimitCooldown;
            return null;
        }
        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array ||
            translations.GetArrayLength() == 0)
            return null;

        var translated = translations[0].TryGetProperty("translatedText", out var textElem)
            ? WebUtility.HtmlDecode(textElem.GetString())?.Trim()
            : null;
        return string.IsNullOrWhiteSpace(translated) ? null : translated;
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken token)
    {
        if (cacheLoaded || string.IsNullOrWhiteSpace(cacheFilePath))
            return;

        await ioLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cacheLoaded) return;
            cacheLoaded = true;
            if (!File.Exists(cacheFilePath)) return;

            SafePaths.RejectLinks(cacheFilePath);
            var json = await File.ReadAllTextAsync(cacheFilePath, token).ConfigureAwait(false);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (stored is null) return;

            foreach (var (k, v) in stored)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                    memoryCache.TryAdd(k, v);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        finally
        {
            ioLock.Release();
        }
    }

    private async Task PersistCacheAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(cacheFilePath))
            return;

        await ioLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var full = SafePaths.Root(cacheFilePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var snapshot = new Dictionary<string, string>(memoryCache, StringComparer.OrdinalIgnoreCase);
            DurableFiles.Json(full, snapshot, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            ioLock.Release();
        }
    }
}
