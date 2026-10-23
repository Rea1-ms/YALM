# L-Mechrevo 的 GCU 实现分析

本文整理对 L-Mechrevo `v0.290.6`（版本号）安装包、运行进程、驱动文件和源码文档的现有调查结果。重点是区分 L-Mechrevo 自己实现的部分，以及随安装包提供的厂商闭源组件。

## 结论

L-Mechrevo 主要是控制界面和协议客户端，并没有重写机械革命笔记本的底层硬件控制。风扇、性能模式、灯效等功能大多复用了 AISTONE GLOBAL（厂商服务提供方）和 Uniwill（驱动供应方）的后台与驱动。

```text
L-Mechrevo
    │ 发布本机 MQTT（消息协议）命令
    ▼
GCUBridge（消息桥接服务，本机 MQTT 服务端）
    ▼
GCUService（硬件执行服务）
    ▼
ACPIDriverDll（原生驱动库）/ HID（设备接口）/ WMI（系统管理接口）/ 显卡接口
    ▼
UWACPIDriver.sys（内核驱动）
    ▼
EC（嵌入式控制器）
```

EC 负责笔记本主板上的风扇、功耗、灯效和电池等功能。L-Mechrevo 只在少数场景绕过 GCU，例如电池限充可以直接写入 EC 地址，显卡超频也可以走显卡驱动接口。

## 已确认的厂商组件

| 文件 | 观察到的证据 | 作用判断 |
| --- | --- | --- |
| `GCUBridge.exe` | 文件版本 `1.0.1.10`；签名为 AISTONE GLOBAL；包含 MQTTnet（消息库）和 `CreateMqttServer` 等字符串 | 本机 MQTT 服务端和消息中转 |
| `GCUService.exe` | 文件版本 `1.2.0.0`；签名为 AISTONE GLOBAL；引用 `ACPIDriverDll.dll`、`GPUInfoDLL.dll`、WMI、蓝牙和 M2Mqtt | 执行硬件控制和状态读取 |
| `ACPIDriverDll.dll` | 原生库；签名为 AISTONE GLOBAL；导出或包含 `ReadEC`、`WriteEC`、`SMAPCTable`、`WriteCMOS`、`DeviceIoControl` | 通过驱动访问 ACPI 和 EC |
| `UWACPIDriver.sys` | Microsoft Windows Hardware Compatibility Publisher（微软硬件兼容性签名）；INF Provider 为 Uniwill；硬件 ID 为 `ACPI\INOU0000`；服务名为 `UWACPIDriver` | 内核层 ACPI/EC 访问 |

安装目录中的 `GCUBridge.exe` 路径为 `C:\Program Files\L-Mechrevo\GCU\AiStoneService\GCUBridge.exe`。运行时观察到它监听 `0.0.0.0:13688`，也就是所有本机网络接口；L-Mechrevo 使用 `127.0.0.1` 连接，GCUService 使用本机回环地址连接。安装器源码还尝试创建入站阻止防火墙规则，因此不能简单地把它理解成只绑定本机回环地址。

## MQTT 在这里做什么

MQTT 是官方控制台原本使用的进程间消息协议。L-Mechrevo 复用了这套协议，界面只需要发布命令并订阅状态，不必直接操作驱动。

已看到的主题包括：

- `Fan/Control` 和 `Fan/Status`：风扇、性能模式和相关状态。
- `Setting/Control` 和 `Setting/Status`：系统设置和状态。
- `Keyboard/Ctrl` 和 `Keyboard/Status`：键盘控制和状态。
- `System/CpuInfo`、`System/GpuInfo`：处理器和显卡遥测。

因此，MQTT 本身不是云端硬件接口，而是本机界面、桥接服务和硬件服务之间的通信层。不过 GCUServicePlugin（服务插件）和 OTA（在线更新）组件另有 HTTP（网页传输协议）能力，不能据此断言整个安装包完全不联网。

## 驱动访问的具体形式

`ACPIDriverDll.dll` 会打开 `\\.\ACPIDriver` 设备，并向 `UWACPIDriver.sys` 发送驱动控制请求：

