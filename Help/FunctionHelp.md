# FunctionHelp - AudioBridge 项目函数帮助文档

## audio_player (C/NDK)

### main
```
name : int main(int argc, char *argv[])
input : int argc - 命令行参数个数, char *argv[] - 命令行参数数组
         argv[1] = sample_rate (默认48000)
         argv[2] = channels (默认2)
         argv[3] = buffer_ms (默认50ms)
output : int - 0=正常退出, 1=AAudio初始化失败
effect : 使用形参作为播放器配置参数，内部进行AAudio流初始化和stdin读取循环，返回0表示正常退出，返回1表示初始化失败
```

## AudioBridge Service (C#)

### Program (AudioBridge.Program)
```
name : static void Main(string[] args)
input : string[] args - 命令行参数
output : void
effect : 使用形参作为启动配置，内部进行服务初始化、设备检测和音频转发的主循环编排，输出程序状态到控制台
```

### Config (AudioBridge.Config)
```
name : static Config Load(string path)
input : string path - 配置文件路径
output : Config - 配置对象
effect : 使用形参path作为配置文件路径，内部进行JSON读取和解析，返回Config对象包含所有配置项
```

### DeviceMonitor (AudioBridge.DeviceMonitor)
```
name : void Start()
input : void
output : void
effect : 启动ADB设备轮询定时器，每隔1秒检测设备状态，触发DeviceConnected/DeviceDisconnected事件
```

```
name : void Stop()
input : void
output : void
effect : 停止设备轮询定时器，清理资源
```

### AdbWrapper (AudioBridge.AdbWrapper)
```
name : DeviceInfo[] GetDevices()
input : void
output : DeviceInfo[] - 已连接设备列表
effect : 执行adb devices命令并解析输出，返回所有已连接设备的序列号和状态信息
```

```
name : bool CheckHasSpeaker(string serial)
input : string serial - 设备序列号
output : bool - true=有扬声器
effect : 使用形参serial作为设备标识，内部执行adb -s serial shell dumpsys audio并解析输出，返回bool表示设备是否有扬声器
```

```
name : Process StartAudioPlayer(string serial, string playerPath)
input : string serial - 设备序列号, string playerPath - 播放器在设备上的路径
output : Process - adb exec-out进程对象
effect : 使用形参serial和playerPath作为参数，内部通过adb exec-out启动设备上的播放器进程，返回该进程的Process对象用于写入stdin
```

```
name : bool PushFile(string local, string remote, string serial)
input : string local - 本地文件路径, string remote - 远程路径, string serial - 设备序列号
output : bool - true=推送成功
effect : 使用形参local, remote, serial作为传输参数，内部执行adb push，返回bool表示推送是否成功
```

### AudioCaptureManager (AudioBridge.AudioCaptureManager)
```
name : void Start()
input : void
output : void
effect : 初始化UDP组播客户端，绑定Scream驱动输出端口(4010)，开始接收音频数据并触发AudioDataReceived事件
```

```
name : void Stop()
input : void
output : void
effect : 关闭UDP客户端，停止音频数据接收
```

### StreamForwarder (AudioBridge.StreamForwarder)
```
name : void Start(string deviceSerial)
input : string deviceSerial - 设备序列号
output : void
effect : 使用形参deviceSerial作为目标设备，内部通过AdbWrapper启动android播放器进程，准备音频数据转发通道
```

```
name : void Stop()
input : void
output : void
effect : 终止播放器进程，关闭ADB转发通道
```

```
name : void SendAudioData(byte[] pcmData)
input : byte[] pcmData - PCM音频数据
output : void
effect : 使用形参pcmData作为音频数据，内部写入ADB进程的stdin，将音频数据发送到Android设备扬声器播放
```

### Logger (AudioBridge.Logger)
```
name : static void Info(string message)
input : string message - 信息消息
output : void
effect : 使用形参message作为日志内容，内部以[INFO]级别输出到控制台，包含时间戳前缀
```

```
name : static void Warn(string message)
input : string message - 警告消息
output : void
effect : 使用形参message作为日志内容，内部以[WARN]级别输出到控制台，包含时间戳前缀
```

```
name : static void Error(string message)
input : string message - 错误消息
output : void
effect : 使用形参message作为日志内容，内部以[ERROR]级别输出到控制台，包含时间戳前缀和时间
```

```
name : static void Debug(string message)
input : string message - 调试消息
output : void
effect : 使用形参message作为调试信息，内部以[DEBUG]级别输出到控制台，仅在调试版本中显示
```

### SystemAudio (AudioBridge.SystemAudio)
```
name : static bool SetDefaultAudioDevice(string deviceName)
input : string deviceName - 设备名称
output : bool - true=设置成功
effect : 使用形参deviceName作为目标音频设备名，内部通过Windows IMMDeviceEnumerator API设置默认音频设备，返回bool表示是否成功
```

```
name : static string GetCurrentDefaultDevice()
input : void
output : string - 当前默认设备ID
effect : 通过Windows IMMDeviceEnumerator API查询当前默认音频输出设备，返回设备ID字符串
```
