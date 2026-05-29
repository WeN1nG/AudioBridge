# 构建与运行脚本

## build_all.ps1

一键构建所有组件：
1. 编译 Android audio_player（NDK 交叉编译）
2. 构建 C# AudioBridge 服务（dotnet build）

## build_audio_player.ps1

编译 audio_player C 二进制（需要 NDK, 自动使用 Source/ndk/ 下的工具链，首次自动下载 NDK）。

## run_bridge.ps1

启动 AudioBridge 服务（dotnet run --configuration Release）。

## kill_audio_bridge.ps1

终止 AudioBridge 服务进程。
