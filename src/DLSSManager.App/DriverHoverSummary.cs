using System.Text.RegularExpressions;

namespace DLSSManager.App;

internal sealed record DriverHoverFix(string? AppPrefix, string Description);

internal sealed partial record DriverHoverSummary(
    string Highlights, string DetailedHighlights, string[] Badges, string[] AllFixes, string? HotfixDetails)
{
    private enum Section { Intro, Highlights, Fixes, Ignored }

    public string[] Fixes { get; } = AllFixes.Take(3).ToArray();
    public DriverHoverFix[] DetailedFixes { get; } = AllFixes.Length > 0
        ? AllFixes.Select(FormatFix).ToArray()
        : HotfixDetails is not null ? [FormatFix(HotfixDetails)] : [];
    public string? HotfixMessage { get; } = HotfixDetails is null ? null : Sentences().Split(HotfixDetails, 2)[0];
    public bool HasHighlights => Highlights.Length != 0;
    public bool HasDetailedHighlights => DetailedHighlights.Length != 0;
    public bool HasFixes => AllFixes.Length != 0;
    public bool HasHotfixMessage => HotfixMessage is not null;
    public bool HasBadges => Badges.Length != 0;
    public bool HasDetailedFixes => DetailedFixes.Length != 0;

    public static DriverHoverSummary Create(string? activeNotes, string? originalNotes, bool isHotfix)
    {
        var active = Parse(activeNotes, isHotfix);
        if (string.Equals(activeNotes, originalNotes, StringComparison.Ordinal))
            return active;

        var original = Parse(originalNotes, isHotfix);
        return new DriverHoverSummary(
            active.HasHighlights ? active.Highlights : original.Highlights,
            active.HasDetailedHighlights ? active.DetailedHighlights : original.DetailedHighlights,
            active.HasBadges ? active.Badges : original.Badges,
            active.HasFixes ? active.AllFixes : original.AllFixes,
            active.HasFixes || original.HasFixes ? null : active.HotfixDetails ?? original.HotfixDetails);
    }

    private static DriverHoverSummary Parse(string? notes, bool isHotfix)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return new("", "", [], [], null);

        var highlights = new List<string>();
        var detailedHighlights = new List<string>();
        var badges = new List<string>();
        var fixes = new List<string>();
        string? hotfixDetails = null;
        var section = Section.Intro;

        var lines = SectionBreak().Replace(notes, "\n")
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var heading = SectionHeading().Match(line);
            if (heading.Success)
            {
                section = heading.Groups["highlights"].Success ? Section.Highlights
                    : heading.Groups["fixes"].Success ? Section.Fixes : Section.Ignored;
                line = line[heading.Length..];
            }

            if (section == Section.Ignored)
                continue;

            var entries = line.Split(['•', '●', '▪'], StringSplitOptions.TrimEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                var listPrefix = ListPrefix().Match(entries[i]);
                var isBullet = i > 0 || listPrefix.Success;
                var entry = entries[i][listPrefix.Length..];
                var hasBugId = BugId().IsMatch(entry);
                var text = Footer().Split(entry, 2)[0];
                text = BugId().Replace(text, "");
                text = WebsiteSuffix().Replace(text, "");
                text = Whitespace().Replace(text, " ").Trim(' ', '-', '–', '—', ':');
                if (text.Length == 0 || EmptyEntry().IsMatch(text))
                    continue;

                if (section == Section.Highlights)
                {
                    AddHighlights(highlights, detailedHighlights, badges, text, inHighlightsSection: true);
                }
                else if (isHotfix && !isBullet && section != Section.Fixes)
                {
                    var message = HotfixTitle().Replace(text, "").Trim();
                    if (message.Length > 0 && !HotfixBoilerplate().IsMatch(message))
                    {
                        if (hotfixDetails is null ||
                            (!FixIntro().IsMatch(hotfixDetails) && FixIntro().IsMatch(message)))
                            hotfixDetails = message;
                    }
                }
                else if (section == Section.Fixes || isHotfix || hasBugId || FixIntro().IsMatch(text))
                {
                    fixes.Add(text);
                }
                else
                {
                    AddHighlights(highlights, detailedHighlights, badges, text, inHighlightsSection: false);
                }
            }
        }

        var allFixes = fixes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var highlightText = string.Join(", ", highlights.Distinct(StringComparer.OrdinalIgnoreCase));
        return new(FeatureVersionGap().Replace(highlightText, "\u00A0"),
            string.Join("\n\n", detailedHighlights.Distinct(StringComparer.OrdinalIgnoreCase)),
            badges.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            allFixes, allFixes.Length == 0 ? hotfixDetails : null);
    }

    private static void AddHighlights(
        List<string> highlights, List<string> detailedHighlights, List<string> badges, string text, bool inHighlightsSection)
    {
        var isHighlight = inHighlightsSection;
        foreach (var sentence in Sentences().Split(text))
        {
            var intro = HighlightExamples().Match(sentence);
            if (!intro.Success)
                intro = HighlightIntro().Match(sentence);
            if (!intro.Success && !inHighlightsSection)
            {
                if (DetailedFeatureIntro().IsMatch(sentence))
                {
                    foreach (Match feature in DetailedFeatureName().Matches(sentence))
                    {
                        badges.Add(feature.Value);
                        isHighlight = true;
                    }
                }
                continue;
            }

            isHighlight = true;
            var names = intro.Success ? sentence[(intro.Index + intro.Length)..] : sentence;
            names = HighlightDetail().Split(names, 2)[0].Trim(' ', '.', ':', ';');
            var hasNames = names.Length > 0 && !MarketingIntro().IsMatch(names) && !EmptyEntry().IsMatch(names);
            if (hasNames)
            {
                highlights.Add(names);
                foreach (var item in BadgeSeparator().Split(names))
                {
                    var label = item.Trim(' ', ',', '.', ':', ';');
                    var feature = DetailedFeatureName().Match(label);
                    if (feature.Success)
                    {
                        var prefix = label[..feature.Index];
                        var qualifier = FeatureQualifier().Match(prefix);
                        label = qualifier.Success ? prefix[..qualifier.Index].TrimEnd() : "";
                    }

                    if (label.Length > 0 && !EmptyEntry().IsMatch(label))
                        badges.Add(label);
                }
            }

            foreach (Match feature in FeatureName().Matches(sentence))
            {
                if (!hasNames || !names.Contains(feature.Value, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(feature.Value);
            }

            foreach (Match feature in DetailedFeatureName().Matches(sentence))
                badges.Add(feature.Value);
        }

        if (isHighlight)
            detailedHighlights.Add(text);
    }

    private static DriverHoverFix FormatFix(string text)
    {
        if (text.StartsWith('[') && text.IndexOf(']') is > 1 and var bracket)
        {
            var description = text[(bracket + 1)..].TrimStart(' ', ':');
            if (description.Length > 0)
                return new($"{text[1..bracket].Trim()}: ", description);
        }

        var separator = text.IndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
            return new(null, text);

        var appName = text[..separator].Trim().Trim('[', ']');
        return new($"{appName}: ", text[(separator + 1)..].TrimStart());
    }

    [GeneratedRegex(@"[ \t]+(?=(?:Fixed (?:Gaming|General|Application) (?:Bugs|Issues)|(?:Open|Known) Issues)\s*[:：•●▪])", RegexOptions.IgnoreCase)]
    private static partial Regex SectionBreak();

    [GeneratedRegex(@"^(?:(?<highlights>Game Ready(?: Driver)?(?: Highlights)?|Gaming Technology|Highlights|New Features(?: and Changes)?|Support for New Games|Unterstützte Spiele|Neue Funktionen)|(?<fixes>Fixed (?:(?:Gaming|General|Application) )?(?:Bugs|Issues)|(?:Gaming|General) Bugs Fixed|Bug Fixes|Fixes|(?:Behobene|Korrigierte) (?:Spiel(?:e)?fehler|Gaming-Fehler|(?:allgemeine )?Fehler|Probleme))|(?<ignored>(?:(?:Open|Known) (?:Issues|Bugs)|(?:Offene|Bekannte) Probleme)(?: in (?:Version )?[\d.]+)?|Release Notes))\s*(?:[:：]\s*|(?=[•●▪])|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^(?:[-*–]\s+|\d+[.)]\s+)")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"[ \t]*\[\s*\d+(?:\s*[,;/]\s*\d+)*\s*\]")]
    private static partial Regex BugId();

    [GeneratedRegex(@"\b(?:Learn more in|Learn more about|Read more in|Find out more at)\b|^(?:Learn more|Read more|Find out more|Click here|Please (?:visit|see)|Weitere Informationen|Erfahren Sie mehr|Klicken Sie hier)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Footer();

    [GeneratedRegex(@"\s+[-–—|]\s+(?:TechPowerUp|VideoCardz(?:\.com)?|Guru3D(?:\.com)?|Wccftech|NVIDIA(?: Support)?|Tom['’]s Hardware)\s*[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex WebsiteSuffix();

    [GeneratedRegex(@"^(?:N\s*/?\s*A|None|Not applicable|No (?:known )?(?:fixes|issues)|Keine(?:r|s)?|Entfällt|Nicht zutreffend|K\.\s*A\.)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyEntry();

    [GeneratedRegex(@"^(?:(?:NVIDIA\s+(?:releases\s+)?)?(?:GeForce\s+)?Hotfix(?: (?:Display|Graphics))? Driver(?: version)?\s+v?\d+\.\d+\s*[:–—-]?\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex HotfixTitle();

    [GeneratedRegex(@"^(?:is based on|based on|basiert auf|This (?:hotfix|driver) (?:addresses|fixes) the following|Download\b|GeForce Hotfix Display Driver\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HotfixBoilerplate();

    [GeneratedRegex(@"^(?:fix(?:ed|es)?|resolv(?:ed|es)|address(?:ed|es)|correct(?:ed|s)|beheb(?:t|en)|behoben)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FixIntro();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"\b(?:including|such as|einschließlich|darunter)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightExamples();

    [GeneratedRegex(@"\b(?:includes?|(?:provides|offers) the (?:best|optimal) (?:day[- ](?:0|one) )?(?:gaming )?experience for|Game Ready for|support for|supports|Unterstützung für|unterstützt)\s+(?:the (?:launch|release) of\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightIntro();

    [GeneratedRegex(@"(?:^|,\s*|\s+)(?:which\b|featuring\b|with support for\b|that (?:features|supports|uses)\b|(?:(?:the|latest|new)\s+)*(?:games|titles) (?:that )?(?:supporting|support|leveraging|using)\b|mit Unterstützung für\b|die (?:DLSS|RTX)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightDetail();

    [GeneratedRegex(@"^(?:This|These|Our|In addition|It|With this|The latest|The new|Dieser|Diese|Der neue|Außerdem)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarketingIntro();

    private const string FeaturePattern =
        @"\b(?:DLSS(?:\s+\d+(?:\.\d+)?)?(?:\s+(?:Multi Frame Generation|Frame Generation|Ray Reconstruction|Super Resolution))?|RTX\s+(?:HDR|Video(?: Super Resolution| HDR)?|Dynamic Vibrance|Mega Geometry|Neural Shaders)|(?:Multi )?Frame Generation|Reflex(?:\s+\d+)?)\b";

    [GeneratedRegex(FeaturePattern, RegexOptions.IgnoreCase)]
    private static partial Regex FeatureName();

    [GeneratedRegex(FeaturePattern + @"|\b(?:Neural Rendering|Smooth Motion|Ray Reconstruction|Path Tracing|Ray Tracing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DetailedFeatureName();

    [GeneratedRegex(@"\b(?:introduces?|enables?|adds?|introducing|enabled|added)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DetailedFeatureIntro();

    [GeneratedRegex(@",(?!\d)\s*(?:(?i:and|und)\s+)?|;\s*|\s+(?i:and|as well as|und|sowie)\s+(?=[\p{Lu}\d])")]
    private static partial Regex BadgeSeparator();

    [GeneratedRegex(@"\s+(?:with|using|featuring|supports?|mit)(?:\s+(?:support for|NVIDIA))*\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureQualifier();

    [GeneratedRegex(@"(?<=\bDLSS|\bReflex) (?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureVersionGap();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
