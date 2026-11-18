using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace VirtuStream
{
    /// <summary>
    /// Pipeline : ffmpeg (décodage, accéléré DXVA2/D3D11VA si possible, mise à l'échelle Lanczos sans agrandissement)
    /// -> BGR24 sur un pipe -> SoftCam (caméra DirectShow). Audio PCM -> pipe nommé -> VirtualMic.
    /// Une seule conversion d'échelle, aucune copie intermédiaire inutile.
    /// </summary>
    public sealed class MediaEngine : IDisposable
    {
        public string File { get; private set; }
        public int Width = 1280, Height = 720; public int Fps = 30;
        public bool Loop = true, HwDecode = true, FlipVertical = false;
        public int SrcW, SrcH; public double Duration;
        public bool Playing { get; private set; }
        public bool CameraOn => cam != null;
        public bool MicOn => mic != null;
        public float MicPeak => mic?.Peak ?? 0f;
        public event Action<byte[], int, int> Preview;
        public event Action<string> Error;
        public event Action Ended;

        // Statistiques mesurées
        public double MeasuredFps; public long Sent, Dropped; public string DecodeInfo = "-";

        Process ff; Thread reader; volatile int gen;
        SoftCam cam; VirtualMic mic; Timer keep;
        byte[] last; readonly object sendLock = new object();
        long frames; double startPos; long lastTicks; long winTicks = Stopwatch.GetTimestamp(); int winCount;
        public double Position => Playing ? Math.Min(Duration, startPos + frames / (double)Fps) : startPos;

        static string Tool(string n) => Path.Combine(AppContext.BaseDirectory, "tools", n);
        static readonly string[] Ext = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v" };

        public static bool IsValidVideoPath(string p)
        {
            try { var fi = new FileInfo(p); return fi.Exists && Array.IndexOf(Ext, fi.Extension.ToLowerInvariant()) >= 0 && fi.Length > 1024; }
            catch { return false; }
        }

        public bool Load(string path)
        {
            if (!IsValidVideoPath(path)) { Error?.Invoke("Fichier vidéo invalide ou format non pris en charge."); return false; }
            Stop();
            var psi = new ProcessStartInfo(Tool("ffprobe.exe"),
                "-v error -select_streams v:0 -show_entries stream=width,height:format=duration -of csv=p=0 \"" + path + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(8000);
                    var lines = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var wh = lines[0].Split(',');
                    SrcW = int.Parse(wh[0]); SrcH = int.Parse(wh[1]);
                    Duration = double.Parse(lines[lines.Length - 1].Trim(','), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) { Error?.Invoke("Lecture des métadonnées impossible : " + ex.Message); return false; }
            File = path; startPos = 0; frames = 0; last = null;
            return true;
        }

        public void Play() { if (File == null || Playing) return; StartFfmpeg(startPos); }
        public void Pause() { if (!Playing) return; startPos = Position; KillFfmpeg(); Playing = false; }
        public void Stop() { KillFfmpeg(); Playing = false; startPos = 0; frames = 0; }
        public void Seek(double sec)
        {
            bool was = Playing; KillFfmpeg(); Playing = false;
            startPos = Math.Max(0, Math.Min(sec, Math.Max(0, Duration - 0.5)));
            if (was) StartFfmpeg(startPos);
        }
        /// <summary>Applique un nouveau réglage (résolution, fps, matériel) en reprenant à la position courante.</summary>
        public void Reconfigure(int w, int h, int fps, bool hw)
        {
            bool was = Playing; double pos = Position; KillFfmpeg(); Playing = false; startPos = pos;
            bool camWas = CameraOn, micWas = MicOn;
            Width = w; Height = h; Fps = fps; HwDecode = hw;
            if (camWas) { DisableCamera(); try { EnableCamera(); } catch (Exception e) { Error?.Invoke(e.Message); } }
            if (was) StartFfmpeg(startPos);
        }

        public void EnableCamera()
        {
            if (cam != null) return;
            cam = new SoftCam(Width, Height, Fps);
            keep = new Timer(_ => KeepAlive(), null, 0, Math.Max(10, 1000 / Fps));
        }
        public void DisableCamera() { keep?.Dispose(); keep = null; lock (sendLock) { cam?.Dispose(); cam = null; } }
        public bool CameraHasConsumer => cam != null && cam.HasConsumer;
        public void EnableMic() { if (mic == null) mic = new VirtualMic(); }
        public void DisableMic() { mic?.Dispose(); mic = null; }
        public void SetMicVolume(float v) { if (mic != null) mic.Volume = v; }

        void KeepAlive()
        {   // Garde le flux actif (pause, changement de fichier) en renvoyant la dernière image.
            if (Playing || last == null) return;
            Push(last);
        }

        void StartFfmpeg(double pos)
        {
            int g = ++gen; frames = 0; startPos = pos;
            string vf = string.Format(CultureInfo.InvariantCulture,
                "scale=w='min(iw,{0})':h='min(ih,{1})':force_original_aspect_ratio=decrease:flags=lanczos," +
                "pad={0}:{1}:(ow-iw)/2:(oh-ih)/2:color=black,fps={2}{3},format=bgr24",
                Width, Height, Fps, FlipVertical ? ",vflip" : "");
            string args = "-hide_banner -loglevel error -nostdin " + (HwDecode ? "-hwaccel auto " : "") +
                (Loop ? "-stream_loop -1 " : "") + "-ss " + pos.ToString("0.###", CultureInfo.InvariantCulture) +
                " -re -i \"" + File + "\" -map 0:v:0 -an -vf \"" + vf + "\" -f rawvideo pipe:1";
            string pipeName = null;
            if (mic != null)
            {
                pipeName = "vs_audio_" + g;
                var srv = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var t = new Thread(() => AudioLoop(srv, g)) { IsBackground = true, Name = "vs-audio" }; t.Start();
                args += " -map 0:a:0? -vn -f s16le -ar 48000 -ac 2 \\\\.\\pipe\\" + pipeName;
            }
            var psi = new ProcessStartInfo(Tool("ffmpeg.exe"), args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            try { ff = Process.Start(psi); ff.PriorityClass = ProcessPriorityClass.AboveNormal; }
            catch (Exception ex) { Error?.Invoke("ffmpeg introuvable : " + ex.Message); return; }
            DecodeInfo = HwDecode ? "demandé (auto) – repli logiciel possible" : "logiciel";
            var p = ff; Playing = true;
            reader = new Thread(() => ReadLoop(p, g)) { IsBackground = true, Name = "vs-video" }; reader.Start();
            new Thread(() => { string e = p.StandardError.ReadToEnd(); if (g == gen && e.Length > 0) Error?.Invoke(e.Trim()); }) { IsBackground = true }.Start();
        }

        void ReadLoop(Process p, int g)
        {
            int size = Width * Height * 3; var s = p.StandardOutput.BaseStream; int n = 0;
            while (g == gen)
            {
                var buf = new byte[size]; n = 0;
                while (n < size) { int r; try { r = s.Read(buf, n, size - n); } catch { r = 0; } if (r <= 0) break; n += r; }
                if (n < size) break;
                frames++; last = buf; Push(buf);
                if (frames % 6 == 0) Preview?.Invoke(buf, Width, Height);
            }
            if (g == gen) { Playing = false; startPos = Loop ? 0 : Duration; Ended?.Invoke(); }
        }

        void AudioLoop(NamedPipeServerStream srv, int g)
        {
            try
            {
                srv.WaitForConnection(); var b = new byte[9600];
                while (g == gen) { int r = srv.Read(b, 0, b.Length); if (r <= 0) break; mic?.Write(b, r); }
            }
            catch { } finally { srv.Dispose(); }
        }

        void Push(byte[] f)
        {
            long now = Stopwatch.GetTimestamp();
            lock (sendLock)
            {
                cam?.Send(f); Sent++;
                if (Playing && lastTicks != 0)
                {   // Images manquantes = écart > 1,5 période (mesure réelle côté émetteur)
                    double gap = (now - lastTicks) / (double)Stopwatch.Frequency;
                    int missed = (int)Math.Round(gap * Fps) - 1; if (missed > 0 && gap > 1.5 / Fps) Dropped += missed;
                }
                lastTicks = now; winCount++;
                double el = (now - winTicks) / (double)Stopwatch.Frequency;
                if (el >= 1) { MeasuredFps = winCount / el; winCount = 0; winTicks = now; }
            }
        }

        void KillFfmpeg()
        {
            gen++; var p = ff; ff = null;
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
        public void Dispose() { KillFfmpeg(); DisableCamera(); DisableMic(); }
    }
}
