using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace TestRunnerApp
{
    public enum BackupMode { Always, FailedOnly, Never }

    [Serializable]
    public class AppSettings
    {
        public string SiteUrl { get; set; } = "http://BEL-HYP-QA-009:32327";
        public string Login { get; set; } = "admin";
        public string Password { get; set; } = "123";
        public string Language { get; set; } = "English";
        public string CompanyId { get; set; } = "";
        public string RmHost { get; set; } = "";
        public string AcumaticaVersion { get; set; } = "24.2";
        public string DbServer { get; set; } = "Localhost";
        public string DbName { get; set; } = "Bc1200_tr2615000120260";
        public string BackupPath { get; set; } = @"C:\DBBAK\Bc1200_tr2615000120260.bak";
        public string PostRunBackupFolder { get; set; } = @"C:\DBBAK\PostRun";
        public BackupMode BackupMode { get; set; } = BackupMode.FailedOnly;
        public string BrowserBin { get; set; } = @"C:\repos\qaauto\Selenium\packages\chrome\chrome.exe";
        public bool Headless { get; set; } = false;
        public string TestExePath { get; set; } = @"C:\repos\qaauto\Selenium\TestProject\bin\Debug\net48\TestProject.exe";
        public string LogLevel { get; set; } = "DEBUG";
        public string ScreenshotActive { get; set; } = "true";
        public string LogFolder { get; set; } = @"C:\share\result";
        public string ScreenshotFolder { get; set; } = @"C:\share\pics";
        public string LogArchiveFolder { get; set; } = @"C:\share\archive";
        public bool DeleteOldLogs { get; set; } = true;
        public bool PlaySound { get; set; } = true;
        public List<string> LastLoadedDlls { get; set; } = new List<string>();
        public string BreakpointSlnPath { get; set; } = "";
        public string BreakpointLastCsFile { get; set; } = "";
        public bool IsDarkTheme { get; set; } = false;
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 800;



        static string FP { get { var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QATestRunner"); if (!Directory.Exists(d)) Directory.CreateDirectory(d); return Path.Combine(d, "settings.xml"); } }
        public void Save() { try { var s = new XmlSerializer(typeof(AppSettings)); using (var w = new StreamWriter(FP)) s.Serialize(w, this); } catch { } }
        public static AppSettings Load() { try { if (File.Exists(FP)) { var s = new XmlSerializer(typeof(AppSettings)); using (var r = new StreamReader(FP)) return (AppSettings)s.Deserialize(r); } } catch { } return new AppSettings(); }
    }
}
