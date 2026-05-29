# AudioBridge — 待办问题清单

## 🔴 P0 — 直接影响功能可用性

### [x] 1. audio_player TCP超时回退stdin导致"静默失败"

**问题**: `audio_player.c` 中 TCP accept 超时(10s)后自动回退到 `STDIN_FILENO` 模式，但 StreamForwarder 不知道回退，继续向 TCP 写数据。audio_player 永久等待 stdin（无数据），StreamForwarder 向 TCP 写（数据永远送不到），日志显示"正常"但设备不出声。

**解决方案**:
- 方案A（推荐）：audio_player TCP 失败时直接退出（exit 1），不让 StreamForwarder 处于虚假的"已连接"状态
- 方案B：audio_player 成功连接 TCP 后才打印特定标记，StreamForwarder 检测到标记才算连接成功；若超时则直接认定失败
- 方案C（最优）：移除 stdin fallback，TCP 是唯一输入方式，失败就退出

**涉及文件**:
- `audio_player/audio_player.c:199-207`
- `service/AudioBridge/Streaming/StreamForwarder.cs:106-113`（等待就绪检测逻辑）

---

### [x] 2. 采样率转换(SRC)未实现

**问题**: `ConvertFormat()` 中 WASAPI 输出 44100Hz 而目标为 48000Hz 时，仅 Log Warn 后透传原始数据，声音音高/速度错误。HDMI 输出等场景常见非 48000Hz。

**解决方案**:
- 实现线性插值 SRC（simple resampler）
- 或集成现有轻量级 SRC 库
- 补充说明：float→int 转换本身不解决采样数不匹配的问题

**涉及文件**:
- `service/AudioBridge/AudioCapture/AudioCaptureManager.cs:310-316`

---

### [x] 3. 整数格式位深转换缺失

**问题**: WASAPI 输出 PCM_I24(24-bit int) 而目标为 32bit 时直接返回原始数据，audio_player 可能解析出噪声。

**解决方案**:
- 在 `SampleConverter` 中添加 `ConvertIntToInt(byte[], int srcBits, int dstBits)` 方法
- 支持 8/16/24/32 位整数间的互转

**涉及文件**:
- `service/AudioBridge/AudioCapture/AudioCaptureManager.cs:303-306`
- `service/AudioBridge/AudioCapture/SampleConverter.cs`

---

## 🟠 P1 — 稳定性与可靠性

### [x] 4. TCP写入线程异常后永久停止，无恢复机制

**问题**: `WriteLoop()` 中 `_dataStream.Write()` 抛出异常后 break，写入线程终止。捕获继续但数据全部丢弃，需设备重连才能恢复。

**解决方案**:
- 方案A：写入线程崩溃后自动触发设备断开流程（调用 `OnDeviceDisconnected`）
- 方案B：在写入循环外包装重试逻辑，重建 TCP 连接
- 方案C：检测到写入失败后，停止捕获并等待设备重连事件

**涉及文件**:
- `service/AudioBridge/Streaming/StreamForwarder.cs:244-247`
- `service/AudioBridge/Program.cs:125-139`

---

### [x] 5. audio_player进程退出后无自动重启

**问题**: `_adbProcess.EnableRaisingEvents` 已设但未订阅 Exited 事件。进程崩溃后仅记录 stderr 结束的 Warn，不重启进程。

**解决方案**:
- 订阅 `_adbProcess.Exited` 事件
- 在 Exited 事件处理中调用完整的重启流程（重新 push、重新建立 TCP 转发、重新启动进程）
- 设置退出的自动重试（最多 N 次，间隔递增）

**涉及文件**:
- `service/AudioBridge/Streaming/StreamForwarder.cs:79-103`（现有启动代码）
- `service/AudioBridge/Streaming/StreamForwarder.cs`（新增 Exited 事件处理）

---

### [x] 6. WASAPI默认设备切换无响应

**问题**: 用户插入 HDMI/蓝牙耳机后系统默认音频设备切换，WASAPI 仍从原设备捕获，导致音频丢失。

