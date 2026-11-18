using System;
using System.Runtime.InteropServices;

namespace VirtuStream
{
    /// <summary>
    /// Émetteur vers le filtre DirectShow "Softcam" (https://github.com/tshino/softcam, licence MIT).
    /// softcam.dll doit être enregistré (regsvr32, droits admin) : fait par l'installateur.
    /// Vérifiez les signatures dans softcam.h de la version que vous compilez.
    /// </summary>
    public sealed class SoftCam : IDisposable
    {
        const string Dll = "softcam.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr scCreateCamera(int width, int height, float framerate);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void scDeleteCamera(IntPtr camera);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void scSendFrame(IntPtr camera, byte[] imageBits);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool scWaitForConnection(IntPtr camera, float timeout);

        IntPtr cam;
        public static bool IsAvailable()
        {
            try { var c = scCreateCamera(16, 16, 30f); if (c == IntPtr.Zero) return false; scDeleteCamera(c); return true; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }
        public SoftCam(int w, int h, float fps)
        {
            cam = scCreateCamera(w, h, fps);
            if (cam == IntPtr.Zero) throw new InvalidOperationException("Création de la caméra virtuelle impossible (softcam.dll enregistré ? caméra déjà utilisée ?)");
        }
        public bool HasConsumer => cam != IntPtr.Zero && scWaitForConnection(cam, 0f);
        public void Send(byte[] bgr24) { if (cam != IntPtr.Zero) scSendFrame(cam, bgr24); }
        public void Dispose() { if (cam != IntPtr.Zero) { scDeleteCamera(cam); cam = IntPtr.Zero; } }
    }
}
