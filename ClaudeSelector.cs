using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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
        public override string ToString() { return Name; }
    }
    public sealed class Settings
    {
        public List<Account> Accounts { get; set; }
        public string SelectedId { get; set; }
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
                return new Settings { SelectedId = "existing", Accounts = new List<Account> {
                    new Account { Id = "existing", Name = "Current CLI account", ConfigDirectory = Path.GetFullPath(config), Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                }};
            }
            var result = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(file));
            if (result == null || result.Accounts == null || result.Accounts.Any(a => a == null || String.IsNullOrWhiteSpace(a.Id) || String.IsNullOrWhiteSpace(a.Name) || String.IsNullOrWhiteSpace(a.ConfigDirectory)) || result.Accounts.Select(a => a.Id).Distinct().Count() != result.Accounts.Count)
                throw new InvalidDataException("The saved account list is invalid. Restore settings.json from settings.json.bak in " + Path.GetDirectoryName(file));
            return result;
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
            var script = new StringBuilder("$ErrorActionPreference = 'Stop'; try { ");
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
    }
    public sealed class MainForm : Form
    {
        readonly string settingsFile = Path.Combine(Core.DataDirectory, "settings.json");
        readonly Settings settings;
        readonly ListBox accounts = new ListBox();
        readonly TextBox folder = new TextBox();
        readonly Label detail = new Label();
        readonly Label feedback = new Label();
        readonly List<Button> selectionButtons = new List<Button>();
        Account selected;
        static readonly Color Background = Color.FromArgb(38, 38, 36);
        static readonly Color Surface = Color.FromArgb(48, 48, 46);
        static readonly Color Border = Color.FromArgb(76, 75, 70);
        static readonly Color Ink = Color.FromArgb(240, 238, 230);
        static readonly Color Muted = Color.FromArgb(177, 175, 165);
        static readonly Color Accent = Color.FromArgb(217, 119, 87);

        public MainForm(Settings state, string initialFolder)
        {
            settings = state;
            Text = "Claude Selector"; StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(740, 640); MinimumSize = new Size(680, 650);
            BackColor = Background; ForeColor = Ink;
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, RowCount = 10 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles[3] = new RowStyle(SizeType.Percent, 100);
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Choose your Claude account", Font = new Font("Georgia", 23), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            layout.Controls.Add(new Label { Text = "Your accounts. Your projects. One terminal away.", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 26) });
            layout.Controls.Add(new Label { Text = "ACCOUNTS", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 9), Margin = new Padding(0, 0, 0, 10) });
            accounts.Dock = DockStyle.Fill; accounts.BorderStyle = BorderStyle.None; accounts.ItemHeight = 52; accounts.DrawMode = DrawMode.OwnerDrawFixed;
            accounts.BackColor = Surface; accounts.ForeColor = Ink; accounts.IntegralHeight = false; accounts.Margin = Padding.Empty;
            accounts.AccessibleName = "Accounts";
            accounts.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                if (e.Index < 0) return;
                bool active = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(active ? Color.FromArgb(69, 53, 46) : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (active) using (var brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, e.Bounds.X, e.Bounds.Y + 10, 3, e.Bounds.Height - 20);
                TextRenderer.DrawText(e.Graphics, accounts.Items[e.Index].ToString(), e.Font, new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - 32, e.Bounds.Height), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.DrawFocusRectangle();
            };
            accounts.SelectedIndexChanged += delegate { ChangeSelection(); };
            accounts.DoubleClick += delegate { Run(false); };
            layout.Controls.Add(accounts);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 12) };
            actions.Controls.Add(Button("+ Add account", AddAccount));
            actions.Controls.Add(ForSelection("Rename", RenameAccount));
            actions.Controls.Add(ForSelection("Remove", RemoveAccount));
            layout.Controls.Add(actions);
            detail.Dock = DockStyle.Fill; detail.ForeColor = Muted; detail.Font = new Font("Segoe UI", 9); detail.AutoSize = true; detail.Margin = new Padding(0, 0, 0, 24);
            layout.Controls.Add(detail);
            layout.Controls.Add(new Label { Text = "WORKING FOLDER", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 9), Margin = new Padding(0, 0, 0, 10) });
            var location = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 24) };
            location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folder.Anchor = AnchorStyles.Left | AnchorStyles.Right; folder.AccessibleName = "Working folder";
            folder.BackColor = Surface; folder.ForeColor = Ink; folder.BorderStyle = BorderStyle.None; folder.Margin = new Padding(12, 0, 12, 0);
            var folderFrame = new Panel { Dock = DockStyle.Fill, Height = 40, BackColor = Surface, Margin = new Padding(0, 0, 10, 0), MinimumSize = new Size(0, 40) };
            folderFrame.Controls.Add(folder);
            folderFrame.Resize += delegate { folder.SetBounds(12, (folderFrame.ClientSize.Height - folder.PreferredHeight) / 2, Math.Max(0, folderFrame.ClientSize.Width - 24), folder.PreferredHeight); };
            var browse = ForSelection("Browse...", Browse); browse.Margin = Padding.Empty;
            location.Controls.Add(folderFrame, 0, 0); location.Controls.Add(browse, 1, 0); layout.Controls.Add(location);
            var launch = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 14) };
            launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var open = ForSelection("Open Claude Code", delegate { Run(false); }); StylePrimary(open);
            open.Anchor = AnchorStyles.Left;
            var signIn = ForSelection("Sign in / switch login", delegate { Run(true); });
            signIn.Anchor = AnchorStyles.Right; signIn.Margin = Padding.Empty; signIn.MinimumSize = new Size(0, 32);
            signIn.Padding = new Padding(10, 3, 10, 3); signIn.Font = new Font("Segoe UI", 9);
            launch.Controls.Add(open, 0, 0); launch.Controls.Add(signIn, 1, 0);
            layout.Controls.Add(launch); AcceptButton = open;
            feedback.Dock = DockStyle.Fill; feedback.ForeColor = Muted; feedback.Font = new Font("Segoe UI", 9); feedback.AutoSize = true; feedback.Margin = Padding.Empty;
            feedback.Text = "Add an account, then sign in once. Claude remembers each login."; layout.Controls.Add(feedback);
            FormClosing += delegate { try { RememberFolder(); Core.Save(settings, settingsFile); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save preferences"); } };
            Reload(settings.SelectedId);
            if (!String.IsNullOrWhiteSpace(initialFolder)) folder.Text = Path.GetFullPath(initialFolder);
        }
        Button Button(string text, Action action)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 40), Padding = new Padding(14, 6, 14, 6), FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(61, 61, 57);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 72, 66);
            b.Click += delegate { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Claude Selector", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
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
            settings.SelectedId = selected == null ? null : selected.Id;
            detail.Text = selected == null ? "Add an account to get started." : selected.Id == "existing" ? "Uses your existing CLI login and settings. Rename this to Work or Personal." : "Separate Claude login and settings. Use Sign in to connect this account.";
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
            Reload(id); Core.Save(settings, settingsFile); feedback.Text = "Account added. Click Sign in and choose the matching account in your browser.";
        }
        void RenameAccount() { if (selected == null) return; var name = AskName("Rename account", selected.Name); if (name == null) return; selected.Name = name; Reload(selected.Id); Core.Save(settings, settingsFile); }
        void RemoveAccount()
        {
            if (selected == null) return;
            if (MessageBox.Show(this, "Remove " + selected.Name + " from the launcher? Its Claude login and files will stay on disk.", "Remove account", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            settings.Accounts.Remove(selected); Reload(null); Core.Save(settings, settingsFile);
        }
        void Browse() { using (var picker = new FolderBrowserDialog { Description = "Choose the project folder for Claude Code", SelectedPath = folder.Text, ShowNewFolderButton = true }) if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath; }
        void Run(bool login)
        {
            if (selected == null) return;
            RememberFolder();
            if (!Directory.Exists(selected.Folder)) throw new Exception("Choose an existing working folder first.");
            selected.Folder = Path.GetFullPath(selected.Folder);
            string cli = Core.FindClaude();
            if (cli == null) throw new Exception("Claude Code CLI was not found. Install it so that the claude command works, then reopen this app.");
            Directory.CreateDirectory(selected.ConfigDirectory); Core.Save(settings, settingsFile);
            string terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (!File.Exists(terminal)) terminal = null;
            try { Process.Start(Core.LaunchInfo(selected, cli, login, terminal)); }
            catch (System.ComponentModel.Win32Exception) { if (terminal == null) throw; Process.Start(Core.LaunchInfo(selected, cli, login, null)); }
            feedback.Text = "Opened " + selected.Name + ". You can launch another account alongside it.";
        }
    }
    public static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
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
