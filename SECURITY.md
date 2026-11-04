# Security

## Reporting a vulnerability

Please **do not** open a public issue for a security problem. Use GitHub's private
[security advisory](../../security/advisories/new) form on this repository instead. You should hear
back within a week.

## What this add-in can reach, and how

### Sign-in: the Revit session, no credentials of its own
- The repo holds no APS client ID, secret or stored token.
- `Auth/SessionTokenProvider.cs` borrows the Autodesk session Revit already has, using
  **reflection on Revit's undocumented SSONET internals**.
- Each user acts as themselves and sees only the hubs and projects their own Autodesk account can
  open.
- That route is **unsupported**: a Revit update can break it. Keep the reflection in
  `SessionTokenProvider.cs` only.

### Local token pipe
- `Auth/TokenBrokerServer.cs` hands the token to the launcher and controller over a named pipe.
  `Mcp/McpBridge.cs` uses a pipe built the same way.
- The pipe's ACL admits **only the current Windows user**. Never loosen it: every process that can
  open the pipe can act in Forma as that user.

### Tokens never reach logs
- Logs, `RunTrace`/`surface.log`, self-tests and the diagnostics ZIP
  (`Commands/DiagnosticsReportCommand.cs`, `install/Collect-*.ps1`) record at most a token's
  length and its decoded scopes.
- A change that writes an `Authorization` header or a token string anywhere is a security bug.

### AI Bridge and AI Code (`run_code`)
- The KH-Tool AI Bridge (`Mcp/`, `src/KhMcp`) lets a local MCP client (an AI assistant) call
  KH-Tool's tools in the open Revit model.
- With **AI Code** switched on, `run_code` **compiles and runs C# the AI wrote** inside Revit, with
  the user's full rights.
- AI Code is off at every Revit start, and its ribbon toggle (KH-Tool > Setting) is the only way
  to turn it on.
- Every model-changing call is one undo step.
- Turn AI Code on only for an AI client you trust, and only while you watch it.

### `khbatchlog:` URL handler
- The installer registers `khbatchlog:` (and the 1.x `accbatchlog:`) so a notification can open
  a run log.
- The handler (`KhBatchLauncher.exe --open-log`) opens only an **existing** file with a
  report/text extension, through the shell's normal handler for that type. Keep that gate narrow.

### Autodesk Model Checker (Batch Interop)
- `Interop/` drives the installed Autodesk Model Checker **by reflection**. That is also
  unsupported and may break on any update of it.
- No Autodesk binaries are in this repository, and none may be added.

## Scope

The validity date and update check (`Update/UpdateCheck.cs`) are a convenience for published
installers, not a security boundary. The `FullUnlimited` edition removes them.
