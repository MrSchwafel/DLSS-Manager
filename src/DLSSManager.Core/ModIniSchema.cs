using System.Globalization;
using System.Text.RegularExpressions;

namespace DLSSManager.Core;

public sealed record IniChoice(string Value, string Label);
public sealed record IniNumber(double Min, double Max, double Step, bool AllowsAuto);
public sealed record IniField(IniSetting Default, string Description, bool Important,
    IReadOnlyList<IniChoice> Choices, IniNumber? Number, bool EditableChoices = false);

public static partial class ModIniSchema
{
    private static readonly string[] Booleans = ["auto", "true", "false"];

    private static readonly Dictionary<string, string[]> Sm86 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["General|Enabled"] = ["1:1 - Aktiviert (Standard)", "0:0 - Deaktiviert"],
        ["FrameGeneration|Engine"] = ["DLSS:DLSS (NVIDIA SM86/SM75)", "XeSS:XeSS (Intel XeSS-FG)"],
        ["FrameGeneration|MaxGeneratedFrames"] = ["1:1 (2X)", "2:2 (3X)", "3:3 (4X - Standard)", "4:4 (5X)", "5:5 (6X - Max)"],
        ["DLSSG|Optimized"] = ["0:0 - Original (Stock)", "1:1 - Bit-Identisch (Standard)", "2:2 - Optimiert / Leicht verlustbehaftet", "3:3 - Maximal schnell / Verlustbehaftet"],
        ["DLSSG|Preset"] = ["A:A (Standard)", "B:B (UI-Rekombination)", "C:C (FG Lite - schneller)"],
        ["XeSSFG|FlowResolution"] = ["Coarse", "Middle:Middle (Standard)", "Fine", "CoarseAt4K"],
        ["XeSSFG|UseHudless"] = ["Auto:Auto (Standard)", "1:1 - Immer UI-Komposition", "0:0 - Nie"],
        ["SuperResolution|Mode"] = ["Off:Off (Standard)", "M-INT8", "L-INT8", "M-FP16", "L-FP16"],
        ["Logging|Level"] = ["0:0 - Aus", "1:1 - Fehler (Standard)", "2:2 - Info / Konfiguration", "3:3 - Debug / Trace"],
        ["Logging|Directory"] = [],
        ["Runtime|Mode"] = ["Bundled:Bundled (Standard)", "External", "Pinned"]
    };

    private static readonly Dictionary<string, string[]> Opti = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Upscalers|Dx12Upscaler"] = ["auto", "dlss", "xess", "ffx", "fsr22", "fsr21"],
        ["Upscalers|Dx11Upscaler"] = ["auto", "fsr22", "fsr31", "xess", "xess_12", "ffx_12", "fsr22_12", "fsr21_12", "dlss"],
        ["Upscalers|VulkanUpscaler"] = ["auto", "fsr22", "fsr21", "ffx", "xess", "ffx_12", "fsr21_12", "dlss"],
        ["Upscalers|RRUpscaler"] = ["auto", "dlssd", "dlss", "xess", "ffx", "fsr22", "fsr21"],
        ["FrameGen|Enabled"] = Booleans,
        ["FrameGen|FGInput"] = ["auto", "nofg", "dlssg", "nvngxfg", "fsrfg", "upscaler", "fsrfg30", "xefg"],
        ["FrameGen|FGOutput"] = ["auto", "nofg", "fsrfg", "xefg", "dlssg", "flipmeter"],
        ["FrameGen|FGNvngxReplacement"] = ["auto", "None", "Nukems", "Arturs", "FFX", "Combo"],
        ["OptiFG|HUDFix"] = Booleans,
        ["DLSS|RenderPresetOverride"] = Booleans,
        ["DLSS|RenderPresetForAll"] = ["auto", "0:0 - Default", "1:1 - A", "2:2 - B", "3:3 - C", "4:4 - D", "5:5 - E", "6:6 - F", "10:10 - J", "11:11 - K", "12:12 - L", "13:13 - M", "14:14 - N", "15:15 - O"],
        ["FSR|UpscalerIndex"] = ["auto", "0:0 - FSR 4.0.2", "1:1 - FSR 3.1.5", "2:2 - FSR 2.3.4"],
        ["FSR|Fsr4GpuSupport"] = ["auto", "0:0 - No override", "1:1 - FP8", "2:2 - INT8", "3:3 - INT8-2", "4:4 - INT8 Lite"],
        ["Sharpness|Shader"] = ["auto", "rcas", "da", "lcda"],
        ["Sharpness|OverrideSharpness"] = Booleans,
        ["Sharpness|Sharpness"] = [],
        ["CAS|Enabled"] = Booleans,
        ["Spoofing|SpoofedGPUName"] = ["auto", "NVIDIA GeForce RTX 4090", "AMD Radeon RX 9070 XT", "Intel(R) Arc(TM) B580 Graphics"],
        ["Spoofing|Dxgi"] = Booleans,
        ["Spoofing|StreamlineSpoofing"] = Booleans,
        ["Spoofing|SpoofHAGS"] = Booleans,
        ["Menu|OverlayMenu"] = Booleans,
        ["Menu|ShortcutKey"] = ["auto", "0x2D:0x2D - Insert / Einfg", "0x24:0x24 - Home / Pos1", "0x70:0x70 - F1", "0x7B:0x7B - F12", "-1:-1 - Deaktiviert"],
        ["Menu|ShowFps"] = Booleans,
        ["Framerate|FramerateLimit"] = [],
        ["fakenvapi|UseFakenvapi"] = Booleans,
        ["fakenvapi|ForceReflex"] = ["auto", "0:0 - Spiel-Standard", "1:1 - Erzwingen Aus", "2:2 - Erzwingen Ein"],
        ["Log|LogToFile"] = Booleans,
        ["Log|LogLevel"] = ["auto", "0:0 - Trace", "1:1 - Debug", "2:2 - Info", "3:3 - Warning", "4:4 - Error"]
    };

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*(?:-|–|\bto\b|\.\.)\s*(-?\d+(?:\.\d+)?)(?![\d.])",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Range();

    [GeneratedRegex(@"^\s*([\w.-]+(?:\s*,\s*[\w.-]+)+)\s*(?:-|$)", RegexOptions.Multiline)]
    private static partial Regex List();

    [GeneratedRegex(@"(?:^|\|)\s*(-?\d+)\s*[:=]\s*([^|\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex NumberedChoices();

    public static IniField[] Read(IniMod mod, byte[] original)
    {
        var catalog = mod == IniMod.Sm86 ? Sm86 : Opti;
        if (mod == IniMod.Sm86) original = ModIniService.NormalizeSm86(original);
        return ModIniService.ReadEntries(original)
            .Reverse().DistinctBy(e => e.Setting.Section + "|" + e.Setting.Key, StringComparer.OrdinalIgnoreCase).Reverse()
            .Select(e =>
            {
                var (setting, description) = e;
                var important = catalog.TryGetValue(setting.Section + "|" + setting.Key, out var options);
                var choices = important ? options!.Select(Choice).ToArray() : InferChoices(setting, description);
                var number = choices.Length == 0 ? InferNumber(setting, description) : null;
                return new IniField(setting, description, important, choices, number,
                    setting.Matches("Spoofing", "SpoofedGPUName"));
            }).ToArray();
    }

    private static IniChoice Choice(string text)
    {
        var parts = text.Split(':', 2);
        return new(parts[0], parts[^1]);
    }

    private static IniChoice[] InferChoices(IniSetting setting, string description)
    {
        var auto = setting.Value.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (bool.TryParse(setting.Value, out _) ||
            Regex.IsMatch(description, @"\btrue\s*(?:or|/|,|and)\s*false\b", RegexOptions.IgnoreCase))
            return Booleans.Select(Choice).ToArray();
        var list = List().Match(description);
        var numbered = NumberedChoices().Matches(description);
        IniChoice[] choices = list.Success ? list.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(Choice).ToArray() :
            numbered.Count > 1 ? numbered.Select(m => new IniChoice(m.Groups[1].Value, $"{m.Groups[1].Value} - {m.Groups[2].Value.Trim()}")).ToArray() : [];
        return auto && choices.Length > 0 ? [new("auto", "auto"), .. choices] : choices;
    }

    private static IniNumber? InferNumber(IniSetting setting, string description)
    {
        var auto = setting.Value.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (setting.Matches("Sharpness", "Sharpness")) return new(0, 1.3, 0.05, true);
        if (setting.Matches("Framerate", "FramerateLimit")) return new(0, double.PositiveInfinity, 1, true);
        var range = Range().Match(description);
        if (range.Success)
            return new(double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture), range.Value.Contains('.') ? 0.1 : 1, auto);
        if (double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            Regex.IsMatch(description, @"^\s*(float|u?int(?:eger)?|double)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var fractional = setting.Value.Contains('.') || Regex.IsMatch(description, @"^\s*(float|double)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return new(double.NegativeInfinity, double.PositiveInfinity, fractional ? 0.1 : 1, auto);
        }
        return null;
    }
}
