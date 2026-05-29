# 跨平台音频桥接方案设计文档

## 1. 项目概述

本项目解决电脑缺少扬声器/音响时的声音输出问题。用户通过 USB 线连接一部 Android 手机到电脑，程序自动将电脑的所有音频输出转发到手机的扬声器播放。核心价值是零成本复用现有设备的扬声器，无需购买外接音响。

目标用户：刚购入电脑但尚未配备音响的用户、临时需要声音输出的场景、嵌入式开发板等无音频输出设备的场景。

---

## 2. 技术栈选型

| 层面 | 选择 | 理由 |
|------|------|------|
| 虚拟音频驱动 | **C++ (KMDF / WDM Audio Driver)** 基于 [Scream](https://github.com/duncanthrax/scream) 改造 | Windows 虚拟音频设备必须通过内核级驱动实现，WASAPI 等用户态 API 无法创建虚拟端点。Scream 是 MIT 开源的全双工虚拟音频驱动，可直接作为起点修改 |
| 用户态主程序 | **C# .NET 8** | 与 Windows 音频 API、命名管道和 ADB 进程通信方便，开发效率高。NAudio 库提供音频格式转换支持 |
| Android 音频播放 | **C + NDK (AAudio API)** 交叉编译为 ARM64 静态链接 ELF | 不需要开发 Android App。一个约 100 行的 C 程序，通过 AAudio 从 stdin 读取 PCM 数据并播放到扬声器。用 NDK 交叉编译后 `adb push` 到设备，通过 `adb exec-out` 启动并管道传输 PCM |
| USB 传输 | **ADB exec-out 管道** | Android 设备默认运行 adbd。PC 端通过 `adb exec-out` 启动设备上的播放器进程，播放器从 stdin 读取 PCM。不需要 TCP forward，不需要网络，纯管道传输 |
| 音频传输格式 | **原始 PCM 流 (16-bit 48000Hz 双声道)** | 免编解码延迟最小，USB 2.0 带宽足够。播放器不做任何编解码，直接从 stdin read → AAudio write |
| 设备检测 | **ADB 命令 + Android `dumpsys audio`** | 通过 `adb devices` 枚举设备，通过 `adb shell dumpsys audio` 判断设备是否具有扬声器 |
| 构建工具 | MSBuild / Android NDK | C# 用 MSBuild 构建；C 播放器用 NDK 的 standalone toolchain 编译 |

**为什么不是 C++ 全栈？** 用户态涉及大量 Windows API 调用和 ADB 进程管理，C# 开发效率更高。仅在必须的内核驱动和 Android 播放器部分使用 C/C++。

**为什么不用 libusb？** ADB 提供了成熟的 USB 数据通道，用户只需一次性的"开启 USB 调试"配置。libusb 方式需要编写 USB 驱动且兼容性差。

**为什么不用 Android App（Kotlin + SDK）？** 不需要。一个 `adb exec-out` 启动的进程即可完成 PCM 播放，无需 Activity、Service、Manifest、APK 打包。开发和调试成本降低一个数量级。

---

## 3. 系统架构

### 架构风格：管道-过滤器

音频数据流是纯粹的管道-过滤器模式：Windows 音频系统 → 虚拟驱动 → 命名管道 → C# 服务 → ADB 管道 → Android 播放器进程 → 扬声器。不需要网络层和 TCP 协议。

### 核心模块

```
┌──────────────────────────────────────────────────────────────────┐
│                          Windows 电脑                             │
│                                                                  │
│  ┌──────────────────┐     ┌─────────────────────────────┐        │
│  │  Windows 音频系统  │     │   Audio Bridge Service      │        │
│  │  (WASAPI)         │     │   (C# .NET 8 Console App)   │        │
│  │                   │     │                              │        │
│  │  应用输出音频 →    │────→│  ┌─────────────────────┐   │        │
│  │                   │     │  │ AudioCaptureManager  │   │        │
│  └───────────────────┘     │  │ (命名管道读取 PCM)   │   │        │
│          │                 │  └─────────┬───────────┘   │        │
│          ▼                 │            │ PCM 数据      │        │
│  ┌──────────────────┐      │  ┌─────────▼───────────┐   │        │
│  │  虚拟音频驱动      │      │  │ StreamForwarder     │   │        │
│  │  (C++ KMDF)       │◄────│  │ (写入 adb exec-out   │   │        │
│  │  Scream 改造 →     │     │  │  进程的 stdin)       │   │        │
│  │  命名管道输出      │     │  └─────────┬───────────┘   │        │
│  └──────────────────┘      │            │ stdin           │        │
│                            │  ┌─────────▼───────────┐   │        │
│                            │  │ DeviceMonitor       │   │        │
│                            │  │ (USB/ADB 设备检测)   │   │        │
│                            │  └─────────────────────┘   │        │
│                            └─────────────────────────────┘        │
│                                         │ adb exec-out (stdin)    │
└─────────────────────────────────────────┼────────────────────────┘
                                          │ USB
┌─────────────────────────────────────────┼────────────────────────┐
│                          Android 手机   │                         │
│                                         ▼                        │
│                            ┌───────────────────────┐             │
│                            │  audio_player (C)      │             │
│                            │  (adb exec-out 启动的   │             │
│                            │   临时进程)             │             │
│                            │                        │             │
│                            │  stdin ← 管道          │             │
│                            │    ↓                   │             │
│                            │  AAudio/AudioTrack     │             │
│                            │    ↓                   │             │
│                            │  扬声器                 │             │
│                            └───────────────────────┘             │
└──────────────────────────────────────────────────────────────────┘
```

### 模块职责

| 模块 | 职责 |
|------|------|
| **虚拟音频驱动** | 注册为 Windows 音频输出设备，接收 WASAPI 音频流，通过命名管道发送到用户态服务 |
| **AudioCaptureManager** | 从命名管道读取 PCM 音频数据，管理缓冲区，统一采样格式 |
| **StreamForwarder** | 启动 `adb exec-out /data/local/tmp/audio_player`，将 PCM 数据写入该进程的 stdin |
| **DeviceMonitor** | 定时枚举 ADB 设备，检测连接/断开，查询扬声器能力，断线时切回默认音频设备 |
| **audio_player** | 极简 C 程序（≈100行），从 stdin 读取 PCM，通过 AAudio API 写入扬声器 |

### 模块间通信

- **虚拟音频驱动 ↔ AudioCaptureManager**: **命名管道** `\\.\pipe\AudioBridgePipe`。内核态驱动写入，用户态 C# 服务读取。
- **StreamForwarder ↔ audio_player**: **ADB exec-out 管道**。C# 进程启动 `adb exec-out` 并获得其 stdin 句柄，直接写入 PCM 数据。不需要 TCP，不需要端口转发。
- **C# 模块之间**: 同一进程内的函数调用和事件回调。

---

## 4. 目录结构

```
Win_Use_Andorid_Audio/
├── DESIGN.md                        # 本设计文档
├── CLAUDE.md                        # 项目指南
├── README.md                        # 使用说明
│
├── driver/                          # 虚拟音频驱动 (C++ KMDF)
│   ├── scream/                      # Scream 源码 fork
│   │   ├── driver/                  # WDM 音频驱动内核代码
│   │   │   ├── audio.cpp
│   │   │   ├── audio.h
│   │   │   ├── device.cpp
│   │   │   ├── device.h
│   │   │   └── driver.c
│   │   └── inf/
│   │       └── screamaudio.inf
│   └── patch/
│       └── pipe_output.patch        # Scream 修改：UDP → 命名管道
│
├── service/                         # 用户态桥接服务 (C# .NET 8)
│   ├── AudioBridge.sln
│   └── AudioBridge/
│       ├── AudioBridge.csproj
│       ├── Program.cs               # 入口：编排整个流程
│       ├── Config.cs                # 配置管理
│       │
│       ├── DeviceMonitor/
│       │   ├── DeviceMonitor.cs     # ADB 设备枚举与状态机
│       │   ├── AdbWrapper.cs        # ADB 命令封装
│       │   └── DeviceInfo.cs        # 设备信息模型
│       │
│       ├── AudioCapture/
│       │   ├── AudioCaptureManager.cs  # 命名管道读取 PCM
│       │   └── SampleConverter.cs      # 采样格式转换
│       │
│       ├── Streaming/
│       │   ├── StreamForwarder.cs      # adb exec-out 进程 + stdin 写入
│       │   └── AudioPacket.cs          # PCM 数据块封装（含时间戳）
│       │
│       └── Utils/
│           ├── Logger.cs               # 日志 + 中文提示
│           └── SystemAudio.cs          # 默认音频设备切换
│
├── audio_player/                    # Android 端音频播放器 (C + NDK)
│   ├── audio_player.c               # 主程序：stdin → AAudio → 扬声器
│   ├── Makefile                     # NDK standalone 交叉编译
│   └── build_audio_player.ps1      # Windows 上一键编译脚本
│
├── scripts/
│   ├── build_driver.ps1             # 编译驱动
│   ├── install_driver.ps1           # 安装驱动（管理员）
│   └── run_bridge.ps1              # 一键启动
│
└── tools/
    └── adb/                         # adb.exe（或系统 PATH 中的 adb）
```

---

## 5. 核心接口设计

### 5.1 虚拟音频驱动接口（内核态 ↔ 用户态）

```
命名管道名称: \\.\pipe\AudioBridgePipe
管道服务端: AudioBridge.Service (C#, Named Pipe Server)
管道客户端: 虚拟音频驱动 (内核态 ZwCreateFile)
```

数据块格式（驱动输出到管道的每个消息）：

```c
typedef struct _AUDIO_BLOCK {
    UINT32  SampleRate;      // 采样率 (44100 / 48000)
    UINT16  BitsPerSample;   // 位深 (16 / 24 / 32)
    UINT16  Channels;        // 声道数 (1 / 2)
    UINT32  DataSize;        // 音频数据大小 (bytes)
    BYTE    Data[DataSize];  // PCM 样本数据
} AUDIO_BLOCK;
```

### 5.2 ADB 管道接口

```
# 启动流程（由 C# StreamForwarder 完成）：

# Step 1: 推送播放器到设备
adb push audio_player /data/local/tmp/

# Step 2: 设置可执行权限
adb shell chmod 755 /data/local/tmp/audio_player

# Step 3: 启动播放器并绑定 stdin（关键步骤）
adb exec-out /data/local/tmp/audio_player
  → C# 端保留此进程的 Process 对象
  → C# 端将 PCM 数据不断写入 process.StandardInput.BaseStream

# Step 4: 退出时自动清理
adb shell rm /data/local/tmp/audio_player

# 设备检测
adb devices                          # 枚举设备
adb -s <serial> shell dumpsys audio  # 检查是否有扬声器
```

### 5.3 audio_player 接口（Android 端）

```c
// audio_player.c — 完整的程序接口说明

// 命令行参数 (可选):
//   audio_player [sample_rate] [channels] [buffer_ms]
//   默认值: 48000, 2, 50

// 输入: stdin
//   持续读取 PCM 数据，格式: 16-bit signed, little-endian,
//   声道交错 (L,R,L,R,...)，默认 48000Hz

// 输出: 手机扬声器
//   使用 AAudio API 创建音频流，以 MODE_STREAM 模式写入

// 退出条件:
//   stdin 关闭 (EOF) → 播放完缓冲区剩余数据后退出

// 返回值:
//   0 = 正常退出, 1 = AAudio 初始化失败
```

### 5.4 内部模块接口 (C# 类)

```csharp
// === DeviceMonitor ===

class DeviceInfo {
    string Serial;
    string Model;
    bool HasSpeaker;
    DeviceState State;
}

enum DeviceState { Disconnected, Connected, Ready, Unsupported }

class DeviceMonitor : IDisposable {
    event EventHandler<DeviceInfo> DeviceConnected;
    event EventHandler<DeviceInfo> DeviceDisconnected;
    event EventHandler<string> ErrorOccurred;      // 中文提示
    void Start();                                   // 每 1 秒轮询
    void Stop();
}

class AdbWrapper {
    DeviceInfo[] GetDevices();
    bool CheckHasSpeaker(string serial);            // dumpsys audio
    Process StartAudioPlayer(string serial);         // adb exec-out
    void KillAudioPlayer(Process proc);
    bool PushFile(string local, string remote);
}


// === AudioCapture ===

class AudioCaptureManager : IDisposable {
    event EventHandler<byte[]> AudioDataReceived;   // PCM 事件
    void Start();                                   // 连接命名管道
    void Stop();
}

class SampleConverter {
    byte[] ConvertTo16Bit48kHz(
        byte[] input, int srcRate, int srcBits, int srcChannels);
}


// === Streaming ===

class StreamForwarder : IDisposable {
    void Start(string deviceSerial);
    void Stop();
    void SendAudioData(byte[] pcmData);             // 写入 ADB stdin
    bool IsConnected { get; }
}
```

### 5.5 配置项

```json
{
  "Audio": {
    "TargetSampleRate": 48000,
    "TargetBitsPerSample": 16,
    "TargetChannels": 2,
    "BufferSizeMs": 50
  },
  "Adb": {
    "AdbPath": "tools/adb/adb.exe",
    "PlayerBinary": "audio_player"
  },
  "Pipe": {
    "PipeName": "AudioBridgePipe"
  },
  "Logging": {
    "Level": "Info",
    "MaxFiles": 7
  }
}
```

---

## 6. 数据流设计

### 6.1 主业务流程

```
[用户插上手机 USB 线]
     │
     ▼
[DeviceMonitor 检测到新 ADB 设备]
     │
     ▼
[检查设备是否有扬声器]  ← adb shell dumpsys audio
  ┌──┴──┐
  │ 有  │  无 → [提示"该设备不包含扬声器，无法使用"] → 结束
  └──┬──┘
     ▼
[adb push audio_player → /data/local/tmp/]
[adb shell chmod 755 …]
     │
     ▼
[安装/启动虚拟音频驱动（如尚未安装）]
     │
     ▼
[启动 AudioCaptureManager → 连接命名管道]
     │
     ▼
[启动 adb exec-out /data/local/tmp/audio_player]
  → 获取 process.StandardInput 句柄
     │
     ▼
[主循环: 命名管道 → PCM → SampleConverter → ADB stdin → Android手机播放]
     │
     ▼
[用户拔掉 USB 或程序退出]
     │
     ▼
[恢复 Windows 默认音频设备 → 终止 adb exec-out → 清理设备文件]
```

### 6.2 音频数据路径（详细）

```
Windows 音频子系统 (应用输出 → 虚拟设备)
    │
    ▼
虚拟音频驱动 (Scream 改造)
    │ 从 WDM 缓冲区提取 PCM，写入命名管道
    ▼
命名管道 \\.\pipe\AudioBridgePipe
    │ 内核态 → 用户态传输
    ▼
AudioCaptureManager
    │ 读取 AUDIO_BLOCK，保持内部环形缓冲区
    ▼
SampleConverter
    │ 将任意采样格式转换为 16-bit 48000Hz 立体声
    ▼
StreamForwarder
    │ 写入 adb exec-out 进程的 stdin
    ▼
adb exec-out (USB 传输)
    │ stdin 数据流经 USB → Android 端进程
    ▼
audio_player (stdin)
    │ 循环: read(stdin, buf, 4096) → AAudioStream::write(buf)
    ▼
AAudio 音频流 → 硬件混音器 → 扬声器
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
         │ USB 断开 / ADB 断开 / audio_player 进程退出
         ▼
 ┌────────────────┐
 │  Disconnected   │
 └────────────────┘
```

### 6.4 异常/错误处理路径

| 异常场景 | 处理方式 |
|----------|----------|
| ADB 未找到 | 启动时检测 adb.exe，提示"请安装 ADB 工具" |
| 手机未开启 USB 调试 | `adb devices` 显示 unauthorized，提示"请在手机上开启 USB 调试并授权" |
| 设备无扬声器 | 提示"该设备不包含扬声器，无法作为音频输出设备" |
| USB 断开 | DeviceMonitor 检测到设备消失 → 终止 exec-out 进程 → 恢复默认音频设备 → 提示"设备已断开" |
| audio_player 崩溃 | StreamForwarder 检测到进程退出 → 自动重启 |
| 驱动安装失败 | 提示"虚拟音频驱动安装失败，请尝试以管理员身份运行" |
| 音频卡顿 | 自动增大缓冲区大小 |

---

## 7. 数据存储设计

本项目**不需要持久化存储**。音频流实时传输、不落盘。配置通过 `config.json` 保存（见 5.5 节）。

---

## 8. 分步实现计划

### 阶段一：虚拟音频驱动改造（预计 3-5 天）

**目标**：虚拟音频驱动能在 Windows 中注册为扬声器设备，将音频数据输出到命名管道。

**具体工作**：

1. 克隆 [Scream](https://github.com/duncanthrax/scream) 仓库，理解其驱动架构
2. 修改 Scream 驱动：移除 UDP 网络发送，改为写入命名管道
3. 编写 INF 文件，注册为"AudioBridge Virtual Audio Device"
4. 用 C# 写一个测试工具连接命名管道，验证接收到的音频数据可通过写入 WAV 文件回放

**前置依赖**：Windows 10/11 + Visual Studio 2022 (C++ 桌面开发 + WDK)，驱动测试签名模式

**验收标准**：
- Windows 声音输出设备中可见 AudioBridge 设备
- 设置默认设备后播放音乐，命名管道侧能收到可识别 PCM 数据

### 阶段二：Android 音频播放器与 ADB 管道（预计 2 天）

**目标**：通过 `adb exec-out` 在手机上启动播放器进程，PC 端写入 PCM 数据到其 stdin，手机扬声器发声。

**具体工作**：

1. 编写 `audio_player.c`（约 100 行）：
   - 使用 AAudio API 创建音频输出流
   - 从 stdin 循环读取 PCM 数据并写入 AAudioStream
   - 处理 stdin EOF 退出和 SIGTERM 信号
2. 编写 Makefile，用 NDK standalone toolchain 交叉编译
3. 用 C# 写测试工具：`adb push` 播放器 → `adb exec-out` → 写入测试 PCM 数据
4. 验证：播放已知的正弦波或 WAV 数据，手机扬声器输出可识别的声音

**前置依赖**：Android NDK (r25+)，一台开启 USB 调试的手机（系统 Android 8.0+，AAudio 的最低版本）

**验收标准**：
- 手动执行 `adb exec-out /data/local/tmp/audio_player < test.pcm` 可听到声音
- C# 测试工具启动后，手机扬声器输出电脑播放的音乐

### 阶段三：主服务集成（预计 3-4 天）

**目标**：完整的 C# 服务自动完成设备检测、驱动加载、音频转发全流程。

**具体工作**：

1. 实现 `DeviceMonitor`：每 1 秒轮询 ADB 设备列表，管理状态机
2. 实现 `AdbWrapper`：封装 `devices`、`dumpsys audio`、`push`、`exec-out` 等命令
3. 实现 `StreamForwarder`：启动 ADB 进程并管理 stdin 写入
4. 实现 `SystemAudio`：通过 Windows API 设置/恢复默认音频设备
5. 实现 `Program.cs` 流程编排
6. 中文提示和错误处理覆盖所有用户可见输出

**前置依赖**：阶段一（驱动可输出命名管道）、阶段二（audio_player 可用）

**验收标准**：
- 插上手机启动程序 → 自动检测 →  开始转发 → 手机播放电脑声音
- 拔掉 USB → 自动停止转发 → 恢复默认音频设备 → 中文提示
- 连接不支持音频的设备 → 提示"该设备不包含扬声器"

### 阶段四：部署与优化（预计 2 天）

**目标**：一键部署，性能达标。

**具体工作**：

1. 驱动安装脚本（测试签名模式 + INF 安装）
2. `run_bridge.ps1` 一键启动脚本
3. C# 单文件发布（`dotnet publish --self-contained`）
4. 调节命名管道和 AAudio 缓冲区大小以优化延迟
5. 测试 44100/48000Hz、16/24/32-bit 格式的兼容性

**验收标准**：
- 新用户按 README 步骤在 10 分钟内完成配置
- 延迟 < 300ms，播放音乐无明显爆音卡顿
