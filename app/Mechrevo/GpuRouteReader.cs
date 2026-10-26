using System.ComponentModel;
using System.Runtime.InteropServices;
using GHelper.Display;

namespace YALM.Mechrevo;

/// <summary>
/// Reads the display route without waking or changing the GPU:
/// the internal panel's adapter comes from DisplayConfig, while NVIDIA-device
/// presence comes from the Windows device tree.
/// </summary>
internal static class GpuRouteReader
{
    private const int ErrorInsufficientBuffer = 122;
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmGetIdListFilterClass = 0x00000200;
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint DnStarted = 0x00000008;
    private const uint CmProblemDisabled = 22;

    public static GpuRouteSnapshot Read()
    {
        try
        {
            InternalPanelReadback panelReadback = ReadInternalPanel();
            InternalPanelAdapter panel = GpuRouteInference.ClassifyAdapterPath(panelReadback.AdapterDevicePath);
            DgpuPresence dgpu = ReadDgpuPresence();
            return new GpuRouteSnapshot
            {
                InternalPanelAdapter = panel,
                DgpuPresence = dgpu,
                Route = GpuRouteInference.Infer(panel, dgpu),
                AdapterDevicePath = panelReadback.AdapterDevicePath,
                SourceDeviceName = panelReadback.SourceDeviceName,
                MonitorFriendlyName = panelReadback.MonitorFriendlyName,
                OutputTechnology = panelReadback.OutputTechnology,
                InternalPanelActive = panelReadback.IsActive,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastError = panel == InternalPanelAdapter.Unknown
                    ? "未能识别内屏所在的显示适配器。"
                    : null
            };
        }
        catch (Exception exception)
        {
            return new GpuRouteSnapshot { LastError = exception.Message, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    private static InternalPanelReadback ReadInternalPanel()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int error = DisplayNative.GetDisplayConfigBufferSizes(
                DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ALL_PATHS,
                out uint pathCount,
                out uint modeCount);
            if (error != 0) throw new Win32Exception(error);

            var paths = new DisplayNative.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DisplayNative.DISPLAYCONFIG_MODE_INFO[modeCount];
            error = DisplayNative.QueryDisplayConfig(
                DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ALL_PATHS,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                nint.Zero);
            if (error == ErrorInsufficientBuffer) continue;
            if (error != 0) throw new Win32Exception(error);

            InternalPanelReadback active = ReadInternalPanelCandidate(paths, pathCount, activeOnly: true);
            if (active != InternalPanelReadback.Empty) return active;
            return ReadInternalPanelCandidate(paths, pathCount, activeOnly: false);
        }
        return InternalPanelReadback.Empty;
    }

    private static InternalPanelReadback ReadInternalPanelCandidate(
        DisplayNative.DISPLAYCONFIG_PATH_INFO[] paths,
        uint pathCount,
        bool activeOnly)
    {
        var seenTargets = new HashSet<(uint Low, int High, uint Id)>();
        InternalPanelReadback found = InternalPanelReadback.Empty;
        InternalPanelAdapter foundAdapter = InternalPanelAdapter.Unknown;
        for (int index = 0; index < pathCount; index++)
        {
            DisplayNative.DISPLAYCONFIG_PATH_TARGET_INFO target = paths[index].targetInfo;
            bool isActive = (paths[index].flags &
                DisplayNative.DISPLAYCONFIG_PATH_FLAGS.DISPLAYCONFIG_PATH_ACTIVE) != 0;
            if (activeOnly && !isActive) continue;
            if (!target.targetAvailable || !IsInternalOutput(target.outputTechnology)) continue;
            if (!seenTargets.Add((target.adapterId.LowPart, target.adapterId.HighPart, target.id))) continue;

            var adapter = new DisplayNative.DISPLAYCONFIG_ADAPTER_NAME
            {
                header = new DisplayNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
                    size = (uint)Marshal.SizeOf<DisplayNative.DISPLAYCONFIG_ADAPTER_NAME>(),
                    adapterId = target.adapterId,
                    id = 0
                }
            };
            if (DisplayNative.DisplayConfigGetDeviceInfo(ref adapter) != 0) continue;

            InternalPanelAdapter kind = GpuRouteInference.ClassifyAdapterPath(adapter.adapterDevicePath);
            if (kind == InternalPanelAdapter.Unknown) continue;
            if (foundAdapter != InternalPanelAdapter.Unknown && foundAdapter != kind)
                return InternalPanelReadback.Empty;
            foundAdapter = kind;

            var targetName = new DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DisplayNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = target.adapterId,
                    id = target.id
                }
            };
            string? friendlyName = DisplayNative.DisplayConfigGetDeviceInfo(ref targetName) == 0
                ? targetName.monitorFriendlyDeviceName
                : null;

            var source = paths[index].sourceInfo;
            var sourceName = new DisplayNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DisplayNative.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DisplayNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = source.adapterId,
                    id = source.id
                }
            };
            string? sourceDevice = DisplayNative.DisplayConfigGetDeviceInfo(ref sourceName) == 0
                ? sourceName.viewGdiDeviceName
                : null;
            bool sourceActive = !string.IsNullOrWhiteSpace(sourceDevice) &&
                System.Windows.Forms.Screen.AllScreens.Any(screen =>
                    string.Equals(screen.DeviceName, sourceDevice, StringComparison.OrdinalIgnoreCase));

            found = new InternalPanelReadback(
                adapter.adapterDevicePath,
                sourceDevice,
                friendlyName,
                target.outputTechnology.ToString(),
                isActive || sourceActive);
        }
        return found;
    }

    private static bool IsInternalOutput(DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology) =>
        technology is DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

    private static DgpuPresence ReadDgpuPresence()
    {
        string[] ids = ReadPresentDisplayDeviceIds();
        string[] nvidiaIds = ids
            .Where(id => id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nvidiaIds.Length == 0) return DgpuPresence.Absent;

        bool started = false;
        bool disabled = false;
        bool other = false;
        foreach (string id in nvidiaIds)
        {
            if (CM_Locate_DevNodeW(out uint deviceNode, id, 0) != CrSuccess)
            {
                other = true;
                continue;
            }
            if (CM_Get_DevNode_Status(out uint status, out uint problem, deviceNode, 0) != CrSuccess)
            {
                other = true;
                continue;
            }
            if ((status & DnStarted) != 0) started = true;
            else if (problem == CmProblemDisabled) disabled = true;
            else other = true;
        }
        if (started) return DgpuPresence.Present;
        if (disabled && !other) return DgpuPresence.Disabled;
        return DgpuPresence.Unknown;
    }

    private static string[] ReadPresentDisplayDeviceIds()
    {
        const uint flags = CmGetIdListFilterClass | CmGetIdListFilterPresent;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, DisplayClassGuid, flags) != CrSuccess)
                throw new Win32Exception("无法读取显示设备列表长度。");
            if (length <= 1) return Array.Empty<string>();

            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(DisplayClassGuid, buffer, length, flags);
            if (result == CrBufferSmall) continue;
            if (result != CrSuccess)
                throw new Win32Exception($"无法读取显示设备列表（错误 0x{result:X}）。");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        throw new Win32Exception("显示设备列表在读取期间持续变化。");
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint deviceNode, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceNode, uint flags);

    private sealed record InternalPanelReadback(
        string? AdapterDevicePath,
        string? SourceDeviceName,
        string? MonitorFriendlyName,
        string? OutputTechnology,
        bool IsActive)
    {
        public static readonly InternalPanelReadback Empty = new(null, null, null, null, false);
    }
}
