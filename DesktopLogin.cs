using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeSelector
{
    public sealed class PendingDesktopLogin
    {
        public string Id { get; set; }
        public string AccountId { get; set; }
        public string DesktopDirectory { get; set; }
        public long CreatedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }
    }

    // Only account routing metadata is stored. Callback URLs/codes never go to disk or logs.
    public sealed class DesktopLoginStore
    {
        readonly string file;
        public DesktopLoginStore(string directory) { file = Path.Combine(directory, "desktop-sign-in.json"); }
        public PendingDesktopLogin Read()
        {
            if (!File.Exists(file)) return null;
            try { return new JavaScriptSerializer().Deserialize<PendingDesktopLogin>(File.ReadAllText(file)); }
            catch { throw new InvalidOperationException("The pending Desktop sign-in could not be read. Cancel it and start a new sign-in from the selector."); }
        }
        public void Begin(Account account, DateTime now)
        {
            var pending = new PendingDesktopLogin {
                Id = Guid.NewGuid().ToString("N"), AccountId = account.Id,
                DesktopDirectory = Core.UsesDefaultDesktop(account) ? "" : account.DesktopDirectory,
                CreatedUtcTicks = now.Ticks, ExpiresUtcTicks = now.AddMinutes(10).Ticks
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            // Callers hold LaunchGate. An interrupted write is rejected, never routed to Default.
            File.WriteAllText(file, new JavaScriptSerializer().Serialize(pending));
        }
        public void Cancel() { if (File.Exists(file)) File.Delete(file); }
        public void Consume(string expectedId)
        {
            var pending = Read();
            if (pending == null || pending.Id != expectedId) throw new InvalidOperationException("This sign-in was cancelled or replaced. Start a new sign-in from the selector.");
            Cancel();
        }
    }

    public sealed class LaunchGate : IDisposable
    {
        readonly Mutex mutex;
        bool held;
        public LaunchGate(string suffix)
        {
            mutex = new Mutex(false, "Local\\ClaudeSelector-" + suffix + "-" + Environment.UserName);
            try { held = mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) { mutex.Dispose(); throw new InvalidOperationException("Another Claude launch or sign-in is being handled. Try again in a moment."); }
        }
        public void Dispose() { if (held) { held = false; mutex.ReleaseMutex(); mutex.Dispose(); } }
    }

    public enum DesktopLinkKind { Login, Normal }
    public static class DesktopLogin
    {
        public const string ProgId = "ClaudeSelector.ClaudeLink";
        public const string RegisteredName = "Claude Selector";
        const string Capabilities = @"Software\ClaudeSelector\Capabilities";
        [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
        public const string SetupInstructions = "In Windows Settings, choose Claude Selector for the CLAUDE link type. If the app page does not open, search for 'claude' under Default apps. This routes browser return links; it does not change the normal Claude Desktop shortcut.";
        public const string BrowserInstructions = "In Claude Desktop, start a fresh SSO sign-in. Choose the identity matching this account; for Work, do not accept Personal. If you need another browser or a private window, expand Browser options in the selector and paste the initial login URL into Open login link. Keep the selected Desktop window open. Do not reuse the earlier failed callback.";

        [ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAssociationRegistration
        {
            void QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string query, int associationType, int level, [MarshalAs(UnmanagedType.LPWStr)] out string progId);
        }
        public static string HandlerCommand(string executable)
        {
            return Core.WindowsQuote(Path.GetFullPath(executable)) + " --handle-desktop-link \"%1\"";
        }
        public static bool IsHandlerReady(string executable)
        {
            object registration = null;
            try
            {
                registration = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7")));
                string current;
                ((IAssociationRegistration)registration).QueryCurrentDefault("claude", 1, 1, out current); // URL protocol, effective association
                if (!String.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase)) return false;
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                    return key != null && String.Equals(key.GetValue("") as string, HandlerCommand(executable), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { if (registration != null && Marshal.IsComObject(registration)) Marshal.ReleaseComObject(registration); }
        }
        public static void RegisterHandler(string executable)
        {
            string fullPath = Path.GetFullPath(executable);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The selector executable was not found.");
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                key.SetValue("", "Claude Selector sign-in links"); key.SetValue("URL Protocol", "");
                using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", Core.WindowsQuote(fullPath) + ",0");
                using (var app = key.CreateSubKey("Application")) { app.SetValue("ApplicationName", RegisteredName); app.SetValue("ApplicationDescription", "Return Claude Desktop sign-ins to the selected account."); app.SetValue("ApplicationIcon", Core.WindowsQuote(fullPath) + ",0"); }
                using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", HandlerCommand(fullPath));
            }
            using (var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities))
            {
                capabilities.SetValue("ApplicationName", RegisteredName);
                capabilities.SetValue("ApplicationDescription", "Return Claude Desktop sign-ins to the selected account.");
                capabilities.SetValue("ApplicationIcon", Core.WindowsQuote(fullPath) + ",0");
                using (var urls = capabilities.CreateSubKey("URLAssociations")) urls.SetValue("claude", ProgId);
            }
            using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) apps.SetValue(RegisteredName, Capabilities);
            // Never write UserChoice or the original Claude protocol registration. Windows owns the choice.
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        public static void OpenSettings()
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(RegisteredName)) { UseShellExecute = true });
        }
        public static DesktopLinkKind Classify(string value)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 16000 || value.Any(Char.IsControl) || value.Contains("\"") || value.Contains("\\")
                || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "claude" || !String.IsNullOrEmpty(uri.UserInfo) || uri.Port != -1)
                throw new InvalidOperationException("The Claude link is invalid. Start a fresh sign-in from the selector.");
            // Verified against the installed Desktop app's login routes. Unknown routes never fall back to Default.
            if ((uri.Host == "login" && uri.AbsolutePath == "/google-auth") || (uri.Host == "claude.ai" && (uri.AbsolutePath == "/sso-callback" || uri.AbsolutePath == "/magic-link")))
                return DesktopLinkKind.Login;
            string route = uri.AbsolutePath.Split('/').Skip(1).FirstOrDefault() ?? "";
            bool normal = uri.Host == "claude.ai" && new[] { "new", "chat", "project", "settings", "customize", "directory", "tasks", "task", "space", "code", "cowork", "local_sessions" }.Contains(route)
                || (uri.Host == "code" || uri.Host == "cowork") && (route == "new" || route == "resume" || route == "shared-artifact")
                || uri.Host == "hotkey";
            string[] sensitiveKeys = { "code", "token", "state", "ticket", "session_key", "access_token", "id_token", "refresh_token", "assertion", "samlresponse" };
            bool credentials = uri.Query.TrimStart('?').Split('&').Any(part => sensitiveKeys.Contains(Uri.UnescapeDataString(part.Split('=')[0]).ToLowerInvariant()));
            if (!normal || credentials || !String.IsNullOrEmpty(uri.Fragment)) throw new InvalidOperationException("This Claude link is not a supported navigation or sign-in link. It was not sent to any account.");
            return DesktopLinkKind.Normal;
        }
        public static Account LoginTarget(Settings settings, PendingDesktopLogin pending, Activity activity, DateTime now)
        {
            if (pending == null || String.IsNullOrWhiteSpace(pending.Id) || pending.CreatedUtcTicks < 0 || pending.ExpiresUtcTicks > DateTime.MaxValue.Ticks || now.Ticks < pending.CreatedUtcTicks || now.Ticks >= pending.ExpiresUtcTicks
                || pending.ExpiresUtcTicks - pending.CreatedUtcTicks > TimeSpan.FromMinutes(10).Ticks)
                throw new InvalidOperationException("No current Desktop sign-in is waiting for this link. Select the intended account and click Sign in to Desktop, then start a fresh SSO login.");
            var account = settings.Accounts.FirstOrDefault(a => a.Id == pending.AccountId);
            if (account == null || !String.Equals(Core.UsesDefaultDesktop(account) ? "" : account.DesktopDirectory, pending.DesktopDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sign-in account or its profile changed. Start a new sign-in from the selector.");
            if (!activity.CanOpen(account.Id)) throw new InvalidOperationException(activity.Message(settings));
            if (!activity.DesktopAccountIds.Contains(account.Id)) throw new InvalidOperationException("The Desktop window that was awaiting sign-in is no longer running. Reopen it with Sign in to Desktop and try again.");
            return account;
        }
        public static ProcessStartInfo CallbackLaunchInfo(Account account, string executable, string value)
        {
            Classify(value);
            var info = Core.DesktopLaunchInfo(account, executable);
            info.Arguments = (info.Arguments.Length == 0 ? "" : info.Arguments + " ") + Core.WindowsQuote(value);
            return info;
        }
        public static void HandleLink(string value)
        {
            // Separate callback process: bypass the selector UI's single-instance mutex.
            // Never include exception details: URI inputs may contain short-lived credentials.
            try
            {
                var kind = Classify(value);
                var store = new DesktopLoginStore(Core.DataDirectory);
                string settingsFile = Path.Combine(Core.DataDirectory, "settings.json");
                using (var callbackGate = new LaunchGate("Callback"))
                {
                    string pendingId = null;
                    if (kind == DesktopLinkKind.Login)
                    {
                        var state = Core.Load(settingsFile); var pending = store.Read();
                        var target = LoginTarget(state, pending, SessionGuard.Read(state), DateTime.UtcNow);
                        pendingId = pending.Id;
                        if (MessageBox.Show("Send this browser sign-in to " + target.Name + "?\n\nContinue only if you chose the matching account in the browser. If the browser chose Personal instead of Work, cancel and start a fresh sign-in using your work browser profile or a private window.", "Complete Desktop sign-in", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                        {
                            using (var gate = new LaunchGate("Launch")) { if (store.Read() != null && store.Read().Id == pendingId) store.Cancel(); }
                            return;
                        }
                    }
                    using (var gate = new LaunchGate("Launch"))
                    {
                        var state = Core.Load(settingsFile); var activity = SessionGuard.Read(state);
                        Account target;
                        if (kind == DesktopLinkKind.Login)
                        {
                            var pending = store.Read();
                            if (pending == null || pending.Id != pendingId) throw new InvalidOperationException("This sign-in was cancelled or replaced. Start a new sign-in from the selector.");
                            target = LoginTarget(state, pending, activity, DateTime.UtcNow);
                        }
                        else
                        {
                            target = state.Accounts.FirstOrDefault(Core.UsesDefaultDesktop);
                            if (target == null) throw new InvalidOperationException("The default account is not in the selector. Open the intended account directly.");
                            if (!activity.CanOpen(target.Id)) throw new InvalidOperationException(activity.Message(state));
                        }
                        string executable = Core.FindDesktop();
                        if (executable == null) throw new InvalidOperationException("Claude Desktop was not found. Install it and try again.");
                        // Resolve the package before consuming the single-use intent, then recheck for external launches.
                        activity = SessionGuard.Read(state);
                        if (kind == DesktopLinkKind.Login) target = LoginTarget(state, store.Read(), activity, DateTime.UtcNow);
                        else if (!activity.CanOpen(target.Id)) throw new InvalidOperationException(activity.Message(state));
                        if (kind == DesktopLinkKind.Login) store.Consume(pendingId);
                        using (var process = Process.Start(CallbackLaunchInfo(target, executable, value))) { }
                    }
                }
            }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "Desktop sign-in not forwarded", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch { MessageBox.Show("The link could not be forwarded. No fallback to the default account was attempted. Start a fresh sign-in from the selector.", "Desktop sign-in not forwarded", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