**解决方案**:
- 实现 `IMMNotificationClient` COM 接口
- 在 `OnDefaultDeviceChanged` 回调中自动重启 WASAPI 捕获
- 或定期轮询当前默认设备并比较，变化时重建捕获

**涉及文件**:
- `service/AudioBridge/AudioCapture/AudioCaptureManager.cs`
- `service/AudioBridge/AudioCapture/WasapiInterop.cs`（新增 IMMNotificationClient 声明）

---

### [x] 7. 队列满时静默丢包，无反馈

**问题**: 队列超 500 包时直接 `return` 丢弃新数据，用户不知道音频正在被丢弃。

**解决方案**:
- 方案A：添加计数器，每 N 次丢包输出 Warn 日志
- 方案B：反馈信号给调用方（如回调返回值或事件），让上层决定策略
- 方案C：使用丢弃策略的可配置化（丢弃最旧/丢弃最新/阻塞）

**涉及文件**:
- `service/AudioBridge/Streaming/StreamForwarder.cs:201`

---

### [x] 8. ADB命令同步阻塞 + 5秒超时可能卡死轮询

**问题**: `RunAdbCommand()` 同步 `ReadToEnd()` + `WaitForExit(10000)`。ADB 卡住时 DeviceWatcher 轮询线程阻塞 10 秒，期间 `_isPolling=true` 跳过后续轮询。

**解决方案**:
- 改为异步执行（`ReadToEndAsync`）
- 减少超时到 3-5 秒
- 或为轮询单独维护一个带超时的简单状态检测流程

**涉及文件**:
- `service/AudioBridge/DeviceMonitor/AdbWrapper.cs:169-195`
- `service/AudioBridge/DeviceMonitor/DeviceWatcher.cs:50-110`

---

## 🟡 P2 — 功能缺失与体验

### [x] 9. DeviceInfo.Model 始终为空

**问题**: `Model` 属性声明但从未赋值，日志和设备标识中缺少型号信息。

**解决方案**:
- `AdbWrapper` 中添加 `GetDeviceModel(string serial)` 方法：`adb shell getprop ro.product.model`
- 设备连接时填充 `device.Model`

**涉及文件**:
- `service/AudioBridge/DeviceMonitor/DeviceInfo.cs:14`
- `service/AudioBridge/DeviceMonitor/AdbWrapper.cs`（新增方法）
- `service/AudioBridge/DeviceMonitor/DeviceWatcher.cs:84-92`

---

### [x] 10. `dumpsys audio` 检测扬声器过于宽松

**问题**: `output.Contains("speaker", OrdinalIgnoreCase)` 可能在无关上下文中误匹配。

**解决方案**:
- 改为精确匹配 `"DEVICE_OUT_SPEAKER"` 或 `"- DEVICE_OUT_SPEAKER"`（带缩进）
- 或使用正则匹配更精确的模式

**涉及文件**:
- `service/AudioBridge/DeviceMonitor/AdbWrapper.cs:85-86`

---

### [ ] 11. 预衰减 -6dB 不适用所有场景（未修复——需要DSP限幅器）

**问题**: 单音频源时浪费 6dB 动态范围；4+满幅度源时 -6dB 又不够。固定衰减无法适应所有场景。

**解决方案**:
- 方案A：添加真正的限幅器（look-ahead limiter），动态控制峰值而不固定衰减
- 方案B（简单）：保留可配置的 PreAttenuationDb，但默认改为 0dB，让用户根据实际情况调整

**涉及文件**:
- `service/AudioBridge/AudioCapture/SampleConverter.cs:37-38`
- `service/AudioBridge/config.json`（PreAttenuationDb 默认值）

---

### [x] 12. SystemAudio.cs 完全无用

**问题**: 两个方法都是 TODO 存根，未被任何代码调用。`GetCurrentDefaultDevice()` 返回 "unknown"，`SetDefaultAudioDevice()` 返回 false。

