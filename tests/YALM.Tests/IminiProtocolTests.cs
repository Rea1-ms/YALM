using System.Collections.Generic;
using YALM.Mechrevo;

internal static partial class Program
{
    private static Task IminiRequestsAreReadOnly()
    {
        Assert.Equal(@"root\WMI", IminiReadProtocol.Namespace, "the reviewed mini WMI namespace");
        Assert.Equal("PowerSwitchInterface", IminiReadProtocol.ClassName, "the reviewed mini WMI class");
        Assert.Equal(@"ACPI\PNP0C14\IP3POWERSWITCH_0", IminiReadProtocol.InstanceName, "the exact reviewed instance");
        var methods = new[] { IminiReadProtocol.PowerModeMethod, IminiReadProtocol.FanMethod,
            IminiReadProtocol.FeatureMethod, IminiReadProtocol.TemperatureMethod };
        Assert.True(methods.SequenceEqual(new[] { "GetPowerMode", "GetFanControl", "GetFeatureValue", "GetHwTemp" }),
            "only the four statically reviewed read methods are exposed");
        Assert.Equal(0, IminiReadProtocol.PowerModeParameters.Count, "power mode read has no input");
        Assert.Equal(1, IminiReadProtocol.FanParameters.Count, "fan read has one input");
        Assert.Equal(1u, (uint)IminiReadProtocol.FanParameters["FanNumber"], "one read returns both fan words");
        Assert.Equal(1u, (uint)IminiReadProtocol.CpuTemperatureParameters["HwTempType"], "temperature read selects the CPU");
        foreach (var (parameters, feature) in new[]
        {
            (IminiReadProtocol.FanCountParameters, 3u),
            (IminiReadProtocol.GpuSupportParameters, 4u)
        })
        {
            Assert.Equal(4, parameters.Count, "feature reads include all four reserved arguments");
            Assert.Equal(feature, (uint)parameters["Reserved1"], "the selected read-only feature");
            foreach (string name in new[] { "Reserved2", "Reserved3", "Reserved4" })
                Assert.Equal(0u, (uint)parameters[name], "unused feature fields are zero");
            Assert.True(((IDictionary<string, object>)parameters).IsReadOnly, "shared request dictionaries cannot be changed into writes");
        }
        return Task.CompletedTask;
    }

