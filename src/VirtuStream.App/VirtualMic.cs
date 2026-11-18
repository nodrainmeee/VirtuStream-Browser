using System;
using NAudio.Wave;

namespace VirtuStream
{
    /// <summary>
    /// Microphone virtuel : envoie le PCM vers l'entrée "CABLE Input" du pilote VB-Audio Virtual Cable.
    /// Les applications choisissent ensuite "CABLE Output" comme microphone. Le micro physique n'est jamais touché.
    /// </summary>
    public sealed class VirtualMic : IDisposable
    {
        WaveOutEvent output; BufferedWaveProvider buffer;
        public volatile float Peak;
        public float Volume { get => output?.Volume ?? 1f; set { if (output != null) output.Volume = value; vol = value; } }
        float vol = 1f;

        public static int FindCableDevice()
        {
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                if (WaveOut.GetCapabilities(i).ProductName.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }
        public static bool IsAvailable() => FindCableDevice() >= 0;

        public VirtualMic()
        {
            int dev = FindCableDevice();
            if (dev < 0) throw new InvalidOperationException("VB-Audio Virtual Cable introuvable. Installez-le (droits admin) puis relancez.");
            buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true };
            output = new WaveOutEvent { DeviceNumber = dev, DesiredLatency = 80, Volume = vol };
            output.Init(buffer); output.Play();
        }
        public void Write(byte[] data, int count)
        {
            buffer.AddSamples(data, 0, count);
            int max = 0;
            for (int i = 0; i + 1 < count; i += 8) { int s = Math.Abs((short)(data[i] | (data[i + 1] << 8))); if (s > max) max = s; }
            Peak = Math.Min(1f, max / 32768f);
        }
        public void Dispose() { output?.Stop(); output?.Dispose(); output = null; }
    }
}
