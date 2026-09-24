using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ClaudeSelector;

public static class Tests
{
    static int checks;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
    [STAThread] public static void Main()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "test-output", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string folder = Path.Combine(root, "project ' & $; unicode ա \u2018\u2019\u201a\u201b"); Directory.CreateDirectory(folder);
        var account = new Account { Id = "test", Name = "Work '\u2019 ; throw 'injection'; # $(throw 'injection')", ConfigDirectory = Path.Combine(root, "account '\u2019 & $;"), Folder = folder };
        var state = new Settings { SelectedId = "test", Accounts = new System.Collections.Generic.List<Account> { account } };
        string settingsFile = Path.Combine(root, "settings.json"); Core.Save(state, settingsFile);
        Check(Core.Load(settingsFile).Accounts[0].Name == account.Name, "Account preferences round trip");
        Core.Save(state, settingsFile); Check(File.Exists(settingsFile + ".bak"), "Atomic save keeps backup");
        string fake = Path.Combine(root, "fake claude.ps1");
        string result = Path.Combine(root, "launch.json");
        File.WriteAllText(fake, "@{config=$env:CLAUDE_CONFIG_DIR; folder=(Get-Location).Path; key=$env:ANTHROPIC_API_KEY; token=$env:CLAUDE_CODE_OAUTH_TOKEN; title=$Host.UI.RawUI.WindowTitle} | ConvertTo-Json | Set-Content -LiteralPath " + Core.Quote(result) + " -Encoding UTF8; $global:LASTEXITCODE = 0", Encoding.UTF8);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-inherited-key"); Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "fake-inherited-token");
        string script = Core.Script(account, fake, false);
        var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Launch test timed out"); }
        Check(process.ExitCode == 0 && File.Exists(result), "Generated launch command executes");
        var output = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(result));
        Check(output["config"] == account.ConfigDirectory, "Selected account environment reaches child process");
        Check(output["folder"] == folder, "Special characters and Unicode in working folder are preserved");
        Check(output["title"] == "Claude Code - " + account.Name, "Typographic quotes and malicious-looking account names remain literal data");
        Check(String.IsNullOrEmpty(output["key"]) && String.IsNullOrEmpty(output["token"]), "Inherited credentials cannot override added account");
        Check(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") == "fake-inherited-key", "Parent environment remains unchanged");
        Check(Core.LaunchInfo(account, fake, false, "wt.exe").Arguments.StartsWith("-w new new-tab"), "Windows Terminal opens a new window");
        Check(!Core.Script(new Account { Id = "existing", Name = "Existing", Folder = folder, ConfigDirectory = root }, fake, false).Contains("Remove-Item"), "Existing CLI profile retains provider environment");
        string priorConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            var current = new Account { Id = "existing", Name = "Personal", Folder = folder, ConfigDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") };
            string currentScript = Core.Script(current, fake, false);
            var currentInfo = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(currentScript))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            currentInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "incorrect-terminal-inherited-directory";
            File.Delete(result);
            using (var currentProcess = Process.Start(currentInfo))
            {
                if (!currentProcess.WaitForExit(15000)) { currentProcess.Kill(); throw new Exception("Existing profile launch timed out"); }
                Check(currentProcess.ExitCode == 0 && File.Exists(result), "Existing profile command executes successfully");
            }
            var currentOutput = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(result));
            Check(String.IsNullOrEmpty(currentOutput["config"]), "Existing default profile leaves CLAUDE_CONFIG_DIR unset, preserving original onboarding state");
            Check(currentOutput["key"] == "fake-inherited-key", "Existing default profile preserves its authentication environment");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", root);
            Check(!Core.Script(current, fake, false).Contains("Remove-Item Env:CLAUDE_CONFIG_DIR"), "Explicit custom configuration retains override behavior");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", priorConfig); }
        string failed = Path.Combine(root, "failed login.ps1"); File.WriteAllText(failed, "if ($args[0] -eq 'auth') { $global:LASTEXITCODE = 1 } else { throw 'SHOULD NOT LAUNCH' }");
        string failureScript = Core.Script(account, failed, true);
        var failure = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(failureScript))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        string failureText = failure.StandardOutput.ReadToEnd(); failure.WaitForExit();
        Check(failureText.Contains("Sign-in did not complete") && !failureText.Contains("SHOULD NOT LAUNCH"), "Failed login does not start Claude session");
        File.WriteAllText(Path.Combine(root, "invalid.json"), "{}");
        bool rejected = false; try { Core.Load(Path.Combine(root, "invalid.json")); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid preferences are rejected without overwrite");
        string originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);
            File.WriteAllText(Path.Combine(root, "claude.exe"), "mock - never execute");
            Check(Core.FindClaude(root, ".; ;relative;D:relative;\\root-relative") == null, "Relative PATH entries cannot launch a project-local Claude impostor");
            Check(Core.FindClaude(root, root) == Path.Combine(root, "claude.exe"), "Explicit absolute PATH installation is still found");
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); }
        Check(!script.Contains("-ExecutionPolicy") && !script.Contains("dangerously-skip-permissions"), "Production command does not bypass execution policy or Claude permissions");
        Application.EnableVisualStyles();
        using (var form = new MainForm(state, null))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(root, "preview.png")); }
        }
        Console.WriteLine("Passed " + checks + " checks.");
    }
}
