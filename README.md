# AudioBridge - 跨平台音频桥接方案

通过 USB 连接 Android 手机，将电脑音频转发到手机扬声器输出。无需购买外接音响。

## 工作原理

程序通过 **WASAPI Loopback** 从电脑当前默认音频输出设备捕获混音后的音频，经格式转换后通过 **ADB 端口转发** 发送到 Android 设备，由 **audio_player** 进程通过 **AAudio API** 播放到扬声器。

不需要安装任何驱动，不需要关闭安全启动。

## 文件说明

| 文件 | 说明 |
|------|------|
| `audio_player/audio_player.c` | Android 端 C 播放器，通过 NDK 交叉编译为 ARM64 ELF |
| `audio_player/audio_player` | 编译产物，通过 adb push 到设备执行 |
| `service/AudioBridge/` | C# .NET 8 控制台应用（桥接服务） |
| `scripts/` | 构建和运行脚本 |
| `Source/ndk/` | Android NDK r27d（构建 audio_player 所需） |

## 使用流程

### 1. 编译 Android 播放器

```
audio_player\build_audio_player.ps1
```

脚本会自动下载 NDK（如未找到），然后交叉编译 `audio_player.c`。

### 2. 构建桥接服务

```
dotnet build .\service\AudioBridge --configuration Release
```

或一键构建全部：

```
scripts\build_all.ps1
```

### 3. 连接手机并运行

1. 用 USB 线连接 Android 手机
2. 开启手机的 **USB 调试**（开发者选项）
3. 运行桥接服务：

```
scripts\run_bridge.ps1
```

4. 程序自动检测设备、推送播放器、开始转发音频
5. 手机扬声器将输出电脑的所有声音

### 4. 可选配置

编辑 `service/AudioBridge/config.json`：

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

- `AdbPath` 留空则自动搜索 adb.exe
- `PreAttenuationDb`：前置衰减，防止多个音源同时输出时削波。默认 -6dB

## 系统要求

- **Windows 10/11**（需要有一个活动的音频输出设备，如板载声卡、HDMI 音频、USB 声卡等）
- **Android 手机**（Android 8.0+，需开启 USB 调试）
- **ADB**（可自动搜索或手动配置路径）

## 注意事项

- 程序捕获的是电脑**默认音频输出设备**的混音输出，请确保系统有至少一个活动的音频设备
- 插拔 HDMI/蓝牙耳机等导致默认设备切换时，程序会自动重启捕获
- 拔掉 USB 或断开连接后，程序会自动清理并等待重新连接
