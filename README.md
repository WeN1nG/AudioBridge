# AudioBridge - 跨平台音频桥接方案

## 文件说明

| 文件 | 说明 |
|------|------|
| `audio_player/audio_player.c` | Android 端 C 播放器，通过 NDK 交叉编译为 ARM64 ELF |
| `audio_player/audio_player` | 编译产物，通过 adb push 到设备执行 |
| `service/AudioBridge/` | C# .NET 8 控制台应用（桥接服务） |
| `driver/` | Scream 虚拟音频驱动 (INF + .sys + .cat) |
| `scripts/` | 构建和运行脚本 |
| `Source/scream/` | Scream 项目源码（已预编译驱动在 Install/ 下） |
| `Source/ndk/` | Android NDK r27d |
| `Help/` | 开发调试文档 |
| `tools/` | 工具目录（ADB 等） |

## 使用流程

### 1. 安装虚拟音频驱动

以管理员身份运行：
```
scripts\install_driver.ps1
```

或在设备管理器中手动添加过时硬件 → 从磁盘安装 → 选择 `driver/x64/Scream.inf`

安装后，Windows 声音输出设备中会出现 "Scream" 设备。

### 2. 编译 Android 播放器

```
audio_player\build_audio_player.ps1
```

需要 NDK 已配置在 `Source/ndk/android-ndk-r27d`。

### 3. 运行桥接服务

```
scripts\run_bridge.ps1
```

或用 dotnet 直接运行：
```
cd service/AudioBridge && dotnet run
```

### 4. 连接手机

1. 用 USB 线连接 Android 手机
2. 开启 USB 调试
3. 程序自动检测设备并开始转发音频
4. 在 Windows 声音设置中选择 "Scream" 作为默认输出设备
