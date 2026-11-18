using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace VirtuStream
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (s, e) => { Report(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => { Report(e.Exception); e.SetObserved(); };
        }

        /// <summary>Affiche l'erreur et l'écrit dans %LocalAppData%\VirtuStream\crash.log (au lieu de fermer l'application en silence).</summary>
        public static void Report(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtuStream");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), DateTime.Now + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
            MessageBox.Show(ex != null ? ex.ToString() : "Erreur inconnue", "VirtuStream - erreur");
        }
    }
}
