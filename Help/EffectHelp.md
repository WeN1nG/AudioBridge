# EffectHelp - AudioBridge 功能实现模拟

## 1. 程序启动流程

```
程序启动 -> Main()
  -> LoadConfig() -> Config对象
  -> InitLogger()
  -> DetectADB() -> AdbWrapper检测adb是否可用
  -> StartDeviceMonitor() -> 每秒轮询adb devices
  -> 等待设备连接...
```

## 2. 设备连接流程

```
[DeviceMonitor轮询]
  -> adb devices (GetDevices())
  -> 发现新设备
  -> DeviceConnected事件触发
  -> CheckHasSpeaker(serial) -> adb shell dumpsys audio
     ├── 有扬声器 -> PushPlayer(serial) -> 推送audio_player到设备
     │              -> chmod 755
     │              -> StartAudioPlayer(serial) -> adb exec-out启动播放器
     │              -> StartAudioCapture() -> 连接UDP组播
     │              -> StartForwarding() -> 开始将音频数据写入ADB stdin
     │              -> Logger.Info("设备已就绪，开始转发音频")
     │
     └── 无扬声器 -> Logger.Warn("设备序列号不包含扬声器，无法使用")
                   -> 等待拔除
```

## 3. 音频转发循环

```
[永久循环]
  -> AudioCaptureManager接收UDP数据
  -> 解析5字节头(rate,width,channels,channelsMap)
  -> 提取PCM数据体
  -> AudioDataReceived事件
  -> StreamForwarder.SendAudioData(pcmData)
  -> 写入adb exec-out进程的stdin
  -> Android端audio_player从stdin读取PCM
  -> AAudioStreamWrite() -> 扬声器
```

## 4. 设备断开流程

```
[DeviceMonitor轮询]
  -> 发现设备消失
  -> DeviceDisconnected事件触发
  -> StopForwarding() -> 终止adb exec-out进程
  -> Logger.Info("设备已断开，音频转发已停止")
  -> 等待新设备连接...
```

## 5. 程序退出流程

```
[用户按Ctrl+C]
  -> Console.CancelKeyPress事件
  -> StopForwarder()
  -> StopAudioCapture()
  -> StopDeviceMonitor()
  -> AdbWrapper.KillAudioPlayer()
  -> Logger.Info("程序已退出")
  -> 打印时间戳和修改次数
```

## 6. 音频数据格式

```
Scream UDP包格式:
  [0]    = rate编码 (>=128: 44100 * (rate-128), <128: 48000 * rate)
  [1]    = bitsPerSample (通常16)
  [2]    = channels (1或2)
  [3]    = channelMap LSB
  [4]    = channelMap MSB
  [5..n] = PCM交错样本数据 (16bit little-endian)
```

## 7. 程序结构图

```
Program.Main()
  ├── Config.Load()
  ├── AdbWrapper (工具类)
  ├── DeviceMonitor (设备检测)
  │   ├── Start() -> 启动Timer轮询
  │   ├── DeviceConnected event -> 连接处理
  │   └── DeviceDisconnected event -> 断开处理
  ├── AudioCaptureManager (音频接收)
  │   ├── Start() -> UDP组播监听
  │   └── AudioDataReceived event -> PCM数据
  └── StreamForwarder (音频转发)
      ├── Start() -> adb exec-out启动
      └── SendAudioData() -> 写入stdin
```
