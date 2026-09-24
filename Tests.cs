using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ClaudeSelector;

public static class Tests
{
    static int checks;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
    static void Reject(Action action, string description) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, description); }
    static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    static void Draw(Form form, string file) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(file); } }
    static void Pump() { for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
    [STAThread] public static void Main(string[] args)
    {
        bool sandboxSafe = args.Contains("--sandbox-safe");
        string root = Path.Combine(Directory.GetCurrentDirectory(), "test-output", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string folder = Path.Combine(root, "project ' & $; unicode ա \u2018\u2019\u201a\u201b"); Directory.CreateDirectory(folder);
        var account = new Account { Id = "test", Name = "Work '\u2019 ; throw 'injection'; # $(throw 'injection')", ConfigDirectory = Path.Combine(root, "account '\u2019 & $;"), Folder = folder };
        var state = new Settings { SelectedId = "test", Accounts = new System.Collections.Generic.List<Account> { account } };
        string settingsFile = Path.Combine(root, "settings.json"); Core.Save(state, settingsFile);
        Check(Core.Load(settingsFile).Accounts[0].Name == account.Name, "Account preferences round trip");
        if (!sandboxSafe) { Core.Save(state, settingsFile); Check(File.Exists(settingsFile + ".bak"), "Atomic save keeps backup"); }
        else Console.WriteLine("SKIP Atomic replacement test (sandbox file-replacement permission unavailable)");
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

        var migrated = Core.Load(settingsFile);
        Check(migrated.Accounts[0].LoginBrowser == "default" && !migrated.Accounts[0].LoginPrivateWindow, "Existing accounts keep Windows browser behavior until explicitly changed");
        var browserAccount = new Account { LoginBrowser = "chrome", LoginPrivateWindow = true };
        string loginUrl = "https://claude.ai/oauth/authorize?state=fake%20state&redirect_uri=http%3A%2F%2Flocalhost%3A1234&extra=%22%26%24";
        var installedBrowsers = LoginBrowser.Available(null, delegate(BrowserChoice b) { return b.Id == "brave" ? fake : null; });
        Check(installedBrowsers.Select(b => b.Id).SequenceEqual(new[] { "default", "brave" }), "Browser menu includes only detected installations plus Windows default");
        Check(LoginBrowser.Available(null, delegate { return Path.Combine(root, "removed-browser.exe"); }).Single().Id == "default", "Missing executable files are not offered as installed browsers");
        var unavailableBrowser = LoginBrowser.Available("firefox", delegate { return null; });
        Check(unavailableBrowser.Length == 2 && unavailableBrowser[1].Id == "firefox" && unavailableBrowser[1].Name.Contains("unavailable"), "A saved uninstalled browser stays clearly marked instead of silently selecting Windows default");
        foreach (var choice in LoginBrowser.Choices.Where(b => b.Id != "default"))
        {
            browserAccount.LoginBrowser = choice.Id;
            var browserInfo = LoginBrowser.LaunchInfo(browserAccount, loginUrl, delegate { return fake; });
            var browserArgs = SessionGuard.Arguments(Core.WindowsQuote(browserInfo.FileName) + " " + browserInfo.Arguments);
            Check(!browserInfo.UseShellExecute && browserArgs.Length == 3 && browserArgs[1] == choice.PrivateFlag && browserArgs[2] == loginUrl, choice.Name + " receives its private flag and the exact login URL as one argument");
        }
        browserAccount.LoginPrivateWindow = false;
        var regularBrowser = LoginBrowser.LaunchInfo(browserAccount, loginUrl, delegate { return fake; });
        Check(SessionGuard.Arguments("app.exe " + regularBrowser.Arguments).Length == 2, "Normal browser mode sends no private flag");
        Reject(delegate { LoginBrowser.LaunchInfo(browserAccount, loginUrl, delegate { return null; }); }, "An unavailable selected browser cannot silently fall back to Personal's default browser");
        browserAccount.LoginBrowser = "default";
        var defaultBrowser = LoginBrowser.LaunchInfo(browserAccount, loginUrl, delegate { throw new Exception("Must not resolve executable"); });
        Check(defaultBrowser.UseShellExecute && defaultBrowser.FileName == loginUrl && defaultBrowser.Arguments == "", "Default browser uses Windows HTTPS association unchanged");
        browserAccount.LoginPrivateWindow = true;
        Reject(delegate { LoginBrowser.LaunchInfo(browserAccount, loginUrl, delegate { return fake; }); }, "Private mode cannot silently open a regular default-browser window");
        foreach (var badUrl in new[] { "", "claude://claude.ai/sso-callback?code=fake", "javascript:alert(1)", "file:///C:/Windows/test", "http://claude.ai/login", "https://user@claude.ai/login", "https://claude.ai/login\r\n--incognito", "https://claude.ai/\" --load-extension=bad", "https://claude.ai/\\bad", "--incognito", new string('x', 16001) })
            Reject(delegate { LoginBrowser.ValidateUrl(badUrl); }, "Invalid login input is rejected before browser launch");
        Check(LoginBrowser.ValidateUrl("https://sso.example.org/login?request=fake") == "https://sso.example.org/login?request=fake", "Enterprise SSO domains are accepted without rewriting their URL");
        account.LoginBrowser = "edge"; account.LoginPrivateWindow = true;
        string browserSettingsFile = Path.Combine(root, "browser-settings.json"); Core.Save(state, browserSettingsFile);
        var savedBrowser = Core.Load(browserSettingsFile).Accounts[0];
        Check(savedBrowser.LoginBrowser == "edge" && savedBrowser.LoginPrivateWindow, "Per-account browser and private-window preferences survive reload");
        Check(migrated.Mode == "cli" && migrated.Accounts[0].Folder == folder && migrated.Accounts[0].ConfigDirectory == account.ConfigDirectory, "Old settings migrate without changing CLI login or folder");
        Check(Path.IsPathRooted(migrated.Accounts[0].DesktopDirectory), "Old account receives its own absolute Desktop profile path");
        Core.Normalize(state);
        var other = new Account { Id = "other", Name = "Personal", ConfigDirectory = Path.Combine(root, "personal-cli"), DesktopDirectory = Path.Combine(root, "desktop ' & $; unicode ա"), Folder = "Z:\\missing-folder" };
        state.Accounts.Add(other); state.Mode = "desktop";
        if (sandboxSafe) settingsFile = Path.Combine(root, "updated-settings.json");
        Core.Save(state, settingsFile);
        var restored = Core.Load(settingsFile);
        Check(restored.Mode == "desktop" && restored.SelectedId == account.Id && restored.Accounts.Count == 2, "Mode and selection persist in one shared account list");
        string originalProfile = account.DesktopDirectory; account.Name = "Work";
        Core.Normalize(state);
        Check(account.DesktopDirectory == originalProfile, "Renaming leaves the Desktop profile unchanged");
        var desktopInfo = Core.DesktopLaunchInfo(other, Path.Combine(root, "installed app", "Claude.exe"));
        var desktopArgs = SessionGuard.Arguments("claude.exe " + desktopInfo.Arguments);
        Check(desktopArgs.Length == 2 && desktopArgs[1] == "--user-data-dir=" + other.DesktopDirectory, "Desktop profile arguments preserve spaces, quotes and Unicode");
        Check(desktopInfo.WorkingDirectory != other.Folder && !desktopInfo.UseShellExecute, "Desktop launch does not depend on a project folder or terminal");
        Check(desktopInfo.EnvironmentVariables["CLAUDE_USER_DATA_DIR"] == other.DesktopDirectory && desktopInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] == other.ConfigDirectory, "Desktop uses the selected profile and account's Code configuration");
        Check(desktopInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] == null && desktopInfo.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] == null, "Desktop does not inherit credentials from a different account");
        foreach (var argument in new[] { "", "plain", "C:\\folder with spaces\\", "a\"b", "C:\\some\\\"quoted\"\\", "D:\\Հայերեն path" })
            Check(SessionGuard.Arguments("app.exe " + Core.WindowsQuote(argument))[1] == argument, "Windows argument quoting round trips: " + argument);
        var now = DateTime.Now;
        var cliEntry = new ProcessEntry { Id = 100, Name = "powershell.exe", CommandLine = "powershell.exe " + Core.LaunchInfo(account, fake, false, null).Arguments, Created = now };
        var desktopEntry = new ProcessEntry { Id = 200, Name = "claude.exe", CommandLine = "claude.exe " + Core.DesktopLaunchInfo(account, Path.Combine(root, "Claude.exe")).Arguments, Created = now };
        var empty = new List<ProcessEntry>();
        Check(SessionGuard.Analyze(state, empty).CanOpen(other.Id), "No processes allows any account");
        var activeCli = SessionGuard.Analyze(state, new[] { cliEntry });
        Check(activeCli.CanOpen(account.Id) && !activeCli.CanOpen(other.Id), "A marked CLI shell locks launches to its account");
        Check(!SessionGuard.Analyze(Core.Load(settingsFile), new[] { cliEntry }).CanOpen(other.Id), "A fresh launcher recovers the CLI account lock from its process");
        var activeDesktop = SessionGuard.Analyze(state, new[] { desktopEntry });
        Check(activeDesktop.CanOpen(account.Id) && !activeDesktop.CanOpen(other.Id), "Desktop blocks another account even without a visible window");
        Check(SessionGuard.Analyze(state, new[] { cliEntry, desktopEntry }).CanOpen(account.Id), "Same account can use CLI and Desktop together");
        var otherDesktop = new ProcessEntry { Id = 300, Name = "claude.exe", CommandLine = "claude.exe " + desktopInfo.Arguments, Created = now };
        var conflicting = SessionGuard.Analyze(state, new[] { cliEntry, otherDesktop });
        Check(!conflicting.CanOpen(account.Id) && !conflicting.CanOpen(other.Id), "Conflicting running accounts block all launches");
        var child = new ProcessEntry { Id = 101, ParentId = 100, Name = "claude.exe", CommandLine = "claude.exe", Created = now.AddSeconds(1) };
        Check(SessionGuard.Analyze(state, new[] { cliEntry, child }).CanOpen(account.Id), "CLI child processes inherit their marked shell's ownership");
        var intermediary = new ProcessEntry { Id = 102, ParentId = 100, Name = "cmd.exe", Created = now.AddSeconds(1) };
        child.ParentId = 102; child.Created = now.AddSeconds(2);
        Check(SessionGuard.Analyze(state, new[] { cliEntry, intermediary, child }).CanOpen(account.Id), "CLI ownership follows intermediate wrapper processes");
        Check(!SessionGuard.Analyze(state, new[] { child }).CanOpen(other.Id), "An orphaned Claude process blocks switching instead of guessing ownership");
        Check(!SessionGuard.Analyze(state, new[] { new ProcessEntry { Id = 400, Name = "claude.exe", CommandLine = null } }).CanOpen(account.Id), "Unreadable Claude process metadata blocks launching");
        child.ParentId = 100; child.Created = now.AddSeconds(-1);
        Check(!SessionGuard.Analyze(state, new[] { cliEntry, child }).CanOpen(account.Id), "Reused parent PID cannot assign ownership to an older process");
        Check(SessionGuard.Analyze(state, empty).CanOpen(other.Id), "Closing all sessions releases the account restriction");
        var duplicate = new Settings { Accounts = new List<Account> { new Account { Id = "one", DesktopDirectory = other.DesktopDirectory }, new Account { Id = "two", DesktopDirectory = other.DesktopDirectory.ToUpperInvariant() } } };
        rejected = false; try { Core.Normalize(duplicate); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Two accounts cannot share a Desktop profile directory");

        var personal = new Account { Id = "existing", Name = "Personal", ConfigDirectory = Path.Combine(root, "existing-cli"), DesktopDirectory = Path.Combine(root, "old-isolated-personal"), Folder = folder };
        var defaultState = Core.Normalize(new Settings { Accounts = new List<Account> { personal, other }, SelectedId = personal.Id, Mode = "desktop" });
        string oldDesktopOverride = Environment.GetEnvironmentVariable("CLAUDE_USER_DATA_DIR");
        string oldCodeOverride = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_USER_DATA_DIR", other.DesktopDirectory);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", other.ConfigDirectory);
            var personalInfo = Core.DesktopLaunchInfo(personal, Path.Combine(root, "installed app", "Claude.exe"));
            Check(personalInfo.Arguments == "", "Personal launches Desktop with no profile override, reusing its default PC login");
            Check(personalInfo.EnvironmentVariables["CLAUDE_USER_DATA_DIR"] == null && personalInfo.EnvironmentVariables["CLAUDE_CONFIG_DIR"] == null, "Inherited overrides cannot redirect the default Desktop profile");
            Check(Environment.GetEnvironmentVariable("CLAUDE_USER_DATA_DIR") == other.DesktopDirectory, "Default Desktop launch leaves the parent environment unchanged");
            Check(!String.IsNullOrEmpty(Core.DesktopLaunchInfo(other, personalInfo.FileName).Arguments), "Added accounts still launch their own isolated Desktop profiles");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_USER_DATA_DIR", oldDesktopOverride); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldCodeOverride); }
        string defaultSettingsFile = Path.Combine(root, "default-desktop-settings.json");
        Core.Save(defaultState, defaultSettingsFile);
        var loadedDefault = Core.Load(defaultSettingsFile).Accounts[0];
        Check(Core.UsesDefaultDesktop(loadedDefault) && loadedDefault.DesktopDirectory == personal.DesktopDirectory, "Previously saved Personal settings use default Desktop without deleting the old profile path");
        personal.Name = "Renamed personal";
        Check(Core.UsesDefaultDesktop(personal) && !Core.UsesDefaultDesktop(other), "Default Desktop follows the original account ID, not a display name");
        personal.Name = "Personal";
        string desktopInstall = Path.Combine(root, "desktop-install");
        Directory.CreateDirectory(Path.Combine(desktopInstall, "resources"));
        File.WriteAllText(Path.Combine(desktopInstall, "resources", "app.asar"), "test fixture, not an executable");
        string desktopExecutable = Path.Combine(desktopInstall, "claude.exe");
        var nativeDesktop = new ProcessEntry { Id = 600, Name = "claude.exe", ExecutablePath = desktopExecutable, CommandLine = Core.WindowsQuote(desktopExecutable), Created = now };
        var nativeActivity = SessionGuard.Analyze(defaultState, new[] { nativeDesktop });
        Check(nativeActivity.AccountIds.Contains(personal.Id) && nativeActivity.CanOpen(personal.Id) && !nativeActivity.CanOpen(other.Id), "Default Desktop opened normally counts as Personal and blocks other accounts");
        var nativeRenderer = new ProcessEntry { Id = 601, ParentId = nativeDesktop.Id, Name = "claude.exe", ExecutablePath = desktopExecutable, CommandLine = Core.WindowsQuote(desktopExecutable) + " --type=renderer --user-data-dir=\"C:\\native-profile\"", Created = now.AddSeconds(1) };
        Check(SessionGuard.Analyze(defaultState, new[] { nativeDesktop, nativeRenderer }).CanOpen(personal.Id), "Default Desktop helper processes inherit Personal ownership");
        Check(!SessionGuard.Analyze(defaultState, new[] { nativeRenderer }).CanOpen(other.Id), "An orphaned Desktop helper is not guessed to be the default account");
        var unmanagedCli = new ProcessEntry { Id = 602, Name = "claude.exe", ExecutablePath = Path.Combine(root, "cli-install", "claude.exe"), CommandLine = "claude.exe", Created = now };
        Check(!SessionGuard.Analyze(defaultState, new[] { unmanagedCli }).CanOpen(personal.Id), "An unmarked CLI executable is not mistaken for default Desktop");
        var unreadableNative = new ProcessEntry { Id = 603, Name = "claude.exe", ExecutablePath = desktopExecutable, CommandLine = null, Created = now };
        Check(!SessionGuard.Analyze(defaultState, new[] { unreadableNative }).CanOpen(personal.Id), "Unreadable Desktop arguments do not bypass account verification");
        var isolatedNative = new ProcessEntry { Id = 604, Name = "claude.exe", ExecutablePath = desktopExecutable, CommandLine = Core.WindowsQuote(desktopExecutable) + " --user-data-dir=" + Core.WindowsQuote(other.DesktopDirectory), Created = now };
        Check(SessionGuard.Analyze(defaultState, new[] { isolatedNative }).CanOpen(other.Id) && !SessionGuard.Analyze(defaultState, new[] { isolatedNative }).CanOpen(personal.Id), "An explicit isolated Desktop profile is not classified as Personal");

        if (args.Contains("--live-process-check"))
        {
            var trackedInfo = Core.LaunchInfo(account, fake, false, null);
            trackedInfo.UseShellExecute = false; trackedInfo.CreateNoWindow = true; trackedInfo.RedirectStandardInput = true; trackedInfo.RedirectStandardOutput = true; trackedInfo.RedirectStandardError = true;
            using (var tracked = Process.Start(trackedInfo))
            {
                try
                {
                    var live = SessionGuard.Snapshot();
                    Check(live.Any(p => p.Id == tracked.Id), "Windows process query sees a real launched CLI shell");
                    Check(SessionGuard.Analyze(state, live).AccountIds.Contains(account.Id), "Real encoded CLI command recovers its account after a launcher restart");
                }
                finally { if (!tracked.HasExited) { tracked.Kill(); tracked.WaitForExit(5000); } }
            }
            Check(!SessionGuard.Analyze(state, SessionGuard.Snapshot()).AccountIds.Contains(account.Id), "Real process exit releases the CLI account lock");
        }
        Application.EnableVisualStyles();
        using (var form = new MainForm(defaultState, null, defaultSettingsFile, delegate { return new List<ProcessEntry> { nativeDesktop }; }, delegate { }, delegate { return true; }, delegate { return fake; }))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Pump();
            var controls = Descendants(form).ToList();
            Check(controls.OfType<Label>().Any(label => label.Text == "Uses this PC's default Claude Desktop profile and any existing sign-in."), "Personal's Desktop description identifies the default PC profile and optional sign-in");
            Check(controls.OfType<Button>().Single(button => button.Text == "Open Claude Desktop").Enabled, "Already-running default Desktop can be reopened from Personal");
            Draw(form, Path.Combine(root, "personal-default-preview.png"));
            form.Close();
        }
        using (var form = new MainForm(state, null, settingsFile, delegate { return new List<ProcessEntry> { cliEntry }; }, delegate { }, delegate { return true; }, delegate { return fake; }))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Pump();
            var controls = Descendants(form).ToList();
            var modeDesktop = controls.OfType<RadioButton>().Single(b => b.Text == "Claude Desktop");
            var modeCli = controls.OfType<RadioButton>().Single(b => b.Text == "Claude Code CLI");
            var list = controls.OfType<ListBox>().Single();
            var folderBox = controls.OfType<TextBox>().Single();
            var openButton = controls.OfType<Button>().Single(b => b.Text == "Open Claude Desktop");
            var loginButton = controls.OfType<Button>().Single(b => b.AccessibleName == "Account sign-in");
            var browserPicker = controls.OfType<ComboBox>().Single(b => b.AccessibleName == "Login browser");
            var privateCheckbox = controls.OfType<CheckBox>().Single(b => b.AccessibleName == "Private login window");
            var linkButton = controls.OfType<Button>().Single(b => b.Text == "Open login link...");
            var browserToggle = controls.OfType<Button>().Single(b => b.AccessibleName == "Browser options");
            Draw(form, Path.Combine(root, "desktop-collapsed-preview.png"));
            browserToggle.PerformClick(); Pump();
            Check(browserPicker.Visible && ((BrowserChoice)browserPicker.SelectedItem).Id == "edge" && privateCheckbox.Checked, "Desktop shows the account's saved browser choice");
            Check(!linkButton.Enabled, "Desktop login link requires a pending sign-in");
            Check(modeDesktop.Checked && !folderBox.Visible && loginButton.Visible && loginButton.Text == "Sign in to Desktop", "Desktop mode hides the folder and offers explicit Desktop sign-in");
            Check(list.Items.Count == 2 && list.SelectedItem == account && openButton.Enabled, "Desktop shares the selected account and permits the active account");
            Draw(form, Path.Combine(root, "desktop-preview.png"));
            list.SelectedItem = other; Pump();
            Check(((BrowserChoice)browserPicker.SelectedItem).Id == "default" && !privateCheckbox.Enabled && !privateCheckbox.Checked, "Changing accounts restores its browser preference without leaking Work's private mode");
            Check(!openButton.Enabled, "Selecting a different account disables the Desktop launch button");
            Draw(form, Path.Combine(root, "blocked-preview.png"));
            modeCli.Checked = true; Pump();
            Check(folderBox.Visible && loginButton.Visible && list.SelectedItem == other && openButton.Text == "Open Claude Code" && !openButton.Enabled, "CLI switch preserves selection and enforces the same account lock");
            var folderCaption = controls.OfType<Label>().Single(b => b.Text == "WORKING FOLDER");
            Check(folderCaption.PointToScreen(Point.Empty).Y < folderBox.PointToScreen(Point.Empty).Y, "Working folder label stays above its input after switching modes");
            list.SelectedItem = account; folderBox.Text = folder; Pump();
            Check(browserPicker.Visible && ((BrowserChoice)browserPicker.SelectedItem).Id == "edge" && privateCheckbox.Checked && linkButton.Enabled, "CLI shares the browser selection and enables link opening for its active account");
            browserPicker.SelectedItem = LoginBrowser.Choice("firefox");
            Check(account.LoginBrowser == "firefox" && account.LoginPrivateWindow, "Changing the browser updates only the selected account");
            browserPicker.SelectedItem = LoginBrowser.Choice("default");
            Check(!privateCheckbox.Enabled && !privateCheckbox.Checked && !account.LoginPrivateWindow, "Windows default browser clears the unsupported private-window option");
            browserPicker.SelectedItem = LoginBrowser.Choice("edge"); privateCheckbox.Checked = true;
            Check(openButton.Enabled && !loginButton.Enabled, "The active account may reopen but cannot switch login during a session");
            Draw(form, Path.Combine(root, "cli-preview.png"));
            browserToggle.PerformClick(); Pump();
            Draw(form, Path.Combine(root, "cli-collapsed-preview.png"));
            browserToggle.PerformClick(); Pump();
            bool loginDialogShown = false, loginDialogFits = false;
            using (var closeDialog = new Timer { Interval = 100 })
            {
                closeDialog.Tick += delegate {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Open login link for " + account.Name);
                    if (dialog == null) return;
                    closeDialog.Stop();
                    var dialogControls = Descendants(dialog).ToList();
                    var input = dialogControls.OfType<TextBox>().Single();
                    var confirm = dialogControls.OfType<Button>().Single(b => b.Text == "Open in browser");
                    loginDialogShown = input.Text == "" && dialogControls.OfType<Label>().Any(l => l.Text.Contains("Microsoft Edge (private window)"));
                    loginDialogFits = input.PointToScreen(Point.Empty).Y + input.Height < confirm.PointToScreen(Point.Empty).Y
                        && dialog.RectangleToScreen(dialog.ClientRectangle).Contains(confirm.RectangleToScreen(confirm.ClientRectangle));
                    Draw(dialog, Path.Combine(root, "login-link-preview.png"));
                    dialog.DialogResult = DialogResult.Cancel;
                };
                closeDialog.Start(); linkButton.PerformClick();
            }
            Check(loginDialogShown && loginDialogFits, "Login-link dialog identifies the account and browser, leaves clipboard untouched, and fits its controls");
            modeDesktop.Checked = true; modeCli.Checked = true;
            Check(folderBox.Text == folder, "Mode switches preserve the CLI working folder");
            form.Close();
        }
        using (var form = new MainForm(state, folder, settingsFile, delegate { throw new IOException("Unavailable"); }, delegate { }, delegate { return true; }, delegate { return fake; }))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Pump();
            var controls = Descendants(form).ToList();
            Check(state.Mode == "cli" && controls.OfType<RadioButton>().Single(b => b.Text == "Claude Code CLI").Checked, "A project-folder argument selects CLI mode");
            Check(!controls.OfType<Button>().Single(b => b.Text == "Open Claude Code").Enabled, "Process-query failure leaves the launch button disabled");
            form.Close();
        }
        state.Mode = "desktop"; state.SelectedId = other.Id;
        using (var form = new MainForm(state, null, settingsFile, delegate { return new List<ProcessEntry>(); }, delegate { }, delegate { return false; }, delegate { return fake; }))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Pump();
            var controls = Descendants(form).ToList();
            Check(!controls.OfType<Button>().Single(b => b.Text == "Open Claude Desktop").Enabled, "Work Desktop is blocked until SSO routing is configured");
            Check(controls.OfType<Button>().Single(b => b.Text == "Set up Desktop sign-in").Enabled, "Missing routing exposes a setup action instead of allowing unsafe sign-in");
            Draw(form, Path.Combine(root, "desktop-setup-preview.png")); form.Close();
        }
        var browserPending = new DesktopLoginStore(Path.GetDirectoryName(settingsFile));
        browserPending.Begin(other, DateTime.UtcNow);
        using (var form = new MainForm(state, null, settingsFile, delegate { return new List<ProcessEntry> { otherDesktop }; }, delegate { }, delegate { return true; }, delegate { return fake; }))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show(); Pump();
            var controls = Descendants(form).ToList();
            var linkButton = controls.OfType<Button>().Single(b => b.Text == "Open login link...");
            Check(linkButton.Enabled, "A current Desktop sign-in enables the chosen-browser action for its account");
            controls.OfType<ListBox>().Single().SelectedItem = account; Pump();
            Check(!linkButton.Enabled, "Highlighting another account cannot open the pending Desktop login in its browser");
            controls.OfType<ListBox>().Single().SelectedItem = other;
            controls.OfType<Button>().Single(b => b.Text == "Cancel Desktop sign-in").PerformClick(); Pump();
            Check(!linkButton.Enabled && browserPending.Read() == null, "Cancelling Desktop sign-in disables opening its login URL");
            form.Close();
        }

        string callback = "claude://claude.ai/sso-callback?code=fake-test-code&state=state%20with%20spaces";
        Check(DesktopLogin.Classify(callback) == DesktopLinkKind.Login, "SSO callback is recognized as authentication");
        Check(DesktopLogin.Classify("claude://login/google-auth?code=fake") == DesktopLinkKind.Login, "Google login callback requires an explicit pending sign-in");
        Check(DesktopLogin.Classify("claude://claude.ai/magic-link#fake:token") == DesktopLinkKind.Login, "Email magic link requires an explicit pending sign-in");
        Check(DesktopLogin.Classify("claude://claude.ai/chat/123") == DesktopLinkKind.Normal, "Ordinary chat navigation remains available");
        foreach (var badLink in new[] { "https://claude.ai/sso-callback?code=fake", "claude://unknown/callback?code=fake", "claude://claude.ai/unknown-auth", "claude://claude.ai/chat/123?code=fake", "claude://claude.ai/chat/123?%63ode=fake", "claude://claude.ai/chat/123#token", "claude://evil@claude.ai/sso-callback", "claude://claude.ai:123/sso-callback", "claude://claude.ai/sso-callback\r\n--inject", "claude://claude.ai/sso-callback?x=\" --user-data-dir=bad" })
            Reject(delegate { DesktopLogin.Classify(badLink); }, "Malformed or unsupported link cannot fall back to Default");
        var callbackInfo = DesktopLogin.CallbackLaunchInfo(other, desktopExecutable, callback);
        var callbackArgs = SessionGuard.Arguments(Core.WindowsQuote(callbackInfo.FileName) + " " + callbackInfo.Arguments);
        Check(callbackArgs.Length == 3 && callbackArgs[1] == "--user-data-dir=" + other.DesktopDirectory && callbackArgs[2] == callback, "SSO callback and isolated profile survive Windows argument parsing exactly");
        Check(callbackInfo.EnvironmentVariables["CLAUDE_USER_DATA_DIR"] == other.DesktopDirectory, "Callback delivery uses the same Work profile environment as normal launch");
        var defaultCallbackArgs = SessionGuard.Arguments("app.exe " + DesktopLogin.CallbackLaunchInfo(personal, desktopExecutable, callback).Arguments);
        Check(defaultCallbackArgs.Length == 2 && defaultCallbackArgs[1] == callback, "Explicit Default sign-in preserves the native Desktop profile");
        var handlerArgs = SessionGuard.Arguments(DesktopLogin.HandlerCommand(Path.Combine(root, "selector with spaces", "ClaudeSelector.exe")));
        Check(handlerArgs.Length == 3 && handlerArgs[1] == "--handle-desktop-link" && handlerArgs[2] == "%1", "Registered handler quotes its executable and callback placeholder");
        var loginStore = new DesktopLoginStore(Path.Combine(root, "routing"));
        var utc = DateTime.UtcNow;
        loginStore.Begin(other, utc);
        var pendingLogin = loginStore.Read();
        var workActivity = SessionGuard.Analyze(defaultState, new[] { isolatedNative });
        Check(workActivity.DesktopAccountIds.Contains(other.Id), "Login routing identifies the active Work Desktop main process");
        Check(DesktopLogin.LoginTarget(defaultState, pendingLogin, workActivity, utc).Id == other.Id, "Pending Work SSO resolves to Work rather than Default");
        defaultState.SelectedId = personal.Id;
        Check(DesktopLogin.LoginTarget(defaultState, pendingLogin, workActivity, utc).Id == other.Id, "Changing the highlighted account cannot redirect a pending login");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, null, workActivity, utc); }, "Unsolicited callbacks never fall back to Personal");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, workActivity, utc.AddMinutes(10)); }, "Expired callbacks never fall back to Personal");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, workActivity, utc.AddSeconds(-1)); }, "Clock rollback invalidates a pending sign-in");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, new Activity(), utc); }, "Closing Work before callback invalidates delivery");
        var onlyCli = new Activity(); onlyCli.AccountIds.Add(other.Id);
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, onlyCli, utc); }, "A CLI session alone cannot receive a Desktop login");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, new Activity { Problem = "Process access failed" }, utc); }, "Callback process-query errors fail closed");
        var both = SessionGuard.Analyze(defaultState, new[] { isolatedNative, nativeDesktop });
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, both, utc); }, "Callback forwarding enforces the one-active-account restriction");
        string unchanged = other.DesktopDirectory; other.DesktopDirectory = Path.Combine(root, "changed-profile");
        Reject(delegate { DesktopLogin.LoginTarget(defaultState, pendingLogin, workActivity, utc); }, "Changing a profile invalidates its pending login");
        other.DesktopDirectory = unchanged;
        string pendingJson = File.ReadAllText(Path.Combine(root, "routing", "desktop-sign-in.json"));
        Check(!pendingJson.Contains("fake-test-code") && !pendingJson.Contains("claude://"), "Pending login file contains no callback URL or credentials");
        loginStore.Consume(pendingLogin.Id);
        Check(loginStore.Read() == null, "Forwarding consumes the pending sign-in exactly once");
        Reject(delegate { loginStore.Consume(pendingLogin.Id); }, "Duplicate callbacks cannot reuse consumed intent");
        loginStore.Begin(other, utc); string replacedId = loginStore.Read().Id; loginStore.Begin(personal, utc);
        Reject(delegate { loginStore.Consume(replacedId); }, "A confirmation cannot consume a newly replaced sign-in");
        loginStore.Cancel(); Check(loginStore.Read() == null, "Cancelling removes the sign-in destination");
        File.WriteAllText(Path.Combine(root, "routing", "desktop-sign-in.json"), "broken metadata");
        Reject(delegate { loginStore.Read(); }, "Corrupt pending metadata cannot select a default account");
        loginStore.Cancel();
        Console.WriteLine("Previews: " + root);
        Console.WriteLine("Passed " + checks + " checks.");
    }
}
