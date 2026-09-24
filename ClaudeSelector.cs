using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeSelector
{
    public sealed class Account
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ConfigDirectory { get; set; }
        public string Folder { get; set; }
        public string DesktopDirectory { get; set; }
        public string LoginBrowser { get; set; }
        public bool LoginPrivateWindow { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class Settings
    {
        public List<Account> Accounts { get; set; }
        public string SelectedId { get; set; }
        public string Mode { get; set; }
    }
    public static class Core
    {
        public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSelector");
        // PowerShell also treats typographic quotes as delimiters. Encode all external
        // strings as data so names and paths can never become shell syntax.
        public static string Quote(string value) { return "([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "')))"; }
        public static Settings Load(string file)
        {
            if (!File.Exists(file))
            {
                string config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (String.IsNullOrWhiteSpace(config)) config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Normalize(new Settings { SelectedId = "existing", Accounts = new List<Account> {
                    new Account { Id = "existing", Name = "Default", ConfigDirectory = Path.GetFullPath(config), Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                }});
            }
            var result = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(file));
            if (result == null || result.Accounts == null || result.Accounts.Any(a => a == null || String.IsNullOrWhiteSpace(a.Id) || String.IsNullOrWhiteSpace(a.Name) || String.IsNullOrWhiteSpace(a.ConfigDirectory)) || result.Accounts.Select(a => a.Id).Distinct().Count() != result.Accounts.Count)
                throw new InvalidDataException("The saved account list is invalid. Restore settings.json from settings.json.bak in " + Path.GetDirectoryName(file));
            return Normalize(result);
        }
        public static Settings Normalize(Settings settings)
        {
            if (settings.Mode != "desktop") settings.Mode = "cli";
            foreach (var account in settings.Accounts)
            {
                if (String.IsNullOrEmpty(account.LoginBrowser)) account.LoginBrowser = "default";
                if (!Regex.IsMatch(account.Id, @"\A[A-Za-z0-9_-]{1,80}\z")) throw new InvalidDataException("Invalid account identifier.");
                if (String.IsNullOrWhiteSpace(account.DesktopDirectory))
                    account.DesktopDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSelector-" + account.Id);
                if (!Path.IsPathRooted(account.DesktopDirectory)) throw new InvalidDataException("Desktop profile paths must be absolute.");
            }
            if (settings.Accounts.Select(a => Path.GetFullPath(a.DesktopDirectory).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Accounts.Count)
                throw new InvalidDataException("Each account must have a different Desktop profile.");
            return settings;
        }
        public static void Save(Settings settings, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(settings), Encoding.UTF8);
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak");
            else File.Move(temporary, file);
        }
        public static string FindClaude()
        {
            return FindClaude(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));
        }
        public static string FindClaude(string userProfile, string searchPath)
        {
            var candidates = new List<string> { Path.Combine(userProfile, ".local", "bin", "claude.exe") };
            foreach (string directory in (searchPath ?? "").Split(';'))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                string fullDirectory = directory.Trim().Trim('"');
                // Relative PATH entries must not select a project-local executable.
                if (fullDirectory.Length < 3 || !Char.IsLetter(fullDirectory[0]) || fullDirectory[1] != ':' || (fullDirectory[2] != '\\' && fullDirectory[2] != '/')) continue;
                foreach (string name in new[] { "claude.exe", "claude.cmd", "claude.bat" })
                    candidates.Add(Path.Combine(fullDirectory, name));
            }
            return candidates.FirstOrDefault(File.Exists);
        }
        public static string Script(Account account, string cli, bool login)
        {
            // Set environment inside the new shell: Windows Terminal can reuse an existing process.
            // This marker is also visible in the shell's encoded command after the selector restarts.
            var script = new StringBuilder("# ClaudeSelector account=" + account.Id + "\r\n$ErrorActionPreference = 'Stop'; try { ");
            // An explicit default directory changes Claude's global onboarding file.
            string defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            bool useDefaultConfiguration = account.Id == "existing"
                && String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))
                && String.Equals(Path.GetFullPath(account.ConfigDirectory).TrimEnd('\\', '/'), defaultDirectory, StringComparison.OrdinalIgnoreCase);
            if (useDefaultConfiguration)
                script.Append("Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; ");
            else
                script.Append("$env:CLAUDE_CONFIG_DIR = " + Quote(account.ConfigDirectory) + "; ");
            if (account.Id != "existing")
                foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
                    script.Append("Remove-Item Env:" + key + " -ErrorAction SilentlyContinue; ");
            script.Append("Set-Location -LiteralPath " + Quote(account.Folder) + "; ");
            script.Append("$Host.UI.RawUI.WindowTitle = " + Quote("Claude Code - " + account.Name) + "; ");
            script.Append("Write-Host " + Quote("Account: " + account.Name) + " -ForegroundColor Cyan; ");
            if (login)
                script.Append("& " + Quote(cli) + " auth login; if ($LASTEXITCODE -ne 0) { throw 'Sign-in did not complete. Try Sign in again.' }; ");
            script.Append("& " + Quote(cli) + "; if ($LASTEXITCODE -ne 0) { Write-Host ('Claude exited with code ' + $LASTEXITCODE) -ForegroundColor Red }; ");
            script.Append("} catch { Write-Host $_ -ForegroundColor Red }");
            return script.ToString();
        }
        public static ProcessStartInfo LaunchInfo(Account account, string cli, bool login, string terminal)
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            string args = "-NoLogo -NoProfile -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(account, cli, login)));
            return new ProcessStartInfo {
                FileName = terminal ?? shell,
                Arguments = terminal == null ? args : "-w new new-tab \"" + shell + "\" " + args,
                WorkingDirectory = account.Folder, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal
            };
        }
        public static string WindowsQuote(string value)
        {
            // CommandLineToArgvW quoting, including trailing backslashes before the closing quote.
            return "\"" + Regex.Replace(Regex.Replace(value, @"(\\*)""", "$1$1\\\""), @"(\\+)\z", "$1$1") + "\"";
        }
        public static ProcessStartInfo DesktopLaunchInfo(Account account, string executable)
        {
            bool useDefault = UsesDefaultDesktop(account);
            var info = new ProcessStartInfo(executable, useDefault ? "" : "--user-data-dir=" + WindowsQuote(account.DesktopDirectory)) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)
            };
            if (useDefault)
            {
                // Let Desktop select its normal profile, including MSIX path virtualization/migration.
                // An inherited override must not redirect the default entry to another profile.
                info.EnvironmentVariables.Remove("CLAUDE_USER_DATA_DIR");
                info.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR");
            }
            else
            {
                // The installed Desktop build explicitly honors this variable for settings and migration too.
                info.EnvironmentVariables["CLAUDE_USER_DATA_DIR"] = account.DesktopDirectory;
                info.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = account.ConfigDirectory;
            }
            foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
                info.EnvironmentVariables.Remove(key);
            return info;
        }
        public static bool UsesDefaultDesktop(Account account) { return account.Id == "existing"; }
        public static bool IsDesktopExecutable(string executable)
        {
            // CLI and Desktop are both named claude.exe. Only the Desktop install has this bundle.
            if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)) return false;
            return String.Equals(Path.GetFileName(executable), "claude.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Path.GetDirectoryName(executable), "resources", "app.asar"));
        }
        public static string FindDesktop()
        {
            // Resolve the MSIX package on every launch; its versioned path changes on updates.
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var info = new ProcessStartInfo(shell, "-NoLogo -NoProfile -NonInteractive -Command \"Get-AppxPackage -Name Claude | Sort-Object Version -Descending | Select-Object -First 1 -ExpandProperty InstallLocation\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(info))
            {
                if (!process.WaitForExit(10000)) { process.Kill(); throw new IOException("Claude Desktop detection timed out. Try again."); }
                string location = process.StandardOutput.ReadToEnd().Trim();
                if (process.ExitCode == 0 && Path.IsPathRooted(location))
                {
                    string exe = Path.Combine(location, "app", "claude.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
            if (Directory.Exists(legacy))
            {
                var candidates = Directory.GetDirectories(legacy, "app-*").Select(p => new { Path = p, Version = ParseVersion(Path.GetFileName(p).Substring(4)) });
                foreach (var candidate in candidates.OrderByDescending(p => p.Version))
                    if (File.Exists(Path.Combine(candidate.Path, "claude.exe"))) return Path.Combine(candidate.Path, "claude.exe");
            }
            return null;
        }
        static Version ParseVersion(string value) { Version result; return Version.TryParse(value, out result) ? result : new Version(0, 0); }
    }

    public sealed class ProcessEntry
    {
        public int Id, ParentId;
        public string Name, CommandLine, ExecutablePath;
        public DateTime Created;
    }
    public sealed class Activity
    {
        public readonly HashSet<string> AccountIds = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> DesktopAccountIds = new HashSet<string>(StringComparer.Ordinal);
        public string Problem;
        public bool CanOpen(string id) { return Problem == null && (AccountIds.Count == 0 || (AccountIds.Count == 1 && AccountIds.Contains(id))); }
        public string Message(Settings settings)
        {
            if (Problem != null) return Problem;
            if (AccountIds.Count == 0) return "No active account. Choose an account to open.";
            return "Active account: " + String.Join(", ", AccountIds.Select(id => settings.Accounts.FirstOrDefault(a => a.Id == id)).Select(a => a == null ? "Removed account" : a.Name)) + ". Close its CLI windows and quit Desktop before switching accounts.";
        }
    }
    public static class SessionGuard
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        public static string[] Arguments(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return new string[0];
            int count; IntPtr pointer = CommandLineToArgvW(command, out count);
            if (pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray(); }
            finally { LocalFree(pointer); }
        }
        public static List<ProcessEntry> Snapshot()
        {
            var entries = new List<ProcessEntry>();
            int sessionId; using (var current = Process.GetCurrentProcess()) sessionId = current.SessionId;
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath, CreationDate FROM Win32_Process WHERE SessionId = " + sessionId))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(5);
                using (var results = searcher.Get())
                    foreach (ManagementObject item in results)
                        using (item) entries.Add(new ProcessEntry { Id = Convert.ToInt32(item["ProcessId"]), ParentId = Convert.ToInt32(item["ParentProcessId"]), Name = (string)item["Name"], CommandLine = (string)item["CommandLine"], ExecutablePath = (string)item["ExecutablePath"], Created = item["CreationDate"] == null ? DateTime.MinValue : ManagementDateTimeConverter.ToDateTime((string)item["CreationDate"]) });
            }
            return entries;
        }
        public static Activity Read(Settings settings)
        {
            try { return Analyze(settings, Snapshot()); }
            catch { return new Activity { Problem = "Cannot verify running Claude sessions. Launching is blocked; try again when process access is available." }; }
        }
        public static Activity Analyze(Settings settings, IList<ProcessEntry> processes)
        {
            var result = new Activity();
            var owners = new Dictionary<int, string>();
            var byId = processes.ToDictionary(p => p.Id);
            foreach (var process in processes)
            {
                if (String.Equals(process.Name, "powershell.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.IsNullOrWhiteSpace(process.CommandLine)) result.Problem = "Cannot identify a running PowerShell session. Close it or restore process access before launching an account.";
                    var args = Arguments(process.CommandLine);
                    for (int i = 0; i + 1 < args.Length; i++)
                        if (String.Equals(args[i], "-EncodedCommand", StringComparison.OrdinalIgnoreCase))
                        {
                            string script;
                            try { script = Encoding.Unicode.GetString(Convert.FromBase64String(args[i + 1])); }
                            catch (FormatException) { continue; }
                            var match = Regex.Match(script, @"\A# ClaudeSelector account=([A-Za-z0-9_-]{1,80})\r?\n");
                            if (match.Success) owners[process.Id] = match.Groups[1].Value;
                        }
                }
                if (!String.Equals(process.Name, "claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string directory = null;
                var desktopArgs = Arguments(process.CommandLine);
                for (int i = 0; i < desktopArgs.Length; i++)
                {
                    if (desktopArgs[i].StartsWith("--user-data-dir=", StringComparison.Ordinal)) directory = desktopArgs[i].Substring(16);
                    else if (desktopArgs[i] == "--user-data-dir" && i + 1 < desktopArgs.Length) directory = desktopArgs[++i];
                }
                if (directory != null)
                {
                    var account = settings.Accounts.FirstOrDefault(a => String.Equals(a.DesktopDirectory.TrimEnd('\\', '/'), directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (account != null) owners[process.Id] = account.Id;
                }
                else if (desktopArgs.Length > 0 && !desktopArgs.Any(a => a == "--type" || a.StartsWith("--type=", StringComparison.Ordinal)) && Core.IsDesktopExecutable(process.ExecutablePath))
                {
                    var account = settings.Accounts.FirstOrDefault(Core.UsesDefaultDesktop);
                    if (account != null) owners[process.Id] = account.Id;
                }
            }
            foreach (var process in processes)
            {
                if (!owners.ContainsKey(process.Id) && !String.Equals(process.Name, "claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var cursor = process; var seen = new HashSet<int>(); string owner = null;
                while (cursor != null && seen.Add(cursor.Id))
                {
                    if (owners.TryGetValue(cursor.Id, out owner)) break;
                    ProcessEntry parent;
                    cursor = byId.TryGetValue(cursor.ParentId, out parent) && parent.Created != DateTime.MinValue && cursor.Created != DateTime.MinValue && parent.Created <= cursor.Created ? parent : null;
                }
                if (owner != null)
                {
                    result.AccountIds.Add(owner);
                    if (Core.IsDesktopExecutable(process.ExecutablePath) && !String.IsNullOrWhiteSpace(process.CommandLine)
                        && !Arguments(process.CommandLine).Any(a => a == "--type" || a.StartsWith("--type=", StringComparison.Ordinal))) result.DesktopAccountIds.Add(owner);
                }
                else result.Problem = "An unrecognized Claude session is running. Close its CLI window or quit Desktop from the tray before launching an account.";
            }
            return result;
        }
    }
    public sealed class LauncherButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            using (var brush = new SolidBrush(Color.FromArgb(48, 48, 46))) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(76, 75, 70))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(150, 148, 139), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    public sealed class MainForm : Form
    {
        readonly string settingsFile;
        readonly Settings settings;
        readonly ListBox accounts = new ListBox();
        readonly TextBox folder = new TextBox();
        readonly Label detail = new Label();
        readonly Label feedback = new Label();
        readonly Label signInHint = new Label();
        readonly Label browserHint = new Label();
        readonly Label activityLabel = new Label();
        readonly Label folderLabel = new Label();
        readonly TableLayoutPanel location = new TableLayoutPanel();
        readonly RadioButton cliMode = new RadioButton();
        readonly RadioButton desktopMode = new RadioButton();
        readonly ComboBox browserChoice = new ComboBox();
        readonly CheckBox privateWindow = new CheckBox();
        readonly Button open, signIn, remove, openLoginLink;
        bool changingBrowser;
        readonly Timer refreshTimer = new Timer { Interval = 2000 };
        readonly System.ComponentModel.BackgroundWorker monitor = new System.ComponentModel.BackgroundWorker();
        readonly Func<List<ProcessEntry>> snapshot;
        readonly Action<Settings, string> persist;
        readonly Func<bool> routingReady;
        readonly Func<BrowserChoice, string> findBrowser;
        readonly DesktopLoginStore desktopLogin;
        Activity activity = new Activity { Problem = "Checking for active accounts..." };
        readonly List<Button> selectionButtons = new List<Button>();
        Account selected;
        bool changingMode;
        static readonly Color Background = Color.FromArgb(38, 38, 36);
        static readonly Color Surface = Color.FromArgb(48, 48, 46);
        static readonly Color Border = Color.FromArgb(76, 75, 70);
        static readonly Color Ink = Color.FromArgb(240, 238, 230);
        static readonly Color Muted = Color.FromArgb(177, 175, 165);
        static readonly Color Accent = Color.FromArgb(217, 119, 87);

        public MainForm(Settings state, string initialFolder) : this(state, initialFolder, Path.Combine(Core.DataDirectory, "settings.json"), SessionGuard.Snapshot) { }
        public MainForm(Settings state, string initialFolder, string preferenceFile, Func<List<ProcessEntry>> processSnapshot, Action<Settings, string> saveSettings = null, Func<bool> isRoutingReady = null, Func<BrowserChoice, string> browserFinder = null)
        {
            settings = Core.Normalize(state); settingsFile = preferenceFile; snapshot = processSnapshot; persist = saveSettings ?? Core.Save;
            routingReady = isRoutingReady ?? delegate { return DesktopLogin.IsHandlerReady(Application.ExecutablePath); };
            findBrowser = browserFinder ?? LoginBrowser.Find;
            desktopLogin = new DesktopLoginStore(Path.GetDirectoryName(preferenceFile));
            if (!String.IsNullOrWhiteSpace(initialFolder)) settings.Mode = "cli";
            Text = "Claude Selector"; StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(760, 830); MinimumSize = new Size(720, 820);
            BackColor = Background; ForeColor = Ink;
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, ClientSize.Height), Padding = new Padding(30), ColumnCount = 1, RowCount = 12 };
            viewport.ClientSizeChanged += delegate { layout.MinimumSize = new Size(0, viewport.ClientSize.Height); };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 12; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles[4] = new RowStyle(SizeType.Percent, 100);
            viewport.Controls.Add(layout); Controls.Add(viewport);
            layout.Controls.Add(new Label { Text = "Choose your Claude account", Font = new Font("Georgia", 23), AutoSize = true, Margin = new Padding(0, 0, 0, 22) });
            var modes = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 16), AccessibleName = "Launch mode" };
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            ConfigureMode(cliMode, "Claude Code CLI", "cli"); ConfigureMode(desktopMode, "Claude Desktop", "desktop");
            modes.Controls.Add(cliMode, 0, 0); modes.Controls.Add(desktopMode, 1, 0); layout.Controls.Add(modes);
            activityLabel.Dock = DockStyle.Fill; activityLabel.AutoSize = true; activityLabel.MinimumSize = new Size(0, 46);
            activityLabel.ForeColor = Muted; activityLabel.Margin = new Padding(0, 0, 0, 16); activityLabel.AccessibleName = "Active account";
            layout.Controls.Add(activityLabel);
            layout.Controls.Add(new Label { Text = "ACCOUNTS", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 9), Margin = new Padding(0, 0, 0, 10) });
            accounts.Dock = DockStyle.Fill; accounts.BorderStyle = BorderStyle.None; accounts.ItemHeight = 52; accounts.DrawMode = DrawMode.OwnerDrawFixed;
            accounts.BackColor = Surface; accounts.ForeColor = Ink; accounts.IntegralHeight = false; accounts.Margin = Padding.Empty;
            accounts.MinimumSize = new Size(0, 104);
            accounts.AccessibleName = "Accounts";
            accounts.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                if (e.Index < 0) return;
                bool active = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(active ? Color.FromArgb(69, 53, 46) : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (active) using (var brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, e.Bounds.X, e.Bounds.Y + 10, 3, e.Bounds.Height - 20);
                var account = (Account)accounts.Items[e.Index];
                bool running = activity.AccountIds.Contains(account.Id);
                TextRenderer.DrawText(e.Graphics, account.Name, e.Font, new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - (running ? 112 : 32), e.Bounds.Height), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (running) TextRenderer.DrawText(e.Graphics, "Active", e.Font, new Rectangle(e.Bounds.Right - 85, e.Bounds.Y, 70, e.Bounds.Height), Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                e.DrawFocusRectangle();
            };
            accounts.SelectedIndexChanged += delegate { ChangeSelection(); };
            accounts.DoubleClick += delegate { HandleAction(delegate { Run(false); }); };
            layout.Controls.Add(accounts);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 12) };
            actions.Controls.Add(Button("+ Add account", AddAccount));
            actions.Controls.Add(ForSelection("Rename", RenameAccount));
            remove = ForSelection("Remove", RemoveAccount); actions.Controls.Add(remove);
            layout.Controls.Add(actions);
            detail.Dock = DockStyle.Fill; detail.ForeColor = Muted; detail.Font = new Font("Segoe UI", 9); detail.AutoSize = true; detail.MinimumSize = new Size(0, 36); detail.Margin = new Padding(0, 0, 0, 18);
            layout.Controls.Add(detail);
            var browserSection = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 18) };
            browserSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            browserSection.RowStyles.Add(new RowStyle(SizeType.AutoSize)); browserSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var browserPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 8, 0, 0), Visible = false };
            bool browserExpanded = false;
            Button browserToggle = null;
            browserToggle = Button("\u25B8 Browser options (optional)", delegate {
                browserExpanded = !browserExpanded;
                browserSection.SuspendLayout();
                browserPanel.Visible = browserExpanded;
                browserToggle.Text = (browserExpanded ? "\u25BE" : "\u25B8") + " Browser options (optional)";
                browserToggle.AccessibleDescription = browserExpanded ? "Expanded. Hide login browser options." : "Collapsed. Show login browser options.";
                browserSection.ResumeLayout(true);
            });
            browserToggle.AccessibleName = "Browser options";
            browserToggle.AccessibleDescription = "Collapsed. Show login browser options.";
            browserToggle.Anchor = AnchorStyles.Left; browserToggle.Margin = Padding.Empty;
            browserToggle.BackColor = Background; browserToggle.ForeColor = Muted; browserToggle.FlatAppearance.BorderSize = 0;
            browserToggle.MinimumSize = new Size(0, 32); browserToggle.Padding = new Padding(0, 3, 6, 3);
            browserSection.Controls.Add(browserToggle, 0, 0); browserSection.Controls.Add(browserPanel, 0, 1);
            browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            browserChoice.DropDownStyle = ComboBoxStyle.DropDownList; browserChoice.Dock = DockStyle.Fill; browserChoice.BackColor = Surface; browserChoice.ForeColor = Ink;
            browserChoice.AccessibleName = "Login browser"; browserChoice.Margin = new Padding(0, 5, 12, 0);
            browserChoice.SelectedIndexChanged += delegate { SaveBrowserChoice(); };
            privateWindow.Text = "Private window"; privateWindow.AutoSize = true; privateWindow.Anchor = AnchorStyles.Left;
            privateWindow.AccessibleName = "Private login window"; privateWindow.Margin = new Padding(0, 0, 12, 0);
            privateWindow.CheckedChanged += delegate { SaveBrowserChoice(); };
            openLoginLink = Button("Open login link...", OpenLoginLink); openLoginLink.Margin = Padding.Empty;
            browserPanel.Controls.Add(browserChoice, 0, 0); browserPanel.Controls.Add(privateWindow, 1, 0); browserPanel.Controls.Add(openLoginLink, 2, 0);
            browserHint.AutoSize = true; browserHint.Dock = DockStyle.Fill; browserHint.ForeColor = Muted;
            browserHint.Font = new Font("Segoe UI", 9); browserHint.Margin = new Padding(0, 10, 0, 0);
            browserHint.AccessibleName = "Browser options explanation";
            browserPanel.Controls.Add(browserHint, 0, 1); browserPanel.SetColumnSpan(browserHint, 3); layout.Controls.Add(browserSection);
            folderLabel.Text = "WORKING FOLDER"; folderLabel.AutoSize = true; folderLabel.ForeColor = Muted; folderLabel.Font = new Font("Segoe UI Semibold", 9); folderLabel.Margin = new Padding(0, 0, 0, 10);
            layout.Controls.Add(folderLabel);
            location.Dock = DockStyle.Fill; location.AutoSize = true; location.ColumnCount = 2; location.RowCount = 1; location.Margin = new Padding(0, 0, 0, 24);
            location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folder.Anchor = AnchorStyles.Left | AnchorStyles.Right; folder.AccessibleName = "Working folder";
            folder.BackColor = Surface; folder.ForeColor = Ink; folder.BorderStyle = BorderStyle.None; folder.Margin = new Padding(12, 0, 12, 0);
            var folderFrame = new Panel { Dock = DockStyle.Fill, Height = 40, BackColor = Surface, Margin = new Padding(0, 0, 10, 0), MinimumSize = new Size(0, 40) };
            folderFrame.Controls.Add(folder);
            folderFrame.Resize += delegate { folder.SetBounds(12, (folderFrame.ClientSize.Height - folder.PreferredHeight) / 2, Math.Max(0, folderFrame.ClientSize.Width - 24), folder.PreferredHeight); };
            var browse = ForSelection("Browse...", Browse); browse.Margin = Padding.Empty;
            location.Controls.Add(folderFrame, 0, 0); location.Controls.Add(browse, 1, 0); layout.Controls.Add(location);
            var launch = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 14) };
            launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            launch.RowStyles.Add(new RowStyle(SizeType.AutoSize)); launch.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            open = ForSelection("Open Claude Code", delegate { Run(false); }); StylePrimary(open);
            open.Anchor = AnchorStyles.Left;
            signIn = ForSelection("Sign in / switch login", delegate { Run(true); });
            signIn.AccessibleName = "Account sign-in";
            signIn.Anchor = AnchorStyles.Right; signIn.Margin = Padding.Empty; signIn.MinimumSize = new Size(0, 32);
            signIn.Padding = new Padding(10, 3, 10, 3); signIn.Font = new Font("Segoe UI", 9);
            launch.Controls.Add(open, 0, 0); launch.Controls.Add(signIn, 1, 0);
            signInHint.AutoSize = true; signInHint.Dock = DockStyle.Fill; signInHint.ForeColor = Muted;
            signInHint.Font = new Font("Segoe UI", 9); signInHint.Margin = new Padding(0, 8, 0, 0);
            signInHint.AccessibleName = "Sign-in explanation"; launch.Controls.Add(signInHint, 0, 1); launch.SetColumnSpan(signInHint, 2);
            launch.SizeChanged += delegate { signInHint.MaximumSize = new Size(Math.Max(1, launch.ClientSize.Width), 0); };
            layout.Controls.Add(launch); AcceptButton = open;
            feedback.Dock = DockStyle.Fill; feedback.ForeColor = Muted; feedback.Font = new Font("Segoe UI", 9); feedback.AutoSize = true; feedback.Margin = Padding.Empty;
            feedback.MinimumSize = new Size(0, 36); layout.Controls.Add(feedback);
            // Pin rows explicitly: automatic flow can reorder hidden controls when modes change.
            for (int row = 0; row < layout.Controls.Count; row++) layout.SetCellPosition(layout.Controls[row], new TableLayoutPanelCellPosition(0, row));
            monitor.DoWork += delegate(object sender, System.ComponentModel.DoWorkEventArgs e) { e.Result = snapshot(); };
            monitor.RunWorkerCompleted += delegate(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e) {
                if (IsDisposed || Disposing) return;
                try
                {
                    if (e.Error != null) throw e.Error;
                    activity = SessionGuard.Analyze(settings, (List<ProcessEntry>)e.Result);
                }
                catch { activity = new Activity { Problem = "Cannot verify running Claude sessions. Launching is blocked; check process access and try again." }; }
                UpdateActivity();
            };
            refreshTimer.Tick += delegate { if (!monitor.IsBusy) monitor.RunWorkerAsync(); };
            Shown += delegate { refreshTimer.Start(); if (!monitor.IsBusy) monitor.RunWorkerAsync(); };
            Activated += delegate { if (open != null) UpdateActivity(); };
            FormClosing += delegate { try { RememberFolder(); persist(settings, settingsFile); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save preferences"); } };
            FormClosed += delegate { refreshTimer.Stop(); refreshTimer.Dispose(); };
            changingMode = true; cliMode.Checked = settings.Mode == "cli"; desktopMode.Checked = settings.Mode == "desktop"; changingMode = false;
            Reload(settings.SelectedId);
            if (!String.IsNullOrWhiteSpace(initialFolder)) folder.Text = Path.GetFullPath(initialFolder);
            ApplyMode();
        }
        void ConfigureMode(RadioButton button, string text, string mode)
        {
            button.Text = text; button.AccessibleName = text; button.Appearance = Appearance.Button; button.TextAlign = ContentAlignment.MiddleCenter;
            button.Dock = DockStyle.Fill; button.AutoSize = false; button.Height = 48; button.MinimumSize = new Size(0, 48);
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Border; button.Cursor = Cursors.Hand;
            button.Font = new Font("Segoe UI Semibold", 11); button.Margin = Padding.Empty;
            button.CheckedChanged += delegate {
                if (changingMode || !button.Checked) return;
                HandleAction(delegate { RememberFolder(); settings.Mode = mode; ApplyMode(); persist(settings, settingsFile); });
            };
        }
        void ApplyMode()
        {
            bool desktop = settings.Mode == "desktop";
            foreach (var button in new[] { cliMode, desktopMode })
            {
                button.BackColor = button.Checked ? Accent : Surface; button.ForeColor = button.Checked ? Background : Ink;
                button.FlatAppearance.CheckedBackColor = Accent;
            }
            folderLabel.Visible = location.Visible = !desktop; signIn.Visible = true;
            open.Text = desktop ? "Open Claude Desktop" : "Open Claude Code";
            browserHint.Text = "This choice only applies to Open login link. "
                + (desktop ? "Clicking SSO inside Claude Desktop still opens your Windows default browser." : "Links opened by Claude or clicked in the terminal still use your Windows default browser.")
                + "\r\n\r\nTo use the browser above: start sign-in, copy the initial login URL from "
                + (desktop ? "the browser that opens" : "the terminal")
                + ", then paste it into Open login link before completing login.\r\n\r\n"
                + "The list shows installed supported browsers. Choices are saved per account. Private window avoids the browser's normal signed-in session; still choose the matching identity.";
            feedback.Text = desktop ? "For SSO, use Sign in to Desktop first. In the browser choose the matching account, or use your work browser profile/private window." : "The default profile reuses your CLI sign-in, if any. Close its terminal windows before switching accounts.";
            UpdateDetail(); UpdateActivity();
        }
        void UpdateDetail()
        {
            detail.Text = selected == null ? "Add an account to get started." : settings.Mode == "desktop" ? Core.UsesDefaultDesktop(selected) ? "Uses this PC's default Claude Desktop profile and any existing sign-in." : "Opens the saved Desktop profile for " + selected.Name + ". No project folder needed." : selected.Id == "existing" ? "Uses your default CLI profile and any existing sign-in." : "Separate Claude CLI login and settings. Use Sign in to connect this account.";
        }
        void UpdateActivity()
        {
            bool desktop = settings.Mode == "desktop";
            bool ready = !desktop || routingReady();
            PendingDesktopLogin pending = null; bool invalidPending = false;
            try { pending = desktopLogin.Read(); } catch { invalidPending = true; }
            activityLabel.Text = activity.Message(settings);
            if (desktop && (pending != null || invalidPending))
            {
                var target = pending == null ? null : settings.Accounts.FirstOrDefault(a => a.Id == pending.AccountId);
                activityLabel.Text += "\r\n" + (pending != null && DateTime.UtcNow.Ticks < pending.ExpiresUtcTicks ? "Waiting for sign-in: " + (target == null ? "unknown account" : target.Name) + ". Return links require confirmation." : "The pending sign-in expired or is invalid. Cancel it before trying again.");
            }
            activityLabel.ForeColor = activity.Problem != null || (selected != null && !activity.CanOpen(selected.Id)) ? Accent : Muted;
            open.Enabled = selected != null && activity.CanOpen(selected.Id) && (!desktop || Core.UsesDefaultDesktop(selected) || ready);
            signIn.Text = !desktop ? "Sign in / switch login" : pending != null || invalidPending ? "Cancel Desktop sign-in" : ready ? "Sign in to Desktop" : "Set up Desktop sign-in";
            signInHint.Text = !desktop
                ? "Sign in: starts login for this account in a terminal, then opens Claude Code when login succeeds. Already signed in? Use Open Claude Code."
                : pending != null || invalidPending
                    ? "Cancel sign-in: stops waiting for the browser return link. It does not sign you out or close Desktop. Start a fresh sign-in to try again."
                    : ready
                        ? "Sign in: opens this account's Desktop and waits 10 minutes for its browser return link. Start login inside Desktop, then confirm the returning account. Already signed in? Use Open Claude Desktop."
                        : "Set up sign-in: opens Windows Settings so you can choose Claude Selector for CLAUDE links. This one-time setup sends browser sign-ins back to the selected account.";
            signIn.Enabled = desktop ? pending != null || invalidPending || (selected != null && (!ready || activity.CanOpen(selected.Id))) : selected != null && activity.Problem == null && activity.AccountIds.Count == 0;
            remove.Enabled = selected != null && activity.Problem == null && !activity.AccountIds.Contains(selected.Id);
            openLoginLink.Enabled = selected != null && activity.CanOpen(selected.Id) && activity.AccountIds.Contains(selected.Id)
                && (!desktop || ready && pending != null && pending.AccountId == selected.Id && DateTime.UtcNow.Ticks >= pending.CreatedUtcTicks && DateTime.UtcNow.Ticks < pending.ExpiresUtcTicks);
            if (desktop && !ready && pending == null) feedback.Text = "SSO setup required: choose Set up Desktop sign-in, then select Claude Selector for CLAUDE links in Windows Settings.";
            else if (desktop && ready && feedback.Text.StartsWith("SSO setup required:", StringComparison.Ordinal)) feedback.Text = "SSO routing is ready. Use Sign in to Desktop before starting a browser login, then choose the matching browser identity.";
            accounts.Invalidate();
        }
        void CheckActivity()
        {
            try { activity = SessionGuard.Analyze(settings, snapshot()); }
            catch { activity = new Activity { Problem = "Cannot verify running Claude sessions. Launching is blocked; check process access and try again." }; }
            UpdateActivity();
        }
        void HandleAction(Action action)
        {
            try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Claude Selector", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        Button Button(string text, Action action)
        {
            var b = new LauncherButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 40), Padding = new Padding(14, 6, 14, 6), FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(61, 61, 57);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 72, 66);
            b.Click += delegate { HandleAction(action); };
            return b;
        }
        static void StylePrimary(Button button)
        {
            button.BackColor = Accent; button.ForeColor = Background;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(231, 145, 115);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(196, 102, 74);
        }
        Button ForSelection(string text, Action action) { var b = Button(text, action); selectionButtons.Add(b); return b; }
        void RememberFolder() { if (selected != null) selected.Folder = folder.Text.Trim(); }
        void ChangeSelection()
        {
            RememberFolder(); selected = accounts.SelectedItem as Account;
            foreach (var b in selectionButtons) b.Enabled = selected != null;
            folder.Enabled = selected != null;
            folder.Text = selected == null ? "" : selected.Folder;
            changingBrowser = true;
            browserChoice.Items.Clear();
            browserChoice.Items.AddRange(LoginBrowser.Available(selected == null ? null : selected.LoginBrowser, findBrowser));
            browserChoice.SelectedItem = selected == null ? null : browserChoice.Items.Cast<BrowserChoice>().FirstOrDefault(b => b.Id == selected.LoginBrowser);
            browserChoice.Enabled = selected != null;
            privateWindow.Checked = selected != null && selected.LoginPrivateWindow;
            privateWindow.Enabled = selected != null && selected.LoginBrowser != "default" && browserChoice.SelectedItem != null;
            changingBrowser = false;
            settings.SelectedId = selected == null ? null : selected.Id;
            UpdateDetail(); UpdateActivity();
        }
        void Reload(string id)
        {
            RememberFolder(); selected = null; accounts.Items.Clear();
            foreach (var account in settings.Accounts) accounts.Items.Add(account);
            accounts.SelectedItem = settings.Accounts.FirstOrDefault(a => a.Id == id) ?? settings.Accounts.FirstOrDefault();
            ChangeSelection();
        }
        string AskName(string title, string value)
        {
            using (var dialog = new Form { Text = title, ClientSize = new Size(440, 190), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font, BackColor = Background, ForeColor = Ink, AutoScaleMode = AutoScaleMode.Dpi })
            {
                var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
                content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(content);
                var input = new TextBox { Text = value, Dock = DockStyle.Top, MaxLength = 60, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 12, 0, 16) };
                content.Controls.Add(new Label { Text = "Account name (for example, Work or Personal)", AutoSize = true, ForeColor = Muted, Margin = Padding.Empty }); content.Controls.Add(input);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
                var ok = Button("Save", delegate { }); ok.DialogResult = DialogResult.OK; ok.Margin = Padding.Empty; StylePrimary(ok);
                var cancel = Button("Cancel", delegate { }); cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); content.Controls.Add(buttons);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                dialog.Shown += delegate { input.Focus(); input.SelectAll(); };
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                var name = input.Text.Trim();
                if (name.Length == 0 || name.Any(Char.IsControl)) throw new Exception("Enter a name for this account.");
                if (settings.Accounts.Any(a => a != selected && String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) throw new Exception("An account already uses that name.");
                return name;
            }
        }
        void AddAccount()
        {
            var name = AskName("Add account", ""); if (name == null) return;
            if (settings.Accounts.Any(a => String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) throw new Exception("An account already uses that name.");
            string id = Guid.NewGuid().ToString("N");
            settings.Accounts.Add(new Account { Id = id, Name = name, ConfigDirectory = Path.Combine(Core.DataDirectory, "accounts", id), Folder = Directory.Exists(folder.Text) ? folder.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            Core.Normalize(settings); Reload(id); persist(settings, settingsFile);
            feedback.Text = settings.Mode == "desktop" ? "Account added. Open Claude Desktop and sign in to the matching account." : "Account added. Click Sign in and choose the matching account in your browser.";
        }
        void RenameAccount() { if (selected == null) return; var name = AskName("Rename account", selected.Name); if (name == null) return; selected.Name = name; Reload(selected.Id); persist(settings, settingsFile); }
        void RemoveAccount()
        {
            if (selected == null) return;
            CheckActivity();
            if (activity.Problem != null || activity.AccountIds.Contains(selected.Id)) throw new Exception("Close this account's CLI windows and quit Desktop before removing it. " + activity.Problem);
            if (MessageBox.Show(this, "Remove " + selected.Name + " from the launcher? Its Claude login and files will stay on disk.", "Remove account", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            settings.Accounts.Remove(selected); Reload(null); persist(settings, settingsFile);
        }
        void Browse() { using (var picker = new FolderBrowserDialog { Description = "Choose the project folder for Claude Code", SelectedPath = folder.Text, ShowNewFolderButton = true }) if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath; }
        void SaveBrowserChoice()
        {
            if (changingBrowser || selected == null || browserChoice.SelectedItem == null) return;
            HandleAction(delegate {
                selected.LoginBrowser = ((BrowserChoice)browserChoice.SelectedItem).Id;
                changingBrowser = true;
                try { privateWindow.Enabled = selected.LoginBrowser != "default"; if (!privateWindow.Enabled) privateWindow.Checked = false; }
                finally { changingBrowser = false; }
                selected.LoginPrivateWindow = privateWindow.Checked;
                persist(settings, settingsFile);
            });
        }
        void OpenLoginLink()
        {
            if (selected == null) return;
            var account = selected;
            bool desktop = settings.Mode == "desktop";
            using (var dialog = new Form { Text = "Open login link for " + account.Name, ClientSize = new Size(630, 300), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font, BackColor = Background, ForeColor = Ink, AutoScaleMode = AutoScaleMode.Dpi })
            {
                var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
                content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(content);
                content.Controls.Add(new Label { Text = "Open in " + LoginBrowser.Choice(account.LoginBrowser).Name + (account.LoginPrivateWindow ? " (private window)" : "") + ".\r\n\r\n" + (desktop ? "Start SSO in the selected Desktop window. Copy the initial login URL from the browser before completing sign-in." : "Start CLI sign-in and copy the login URL shown in the terminal.") + " Paste it below, then choose the identity for " + account.Name + ". Do not paste the return link or a code.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Muted });
                var input = new TextBox { Dock = DockStyle.Top, MaxLength = 16000, BackColor = Surface, ForeColor = Ink, Margin = new Padding(0, 16, 0, 16), AccessibleName = "Initial HTTPS login URL" };
                content.Controls.Add(input);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                var launch = Button("Open in browser", delegate {
                    // Recheck after the modal dialog: another process may have closed or started.
                    using (var gate = new LaunchGate("Launch"))
                    {
                        CheckActivity();
                        if (!activity.CanOpen(account.Id) || !activity.AccountIds.Contains(account.Id)) throw new InvalidOperationException("Keep this account's Claude session open before opening its login link.");
                        if (desktop && (!routingReady() || DesktopLogin.LoginTarget(settings, desktopLogin.Read(), activity, DateTime.UtcNow).Id != account.Id))
                            throw new InvalidOperationException("Start a fresh Desktop sign-in for this account first.");
                        var info = LoginBrowser.LaunchInfo(account, input.Text, findBrowser);
                        try { using (var process = Process.Start(info)) { } }
                        catch { throw new InvalidOperationException("The selected browser could not be opened. Check that it is installed and try again."); }
                    }
                    input.Clear(); dialog.DialogResult = DialogResult.OK;
                });
                StylePrimary(launch); launch.Margin = Padding.Empty;
                var cancel = Button("Cancel", delegate { }); cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(launch); buttons.Controls.Add(cancel); content.Controls.Add(buttons);
                dialog.AcceptButton = launch; dialog.CancelButton = cancel; dialog.Shown += delegate { input.Focus(); };
                dialog.ShowDialog(this); input.Clear();
            }
        }
        void Run(bool login)
        {
            if (selected == null) return;
            bool desktopModeSelected = settings.Mode == "desktop";
            if (desktopModeSelected && login)
            {
                bool pending; try { pending = desktopLogin.Read() != null; } catch { pending = true; }
                if (pending)
                {
                    using (var gate = new LaunchGate("Launch")) desktopLogin.Cancel();
                    feedback.Text = "Desktop sign-in cancelled. Start a fresh sign-in when ready; old callback links will not be forwarded.";
                    UpdateActivity(); return;
                }
            }
            if (desktopModeSelected && !routingReady() && (login || !Core.UsesDefaultDesktop(selected)))
            {
                if (MessageBox.Show(this, DesktopLogin.SetupInstructions + "\n\nContinue to register Claude Selector as an available handler and open Settings?", "Set up Desktop sign-in", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                DesktopLogin.RegisterHandler(Application.ExecutablePath); DesktopLogin.OpenSettings();
                feedback.Text = DesktopLogin.SetupInstructions; return;
            }
            using (var gate = new LaunchGate("Launch"))
            {
            CheckActivity();
            if (!activity.CanOpen(selected.Id)) throw new Exception(activity.Message(settings));
            if (login && !desktopModeSelected && activity.AccountIds.Count != 0) throw new Exception("Close active Claude sessions before changing a login.");
            RememberFolder();
            if (desktopModeSelected)
            {
                if (login && MessageBox.Show(this, "Sign in to " + selected.Name + "\n\n" + DesktopLogin.BrowserInstructions + "\n\nThe return link will be accepted for 10 minutes. You will confirm the destination before it is forwarded.", "Desktop SSO sign-in", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                UseWaitCursor = true;
                try
                {
                    string desktop = Core.FindDesktop();
                    if (desktop == null) throw new Exception("Claude Desktop was not found. Install the official Claude Desktop app, then try again.");
                    // Detection can take time. Recheck immediately before starting any process.
                    CheckActivity();
                    if (!activity.CanOpen(selected.Id)) throw new Exception(activity.Message(settings));
                    if (!Core.UsesDefaultDesktop(selected)) Directory.CreateDirectory(selected.DesktopDirectory);
                    persist(settings, settingsFile);
                    if (login) desktopLogin.Begin(selected, DateTime.UtcNow);
                    try { using (var process = Process.Start(Core.DesktopLaunchInfo(selected, desktop))) { } }
                    catch { if (login) desktopLogin.Cancel(); throw; }
                }
                finally { UseWaitCursor = false; }
            }
            else
            {
                if (!Directory.Exists(selected.Folder)) throw new Exception("Choose an existing working folder first.");
                selected.Folder = Path.GetFullPath(selected.Folder);
                string cli = Core.FindClaude();
                if (cli == null) throw new Exception("Claude Code CLI was not found. Install it so that the claude command works, then reopen this app.");
                Directory.CreateDirectory(selected.ConfigDirectory); persist(settings, settingsFile);
                // Start the marked shell directly so there is no asynchronous terminal dispatch gap.
                var info = Core.LaunchInfo(selected, cli, login, null); info.UseShellExecute = false;
                using (var process = Process.Start(info)) { }
            }
            activity.AccountIds.Add(selected.Id); UpdateActivity();
            feedback.Text = desktopModeSelected && login ? "Start SSO in " + selected.Name + ". Choose your matching browser identity. Keep this Desktop profile open until the return link completes." : "Opened " + selected.Name + ". Close its CLI windows and quit Desktop (including the tray) before opening another account.";
            }
        }
    }
    public static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--handle-desktop-link")
            {
                if (args.Length == 2) DesktopLogin.HandleLink(args[1]);
                else MessageBox.Show("Invalid Desktop return link. Start a fresh sign-in from the selector.", "Claude Selector");
                return;
            }
            if (args.Length == 1 && args[0] == "--register-desktop-links")
            {
                try { DesktopLogin.RegisterHandler(Application.ExecutablePath); }
                catch { Environment.ExitCode = 1; }
                return;
            }
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\ClaudeSelector-" + Environment.UserName, out created))
            {
                if (!created) { MessageBox.Show("Claude Selector is already open. Select its window from the taskbar.", "Claude Selector"); return; }
                try { Application.Run(new MainForm(Core.Load(Path.Combine(Core.DataDirectory, "settings.json")), args.Length > 0 ? args[0] : null)); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Claude Selector", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
