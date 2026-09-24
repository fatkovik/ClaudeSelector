using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ClaudeSelector
{
    public sealed class BrowserChoice
    {
        public string Id, Name, Executable, RelativePath, PrivateFlag;
        public override string ToString() { return Name; }
    }

    public static class LoginBrowser
    {
        public static readonly BrowserChoice[] Choices = {
            new BrowserChoice { Id = "default", Name = "Windows default browser" },
            new BrowserChoice { Id = "edge", Name = "Microsoft Edge", Executable = "msedge.exe", RelativePath = @"Microsoft\Edge\Application\msedge.exe", PrivateFlag = "--inprivate" },
            new BrowserChoice { Id = "chrome", Name = "Google Chrome", Executable = "chrome.exe", RelativePath = @"Google\Chrome\Application\chrome.exe", PrivateFlag = "--incognito" },
            new BrowserChoice { Id = "firefox", Name = "Mozilla Firefox", Executable = "firefox.exe", RelativePath = @"Mozilla Firefox\firefox.exe", PrivateFlag = "-private-window" },
            new BrowserChoice { Id = "brave", Name = "Brave", Executable = "brave.exe", RelativePath = @"BraveSoftware\Brave-Browser\Application\brave.exe", PrivateFlag = "--incognito" }
        };
        public static BrowserChoice Choice(string id)
        {
            var choice = Choices.FirstOrDefault(b => b.Id == (String.IsNullOrEmpty(id) ? "default" : id));
            if (choice == null) throw new InvalidOperationException("Choose a supported login browser for this account.");
            return choice;
        }
        public static BrowserChoice[] Available(string savedId, Func<BrowserChoice, string> find)
        {
            var available = new List<BrowserChoice> { Choices[0] };
            foreach (var browser in Choices.Skip(1))
            {
                string executable = find(browser);
                if (!String.IsNullOrEmpty(executable) && Path.IsPathRooted(executable) && File.Exists(executable)) available.Add(browser);
            }
            // Preserve a saved choice after an uninstall or a settings transfer. Never silently
            // replace it with Windows' default browser, which may have a different login.
            if (!String.IsNullOrEmpty(savedId) && !available.Any(b => b.Id == savedId))
            {
                var saved = Choices.FirstOrDefault(b => b.Id == savedId);
                available.Add(new BrowserChoice { Id = savedId, Name = (saved == null ? "Saved browser" : saved.Name) + " (unavailable)" });
            }
            return available.ToArray();
        }
        public static string Find(BrowserChoice browser)
        {
            var paths = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + browser.Executable))
                        {
                            var path = key == null ? null : key.GetValue("") as string;
                            if (!String.IsNullOrWhiteSpace(path)) paths.Add(path.Trim().Trim('"'));
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
            foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                paths.Add(Path.Combine(Environment.GetFolderPath(folder), browser.RelativePath));
            return paths.FirstOrDefault(p => Path.IsPathRooted(p) && String.Equals(Path.GetFileName(p), browser.Executable, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        }
        public static string ValidateUrl(string value)
        {
            value = (value ?? "").Trim();
            Uri uri;
            if (value.Length == 0 || value.Length > 16000 || value.Any(Char.IsControl) || value.Contains("\"") || value.Contains("\\")
                || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Paste the initial HTTPS login URL from Claude or the browser address bar. Do not paste a claude:// return link or an authorization code.");
            return value; // Preserve signed query strings exactly, without logging or saving them.
        }
        public static ProcessStartInfo LaunchInfo(Account account, string url, Func<BrowserChoice, string> find)
        {
            url = ValidateUrl(url);
            var choice = Choice(account.LoginBrowser);
            if (choice.Id == "default")
            {
                if (account.LoginPrivateWindow) throw new InvalidOperationException("Select a specific browser to request a private window.");
                return new ProcessStartInfo(url) { UseShellExecute = true };
            }
            string executable = find(choice);
            if (String.IsNullOrEmpty(executable) || !Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new InvalidOperationException(choice.Name + " was not found. Install it or choose another browser. The login link was not opened.");
            return new ProcessStartInfo(executable, (account.LoginPrivateWindow ? choice.PrivateFlag + " " : "") + Core.WindowsQuote(url)) { UseShellExecute = false };
        }
    }
}
