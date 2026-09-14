using NAudio.CoreAudioApi;

namespace Whisper.Client.Audio;

public sealed class NAudioDeviceProvider : IAudioDeviceProvider
{
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => Enumerate(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => Enumerate(DataFlow.Render);

    internal static MMDevice? Resolve(MMDeviceEnumerator enumerator, string? deviceId, DataFlow flow)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                if (string.Equals(device.ID, deviceId, StringComparison.Ordinal))
                {
                    return device;
                }

                device.Dispose();
            }
        }

        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Communications))
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }

        return enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : null;
    }

    private static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Communications))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            defaultId = defaultDevice.ID;
        }

        var devices = new List<AudioDeviceInfo>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
            }
        }

        return devices;
    }
}
