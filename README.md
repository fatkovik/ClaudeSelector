# Claude Selector

A native Windows launcher for Claude Code CLI and Claude Desktop. It keeps one shared account list and allows one account at a time to run. Works on Windows 10/11 without installing another runtime.

## Getting started

1. Run `dist\ClaudeSelector.exe`.
2. Choose **Claude Code CLI** or **Claude Desktop**.
3. **Default** reuses this PC's existing Claude profiles and sign-ins. Choose **Add account** to create a separate account.
4. Sign in:
   - **CLI:** Select the account and click **Sign in / switch login**. Complete Claude's sign-in, then select a project folder and click **Open Claude Code**.
   - **Desktop:** Click **Set up Desktop sign-in** once and select **Claude Selector** as the Windows handler for **CLAUDE** links. Then click **Sign in to Desktop**, start SSO in that Desktop window, and complete sign-in in the browser.
5. Confirm the signed-in identity in Claude (`/status` for CLI). Account names in the selector are labels, not verified identities.

Close the CLI terminal, or quit Claude Desktop including its tray process, before switching to another account. The selector does not close sessions for you. You can also pass a project folder at launch: `ClaudeSelector.exe "D:\Projects\My project"`.

## Browser sign-in

Expand **Browser options (optional)** to choose Windows default, Edge, Chrome, Firefox, or Brave, and optionally open a private window. The choice is saved per account. Private windows may share an existing private session, so close other private windows first if needed.

The browser choice applies to **Open login link...** only. To use it, start sign-in, copy the initial HTTPS login URL from the CLI terminal or browser, then paste it into **Open login link...** before completing login. Choose the matching identity in the browser. Claude Desktop's own SSO button still opens Windows' default browser.

For Desktop SSO, Windows must route `claude://` return links to Claude Selector. **Set up Desktop sign-in** opens Windows Default apps; select **Claude Selector** for **CLAUDE**. Keep the intended Desktop profile open during sign-in and confirm its name in the selector when the browser returns. The pending destination expires after 10 minutes. To restore the normal handler, select **Claude** for **CLAUDE** in Windows Default apps. Keep the selector executable in the same location after setup.

## Accounts and local data

Default uses Claude's normal CLI and Desktop profiles. Added CLI accounts are stored under `%LOCALAPPDATA%\ClaudeSelector\accounts\<id>`; added Desktop profiles are stored under `%APPDATA%\ClaudeSelector-<id>`. Selector preferences are in `%LOCALAPPDATA%\ClaudeSelector\settings.json`. The temporary Desktop sign-in record contains the destination and expiry, not credentials.

Claude Selector does not collect or send account details, login links, credentials, or usage data to its developer. It has no telemetry or upload service. Sign-in and Claude use connect to Anthropic and your identity provider through Claude and your browser. The selector does not read or copy Claude credential files; Claude stores sign-in data in its local profiles.

Local files follow Windows permissions and are not encrypted by the selector. **Remove** opens a dialog; by default it keeps local data. Select **Also delete this account's local data** to remove that added account's separate profiles and saved logins. This does not revoke server-side credentials. The shared Default profile and project folders are not deleted.

## Build

Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` to build with the Windows .NET Framework compiler. Run `powershell -ExecutionPolicy Bypass -File .\test.ps1` to run the automated checks. Tests use mock Claude processes and do not access real credentials or start real Claude sessions.

Official references: [Claude authentication](https://code.claude.com/docs/en/authentication) · [Claude environment variables](https://code.claude.com/docs/en/env-vars)
