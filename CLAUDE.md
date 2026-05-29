# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
use chinese chat with user

## 项目概述

本项目旨在解决电脑没有音响/扬声器时的声音输出问题。通过USB连接手机等带扬声器的设备，将电脑音频转发到设备扬声器输出。

整体数据流: WASAPI Loopback → AudioCaptureManager → StreamForwarder (ADB TCP forward) → audio_player (Android AAudio TCP socket) → 设备扬声器

## 项目结构

```
audio_player/               # Android NDK C 二进制，通过 AAudio API 播放 PCM 音频
service/AudioBridge/        # C# .NET 8.0 Windows 服务（主程序）
  ├── Program.cs            # 入口，协调各组件生命周期
  ├── Config.cs             # 从 config.json 加载配置
  ├── config.json           # 运行时配置（音频参数、ADB路径、日志级别）
  ├── AudioCapture/
  │   ├── AudioCaptureManager.cs  # WASAPI Loopback 捕获，从默认音频设备抓取 PCM 数据
  │   ├── SampleConverter.cs      # float→int + SRC + 整数位深转换
  │   └── WasapiInterop.cs        # WASAPI COM 接口 P/Invoke 声明
  ├── DeviceMonitor/
  │   ├── AdbWrapper.cs     # ADB 命令封装（设备查询、push、端口转发、获取设备型号）
  │   ├── DeviceWatcher.cs  # 每1秒轮训 ADB 设备列表，检测连接/断开
  │   └── DeviceInfo.cs     # 设备状态模型
  ├── Streaming/
  │   ├── StreamForwarder.cs # 通过 ADB TCP forward 将 PCM 数据发往 Android audio_player
  │   └── AudioPacket.cs     # PCM 数据封装（含时间戳，用于延迟追踪）
  └── Utils/
      ├── Logger.cs         # 控制台 + 文件日志（Log/ 目录，自动清理昨日日志）
      └── SystemAudio.cs    # 已废弃，仅保留参考注释
scripts/                    # 构建与运行 PowerShell 脚本
Source/scream_origin/       # Scream 虚拟音频驱动源码（参考用，不再使用）
Source/ndk/                 # Android NDK r27d（构建 audio_player 所需）
```

## 构建与运行

- **构建 Android 播放器**: `.\audio_player\build_audio_player.ps1`（依赖 NDK，自动使用 Source/ndk/ 下的工具链，首次自动下载 NDK）
- **构建 C# 服务**: `dotnet build .\service\AudioBridge --configuration Release`
- **一键构建全部**: `.\scripts\build_all.ps1`
- **运行服务**: `.\scripts\run_bridge.ps1`

## 核心流程

1. **设备发现**: DeviceWatcher 每1秒调用 `adb devices` 轮训。新设备接入后通过 `dumpsys audio` 检测是否有扬声器能力
2. **端口转发建立**: 有扬声器的设备触发 DeviceConnected 事件 → `adb forward tcp:27777 tcp:27777` 建立 TCP 端口转发
3. **推送播放器**: ADB push 将 audio_player 推送到设备并设为可执行
4. **启动播放器**: 通过 `adb shell -T` 启动 audio_player，传入参数（采样率/声道/位深/端口），audio_player 在设备端创建 TCP 服务端等待连接
5. **TCP 连接**: StreamForwarder 检测 audio_player 就绪后，连接本地 27777 端口（经 ADB 转发到设备）
6. **音频捕获**: AudioCaptureManager 启动 WASAPI Loopback，从默认音频渲染设备捕获 PCM 数据
7. **音频播放**: PCM 数据通过生产者-消费者队列（BlockingCollection）入队，专用写入线程从队列消费并写入 TcpClient 流，经 ADB 转发到达设备端 audio_player 的 TCP socket，audio_player 通过 `read()` 接收并通过 AAudio API 播放

## 日志

- 日志文件位于运行目录的 `Log/` 文件夹，按启动时间命名如 `26-5-29-19-14-11.log`（格式 `yy-M-dd-HH-mm-ss`）
- 非当日的旧日志文件在下次启动时自动删除
- 日志级别在 `config.json` 的 `Logging.Level` 中配置（Debug/Info/Warn/Error）
- 调试功能时以日志为重要参考

