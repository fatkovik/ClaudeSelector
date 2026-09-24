# Claude Selector

A small native Windows launcher for **Claude Code CLI and Claude Desktop**, with one shared account list and **one active account at a time**. No extra runtime installation on Windows 10/11.

## Use

1. Double-click `dist\ClaudeSelector.exe`.
2. Use the visible **Claude Code CLI / Claude Desktop** switch at the top. Both modes show the same accounts and preserve the same selection. The last mode is remembered.
3. On first launch, the account list contains **Default**. It uses the existing CLI configuration (the inherited `CLAUDE_CONFIG_DIR`, or `%USERPROFILE%\.claude`) and **this PC's default Claude Desktop profile**, reusing whichever logins are already saved in those apps, if any. The entry is present even when neither app is signed in; complete sign-in in the app you want to use. This behavior follows the original account ID even if you rename it. Previously saved account names, such as Personal, are preserved. Added accounts use separate profiles.
4. Click **Add account** and give it a name. In CLI mode, use **Sign in / switch login** and complete Claude's official sign-in. In Desktop mode, complete **Set up Desktop sign-in** once, then use **Sign in to Desktop** before starting SSO inside Claude. Added accounts need separate initial CLI and Desktop sign-ins. Default reuses the existing default Desktop login; it needs sign-in only if that default profile is signed out. The launcher does not transfer credentials or verify that CLI and Desktop identities match.
5. In CLI mode, select a working folder and choose **Open Claude Code**. In Desktop mode, the folder section is hidden and **Open Claude Desktop** opens that account's saved profile. The CLI folder is preserved when switching modes.

The launcher stays open. The same account may use CLI and Desktop together, but another account's launch button is disabled until the active account closes. CLI launches start PowerShell directly, using the Windows-configured console host. This avoids delayed Windows Terminal dispatch that could otherwise race with account switching. The shell remains open after Claude exits so errors are visible; **close that terminal window to release its account lock**. For Desktop, **quit Claude completely, including its tray process**. No running session is terminated automatically.

You can also pass a project folder: `ClaudeSelector.exe "D:\Projects\My project"`. This selects CLI mode, regardless of the last saved mode.

## Active account detection

The selector reads Windows process metadata, not credential files. Marked CLI shells and Desktop profile arguments identify the active account, so reopening the selector recovers the restriction without relying on a saved PID or a stale lock file. A default Desktop process also counts as the Default entry (or its renamed label), including when opened from the normal Claude shortcut. Its executable location and Desktop bundle distinguish it from an unmarked CLI process. Desktop background processes count as active. The same restriction applies to double-click launches. Removing an active account and changing a CLI login while sessions are running are blocked.

If process access fails, or a running Claude process cannot be mapped to an account, launches are blocked with an explanation. Close older CLI sessions or Desktop instances opened outside the selector and try again. The selector cannot prevent you from independently launching another account outside it. Account names remain labels; manually signing into a different identity inside Claude does not change the selector's account label.

## Desktop profiles

The official Claude Desktop app must be installed. The launcher resolves the current Windows package at launch time so app updates do not leave it pointing at an old version. Older per-user installations under `%LOCALAPPDATA%\AnthropicClaude` are also detected.

Default launches Desktop without a `--user-data-dir` argument and clears inherited `CLAUDE_USER_DATA_DIR` and `CLAUDE_CONFIG_DIR` overrides. Claude therefore selects its own normal profile, including any Windows package path handling, and reuses any login already saved there. No credentials or profile files are copied. If an earlier selector build created a separate Desktop profile for this entry, its files remain untouched; the stored path is retained so any still-running old instance can be recognized.

Added accounts use `%APPDATA%\ClaudeSelector-<id>` for Desktop data, with `--user-data-dir` and `CLAUDE_USER_DATA_DIR` pointing at that directory. Renaming the account does not rename its profile. The account's CLI configuration is also passed as `CLAUDE_CONFIG_DIR` for embedded Code. Desktop launches clear inherited credential/provider overrides so another shell's account cannot override the selected profile. Desktop mode does not require the CLI executable or a valid working folder.

## Desktop SSO / browser sign-in

### Choose a login browser (CLI and Desktop)

