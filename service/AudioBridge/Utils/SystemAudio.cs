// DEPRECATED — This file is retained for reference only.
//
// AudioCaptureManager now handles WASAPI capture from the default audio device
// directly. No manual device selection is needed because WASAPI Loopback
// automatically captures from the default render endpoint.
//
// If device selection is needed in the future, use IMMDeviceEnumerator COM
// interface (declared in WasapiInterop.cs) to enumerate and select devices.
