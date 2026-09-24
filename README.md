# Claude Selector

A small native Windows desktop launcher for **Claude Code CLI**. No Claude Desktop integration and no extra runtime installation on Windows 10/11.

## Use

1. Double-click `dist\ClaudeSelector.exe`.
2. **Current CLI account** uses the existing Claude configuration (the inherited `CLAUDE_CONFIG_DIR`, or `%USERPROFILE%\.claude`). Rename it to Work or Personal as appropriate.
3. Click **Add account**, give it a name, then **Sign in / switch login**. Complete Claude's official login in the browser, choosing the intended account. Claude starts after successful sign-in. New configurations may also show Claude's first-run setup.
4. Select an account and working folder, then **Open Claude Code**. The folder is remembered for that account. Multiple accounts can run at once.

The launcher stays open for additional sessions. Windows Terminal is preferred, with a PowerShell window fallback. The shell remains open after Claude exits so errors are visible. You can also pass a project folder: `ClaudeSelector.exe "D:\Projects\My project"`.

Account names are your labels, not verified email addresses. Use `/status` in Claude to confirm the signed-in identity. Sign-in is interactive and must be completed by you. Normal `claude` commands outside this launcher continue to use their usual configuration.

## Account storage

The existing default CLI account is launched with `CLAUDE_CONFIG_DIR` unset, matching a normal terminal. Explicitly setting it to `.claude` changes Claude's global setup-state location and can incorrectly show onboarding again. Added accounts still use explicit separate directories.

Preferences: `%LOCALAPPDATA%\ClaudeSelector\settings.json` (previous save in `.bak`). Added accounts: `%LOCALAPPDATA%\ClaudeSelector\accounts\<id>`. Each added account gets its own `CLAUDE_CONFIG_DIR`, separating credentials, user settings, history, and user MCP configuration. Project-local configuration and machine-managed policies still apply. The app does not read or copy credential contents; Claude handles authentication and storage.

For added accounts, the child shell clears inherited API keys, OAuth token overrides, base URL, Anthropic profile, and cloud-provider selection variables so subscription login can be used. The current CLI account preserves these variables for existing workplace configurations. This launcher targets Claude subscription accounts; added profiles do not automatically copy corporate gateway or user settings. Configure those with your organization if needed.

Removing an account only removes its launcher entry; it neither deletes files nor revokes credentials. To sign out, run `/logout` in that account's Claude session before removing it. Avoid switching the login of a profile while other sessions using that same profile are running.

## Build and verify

Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` to build the executable with the Windows .NET Framework compiler. No NuGet dependencies or network downloads are needed.

The executable embeds `assets\ClaudeSelector.ico`, also used for the window and taskbar icon. The icon contains 16–256 pixel sizes. To regenerate it from the source artwork, run `powershell -ExecutionPolicy Bypass -File .\assets\build-icon.ps1`, then rebuild the app. Artwork generation details are in `assets\icon-prompt.md`.

Run `powershell -ExecutionPolicy Bypass -File .\test.ps1` for persistence, actual shell launch with a mock CLI, environment isolation, special-character paths, failed login handling, and a rendered UI preview. Tests do not access real Claude credentials or start a real Claude session.

The executable is locally built and unsigned. You can create a Windows desktop shortcut to it using Explorer's **Send to > Desktop (create shortcut)**.

Official reference: [Claude environment variables](https://code.claude.com/docs/en/env-vars), [authentication](https://code.claude.com/docs/en/authentication).
