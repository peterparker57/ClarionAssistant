using System;
using System.Diagnostics;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Minimal helper for resolving the GitHub Copilot CLI executable.
    /// Intended for terminal-based launch (ConPTY) where we want a concrete path
    /// when the user configured a bare "copilot" command.
    /// </summary>
    public static class CopilotProcessManager
    {
        public static string FindCopilotPathStatic() => FindCopilotPath();

        private static string FindCopilotPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. WinGet link (common for Windows installs)
            try
            {
                string wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "copilot.exe");
                if (File.Exists(wingetLink)) return wingetLink;
            }
            catch { }

            // 2. PATH via where.exe
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "copilot",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(3000);
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
            }
            catch { }

            return null;
        }
    }
}