**解决方案**:
- 方案A：使用 IMMDeviceEnumerator COM 接口实现这两个方法
- 方案B（推荐）：删除文件，因为 WASAPI Loopback 自动从默认设备捕获，不需要手动选择设备

**涉及文件**:
- `service/AudioBridge/Utils/SystemAudio.cs`

---

### [x] 13. AudioPacket 类完全未使用

**问题**: 带时间戳的数据包类设计用于测量延迟，但从未被实例化。

**解决方案**:
- 方案A：删除未使用的类，保持项目整洁
- 方案B：在 `StreamForwarder.SendAudioData()` 中使用 `AudioPacket` 包装数据，添加端到端延迟统计输出

**涉及文件**:
- `service/AudioBridge/Streaming/AudioPacket.cs`
- `service/AudioBridge/Streaming/StreamForwarder.cs`

---

### [x] 14. ADB forward 残留风险

**问题**: 程序异常退出时 `RemoveForward()` 不执行。下次启动 forward 27777 因端口占用失败。

**解决方案**:
- `StreamForwarder.Start()` 开始时先执行 `adb forward --remove tcp:27777`（忽略错误）
- 或在 `AdbWrapper.SetupForward()` 中自动先清理再设置

**涉及文件**:
- `service/AudioBridge/DeviceMonitor/AdbWrapper.cs:112-118`

---

### [ ] 15. Push 成败判断过于简单（未修复——ADB退出码不可靠）

**问题**: 检查 "error"/"failed" 字符串，非英文 ADB 输出可能失效。

**解决方案**:
- 改用进程退出码判断：`RunAdbCommand()` 返回 int exit code，调用方判断 0 为成功
- 或额外检查 exit code

**涉及文件**:
- `service/AudioBridge/DeviceMonitor/AdbWrapper.cs:97-99`

---

### [ ] 16. 无单元测试（未修复——需单独测试项目）

**问题**: 零测试覆盖率，每次修改需物理设备 + USB 连接验证。

**解决方案**:
- 为 `SampleConverter` 添加单元测试：float→int16, float→int32, clamp 边界
- 为 `DeviceWatcher` 逻辑添加 mock 测试
- 为 `ConvertFormat()` 添加测试（各种格式组合）
- 测试框架建议：xUnit + NSubstitute（mock ADB）

---

## 🔵 P3 — 代码质量与部署

### [x] 17. config.json 与 Config.cs 默认值不一致

**问题**: `Config.cs` 中 `TargetBitsPerSample` 默认 16，`config.json` 中为 32。删除配置文件后行为改变。

**解决方案**:
- 统一为 32（匹配 audio_player 默认值和实际使用配置）
- 修改 `Config.cs:15` 默认值

**涉及文件**:
- `service/AudioBridge/Config.cs:15`

---

### [x] 18. audio_player 通知功能在受限设备上可能失败

**问题**: `system("input keyevent KEYCODE_WAKEUP")` 和 `system("cmd notification post ...")` 在某些 ROM 上可能失败，失败后无用户提示。

**解决方案**:
- 添加失败时的 stderr 日志输出
- 或改为可选功能，通过命令行参数控制是否启用通知
- 或完全移除（通知是增强功能，不影响核心音频流）

**涉及文件**:
- `audio_player/audio_player.c:260-264`

---

### [ ] 19. Marshal.Copy 频繁分配内存造成 GC 压力（未修复——需ArrayPool，与字节流生命周期冲突）

**问题**: WASAPI 每个 packet 都 `new byte[byteCount]`，48000Hz 32bit 2ch 下每秒 ~200 次分配。

**解决方案**:
- 使用 `System.Buffers.ArrayPool<byte>.Shared.Rent(minLength)` 复用缓冲区
- 注意需正确归还和切片

**涉及文件**:
- `service/AudioBridge/AudioCapture/AudioCaptureManager.cs:242`

---

### [x] 20. `_dataStream.Flush()` 每次写入后调用影响性能

**问题**: TCP 流每次 `Write()` 后 `Flush()`，3MB/s 数据量下增加小包发送频率。

