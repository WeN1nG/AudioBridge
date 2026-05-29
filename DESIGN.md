# 跨平台音频桥接方案设计文档

## 1. 项目概述

本项目解决电脑缺少扬声器/音响时的声音输出问题。用户通过 USB 线连接一部 Android 手机到电脑，程序自动将电脑的所有音频输出转发到手机的扬声器播放。核心价值是零成本复用现有设备的扬声器，无需购买外接音响。

目标用户：刚购入电脑但尚未配备音响的用户、临时需要声音输出的场景、嵌入式开发板等无音频输出设备的场景。

---

## 2. 技术栈选型

| 层面 | 选择 | 理由 |
|------|------|------|
| 音频捕获 | **WASAPI Loopback (C# COM P/Invoke)** | 用户态 API，零驱动安装。从默认音频渲染设备捕获混音后的输出流。不需要虚拟音频驱动 |
| 主程序 | **C# .NET 8** | 与 Windows COM API、ADB 进程管理交互方便，开发效率高 |
| Android 音频播放 | **C + NDK (AAudio API)** ARM64 静态链接 ELF | 无需 APK 打包。~300 行 C 程序，TCP socket 读取 PCM 数据，AAudio 写入扬声器。ADB push 到设备后直接运行 |
| USB 传输 | **ADB TCP 端口转发 (adb forward tcp)** | adbd 内置功能，可靠的全双工流，解耦读写速率，避免管道缓冲区死锁 |
| 音频传输格式 | **原始 PCM 流 (32-bit int 48000Hz 双声道)** | 免编解码延迟最小，USB 2.0 带宽足够 |
| 设备检测 | **ADB 命令 + Android `dumpsys audio`** | 通过 `adb devices` 枚举设备，通过 `adb shell dumpsys audio` 多关键词匹配判断设备扬声器能力 |
| 构建工具 | MSBuild / Android NDK r27d | C# 用 MSBuild 构建；C 播放器用 NDK 的 aarch64-linux-android26-clang 交叉编译 |

**为什么不用虚拟音频驱动？** WASAPI Loopback 可以在不出现在音频设备列表中的情况下捕获系统混音输出。用户不需要安装任何驱动，也不需要关闭安全启动或进入测试模式。代价是没有持久的虚拟音频设备，程序退出后音频转发终止。

**为什么不用 libusb？** ADB 提供成熟的 USB 数据通道，用户只需一次性 "开启 USB 调试" 配置。libusb 需要编写 USB 驱动且兼容性差。

**为什么不用 Android App（Kotlin + SDK）？** 不需要。一个 ADB 启动的进程即可完成 PCM 播放，无需 Activity、Service、Manifest、APK 打包。开发和调试成本降低一个数量级。

---

## 3. 系统架构

### 架构风格：管道-过滤器

音频数据流是纯粹的管道-过滤器模式：WASAPI Loopback 捕获 → 格式转换 → 生产者-消费者队列 → TCP 写入 → ADB 转发 → Android 播放器 → 扬声器。

### 核心模块

```
┌──────────────────────────────────────────────────────────────────┐
│                          Windows 电脑                             │
│                                                                  │
│  ┌──────────────────────┐   ┌─────────────────────────────┐      │
│  │  Windows 音频系统      │   │  AudioBridge 服务           │      │
│  │  (WASAPI 混音器)      │   │  (C# .NET 8 Console App)   │      │
│  │                      │   │                              │      │
│  │  应用输出 → 默认渲染   │──→│  ┌─────────────────────┐   │      │
│  │  设备 (板载/HDMI等)   │   │  │ AudioCaptureManager  │   │      │
│  └──────────────────────┘   │  │ (WASAPI Loopback)     │   │      │
│                             │  └─────────┬───────────┘   │      │
│                             │            │ PCM byte[]     │      │
│                             │  ┌─────────▼───────────┐   │      │
│                             │  │ SampleConverter      │   │      │
│                             │  │ (float→int / SRC /   │   │      │
│                             │  │  位深转换)           │   │      │
│                             │  └─────────┬───────────┘   │      │
│                             │            │ 转换后PCM      │      │
│                             │  ┌─────────▼───────────┐   │      │
│                             │  │ StreamForwarder      │   │      │
│                             │  │ (生产者-消费者队列 +  │   │      │
│                             │  │  TCP写入)            │   │      │
│                             │  └─────────┬───────────┘   │      │
│                             │            │ TCP 27777      │      │
│                             │  ┌─────────▼───────────┐   │      │
│                             │  │ DeviceWatcher        │   │      │
│                             │  │ (ADB 设备轮询)        │   │      │
│                             │  └─────────────────────┘   │      │
│                             └─────────────────────────────┘      │
│                                         │ adb forward tcp:27777  │
└─────────────────────────────────────────┼────────────────────────┘
                                          │ USB / TCP
┌─────────────────────────────────────────┼────────────────────────┐
│                          Android 手机    │                        │
│                                         ▼                        │
│                            ┌──────────────────────────┐          │
│                            │  audio_player (C)         │          │
│                            │  ADB shell 启动的进程      │          │
│                            │                           │          │
│                            │  TCP server 0.0.0.0:27777 │          │
│                            │    ↓ accept               │          │
│                            │  TCP client_fd            │          │
│                            │    ↓ read()               │          │
│                            │  AAudioStream_write()     │          │
│                            │    ↓                      │          │
│                            │  设备扬声器                │          │
│                            └──────────────────────────┘          │
└──────────────────────────────────────────────────────────────────┘
```

### 模块职责

| 模块 | 职责 |
|------|------|
| **AudioCaptureManager** | 通过 WASAPI Loopback 从默认音频渲染设备捕获 PCM 数据，管理 COM 生命周期，监听设备变更通知 |
| **SampleConverter** | float→int 转换、采样率转换（线性插值 SRC）、整数位深转换（8/16/24/32 互转） |
| **StreamForwarder** | 建立 ADB 端口转发、推送并启动 audio_player、生产者-消费者队列管理、TCP 写入、端到端延迟监控 |
| **DeviceWatcher** | 每 1 秒轮询 ADB 设备列表，检测连接/断开，获取设备型号，判断扬声器能力 |
| **AdbWrapper** | ADB 命令封装（devices、dumpsys audio、push、forward、shell 等） |
| **audio_player** | C 程序，创建设备端 TCP 服务端，从 TCP socket 读取 PCM，通过 AAudio API 写入扬声器 |

### 模块间通信

- **WASAPI COM 接口（进程内）**：AudioCaptureManager 通过 COM 接口直接调用 WASAPI，从系统音频引擎获取 PCM 缓冲区指针
- **C# 模块之间**：同一进程内的事件回调（AudioDataReceived、DeviceConnected 等）和直接方法调用
- **StreamForwarder ↔ audio_player**：ADB TCP 端口转发（`adb forward tcp:27777 tcp:27777`），C# 端 TcpClient 连接本地 27777，ADB 将数据透传到设备端 audio_player 的 TCP 服务端

---

## 4. 目录结构

```
Win_Use_Andorid_Audio/
├── DESIGN.md                        # 本设计文档
├── CLAUDE.md                        # 项目指南
├── 需求.md                          # 需求文档
├── README.md                        # 使用说明
│
├── service/                         # 用户态桥接服务 (C# .NET 8)
│   ├── AudioBridge.sln
│   └── AudioBridge/
│       ├── AudioBridge.csproj       # 项目文件，将config.json和audio_player链接到requirement/子目录
│       ├── Program.cs               # 入口：编排整个流程
│       ├── Config.cs                # 配置管理
│       ├── config.json              # 运行时配置（音频参数、ADB路径、日志级别、前置衰减）
│       │
│       ├── DeviceMonitor/
│       │   ├── DeviceWatcher.cs     # ADB 设备枚举与状态机
│       │   ├── AdbWrapper.cs        # ADB 命令封装
│       │   └── DeviceInfo.cs        # 设备信息模型
│       │
│       ├── AudioCapture/
│       │   ├── AudioCaptureManager.cs  # WASAPI Loopback 捕获
│       │   ├── SampleConverter.cs      # float→int / SRC / 位深转换
│       │   └── WasapiInterop.cs        # WASAPI COM 接口 P/Invoke 声明
│       │
│       ├── Streaming/
│       │   ├── StreamForwarder.cs      # ADB TCP forward + 生产者-消费者写入
│       │   └── AudioPacket.cs          # PCM 数据封装（含时间戳）
│       │
│       └── Utils/
│           ├── Logger.cs               # 控制台 + 文件日志（自动清理旧日志）
│           └── SystemAudio.cs          # 已废弃，仅保留参考注释
│
├── audio_player/                    # Android 端音频播放器 (C + NDK)
│   ├── audio_player.c               # 主程序：TCP socket → AAudio → 扬声器
│   └── build_audio_player.ps1      # Windows 上一键编译脚本（自动下载 NDK）
│
├── scripts/
│   ├── build_all.ps1                # 一键构建所有组件
│   ├── run_bridge.ps1              # 启动 AudioBridge 服务
│   └── kill_audio_bridge.ps1       # 终止 AudioBridge 进程
│
├── Source/
│   ├── ndk/                         # Android NDK r27d（自动下载到此目录）
│   └── scream_origin/               # Scream 虚拟音频驱动源码（参考用）
```

---

## 5. 核心接口设计

### 5.1 WASAPI 捕获接口

```
// AudioCaptureManager 对外接口
class AudioCaptureManager : IDisposable {
    event EventHandler<byte[]> AudioDataReceived;   // PCM 数据事件
    event EventHandler DefaultDeviceChanged;         // 默认音频设备变更

    void SetTargetFormat(int sampleRate, int bitsPerSample, int channels);
    void Start();                                    // 初始化 WASAPI + 启动捕获循环
    void Stop();

    bool IsRunning { get; }
}

// 格式转换
static class SampleConverter {
    static byte[] ConvertFloatToInt(byte[] input, int targetBitsPerSample);
    static byte[] ConvertFloatToIntWithSRC(byte[] input, int inRate, int outRate, int ch, int bits);
    static byte[] ConvertIntToInt(byte[] input, int srcBits, int dstBits);
    static void SetPreAttenuationDb(double db);
}
```

### 5.2 ADB 管道接口

```
# 端口转发
adb -s <serial> forward tcp:27777 tcp:27777
adb -s <serial> forward --remove tcp:27777

# 推送并启动播放器
adb -s <serial> push audio_player /data/local/tmp/
adb -s <serial> shell chmod 755 /data/local/tmp/audio_player
adb -s <serial> shell -T /data/local/tmp/audio_player 48000 2 32 50 27777

# 设备检测
adb devices
adb -s <serial> shell dumpsys audio              # 检测扬声器
adb -s <serial> shell getprop ro.product.model   # 获取设备型号
```

### 5.3 audio_player 接口（Android 端）

```c
// audio_player.c — 完整的程序接口说明

// 命令行参数:
//   audio_player [sample_rate] [channels] [bits] [buffer_ms] [port]
//   默认: audio_player 48000 2 32 50 0
//   port=0 → stdin 模式（向后兼容）
//   port>0 → TCP 模式（当前模式）

// TCP 模式流程:
// 1. 创建 AAudioStream (AAUDIO_DIRECTION_OUTPUT, 指定格式参数)
// 2. wait_for_tcp_connection(port) → socket → bind 0.0.0.0:port → listen → accept
// 3. 循环: read(tcp_fd, buf, 4096) → AAudioStream_write(buf, 200ms timeout)
// 4. EOF 或 SIGINT/SIGTERM → AAudioStream_close → 退出

// 通知: 首次成功写入音频后发送 Android 通知（input keyevent + cmd notification post）
// 输出: 所有诊断信息输出到 stderr（经 ADB 传回 C# 服务）

// 返回值: 0 = 正常退出, 1 = 初始化失败
```

### 5.4 内部模块接口 (C# 类)

```csharp
// === DeviceMonitor ===

class DeviceInfo {
    string Serial;          // ADB 设备序列号
    string Model;           // 设备型号 (通过 getprop 获取)
    bool HasSpeaker;        // dumpsys audio 检测结果
    DeviceState State;      // Disconnected / Connected / Ready / Unsupported
}

enum DeviceState { Disconnected, Connected, Ready, Unsupported }

class DeviceWatcher : IDisposable {
    event EventHandler<DeviceInfo> DeviceConnected;
    event EventHandler<DeviceInfo> DeviceDisconnected;
    event EventHandler<string> ErrorOccurred;
    void Start();                       // 每 1 秒轮询
    void Stop();
    void ForgetDevice(string serial);   // 用于重连场景
}

class AdbWrapper {
    bool IsAvailable();
    List<DeviceInfo> GetDevices();
    bool CheckHasSpeaker(string serial);
    string? GetDeviceModel(string serial);
    bool PushFile(string local, string remote, string serial);
    bool SetExecutable(string remote, string serial);
    bool SetupForward(string serial, int port);
    bool RemoveForward(string serial, int port);
    Process StartAudioPlayer(string serial, string remotePath, int port, ...);
    void KillAudioPlayer(Process process);
    bool CleanupRemote(string remote, string serial);
}


// === Streaming ===

class StreamForwarder : IDisposable {
    event EventHandler ConnectionLost;      // TCP 连接断开

    bool Start(string serial, string localPlayerPath, ...);
    void Stop();
    void SendAudioData(byte[] pcmData);     // 生产者：入队
    bool IsConnected { get; }
}

class AudioPacket {
    byte[] Data { get; }
    DateTime Timestamp { get; }             // 用于延迟追踪
}
```

### 5.5 配置项

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

`PreAttenuationDb`: 前置衰减 dB。系统混音器叠加多个音源时峰值可能超过 1.0f，-6dB（0.5x）覆盖约 2 路满幅音源。可调低（-9dB、-12dB）以应对更多音源同时输出的场景。

---

## 6. 数据流设计

### 6.1 主业务流程

```
[用户插上手机 USB 线]
     │
     ▼
[DeviceWatcher 检测到新 ADB 设备]  ← 每1秒adb devices
     │
     ▼
[检查设备是否有扬声器]  ← adb shell dumpsys audio（多关键词匹配）
  ┌──┴──┐
  │ 有  │  无 → [提示"该设备不包含扬声器，无法使用"] → 结束
  └──┬──┘
     │
     ▼
[获取设备型号]  ← adb shell getprop ro.product.model
     │
     ▼
[设备连接事件] DeviceConnected
     │
     ├─── Step 1: SetupForward → adb forward tcp:27777 tcp:27777
     ├─── Step 2: PushFile + chmod (audio_player → /data/local/tmp/)
     ├─── Step 3: StartAudioPlayer → adb shell -T (TCP模式, port=27777)
     ├─── Step 4: 等待 "Waiting for TCP connection on" 标记
     ├─── Step 5: TcpClient.Connect(127.0.0.1:27777)
     ├─── Step 6: 启动生产者-消费者队列 + 写入线程
     └─── Step 7: AudioCaptureManager.Start() → WASAPI Loopback开始捕获
     │
     ▼
[主循环: WASAPI → 格式转换 → 队列 → TCP → ADB → Android → 扬声器]
     │
     ▼
[用户拔掉 USB / 进程退出 / TCP 连接丢失]
     │
     ▼
[安全停止: 停止捕获 → 排空队列 → 关闭TCP → Kill进程 → 移除forward → 清理]
```

### 6.2 音频数据路径（详细）

```
Windows 音频子系统 (应用输出 → 默认渲染设备)
    │
    ▼
WASAPI Loopback (AudioCaptureManager)
    │ IMMDeviceEnumerator → IAudioClient(LOOPBACK) → IAudioCaptureClient
    │ GetBuffer → Marshal.Copy → ReleaseBuffer
    ▼
格式转换 (ConvertFormat 内部方法)
    │ float→int (最常见)
    │ 或 线性插值 SRC (float输入, 采样率不匹配时)
    │ 或 整数位深转换 (8/16/24/32互转)
    ▼
AudioDataReceived 事件 (byte[] pcmData)
    │
    ▼
StreamForwarder.SendAudioData()
    │ BlockingCollection<AudioPacket>.Add()
    ▼
生产者-消费者队列 (最大500包，满时丢弃)
    │ WriteLoop() 专用线程 foreach 消费
    ▼
TcpClient.GetStream().Write(data)
    │
    ▼
127.0.0.1:27777 → adb forward
    │ ADB 守护进程通过 USB 透传 TCP 数据
    ▼
audio_player (Android, TCP server 0.0.0.0:27777)
    │ accept() → client_fd
    │ read(client_fd, buf, 4096)
    ▼
AAudioStream_write(stream, buf, frame_count, 200ms timeout)
    │
    ▼
Android AudioFlinger → 硬件混音器 → 扬声器
```

### 6.3 设备状态机

```
          ┌────────────────┐
          │  Disconnected   │
          └───────┬────────┘
                  │ 检测到 USB 连接 + ADB 设备
                  ▼
          ┌────────────────┐
          │   Connected     │  (已连接，验证中)
          └───────┬────────┘
                  │ adb shell dumpsys → 检查扬声器
          ┌───────┴────────┐
          │                │
          ▼                ▼
 ┌────────────────┐ ┌────────────────┐
 │     Ready       │ │  Unsupported   │
 │ (有扬声器)      │ │ (无扬声器)     │
 │ → 开始转发音频  │ │ → 提示用户     │
 └───────┬────────┘ └────────────────┘
         │
         │ USB 断开 / ADB 断开 / TCP 写入异常
         ▼
 ┌────────────────┐
 │  Disconnected   │
 │  (自动清理)      │
 └────────────────┘
         │
         │ (下次轮询可能重新检测)
         ▼
    [回到 Disconnected]
```

### 6.4 异常/错误处理路径

| 异常场景 | 处理方式 |
|----------|----------|
| ADB 未找到 | 启动时检测 adb.exe，提示"请安装 ADB 工具"并退出 |
| 手机未开启 USB 调试 | `adb devices` 不显示"device"状态，不会被检测到 |
| 设备无扬声器 | 提示"该设备不包含扬声器，无法作为音频输出设备" |
| USB 断开 | DeviceWatcher 检测到设备消失 → 停止转发 → 清理 → 提示"设备已断开" |
| TCP 连接异常 | StreamForwarder 触发 ConnectionLost → Program.cs 断开设备 + ForgetDevice → 下次轮询自动重连 |
| audio_player 崩溃 | Exited 事件记录 exit code → stderr reader 结束 → TCP 写入失败 → ConnectionLost → 自动重连 |
| WASAPI 初始化失败 | 日志 Error → 跳过启动，不影响 ADB 管道建立 |
| WASAPI 默认设备切换 | IMMNotificationClient 检测 → 异步重启 WASAPI 捕获 |
| 音频卡顿/队列满 | 日志 Warn → 丢弃新数据 → 继续处理已有数据 |
| 配置文件缺失 | 使用默认配置 + Warn 提示 |

---

## 7. 数据存储设计

本项目**不需要持久化存储**。音频流实时传输、不落盘。配置通过 `config.json` 保存（见 5.5 节）。日志文件存储在 `Log/` 目录，按日期管理，非当日日志在下次启动时自动清理。

---

## 8. 分步实现计划

### 阶段一：Android 音频播放器与 ADB 管道（已完成）

**目标**：通过 `adb shell -T` 在手机上启动播放器进程，Android 扬声器发声。

**具体工作**：

1. 编写 `audio_player.c`（~300 行）：
   - 使用 AAudio API 创建音频输出流
   - wait_for_tcp_connection() 创建设备端 TCP 服务端
   - 循环 read(tcp_fd) → AAudioStream_write()
   - 处理 SIGINT/SIGTERM 信号优雅退出
   - 首次写入后发送手机通知
2. 编写 `build_audio_player.ps1`，用 NDK standalone toolchain 交叉编译（自动下载 NDK）
3. C# 端测试工具验证：adb push → adb forward → adb shell -T → TCP 写入 PCM 数据

**前置依赖**：Android NDK (r27d)，一台开启 USB 调试的手机（Android 8.0+，AAudio 的最低版本）

### 阶段二：WASAPI Loopback 捕获（已完成）

**目标**：通过 WASAPI Loopback 从 Windows 默认音频渲染设备捕获 PCM 数据。

**具体工作**：

1. 声明 WASAPI COM 接口（WasapiInterop.cs）：IMMDeviceEnumerator、IAudioClient、IAudioCaptureClient
2. 实现 AudioCaptureManager：
   - WASAPI 初始化：GetDefaultAudioEndpoint → Activate → GetMixFormat → Initialize(LOOPBACK) → Start
   - 捕获循环：GetNextPacketSize → GetBuffer → 格式转换 → AudioDataReceived → ReleaseBuffer
   - 5 秒统计定时器
   - IMMNotificationClient 注册监听默认设备变更
3. 实现 SampleConverter：float→int、SRC（线性插值）、整数位深转换

**前置依赖**：Windows 10/11，任意活动的音频输出设备

### 阶段三：主服务集成（已完成）

**目标**：完整的 C# 服务自动完成设备检测、音频捕获、ADB 转发全流程。

**具体工作**：

1. 实现 DeviceWatcher：每 1 秒轮询 ADB 设备列表，管理状态机
2. 实现 AdbWrapper：封装 devices、dumpsys audio、push、forward、shell 等命令
3. 实现 StreamForwarder：ADB 端口转发、audio_player 生命周期管理、生产者-消费者队列、TCP 写入
4. 实现 Program.cs 流程编排：设备连接→转发建立→捕获启动→异常处理→自动重连
5. 实现音频格式链式转换（ConvertFormat），支持采样率、位深、声道不匹配的降级处理

### 阶段四：部署与优化（完成度 90%）

**目标**：一键部署，性能达标，异常自愈。

**具体工作**：

1. `build_all.ps1` 一键构建脚本
2. `run_bridge.ps1` 一键启动脚本
3. C# 单文件发布（dotnet build --configuration Release）
4. 生产者-消费者队列解耦捕获和写入速率
5. 前置衰减（PreAttenuationDb）应对多音源削波
6. IMMNotificationClient 处理默认设备切换
7. ConnectionLost + ForgetDevice 自动重连机制
8. 5 秒统计定时器监控音频流健康状态

**待优化**：
- audio_player 崩溃自动重启（当前依赖 ConnectionLost → 轮询重连）
- 多设备支持
- 无音频设备时的自动重试
