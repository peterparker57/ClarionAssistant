# Continuation: ClarionAssistant

## Current Status

### Library CodeGraph — TESTED, WORKING
Deployed and verified. 1,082 symbols indexed from Clarion LibSrc equate files. Confirmed EVENT:, PROP:, COLOR: lookups all work via `query_codegraph` with `db_path` pointing to `C:\Clarion12\Accessory\AddIns\ClarionAssistant\ClarionLib.codegraph.db`. Shows up in `list_codegraph_databases`.

### select_procedure — TESTED, WORKING
- `select_procedure` MCP tool selects a procedure in the ClaList by name
- Uses `PostMessage` + `WM_CHAR` only
- VK_DOWN + VK_UP after typing clears the locator buffer
- Tested 2026-03-22: successfully selected "ScanClass" in the app tree
- Now includes embeditor-open guard (returns error if embeditor is active)

### open_procedure_embed — TESTED, WORKING
- Timing fix deployed and verified 2026-03-22
- 100ms per-char delay + `DoEvents()` after each character
- Tested: ScanClass opened 3 times, FillCheckBoxes opened 3 times — all correct
- Now includes embeditor-open guard (returns error if embeditor is active, tells user to close it first)

### save_and_close_embeditor — TESTED, WORKING
- Calls `IGeneratorDialog.TryClose()` on the ClaGenEditor via reflection
- Tested 2026-03-22: SolutionForm embeditor — prompted to save changes, closed successfully
- `TryClose()` triggers the save dialog then closes

### cancel_embeditor — TESTED, WORKING
- Calls `IGeneratorDialog.Discard()` then `IGeneratorDialog.TryClose()` via reflection
- Tested 2026-03-22: SolutionForm embeditor — closed silently without save prompt
- Discards changes then closes

### Embeditor lifecycle — COMPLETE
Full cycle verified: `open_procedure_embed` → edit → `save_and_close_embeditor` or `cancel_embeditor` → `open_procedure_embed` another procedure

### Embed navigation tools — TESTED, WORKING
- Tested 2026-03-22 in SolutionForm embeditor
- `next_filled_embed`: 634 → 642 (jumps between filled embeds)
- `prev_filled_embed`: 642 → 634 (goes back)
- `next_embed`: 634 → 637 (all embed points, not just filled)
- `prev_embed`: 637 → 634 (goes back)
- Also verified `insert_text_at_cursor` works inside embed points
- Implementation: reflection on SharpDevelop command classes from CommonSources.dll (`GotoNextEmbed`, `GotoPrevEmbed`, `GotoNextFilledEmbed`, `GotoPrevFilledEmbed`)

### "Create COM" toolbar button — TESTED, WORKING
Added a "Create COM" button to the ClarionAssistant toolbar that dispatches to the `/ClarionCOM` skill.
- Tested 2026-03-22: Button reads COM.ProjectsFolder setting, sends `/ClarionCOM Create a new COM control in <folder>` to Claude terminal
- ClarionCOM skill triggers correctly and begins the wizard workflow
- NOTE: AskUserQuestion must be in the settings.json allow list for the wizard selection UI to work (blocked by dontAsk mode otherwise)

**What it does:**
1. Checks if `COM.ProjectsFolder` setting is configured
2. If not, opens Settings dialog so user can set it
3. Sends `/ClarionCOM Create a new COM control in <folder>` to the Claude terminal
4. The ClarionCOM skill takes over from there

**Code locations:**
- `ClaudeChatControl.cs` — `OnCreateCom()` handler, `createComButton` in toolbar setup
- `Dialogs/ClaudeChatSettingsDialog.cs` — "COM Projects" section with `_comFolderInput` + browse button, saves as `COM.ProjectsFolder`

**What to test after deploy:**
1. Click "Create COM" — should prompt for Settings if no folder configured
2. Set a COM Projects Folder (e.g., `H:\DevLaptop\ClarionCOM`)
3. Click "Create COM" again — should send the command to Claude
4. Verify the ClarionCOM skill triggers and starts the interactive wizard
5. If the command format needs adjusting (e.g., skill doesn't trigger), tweak the command string in `OnCreateCom()`

**Design direction (discussed 2026-03-22):**
- ClarionAssistant is evolving into a Clarion-focused skill dispatcher with IDE-native UI buttons
- COM controls and addins live in centralized folders (shared across solutions)
- CodeGraph analysis stays per-solution
- Future buttons: "Create Addin", "Analyze Solution", etc.
- The COM Pane addin (`H:\DevLaptop\ClarionIdeCOMPane`) handles the "use a COM control" side — it reads from `clarion12\accessory\resources` and `bin` folders
- The `clarioncom-create` skill handles the "build a COM control" side
- This button bridges them

**Code locations (embed navigation):**
- `AppTreeService.cs` — `NavigateEmbed(string direction, bool filledOnly)` method (near end of file)
- `McpToolRegistry.cs` — four Register blocks after `cancel_embeditor`, before `open_file`

## Build notes
- ONLY build with MSBuild: `MSYS_NO_PATHCONV=1 "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" ClarionAssistant.csproj /p:Configuration=Debug /v:minimal`
- Need `MSYS_NO_PATHCONV=1` prefix in bash
- Do NOT use `dotnet build` (WebView2 resolution fails) or `deploy.ps1` (DLLs locked)
- User deploys manually (copies DLL to `C:\Clarion12\accessory\addins\ClarionAssistant`)
- Pre-existing warning in LspClient.cs (CS0414) — ignore

## Known issues / TODO
- **`currentEditorDialog` is unreliable** — always reports null even when embeditor is open. Don't use it as a success indicator.
- **IntPtr overflow pattern**: Any hex constant with bit 31 set (>=0x80000000) will overflow `IntPtr` cast on 32-bit. Always use `new IntPtr(unchecked((int)0x...))`.
- **Library CodeGraph**: Consider adding builtins.clw function/procedure declarations (not just EQUATE lines) in a future iteration.
- **ClaList is confirmed NOT a standard listbox** — LB_GETCOUNT, LB_FINDSTRINGEXACT, LB_GETTEXTLEN all return 0. May be a treeview or fully custom control. Keystroke approach is the only viable method for now.

## Locator clearing approach (important pattern)
- **VK_ESCAPE**: DO NOT USE — triggers IDE exit dialog
- **VK_DOWN + VK_UP**: Clears the locator buffer. Send after typing + settling. The selection moves down one item then back up, ending on the same item but with a clean locator.
- This must happen AFTER the WM_CHAR typing and 500ms settle, BEFORE the Embeditor button click (in OpenProcedureEmbed) or thread detach (in SelectProcedure).

## Keystroke timing (current values as of 2026-03-22)
- **100ms** between WM_CHAR posts + `DoEvents()` after each char
- **500ms** settle after all chars typed
- **100ms + DoEvents** between VK_DOWN and VK_UP for locator clear
- **300ms** final settle after locator clear
- If still too fast, try 150-200ms per char next