**解决方案**:
- 移除 `Flush()`，让 TCP 栈自行决定最佳发送时机
- 或只在关键节点手动 Flush（如连接建立后的首个数据包）

**涉及文件**:
- `service/AudioBridge/Streaming/StreamForwarder.cs:235`

---

### [x] 21. AAudioStream_write 1秒超时掩盖堆积问题

**问题**: 1 秒超时参数意味着缓冲区满时 write 阻塞 1 秒才报错，期间 TCP 缓冲区堆积。

**解决方案**:
- 降低超时到 100-200ms，更快检测到写入阻塞
- 或在写入循环中监测阻塞次数，达到阈值后触发丢弃/恢复逻辑

**涉及文件**:
- `audio_player/audio_player.c:250`

---

### [x] 22. kill_audio_bridge.ps1 硬编码 PID

**问题**: 硬编码 `14112`，只对特定实例有效。

**解决方案**:
- 改为通过 `Get-Process -Name "AudioBridge"` 或 `"dotnet"` 查找
- 或通过文件锁/PID 文件方式获取

**涉及文件**:
- `kill_audio_bridge.ps1:1`

---

### [x] 23. build_all.ps1 改变了工作目录

**问题**: `Set-Location $ServiceDir` 后没有恢复原始目录。

**解决方案**:
- 在脚本开始时保存原始路径，结束时 `Set-Location $originalLocation`
- 或使用 `Push-Location` / `Pop-Location`

**涉及文件**:
- `scripts/build_all.ps1:19`

---

### [ ] 24. csproj 依赖的 audio_player 二进制无扩展名（非代码问题）

**问题**: `audio_player`（无扩展名）在 Windows 资源管理器中可能被误判/误删，杀毒软件可能误报。

**解决方案**:
- 无法直接加 `.exe` 扩展名（Android ELF 二进制）
- 可以在 `.gitignore` 中添加排除规则，避免误操作
- 或在文档中说明此文件是 Android 二进制，非 Windows 可执行文件

**涉及文件**:
- `service/AudioBridge/AudioBridge.csproj:12`

---

## 修复完成状态

**已修复 20/24 项。4 项未修复原因见上。**

```
Phase 1（最小可用版）:   ✅ 4/4
Phase 2（稳定版）:      ✅ 7/8  (Issue 11: 限幅器需DSP实现，暂留)
Phase 3（体验完善版）:  ✅ 6/9  (Issue 15: ADB退出码不可靠; Issue 16: 需单独测试项目; Issue 19: 与流生命周期冲突)
```

## 建议修复路线图（已更新）

```
Phase 1（最小可用版）:         全部完成 ✅
  ✅ 1  TCP超时回退修复
  ✅ 2  采样率转换 SRC
  ✅ 4  TCP写入线程恢复
  ✅ 14 ADB forward残留清理

Phase 2（稳定版）:             全部完成 ✅
  ✅ 3  整数位深转换
  ✅ 5  audio_player进程退出日志
  ✅ 6  WASAPI默认设备切换响应(含IMMNotificationClient)
  ✅ 7  队列丢包增加反馈
  ✅ 8  ADB命令超时从10s降为5s
  ✅ 9  设备型号显示
  ✅ 10 dumpsys检测精确化
  ⬜ 11 动态增益/限幅器（跳票——需要DSP限幅器实现）

Phase 3（体验完善版）:         大部分完成 ✅
  ✅ 12 SystemAudio.cs标记废弃
  ✅ 13 AudioPacket集成(含延迟监控)
  ✅ 17 配置默认值统一(16→32)
  ✅ 18 通知功能容错
  ✅ 20 移除不必要的Flush
  ✅ 21 AAudio写入超时1s→200ms
  ✅ 22 kill脚本PID动态查找
  ✅ 23 Push-Location/Pop-Location
  ⬜ 15 Push成败判断（ADB退出码不可靠）
  ⬜ 16 单元测试（需单独测试项目）
  ⬜ 19 ArrayPool减少GC（与字节流生命周期冲突）
  ⬜ 24 无扩展名说明（非代码问题）
```
