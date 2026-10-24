using System.Globalization;

namespace YALM.Mechrevo;

public sealed record GpuDriverPower(
    string? Name,
    double? CurrentDraw,
    double? CurrentLimit,
    double? MinimumLimit,
    double? MaximumLimit,
    double? DefaultLimit,
    string? Error = null)
{
    public static bool TryParse(string output, out GpuDriverPower? power, out string error)
    {
        power = null;
        error = "";
        string line = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length < 6 || string.IsNullOrWhiteSpace(fields[0]))
        {
            error = "驱动没有返回可用的显卡功率信息。";
            return false;
        }

        power = new GpuDriverPower(
            fields[0],
            ParseMetric(fields[1]),
            ParseMetric(fields[2]),
            ParseMetric(fields[3]),
            ParseMetric(fields[4]),
            ParseMetric(fields[5]));
        if (!power.MinimumLimit.HasValue || !power.MaximumLimit.HasValue)
        {
            error = "驱动没有报告完整的显卡功率范围。";
            return false;
        }
        return true;
    }

    private static double? ParseMetric(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
}
