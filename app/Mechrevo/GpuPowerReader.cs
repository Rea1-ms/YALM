using System.Diagnostics;

namespace YALM.Mechrevo;

internal static class GpuPowerReader
{
    private static readonly string[] Arguments =
    [
        "--query-gpu=name,power.draw,power.limit,power.min_limit,power.max_limit,power.default_limit",
        "--format=csv,noheader,nounits"
    ];

    public static async Task<GpuDriverPower?> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("nvidia-smi")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (string argument in Arguments)
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
                return new GpuDriverPower(null, null, null, null, null, null, "驱动查询没有启动。");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new GpuDriverPower(null, null, null, null, null, null, "显卡驱动没有提供功率范围。");
            return GpuDriverPower.TryParse(output, out GpuDriverPower? power, out string error)
                ? power
                : new GpuDriverPower(null, null, null, null, null, null, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GpuDriverPower(null, null, null, null, null, null, "显卡功率查询超时。");
        }
        catch (Exception)
        {
            return new GpuDriverPower(null, null, null, null, null, null, "显卡功率读取失败。");
        }
    }
}