## 配置 (config.json)

```json
{
  "Audio": {
    "TargetSampleRate": 48000,
    "TargetBitsPerSample": 32,
    "TargetChannels": 2,
    "BufferSizeMs": 50,
    "PreAttenuationDb": -6.0
  },
  "Adb": {
    "AdbPath": "",
    "PlayerBinary": "audio_player",
    "PlayerRemotePath": "/data/local/tmp/audio_player"
  },
  "Logging": {
    "Level": "Debug"
  }
}
```

- `AdbPath` 为空时自动搜索：配置路径 → 运行目录 `requirement/adb.exe` → `where adb.exe` → 硬编码常见路径
- `PreAttenuationDb`: 前置衰减 dB，默认 -6dB（0.5x）。系统混音器叠加多个音源时峰值可能超过 1.0f，固定衰减防止削波。多个音源同时满幅输出时可调至 -9dB 或 -12dB
- 构建时 csproj 将 `config.json` 和 `audio_player` 链接到输出目录的 `requirement/` 子文件夹下

## 关键依赖

- **ADB** — 通过 USB 调试桥与 Android 设备通信
- **Android NDK r27d** — 交叉编译 audio_player 所需

---

## 当前代码分析

### 系统架构

项目采用**管道-过滤器**架构风格，整体数据流为单向管道：

```
Windows 音频管线 → WASAPI Loopback捕获 → AudioCaptureManager
  → float→int格式转换 (可选SRC) → StreamForwarder(TCP生产者-消费者队列) → ADB forward(tcp:27777)
  → audio_player(TCP socket read) → AAudio → Android扬声器
```