- EC 读取：`IOCTL_GPD_ACPI_ECREAD`，控制码 `0x9C40A488`。
- EC 写入：`IOCTL_GPD_ACPI_ECWRITE`，控制码 `0x9C40A48C`。
- 写入数据格式：`[u32 地址][u8 值]`，总长度 5 字节。

L-Mechrevo 自己的电池限充代码已经直接使用其中的 EC 写入路径，观察到的地址包括 `0x7B9` 和 `0x7D0`。风扇曲线、性能模式和大多数灯效操作仍然通过 MQTT 交给 GCUService。

## 哪些部分仍然闭源

`THIRD_PARTY_NOTICES.txt` 明确说明，GCU-only（仅 GCU 组件）包中的 AISTONE/Mechrevo 服务二进制和微软签名的 UWACPI 驱动属于独立的专有作品，不属于 L-Mechrevo 的 GPL（开源许可证）授权范围。

目前能确认接口和调用链，但还不能完整还原以下内容：

1. GCUService 收到每个 MQTT 命令后，具体调用了哪些内部函数。
2. 每个性能模式、风扇档位和灯效命令对应哪些 EC 寄存器或固件命令。
3. EC 固件内部如何解释这些寄存器值。

此前的卡点是本次调查的 L-Mechrevo 捆绑版 GCUBridge 和 GCUService 使用了 ConfuserEx（.NET 混淆和反篡改工具）。普通反编译器可以看到类名、常量、依赖和字符串，但部分核心方法体无法直接恢复。这个判断只针对当时的 AISTONE 版本，不能推广到所有机械革命控制中心版本。驱动本身有符号和字符串线索，却没有厂商源代码。

2026 年 10 月 23 日补充：用户提供的原版安装包包含 `GCUService 1.0.2.70`（硬件服务版本），其风扇信息等关键类型可以静态反编译；原版桥接服务的认证生成路径也已能够读取。原版 `GCUService` 带有 Uniwill（驱动和服务供应方）的有效签名，与此前 L-Mechrevo 捆绑的 AISTONE `1.2.0.0` 服务不同。现在已有进一步研究执行路径的材料，不再只能停留于字符串与接口推断。能反编译并不改变这些组件的闭源授权性质。

## 后续研究需要什么

机型、固件、原版文件版本与哈希，以及本机只读消息连接现已核实，详情见同目录的《原版控制中心安装包分析》。当前原版服务会上报模式、处理器温度和风扇占空比，但所观察到的 `System/FanInfo`（风扇信息）只有占空比，没有实际转速；`Fan/Status`（风扇状态）也不含外接电源字段。因此不能把“风扇控制值为 49%”理解为“风扇正在以某个转速运行”。

仍需要进一步核对的内容是：

1. 原版服务针对本机项目标识选择了哪些风扇表与硬件访问分支。
2. 各模式消息如何映射到功耗、风扇和固件操作；服务保存状态与硬件执行成功之间有哪些反馈。
3. 在明确安排真实硬件验证后，对照模式切换前后的实际风扇转速、温度与负载表现。当前尚未执行这些模式切换或负载测试。

后续可以结合可反编译的原版服务和限定范围的消息观察，把界面按钮对应到具体消息及硬件路径。版本差异需要分别验证，不能把旧版代码路径直接当作新版服务的完整实现，也不能从服务回显单独推断风扇已经正常工作。

## 证据范围

初始结论来自 L-Mechrevo `v0.290.6` 源码包中的 `ARCHITECTURE.md`（架构说明）、`docs/gcu-dependency-matrix.md`（组件依赖说明）、`docs/ec-per-generation.md`（分代 EC 说明）、`THIRD_PARTY_NOTICES.txt`（第三方声明），以及安装后的文件签名、导出表、字符串、运行进程和端口观察。源码文档属于逆向分析资料，不是厂商原始源码；未能从中确认的内容已在本文标为待验证。

补充结论来自原版安装包静态分析，以及当前已安装原版服务的版本、哈希和只读连接结果。本文前半部分记录的 L-Mechrevo 路径、监听地址和签名是调查当时的观察，不代表目前仍运行相同安装版本。原始解包与反编译缓存保留在 `research`（研究缓存）目录，不纳入提交，其中可能含厂商认证常量，不应直接分享完整文件。