Browser controls are optional and tucked inside **Browser options (optional)**, collapsed each time the selector opens. The list detects installed supported browsers on the current PC (Edge, Chrome, Firefox, and Brave), plus **Windows default browser**. It refreshes when selecting an account or reopening the selector. A previously saved browser that is no longer installed is marked **unavailable**; it never silently switches to another browser. Other browsers can be used through Windows default if they are the system default. Expand the section to request a **Private window** or use **Open login link...**. Browser preferences are saved per account and shared across CLI and Desktop; collapsing the section preserves them. You can leave it closed for normal sign-in. Close existing private windows first if they are already signed into another identity; private windows can share a session, and enterprise SSO may still offer your Windows identity.

**The browser choice applies only to Open login link.** Clicking SSO inside Claude Desktop still opens the Windows default browser, even when a different browser is selected here. The selector opens a copied login URL in your selected browser; it does **not** intercept Claude's automatic browser launches or change Windows' default browser. The inspected Desktop build uses Windows' default browser directly, and an automatic per-app override is not available here.

1. Select the account, browser and optional private window, then start **Sign in / switch login** (CLI) or **Sign in to Desktop** (Desktop).
2. Copy the **initial HTTPS login URL** from the CLI terminal or the browser address bar opened by Desktop, before completing login there.
3. Click **Open login link...**, paste it and click **Open in browser**. Choose the matching identity and complete Claude's normal flow. Paste a returned code into the CLI if it asks for one. For Desktop, confirm the account in the selector's return-link dialog.

The link action requires that account's Claude session to stay open; Desktop additionally requires a current sign-in destination. Missing browsers show an error with no silent fallback. Browser discovery reads standard Windows App Paths and installation locations; it does not launch executables from the project folder. Only HTTPS URLs are accepted, including enterprise SSO domains. The selector does not save or log pasted URLs or read your clipboard automatically. A browser's own history and policies still apply.

### Route Desktop return links

Browser identity and Desktop callback routing are separate. Your browser may already be signed into Personal; the selector cannot choose your SSO identity for you. Windows also normally sends `claude://` return links to the default Desktop profile. To prevent that:

1. In Desktop mode, click **Set up Desktop sign-in**. The selector registers itself as an available handler for Claude links and opens Windows Default apps. Select **Claude Selector** for the **CLAUDE** link type. On older Windows versions, search for `claude` in Default apps. The application checks Windows' effective association; merely registering it is not enough.
2. Quit unintended Claude instances, including the default Personal window opened by a failed callback. No sessions are closed automatically.
3. Select your Work entry and click **Sign in to Desktop**. Start a **fresh** SSO sign-in inside that Desktop window.
4. Use **Open login link...** to open the initial URL in the account's selected browser, then choose the matching **Work identity**. A private window can help if the regular browser keeps choosing Personal. Do not reuse a previously failed return link.
5. Keep that Desktop window open. When the browser returns, confirm **Send this browser sign-in to Work?** in the selector's callback dialog. Cancel if the browser authenticated the wrong account. The selector passes the original return link to the same profile without interpreting or storing its credentials.

The pending destination lasts ten minutes and is consumed once. **Cancel Desktop sign-in** removes it. Missing, expired, corrupt, cancelled, or conflicting destinations are rejected; login links never silently fall back to Default. Highlighting a different account does not change a pending destination. If the selector is closed, its registered callback entry point can still handle the return using the saved pending destination. A still-running intended Desktop process is required. Callback handling and launches share a process mutex to enforce the account restriction.

Registration adds only the selector's own per-user ProgID and capabilities. It does not alter Claude's profile files, the default browser, Desktop shortcuts, or Windows' protected `UserChoice`. Windows asks you to choose the link handler. Keep the registered selector executable at the same location; run setup again if you move it. To restore normal link handling, choose **Claude** for the CLAUDE link type in Windows Default apps.

Once enabled, sign-ins for **Default** also need to be started with **Sign in to Desktop** in the selector. Normal supported navigation links go to Default, subject to the active-account restriction. Unsupported links are rejected. Opening the regular Claude Desktop shortcut still opens the native default profile. Added accounts' Desktop launch buttons remain disabled until routing is configured, so a fresh SSO flow cannot be started through the launcher with known-unsafe routing.

The pending file `%LOCALAPPDATA%\ClaudeSelector\desktop-sign-in.json` stores only an account ID, profile path, expiry and random intent ID. Callback URLs, authorization codes, and tokens are not written to logs or disk by the selector; they are passed directly to the official Desktop process. Desktop validates the authentication response. The selector confirms the destination but does not independently verify the browser identity or bind the identity provider's authentication state.

Desktop profile isolation is a compatibility mechanism, not an official multi-account API. Real SSO completion requires a manual check with your provider and installed Desktop version. The selector does not promise independent Cowork VMs.