系统由两个独立的子系统组成：
1. **Windows 端 (C# .NET 8)** — 控制台应用，负责WASAPI音频捕获、格式转换、ADB端口转发
2. **Android 端 (C + NDK/AAudio)** — 静态链接的ELF二进制，通过 ADB shell 启动，在设备端创建TCP服务端等待连接，从TCP socket读取PCM并播放

### 架构变更历史

| 版本 | 音频捕获方式 | audio_player 输入 | 说明 |
|------|-------------|------------------|------|
| Scream版 | Scream虚拟驱动 + UDP组播 | 先stdin后TCP socket | 需安装内核驱动，关安全启动+测试模式 |
| 当前 | WASAPI Loopback (用户态COM API) | TCP socket `read()` | 零驱动安装，即开即用 |

### 模块详解

#### 1. Android 播放器 — `audio_player/audio_player.c`

- **职责**：通过AAudio API从TCP socket读取PCM数据并播放到设备扬声器
- **编译**：`build_audio_player.ps1` — 使用NDK r27d的 `aarch64-linux-android26-clang` 交叉编译，flags: `-Oz -Wno-unused-parameter -laaudio -llog -lm`
- **默认格式**：48000Hz, **32-bit int (AAUDIO_FORMAT_PCM_I32)**, 2ch, 50ms缓冲区
- **命令行参数**：`audio_player [sample_rate] [channels] [bits] [buffer_ms] [port]`
- **双模式输入**：
  - port=0（默认）：stdin 模式，向后兼容
  - port>0：TCP 模式，调用 `wait_for_tcp_connection()` 在设备端 0.0.0.0:port 创建TCP服务端等待连接
- **关键函数**：
  - `main()` — 解析命令行参数，创建AAudioStream，阻塞 `wait_for_tcp_connection()`，循环 `read(tcp_fd) → AAudioStream_write(200ms timeout)`
  - `wait_for_tcp_connection(port)` — 创建TCP socket，bind 0.0.0.0:port，listen，accept（10秒超时），返回client_fd
  - `handle_signal()` — 捕获SIGINT/SIGTERM设置`g_running=0`优雅退出
- **通知功能**：首次成功写入音频后，通过 `system()` 调用发送手机通知（`input keyevent KEYCODE_WAKEUP` + `cmd notification post`）
- **输出**：所有诊断信息输出到stderr（经ADB stderr管道传回C#服务记入Info日志）
- **行为**：TCP连接失败时直接退出（不回退到stdin模式），由C#侧触发重连

#### 2. 入口与编排 — `service/AudioBridge/Program.cs`

- **职责**：应用入口，协调所有组件生命周期
- **启动顺序**：
  1. Config.Load() 加载配置
  2. FindAdb() 搜索adb.exe（策略：配置路径 → 运行目录 `requirement/adb.exe` → `where adb.exe` → 硬编码常见路径）
  3. 创建AdbWrapper → DeviceWatcher → AudioCaptureManager
  4. 订阅事件（DeviceConnected/Disconnected, AudioDataReceived, DefaultDeviceChanged, ConnectionLost）
  5. DeviceWatcher.Start() 开始轮询
  6. `ManualResetEvent.WaitOne()` 阻塞主线程直到Ctrl+C
- **设备连接处理**：先启动StreamForwarder（建立TCP转发+启动audio_player），再启动AudioCaptureManager（开始捕获），避免丢包
- **设备断开处理**：先停StreamForwarder，再停AudioCaptureManager
- **连接丢失处理**: StreamForwarder 的 `ConnectionLost` 事件触发 → 模拟设备断开 + 从 DeviceWatcher 中清除设备记录，下次轮询自动重新检测和连接
- **默认设备变更**: AudioCaptureManager 的 `DefaultDeviceChanged` 事件 → 异步停止并重启 WASAPI 捕获
- **清理顺序**：StreamForwarder → AudioCaptureManager → DeviceWatcher → Environment.Exit(0)
- **配置已生效**：`OnDeviceConnected()` 将 `config.Audio` 配置传递给 StreamForwarder.Start() 和 AudioCaptureManager.SetTargetFormat()

#### 3. 配置管理 — `service/AudioBridge/Config.cs`

- **职责**：从config.json加载运行时配置
- **配置类嵌套**：Config → AudioConfig/AdbConfig/LoggingConfig
  - AudioConfig 新增 `PreAttenuationDb`（double，默认 -6.0）
- **行为**：配置文件不存在时使用默认值并Warn提示，无JSON Schema验证

#### 4. 设备监控模块 — `service/AudioBridge/DeviceMonitor/`

**DeviceInfo.cs** — 设备状态模型
- Serial, Model（通过 ADB `getprop ro.product.model` 填充）, HasSpeaker, State枚举

**AdbWrapper.cs** — ADB命令封装
- `IsAvailable()`: `adb --version`
- `GetDevices()`: `adb devices` → 过滤"device"状态设备
- `CheckHasSpeaker(serial)`: `adb shell dumpsys audio` → 检查多组关键词（兼容Android 13+和旧版本）
- `PushFile()`: `adb push`，通过输出中是否含"error"/"failed"判断成功
- `SetExecutable()`: `adb shell chmod 755`
- `SetupForward(serial, port)`: 先清理旧forward，再`adb forward tcp:port tcp:port`
- `RemoveForward(serial, port)`: `adb forward --remove tcp:port`
- `StartAudioPlayer(serial, remotePath, port, ...)`: **关键** — `adb shell -T` 启动进程（raw pipe），重定向stderr，传递音频格式参数和端口号；PCM数据通过独立的TCP连接传输
- `KillAudioPlayer()`: `process.Kill(true)` + Dispose
- `GetDeviceModel(serial)`: `adb shell getprop ro.product.model`，失败时fallback到 `ro.product.name`
- `CleanupRemote()`: `adb shell rm -f remotePath`
- 私有`RunAdbCommand(args)`: 同步执行ADB命令，5秒超时

**DeviceWatcher.cs** — 设备状态轮询
- 轮询间隔：1秒（System.Timers.Timer）
- 并发保护：`_isPolling`标志防止重入
- 状态管理：`_knownDevices`字典跟踪已知设备，`_activeSerial`记录当前活动设备
- 新设备流程：检测到 → CheckHasSpeaker → 有扬声器则获取设备型号 → DeviceConnected / 无扬声器ErrorOccurred
- 设备断开：从adb devices列表消失时触发DeviceDisconnected
- **ForgetDevice(serial)**: 手动从已知设备列表移除，用于连接丢失后的自动重连
- **限制**：仅支持单设备（只记录第一个speaker设备为active）

#### 5. 音频捕获模块 — `service/AudioBridge/AudioCapture/`

**AudioCaptureManager.cs** — WASAPI Loopback 音频捕获
- 使用WASAPI COM接口 (`IMMDeviceEnumerator` → `IAudioClient` → `IAudioCaptureClient`) 从默认渲染设备捕获音频
- 初始化流程：GetDefaultAudioEndpoint → Activate(IAudioClient) → GetMixFormat → Initialize(LOOPBACK) → GetService(IAudioCaptureClient) → Start
- 捕获循环：轮询 `GetNextPacketSize` / `GetBuffer` / `ReleaseBuffer`，有数据时无间隔处理，空闲时5ms休眠
- 格式协商：从 `GetMixFormat()` 获取WAVEFORMATEX/WAVEFORMATEXTENSIBLE，支持PCM/float/Extensible格式
- `AudioDataReceived(byte[] pcmData)` 事件 — 发出转换后的PCM数据
- `DefaultDeviceChanged` 事件 — 通过 `WasapiNotificationClient` (IMMNotificationClient) 监听默认音频设备变更，自动通知上层重启捕获
- 自动格式转换（`ConvertFormat` 内部方法）：
  - float → int（最常用，通过SampleConverter）
  - 采样率转换（SRC，线性插值，通过SampleConverter）
  - 整数位深转换（24→32, 16→32等，通过SampleConverter）
  - 格式完全匹配时直接透传
- 支持设置目标格式：`SetTargetFormat(sampleRate, bitsPerSample, channels)`
- 统计：5秒定时器输出捕获数据量（0时Warn提示）
- 线程：Dedicated LongRunning Task捕获循环 + ThreadPool定时器
- COM资源管理：`Marshal.ReleaseComObject` 清理，`#pragma warning disable CA1416`
- COM回调：`WasapiNotificationClient` 内部类实现 `IMMNotificationClient`，注册到 `IMMDeviceEnumerator.RegisterEndpointNotificationCallback`

**SampleConverter.cs** — 音频格式转换（**数据流实时使用**）
- `ConvertFloatToInt(byte[], targetBits)` — float[-1..1] → int32/int16 小端序PCM，clamp保护
- `ConvertFloatToIntWithSRC(byte[], inputRate, outputRate, channels, targetBits)` — float→int + 线性插值采样率转换，支持升降采样
- `ConvertIntToInt(byte[], srcBits, dstBits)` — 整数位深转换（8/16/24/32bit互转），sign extension处理
- **前置衰减**：`SetPreAttenuationDb(double)` 设置衰减系数，默认 -6dB（0.5x），防止多音源叠加时削波

**WasapiInterop.cs** — WASAPI COM接口声明
- `IMMDeviceEnumerator`、`IMMDevice`、`IAudioClient`、`IAudioCaptureClient` COM接口
- `IMMNotificationClient` — 设备变更通知回调接口
- `WaveFormatEx` / `WaveFormatExtensible` 结构体
- `AudioClientConst` 常量（LOOPBACK flag、format tags、subformat GUIDs）
- `EDataFlow` / `ERole` 枚举

#### 6. 音频转发模块 — `service/AudioBridge/Streaming/`

**StreamForwarder.cs** — ADB TCP端口转发音频
- **启动流程**：
  1. `SetupForward` → 先清理旧forward再设置 `adb forward tcp:27777 tcp:27777`
  2. `PushFile` + `chmod` 推送audio_player
  3. `StartAudioPlayer` → `adb shell -T` 启动audio_player（传入参数和端口号）
  4. 异步读取stderr，检测audio_player的"Waiting for TCP connection on"标记
  5. 连接 `127.0.0.1:port` → 经ADB转发到设备端audio_player的TCP服务端
  6. 启动生产者-消费者队列 + 专用写入线程
- **生产者-消费者模式**：
  - `SendAudioData()`（WASAPI捕获线程调用）→ `BlockingCollection<AudioPacket>.Add()`
  - 专用 `WriteLoop()` 线程 → `foreach` 消费队列 → `TcpClient.GetStream().Write()`
  - 最大队列深度500包（队列满时静默丢弃，避免内存溢出）
  - 使用 `AudioPacket` 时间戳追踪端到端延迟，超过500ms时Warn日志
- **进程监控**：`EnableRaisingEvents` + Exited事件（记录exit code，不自动重启）
- **连接丢失**：`ConnectionLost` 事件 — TCP写入异常时触发，通知Program.cs启动重连流程
- 安全停止：CompleteAdding停止入队 → 等待写入线程排空（3秒超时）→ 关闭流/TCP → Kill进程 → RemoveForward
- **未实现**：崩溃自动重启（audio_player退出不会自动重启进程）

**AudioPacket.cs** — PCM数据封装（**当前已启用**）
- Data(byte[]) + Timestamp(DateTime.UtcNow) — 用于端到端延迟测量

#### 7. 工具模块 — `service/AudioBridge/Utils/`

**Logger.cs** — 日志系统
- 级别：Debug < Info < Warn < Error
- 控制台输出：彩色（灰=Debug, 青=Info, 黄=Warn, 红=Error）
- 文件输出：`Log/{yy-M-dd-HH-mm-ss}.log`（每次启动新文件）
- 旧日志清理：启动时自动删除非当日的日志文件
- 线程安全：`lock(_lock)` 保护文件写入

**SystemAudio.cs** — **已废弃**，仅保留注释说明。WASAPI Loopback自动从默认渲染设备捕获，无需手动选择设备。如将来需要设备选择功能，使用 WasapiInterop.cs 中声明的 IMMDeviceEnumerator COM 接口。

### 核心数据流

```
[应用播放声音 → Windows音频混音器]
    ↓
[默认音频渲染设备 (板载声卡/HDMI等)]
    ↓ WASAPI Loopback (进程内 COM API)
[AudioCaptureManager.CaptureLoop()]
    ↓ 轮询 GetBuffer, Marshal.Copy 到托管数组
[格式转换 (float→int, 可选SRC, 可选位深转换)]
    ↓
[AudioDataReceived 事件]
    ↓ 直接透传byte[]
[StreamForwarder.SendAudioData(byte[])]
    ↓ BlockingCollection<AudioPacket>.Add()  (生产者)
[生产者-消费者队列]
    ↓ WriteLoop() foreach 消费  (消费者)
[_dataStream.Write(data)]
    ↓
[TcpClient → 127.0.0.1:27777]
    ↓ ADB forward tcp:27777 tcp:27777
[Android 端 audio_player TCP server]
    ↓ read(tcp_fd, buf, 4096)
[AAudioStream_write(stream, buf, frame_count, 200ms timeout)]
    ↓
[Android 硬件混音器 → 扬声器]
```

### 线程模型

| 组件 | 线程 |
|------|------|
| Main() | ManualResetEvent.WaitOne() 阻塞 |
| DeviceWatcher轮询 | System.Timers.Timer (ThreadPool), 1s |
| WASAPI捕获循环 | Dedicated LongRunning Task (轮询, 5ms休眠) |
| 5秒统计定时器 | System.Timers.Timer (ThreadPool) |
| TCP写入循环 | Dedicated LongRunning Task |
| adb stderr读取 | Dedicated LongRunning Task |
| ADB进程退出事件 | ThreadPool |
| 默认设备变更响应 | ThreadPool (Task.Run) |

### 错误处理总览

| 场景 | 处理 |
|------|------|
| ADB未找到 | 日志Error + WaitAndExit() |
| audio_player文件缺失 | 日志Error，中断设备连接 |
| Push失败 | 日志Error，中断设备连接 |
| ADB端口转发设置失败 | 日志Error，中断设备连接 |
| TCP连接失败（audio_player未就绪） | 15秒超时后日志Error，中断设备连接 |
| WASAPI初始化失败（无音频设备） | 日志Error，跳过启动（不阻止转发管道建立） |
| WASAPI捕获异常 | 日志Error，捕获循环break |
| WASAPI格式协商不匹配 | 日志Warn，尝试passthrough或格式转换，仍不匹配时返回原始数据 |
| 队列满（>500包） | 静默丢弃新数据，每100次丢弃产生Warn日志，不阻塞WASAPI捕获 |
| TCP写入异常 | 日志Error，写入循环break，触发ConnectionLost事件 |
| 设备轮询异常 | 日志Error，继续轮询 |
| 配置文件缺失 | 使用默认值 + Warn |
| audio_player进程退出 | 记录exit code，stderr stream ended，无自动恢复（等待ConnectionLost触发重连） |
| 默认音频设备切换 | IMMNotificationClient检测到变更 → DefaultDeviceChanged事件 → 异步重启WASAPI捕获 |

### 已知问题与改进空间

1. **无崩溃恢复** — audio_player退出后StreamForwarder不会自动重启进程（依赖ConnectionLost+ForgetDevice在下一轮轮询中重连）
2. **单设备限制** — `_activeSerial`仅跟踪一个设备，多设备同时连接时只使用第一个
3. **无音频设备降级** — 当PC无音频输出设备时WASAPI初始化失败，仅输出错误日志，无自动重试机制
4. **SRC仅支持float输入** — 整数格式的采样率转换未实现，整数+不同采样率时返回原始数据
5. **24→16/8等降位深转换无抖动** — 整数位深降转换时仅移位，无噪声抖动（dither）
6. **AudioPacket时间戳仅用于延迟告警** — 未用于端到端延迟统计面板或自适应缓冲区

### 关键决策记录

1. **WASAPI Loopback代替Scream虚拟驱动**：消除内核驱动安装需求，用户无需关闭安全启动和测试模式。代价是无持久虚拟设备，程序退出后转发终止
2. **ADB TCP forward代替exec-out stdin**：避免管道缓冲区死锁风险，解耦读写速率。代价是需要额外设置端口转发
3. **生产者-消费者队列**：解耦WASAPI捕获线程和TCP写入线程，允许处理速率波动。队列满时丢弃旧数据而非阻塞捕获
4. **C二进制代替Android App**：无需APK打包签名，开发成本降低一个数量级
5. **1秒轮询代替ADB事件监听**：ADB无设备热插拔事件API，轮询是简单可靠方案
6. **dumpsys audio多关键词匹配**：兼容Android 13+（音频服务格式）和旧版本（AudioFlinger格式），减少误判
7. **先建TCP管道再捕获**：StreamForwarder先启动确保TCP就绪，再启动WASAPI捕获
8. **AAUDIO_FORMAT_PCM_I32**：使用32位有符号整数，匹配WASAPI 32-bit float的精度
9. **原生P/Invoke COM代替NAudio**：零外部依赖，接口声明为一次性工作，避免NuGet依赖膨胀
10. **轮询代替事件驱动**：WASAPI捕获使用5ms轮询而非SetEventHandle事件驱动，实现简单且延迟可接受
11. **Marshal.Copy安全代码**：音频缓冲区处理使用Marshal.Copy从IntPtr拷贝到托管数组而非unsafe指针，安全性更好，性能损失可忽略
12. **前置衰减替代动态限制器**：-6dB固定衰减应对多音源叠加削波，避免动态限制器引入的谐波失真和增益泵浦
13. **IMMNotificationClient设备变更通知**：注册WASAPI回调监听默认设备变更，HDMI/蓝牙插拔时自动重启捕获
14. **ConnectionLost + ForgetDevice重连机制**：TCP写入异常时触发ConnectionLost，Program.cs断开设备并清除记录，下次轮询自动重连
15. **运行目录requirement/子文件夹**：csproj通过Link将config.json和audio_player映射到输出目录的requirement/下，保持部署文件整洁