    private static Task IminiModesAndNumberedFansDecode()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (raw, expected) in new[] { (2u, 0), (0u, 1), (1u, 2) })
        {
            var result = IminiReadProtocol.Decode(raw, (4321u << 16) | 1234u, 2u, 1u, now);
            Assert.Equal<int?>(expected, result.Mode, "mini modes have a different numbering order");
            Assert.Equal<int?>(1234, result.Fan1Rpm, "fan one is the low unsigned word");
            Assert.Equal<int?>(4321, result.Fan2Rpm, "fan two is the high unsigned word");
            Assert.Equal<int?>(2, result.NumberedFanCount, "live capability identifies two displayed fans");
            Assert.Equal<int?>(null, result.CpuFanRpm, "fan one is not assumed to cool the CPU");
            Assert.Equal<int?>(null, result.GpuFanRpm, "fan two is not assumed to cool the GPU");
            Assert.Equal<bool?>(null, result.IsAc, "supply state is not inferred from a desktop or a mode");
            Assert.Equal<bool?>(null, result.TurboSupported, "observing a performance mode does not grant control support");
            Assert.Equal<DateTimeOffset?>(now, result.ModeUpdatedAt, "mode time belongs to this read");
            Assert.Equal<DateTimeOffset?>(now, result.Fan1UpdatedAt, "fan one has its own timestamp");
            Assert.Equal<DateTimeOffset?>(now, result.Fan2UpdatedAt, "fan two has its own timestamp");
        }
        return Task.CompletedTask;
    }

    private static Task IminiInvalidAndMissingFieldsStayIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var firstInvalid = IminiReadProtocol.Decode(null, (4567u << 16) | 8000u, 2u, 1u, now);
        Assert.Equal<int?>(null, firstInvalid.Fan1Rpm, "the vendor's special 8000 value remains unknown, not zero");
        Assert.Equal<DateTimeOffset?>(null, firstInvalid.Fan1UpdatedAt, "an invalid field has no fresh timestamp");
        Assert.Equal<int?>(4567, firstInvalid.Fan2Rpm, "one invalid fan cannot discard the other reading");
        Assert.Equal<int?>(null, firstInvalid.Mode, "missing mode does not inherit a prior frame");
        Assert.True(firstInvalid.IsConnected, "valid independent telemetry remains usable without a mode");
        foreach (uint invalid in new[] { 8000u, 20001u, 65535u })
        {
            var result = IminiReadProtocol.Decode(99u, (invalid << 16), 2u, 1u, now);
            Assert.Equal<int?>(0, result.Fan1Rpm, "a valid zero is a stopped fan");
            Assert.Equal<int?>(null, result.Fan2Rpm, "invalid or special high word is unknown");
            Assert.Equal<int?>(null, result.Mode, "undefined mode is not balanced by default");
        }
        foreach (uint? support in new uint?[] { null, 2u, uint.MaxValue })
        {
            var result = IminiReadProtocol.Decode(2u, (4000u << 16) | 3000u, 2u, support, now);
            Assert.Equal<int?>(3000, result.Fan1Rpm, "first fan does not depend on the second fan's qualification");
            Assert.Equal<int?>(null, result.Fan2Rpm, "unknown GPU support cannot qualify the second fan");
            Assert.Equal<int?>(null, result.NumberedFanCount, "unknown support cannot claim complete fan coverage");
        }
        foreach (var (count, support) in new[] { (1u, 1u), (2u, 0u) })
        {
            var result = IminiReadProtocol.Decode(null, (4000u << 16) | 3000u, count, support, now);
            Assert.Equal<int?>(1, result.NumberedFanCount, "the official display omits fan two on these capability combinations");
            Assert.Equal<int?>(null, result.Fan2Rpm, "absent fan two does not become a zero reading");
        }
        foreach (uint? count in new uint?[] { null, 0u, 3u, uint.MaxValue })
        {
            var result = IminiReadProtocol.Decode(null, (4000u << 16) | 3000u, count, 1u, now);
            Assert.Equal<int?>(null, result.Fan1Rpm, "unknown fan count does not qualify numbered telemetry");
            Assert.Equal<int?>(null, result.Fan2Rpm, "invalid fan count does not qualify a second fan");
        }
        var empty = IminiReadProtocol.Decode(null, null, null, null, now);
        Assert.True(!empty.IsConnected, "an empty failed read cannot establish connectivity");
        return Task.CompletedTask;
    }

    private static Task IminiTemperatureUsesTheReviewedLowByte()
    {
        var now = DateTimeOffset.UtcNow;
        var result = IminiReadProtocol.Decode(null, null, null, null, now, 0x1234564bu);
        Assert.Equal<int?>(75, result.CpuTemperature, "the CPU widget extracts the low byte of the temperature word");
        Assert.Equal<DateTimeOffset?>(now, result.CpuTemperatureUpdatedAt, "temperature is independently timestamped");
        Assert.Equal<int?>(null, result.GpuTemperature, "the packed high bytes are not interpreted as GPU temperature");
        Assert.True(result.IsConnected, "a valid temperature can establish readable telemetry by itself");
        foreach (uint? value in new uint?[] { null, 0u, 126u, 255u, uint.MaxValue })
        {
            var invalid = IminiReadProtocol.Decode(2u, null, null, null, now, value);
            Assert.Equal<int?>(null, invalid.CpuTemperature, "missing and out-of-range temperature remains unknown");
            Assert.Equal<DateTimeOffset?>(null, invalid.CpuTemperatureUpdatedAt, "invalid temperature does not get a fresh timestamp");
            Assert.Equal<int?>(0, invalid.Mode, "temperature failure preserves an independently read mode");
        }
        return Task.CompletedTask;
    }
}