Account names are your labels, not verified email addresses. Use `/status` in Claude to confirm the signed-in identity. Sign-in is interactive and must be completed by you. Normal `claude` commands outside this launcher continue to use their usual configuration.

## Account storage

The existing default CLI account is launched with `CLAUDE_CONFIG_DIR` unset, matching a normal terminal. Explicitly setting it to `.claude` changes Claude's global setup-state location and can incorrectly show onboarding again. Added accounts still use explicit separate directories.

Preferences: `%LOCALAPPDATA%\ClaudeSelector\settings.json` (previous save in `.bak`). Existing settings migrate automatically, preserving CLI account IDs, configuration paths, selected account, and folders. Added CLI accounts: `%LOCALAPPDATA%\ClaudeSelector\accounts\<id>`. Each added account gets its own `CLAUDE_CONFIG_DIR`, separating credentials, user settings, history, and user MCP configuration. Project-local configuration and machine-managed policies still apply. The app does not read or copy credential contents; Claude handles authentication and storage.

For added accounts, the child shell clears inherited API keys, OAuth token overrides, base URL, Anthropic profile, and cloud-provider selection variables so subscription login can be used. The current CLI account preserves these variables for existing workplace configurations. This launcher targets Claude subscription accounts; added profiles do not automatically copy corporate gateway or user settings. Configure those with your organization if needed.

Click **Remove** to open a confirmation dialog for the selected account. **Cancel** keeps the account unchanged. By default, removal keeps its local data. Check **Also delete this account's local data** to permanently delete its separate CLI and Desktop profiles, including saved logins, settings and history. This does not revoke server-side credentials. The shared Default profile and custom profile locations cannot be deleted here; the dialog explains when the checkbox is unavailable. Active accounts must be closed before removal. Project folders outside the account profiles are kept.

## Privacy and local data

Claude Selector does not collect or send your account details, login links, credentials, or usage data to its developer. It has no telemetry or remote upload service. Its settings and account-routing state are stored on this PC. Login is handled interactively by the official Claude CLI/Desktop and your browser; those apps communicate with Anthropic and your identity provider as part of sign-in and Claude use.

The selector does not read or copy Claude credential files. Claude stores its own sign-in data in the local profile used by that account, much like Claude normally stores accounts on this PC. The selector's own preferences contain account labels and IDs, profile/configuration paths, browser choices, and working folders, but not passwords, tokens, or pasted login URLs. A temporary Desktop sign-in record contains only the selected account/profile, an expiry, and a random intent ID. See **Account storage** and **Desktop SSO / browser sign-in** above for the exact locations and behavior.

Local storage is not encryption or a separate Windows security boundary: the files are subject to Windows permissions and may be accessible to other programs running as your Windows user or to administrators. Removing an account keeps its local Claude profiles unless you select the data-deletion checkbox. Use Claude's sign-out controls to sign out before removal if needed.

## Build and verify

Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` to build the executable with the Windows .NET Framework compiler. No NuGet dependencies or network downloads are needed.

The executable embeds `assets\ClaudeSelector.ico`, also used for the window and taskbar icon. The icon contains 16–256 pixel sizes. To regenerate it from the source artwork, run `powershell -ExecutionPolicy Bypass -File .\assets\build-icon.ps1`, then rebuild the app. Artwork generation details are in `assets\icon-prompt.md`.

Run `powershell -ExecutionPolicy Bypass -File .\test.ps1` for persistence and migration, actual shell launch with a mock CLI, environment isolation, special-character paths, failed login handling, account-lock scenarios, SSO routing and expiry/cancellation/replay rejection, and rendered previews of both modes and the blocked/setup states. Tests do not register a protocol handler, access real Claude credentials, or start real Claude sessions. UI tests inject process snapshots, routing readiness, and an in-memory preference saver, so they never write the user's settings.

Use `-SandboxSafe` to skip only the atomic file-replacement check when Windows sandbox permissions prevent it; remaining persistence checks create fresh files in `test-output`. Use `-LiveProcessCheck` to additionally validate detection and exit of a real marked mock CLI shell through Windows process queries (requires WMI access).

The executable is locally built and unsigned. You can create a Windows desktop shortcut to it using Explorer's **Send to > Desktop (create shortcut)**.

Official reference: [Claude environment variables](https://code.claude.com/docs/en/env-vars), [authentication](https://code.claude.com/docs/en/authentication).
