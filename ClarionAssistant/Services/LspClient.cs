using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// LSP client that communicates with the Clarion Language Server via stdio.
    /// Sends JSON-RPC requests with Content-Length framing, reads responses.
    /// </summary>
    public class LspClient : IDisposable
    {
        private Process _process;
        private readonly object _writeLock = new object();
        private readonly object _readLock = new object();
        private int _nextId = 1;

        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // Pending responses keyed by request ID
        private readonly Dictionary<int, string> _responses = new Dictionary<int, string>();
        private readonly AutoResetEvent _responseReceived = new AutoResetEvent(false);
        private Thread _readerThread;
        private volatile bool _running;
        private Dictionary<string, object> _pendingUpdatePaths;

        // Diagnostics cache — populated by textDocument/publishDiagnostics notifications.
        // Keyed by canonical file URI (always built via FilePathToUri to avoid encoding drift).
        // LRU-bounded at 50 entries; oldest-by-LastUpdateTicks is evicted on insert.
        private const int MaxCachedDiagnosticFiles = 50;
        private readonly Dictionary<string, DiagnosticSet> _diagnostics =
            new Dictionary<string, DiagnosticSet>(StringComparer.OrdinalIgnoreCase);
        private readonly object _diagnosticsLock = new object();

        // Debug telemetry — populated by ReadLoop/stderr handler. Used by the
        // lsp_debug_status tool to expose what the server is actually sending.
        private readonly Dictionary<string, int> _notificationCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _debugLock = new object();
        private const int MaxStderrBuffer = 100;
        private readonly Queue<string> _stderrBuffer = new Queue<string>();
        private string _lastRawNotificationPreview;

        public bool IsRunning { get { return _running && _process != null && !_process.HasExited; } }

        /// <summary>
        /// Set clarion/updatePaths data to be sent after LSP initialization.
        /// Must be called before Start().
        /// </summary>
        public void SetUpdatePaths(Dictionary<string, object> updatePaths)
        {
            _pendingUpdatePaths = updatePaths;
        }

        /// <summary>
        /// Start the LSP server and initialize the protocol.
        /// </summary>
        public bool Start(string serverJsPath, string workspaceUri, string workspaceName)
        {
            if (_running) return true;

            if (!File.Exists(serverJsPath))
                return false;

            try
            {
                // Resolve node.exe in order:
                // 1. Bundled next to our LSP distribution (when shipped in installer)
                // 2. VS Code's bundled node from the Stable install
                // 3. System PATH
                string nodeExe = ResolveNodeExe(serverJsPath);

                System.Diagnostics.Debug.WriteLine("[LSP] Starting: " + nodeExe + " \"" + serverJsPath + "\" --stdio");

                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = nodeExe,
                        Arguments = "\"" + serverJsPath + "\" --stdio",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                // Capture stderr for diagnostics — ring buffer + Debug output
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    System.Diagnostics.Debug.WriteLine("[LSP stderr] " + e.Data);
                    lock (_debugLock)
                    {
                        _stderrBuffer.Enqueue(e.Data);
                        while (_stderrBuffer.Count > MaxStderrBuffer)
                            _stderrBuffer.Dequeue();
                    }
                };

                _process.Start();
                _process.BeginErrorReadLine();
                _running = true;

                System.Diagnostics.Debug.WriteLine("[LSP] Process started, PID=" + _process.Id);

                // Start reader thread
                _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "LSP-Reader" };
                _readerThread.Start();

                // Send initialize
                var initParams = new Dictionary<string, object>
                {
                    { "processId", Process.GetCurrentProcess().Id },
                    { "capabilities", new Dictionary<string, object>() },
                    { "rootUri", workspaceUri },
                    { "workspaceFolders", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "uri", workspaceUri },
                                { "name", workspaceName }
                            }
                        }
                    }
                };

                System.Diagnostics.Debug.WriteLine("[LSP] Sending initialize request...");
                var initResult = SendRequest("initialize", initParams, 15000);
                if (initResult == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] Initialize timed out or returned null");
                    // Check if process crashed
                    if (_process.HasExited)
                        System.Diagnostics.Debug.WriteLine("[LSP] Process exited with code: " + _process.ExitCode);
                    Stop();
                    return false;
                }

                System.Diagnostics.Debug.WriteLine("[LSP] Initialize succeeded");

                // Send initialized notification
                SendNotification("initialized", new Dictionary<string, object>());

                // Send clarion/updatePaths if provided — required for cross-file LSP features
                if (_pendingUpdatePaths != null)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] Sending clarion/updatePaths...");
                    SendNotification("clarion/updatePaths", _pendingUpdatePaths);
                    _pendingUpdatePaths = null;
                }

                // Give the server a moment to finish initialization
                Thread.Sleep(1000);

                System.Diagnostics.Debug.WriteLine("[LSP] Ready");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Start failed: " + ex.GetType().Name + ": " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[LSP] Stack: " + ex.StackTrace);
                Stop();
                return false;
            }
        }

        /// <summary>
        /// Resolves node.exe for spawning the LSP server. VS Code cannot be used as a
        /// fallback because it embeds node inside Electron (Code.exe) rather than
        /// shipping a standalone binary. The Clarion VS Code extension also does not
        /// bundle its own node. So if a user has only VS Code + the extension, they
        /// still need a real Node.js install somewhere.
        ///
        /// Tries in order:
        /// (1) node.exe bundled next to the LSP distribution (installer-shipped layout),
        /// (2) Node.js official installer default at C:\Program Files\nodejs\node.exe,
        /// (3) Node.js 32-bit installer default at C:\Program Files (x86)\nodejs\node.exe,
        /// (4) Claude Code standalone bundled node at %USERPROFILE%\.claude\local\node.exe,
        /// (5) "node" on the system PATH (last resort — Process.Start will fail with a
        ///     clear error if no node is installed at all).
        /// </summary>
        private static string ResolveNodeExe(string serverJsPath)
        {
            try
            {
                string lspDir = Path.GetDirectoryName(serverJsPath);
                string lspRoot = Path.GetFullPath(Path.Combine(lspDir, "..", "..", ".."));
                string bundled = Path.Combine(lspRoot, "node.exe");
                System.Diagnostics.Debug.WriteLine("[LSP] Looking for bundled node.exe at: " + bundled);
                if (File.Exists(bundled)) return bundled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Bundled node.exe lookup failed: " + ex.Message);
            }

            try
            {
                string[] candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "local", "node.exe"),
                };

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        System.Diagnostics.Debug.WriteLine("[LSP] Using node.exe at: " + candidate);
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] node.exe fallback search failed: " + ex.Message);
            }

            System.Diagnostics.Debug.WriteLine("[LSP] No bundled node.exe found, falling back to PATH");
            return "node";
        }

        public void Stop()
        {
            _running = false;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    SendNotification("shutdown", null);
                    Thread.Sleep(200);
                    SendNotification("exit", null);
                    Thread.Sleep(200);
                    if (!_process.HasExited) _process.Kill();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Stop failed: " + ex.Message);
            }

            _process = null;

            // Release diagnostic events.
            lock (_diagnosticsLock)
            {
                foreach (var set in _diagnostics.Values)
                {
                    try { set.Ready.Dispose(); } catch { }
                }
                _diagnostics.Clear();
            }
        }

        #region LSP Requests

        /// <summary>
        /// textDocument/definition - find where a symbol is defined.
        /// </summary>
        public Dictionary<string, object> GetDefinition(string filePath, int line, int character)
        {
            return SendTextDocumentPositionRequest("textDocument/definition", filePath, line, character);
        }

        /// <summary>
        /// textDocument/references - find all references to a symbol.
        /// </summary>
        public Dictionary<string, object> GetReferences(string filePath, int line, int character)
        {
            var parms = BuildTextDocumentPosition(filePath, line, character);
            parms["context"] = new Dictionary<string, object> { { "includeDeclaration", true } };
            return SendRequest("textDocument/references", parms);
        }

        /// <summary>
        /// textDocument/hover - get hover info (type, signature, docs).
        /// </summary>
        public Dictionary<string, object> GetHover(string filePath, int line, int character)
        {
            return SendTextDocumentPositionRequest("textDocument/hover", filePath, line, character);
        }

        /// <summary>
        /// textDocument/documentSymbol - get all symbols in a document.
        /// </summary>
        public Dictionary<string, object> GetDocumentSymbols(string filePath)
        {
            EnsureDocumentOpen(filePath);
            var parms = new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object> { { "uri", FilePathToUri(filePath) } } }
            };
            return SendRequest("textDocument/documentSymbol", parms);
        }

        /// <summary>
        /// workspace/symbol - search for symbols across the workspace.
        /// </summary>
        public Dictionary<string, object> FindWorkspaceSymbol(string query)
        {
            var parms = new Dictionary<string, object> { { "query", query } };
            return SendRequest("workspace/symbol", parms);
        }

        /// <summary>
        /// textDocument/rename - asks the server for a workspace edit that would
        /// rename the symbol at the given position. Returns the raw LSP WorkspaceEdit
        /// result — the caller is responsible for applying the edits (and MUST seek
        /// developer approval first per CLAUDE.md rule #9).
        /// </summary>
        public Dictionary<string, object> Rename(string filePath, int line, int character, string newName)
        {
            EnsureDocumentOpen(filePath);
            var parms = BuildTextDocumentPosition(filePath, line, character);
            parms["newName"] = newName;
            return SendRequest("textDocument/rename", parms, 8000);
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Triggers fresh analysis of the given file and waits for the server to
        /// publish diagnostics. If the file isn't open yet, sends didOpen; if it is,
        /// sends didChange with the current disk contents so stale cached diagnostics
        /// from a previous version don't satisfy the wait.
        ///
        /// Returns a DiagnosticWaitResult where Pending=false means the server
        /// authoritatively reported results (possibly an empty list = clean file),
        /// and Pending=true means the server didn't respond within timeoutMs.
        /// </summary>
        public DiagnosticWaitResult GetDiagnostics(string filePath, int timeoutMs = 3000)
        {
            var result = new DiagnosticWaitResult { Entries = new List<DiagnosticEntry>(), Pending = true };
            if (!IsRunning || string.IsNullOrEmpty(filePath)) return result;

            // Trigger server analysis before waiting. We always force a new publish
            // so Claude sees the state of the file as of this call — stale cached
            // diagnostics from before the last edit are not good enough.
            try
            {
                if (_openDocuments.ContainsKey(filePath))
                    SendDidChangeFromDisk(filePath);
                else
                    EnsureDocumentOpen(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] GetDiagnostics trigger failed: " + ex.Message);
                return result;
            }

            return WaitForDiagnostics(filePath, timeoutMs, forceRefresh: true);
        }

        /// <summary>
        /// Returns a snapshot of debug telemetry for the lsp_debug_status tool:
        /// process state, notification method counts, diagnostics cache state, and
        /// the last N lines of server stderr. Used to debug why diagnostics aren't
        /// arriving on a live server without needing DebugView access.
        /// </summary>
        public Dictionary<string, object> GetDebugStatus()
        {
            var result = new Dictionary<string, object>();
            result["isRunning"] = IsRunning;
            result["processId"] = _process != null && !_process.HasExited ? _process.Id : -1;

            lock (_debugLock)
            {
                var counts = new Dictionary<string, object>();
                foreach (var kv in _notificationCounts)
                    counts[kv.Key] = kv.Value;
                result["notificationCounts"] = counts;
                result["lastNotificationPreview"] = _lastRawNotificationPreview ?? "(none yet)";
                result["stderrTail"] = new List<string>(_stderrBuffer);
            }

            lock (_diagnosticsLock)
            {
                var cache = new List<Dictionary<string, object>>();
                foreach (var kv in _diagnostics)
                {
                    cache.Add(new Dictionary<string, object>
                    {
                        { "uri", kv.Key },
                        { "wasPublished", kv.Value.WasPublished },
                        { "entryCount", kv.Value.Entries.Count }
                    });
                }
                result["diagnosticsCache"] = cache;
            }

            // Currently-open documents (tracked by EnsureDocumentOpen / didChange)
            var openDocs = new List<string>();
            foreach (var kv in _openDocuments)
                openDocs.Add(kv.Key);
            result["openDocuments"] = openDocs;

            return result;
        }

        /// <summary>
        /// Returns the current diagnostics cached for the given file. Does NOT
        /// wait or trigger re-analysis — intended for UI polling (Phase 3a) where
        /// we just want a fast read of whatever the server has told us so far.
        /// Returns null if no publishDiagnostics has arrived yet for this file.
        /// </summary>
        public List<DiagnosticEntry> GetCachedDiagnostics(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string key = FilePathToUri(filePath);

            lock (_diagnosticsLock)
            {
                DiagnosticSet set;
                if (!_diagnostics.TryGetValue(key, out set)) return null;
                if (!set.WasPublished) return null;
                // Return a snapshot to avoid cross-thread mutation of the caller's list.
                return new List<DiagnosticEntry>(set.Entries);
            }
        }

        /// <summary>
        /// Waits up to timeoutMs for a publishDiagnostics notification to arrive
        /// for the given file. If `forceRefresh` is true, the wait ignores any
        /// previously-cached publish and only returns when a NEW publish arrives
        /// after the call begins (use this when you know the file content has
        /// changed and want a fresh analysis).
        ///
        /// Returns a result with `Pending=false` on signal and `Pending=true` on
        /// timeout. Entries may be empty on a signal result — that means the
        /// server authoritatively reported zero diagnostics (a clean file).
        ///
        /// The caller is responsible for triggering analysis beforehand (didOpen
        /// or didChange) — this method only waits on the publish event.
        /// </summary>
        public DiagnosticWaitResult WaitForDiagnostics(string filePath, int timeoutMs, bool forceRefresh)
        {
            var result = new DiagnosticWaitResult { Entries = new List<DiagnosticEntry>(), Pending = true };
            if (string.IsNullOrEmpty(filePath)) return result;

            string key = FilePathToUri(filePath);

            DiagnosticSet set;
            lock (_diagnosticsLock)
            {
                if (!_diagnostics.TryGetValue(key, out set))
                {
                    set = new DiagnosticSet();
                    _diagnostics[key] = set;
                    EvictOldestIfFull_NoLock();
                }

                if (forceRefresh)
                {
                    // Clear the event so we wait strictly for a publish that happens
                    // AFTER this call — cached diagnostics from before the content
                    // changed must not satisfy the wait.
                    try { set.Ready.Reset(); } catch { }
                }
                else if (set.WasPublished)
                {
                    // Non-force path with an already-cached publish — return immediately.
                    result.Entries = new List<DiagnosticEntry>(set.Entries);
                    result.Pending = false;
                    return result;
                }
            }

            // Wait outside the lock so publish handlers aren't blocked.
            bool signaled;
            try
            {
                signaled = set.Ready.Wait(timeoutMs);
            }
            catch (ObjectDisposedException)
            {
                // Set was evicted between registration and wait — report as pending so the caller can retry.
                return result;
            }

            // Always check the cache — even on timeout. The publish may have arrived
            // before our forceRefresh Reset() cleared the event (race between the
            // initial didOpen publish and the re-trigger). Returning pending:true when
            // the cache has 44 valid entries is the bug this fixes.
            lock (_diagnosticsLock)
            {
                if (_diagnostics.TryGetValue(key, out set) && set.WasPublished)
                {
                    result.Entries = new List<DiagnosticEntry>(set.Entries);
                    result.Pending = false;
                }
            }

            return result;
        }

        #endregion

        #region Document Management

        // Tracks open documents and their current textDocument version number (LSP protocol
        // requires version to increase monotonically across didOpen → didChange for a URI).
        private readonly Dictionary<string, int> _openDocuments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private void EnsureDocumentOpen(string filePath)
        {
            if (_openDocuments.ContainsKey(filePath)) return;
            if (!File.Exists(filePath)) return;

            string uri = FilePathToUri(filePath);
            string content = File.ReadAllText(filePath);

            string ext = Path.GetExtension(filePath).ToLower();
            string languageId = ext == ".inc" || ext == ".clw" || ext == ".equ" ? "clarion" : "plaintext";

            var parms = new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object>
                    {
                        { "uri", uri },
                        { "languageId", languageId },
                        { "version", 1 },
                        { "text", content }
                    }
                }
            };

            SendNotification("textDocument/didOpen", parms);
            _openDocuments[filePath] = 1;
        }

        /// <summary>
        /// Send a full-document textDocument/didChange with the current file contents
        /// from disk. Used to force the server to re-analyze a file that's already open
        /// after it may have changed (e.g., after write_embed_content or an external edit).
        /// If the file hasn't been opened yet, falls through to EnsureDocumentOpen instead.
        /// </summary>
        private void SendDidChangeFromDisk(string filePath)
        {
            if (!File.Exists(filePath)) return;

            int currentVersion;
            if (!_openDocuments.TryGetValue(filePath, out currentVersion))
            {
                EnsureDocumentOpen(filePath);
                return;
            }

            string uri = FilePathToUri(filePath);
            string content = File.ReadAllText(filePath);
            int nextVersion = currentVersion + 1;

            // LSP TextDocumentContentChangeEvent without `range` = full document replacement.
            var changes = new System.Collections.ArrayList
            {
                new Dictionary<string, object> { { "text", content } }
            };

            var parms = new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object>
                    {
                        { "uri", uri },
                        { "version", nextVersion }
                    }
                },
                { "contentChanges", changes }
            };

            SendNotification("textDocument/didChange", parms);
            _openDocuments[filePath] = nextVersion;
        }

        #endregion

        #region JSON-RPC Transport

        private Dictionary<string, object> SendTextDocumentPositionRequest(string method, string filePath, int line, int character)
        {
            EnsureDocumentOpen(filePath);
            var parms = BuildTextDocumentPosition(filePath, line, character);
            return SendRequest(method, parms);
        }

        private Dictionary<string, object> BuildTextDocumentPosition(string filePath, int line, int character)
        {
            return new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object> { { "uri", FilePathToUri(filePath) } } },
                { "position", new Dictionary<string, object> { { "line", line }, { "character", character } } }
            };
        }

        private Dictionary<string, object> SendRequest(string method, Dictionary<string, object> parms, int timeoutMs = 5000)
        {
            if (!_running || _process == null || _process.HasExited) return null;

            int id = Interlocked.Increment(ref _nextId);
            var request = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "method", method }
            };
            if (parms != null) request["params"] = parms;

            string json = _serializer.Serialize(request);
            WriteMessage(json);

            // Wait for response
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_responses)
                {
                    string response;
                    if (_responses.TryGetValue(id, out response))
                    {
                        _responses.Remove(id);
                        return _serializer.Deserialize<Dictionary<string, object>>(response);
                    }
                }
                _responseReceived.WaitOne(100);
            }

            return null; // Timeout
        }

        private void SendNotification(string method, Dictionary<string, object> parms)
        {
            if (!_running || _process == null || _process.HasExited) return;

            var notification = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "method", method }
            };
            if (parms != null) notification["params"] = parms;

            WriteMessage(_serializer.Serialize(notification));
        }

        private void WriteMessage(string json)
        {
            lock (_writeLock)
            {
                try
                {
                    byte[] content = Encoding.UTF8.GetBytes(json);
                    string header = "Content-Length: " + content.Length + "\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);

                    _process.StandardInput.BaseStream.Write(headerBytes, 0, headerBytes.Length);
                    _process.StandardInput.BaseStream.Write(content, 0, content.Length);
                    _process.StandardInput.BaseStream.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] WriteMessage failed: " + ex.Message);
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                var stream = _process.StandardOutput.BaseStream;
                while (_running && !_process.HasExited)
                {
                    string json = ReadMessage(stream);
                    if (json == null) break;

                    try
                    {
                        var msg = _serializer.Deserialize<Dictionary<string, object>>(json);

                        // Response: has `id` → correlate with a pending request
                        if (msg.ContainsKey("id") && msg["id"] != null)
                        {
                            int id;
                            if (int.TryParse(msg["id"].ToString(), out id))
                            {
                                lock (_responses)
                                {
                                    _responses[id] = json;
                                }
                                _responseReceived.Set();
                                continue;
                            }
                        }

                        // Notification: has `method` but no `id` → dispatch by method name
                        if (msg.ContainsKey("method") && msg["method"] != null)
                        {
                            HandleNotification(msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[LSP] ReadLoop message parse failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] ReadLoop terminated: " + ex.Message);
            }
        }

        private void HandleNotification(Dictionary<string, object> msg)
        {
            string method = msg["method"] as string;
            if (string.IsNullOrEmpty(method)) return;

            // Telemetry: count every notification method we see, and keep a preview of
            // the most recent one so the debug tool can show the raw shape.
            lock (_debugLock)
            {
                int cur;
                _notificationCounts.TryGetValue(method, out cur);
                _notificationCounts[method] = cur + 1;
                try
                {
                    string preview = _serializer.Serialize(msg);
                    if (preview.Length > 2000) preview = preview.Substring(0, 2000) + "...";
                    _lastRawNotificationPreview = preview;
                }
                catch { }
            }

            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    HandlePublishDiagnostics(msg["params"] as Dictionary<string, object>);
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine("[LSP] Ignored notification: " + method);
                    break;
            }
        }

        private void HandlePublishDiagnostics(Dictionary<string, object> parms)
        {
            if (parms == null) return;

            string uri = parms.ContainsKey("uri") ? parms["uri"] as string : null;
            if (string.IsNullOrEmpty(uri)) return;

            // Canonicalize via round-trip through FilePathToUri so cache keys are consistent
            // regardless of whether the URI came in with different casing/encoding than what
            // we sent on didOpen.
            string canonical = CanonicalizeUri(uri);

            var entries = new List<DiagnosticEntry>();
            var diagList = parms.ContainsKey("diagnostics") ? parms["diagnostics"] as System.Collections.ArrayList : null;
            if (diagList != null)
            {
                foreach (var obj in diagList)
                {
                    var d = obj as Dictionary<string, object>;
                    if (d == null) continue;

                    var entry = new DiagnosticEntry();
                    if (d.ContainsKey("severity")) entry.Severity = Convert.ToInt32(d["severity"]);
                    if (d.ContainsKey("message")) entry.Message = d["message"] as string;
                    if (d.ContainsKey("source")) entry.Source = d["source"] as string;

                    var range = d.ContainsKey("range") ? d["range"] as Dictionary<string, object> : null;
                    var start = range != null && range.ContainsKey("start") ? range["start"] as Dictionary<string, object> : null;
                    if (start != null)
                    {
                        if (start.ContainsKey("line")) entry.Line = Convert.ToInt32(start["line"]);
                        if (start.ContainsKey("character")) entry.Character = Convert.ToInt32(start["character"]);
                    }

                    entries.Add(entry);
                }
            }

            lock (_diagnosticsLock)
            {
                DiagnosticSet set;
                if (!_diagnostics.TryGetValue(canonical, out set))
                {
                    set = new DiagnosticSet();
                    _diagnostics[canonical] = set;
                    EvictOldestIfFull_NoLock();
                }

                set.Entries = entries;
                set.WasPublished = true;
                set.LastUpdateTicks = DateTime.UtcNow.Ticks;
                // Signal any waiter that new diagnostics have arrived.
                set.Ready.Set();
            }

            System.Diagnostics.Debug.WriteLine(string.Format(
                "[LSP] publishDiagnostics: {0} entries for {1}", entries.Count, canonical));
        }

        private void EvictOldestIfFull_NoLock()
        {
            if (_diagnostics.Count <= MaxCachedDiagnosticFiles) return;

            string oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach (var kv in _diagnostics)
            {
                if (kv.Value.LastUpdateTicks < oldestTicks)
                {
                    oldestTicks = kv.Value.LastUpdateTicks;
                    oldestKey = kv.Key;
                }
            }

            if (oldestKey != null)
            {
                var victim = _diagnostics[oldestKey];
                _diagnostics.Remove(oldestKey);
                try { victim.Ready.Dispose(); } catch { }
                System.Diagnostics.Debug.WriteLine("[LSP] Evicted oldest diagnostics cache entry: " + oldestKey);
            }
        }

        private static string CanonicalizeUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            // Strip file:// prefix, normalize to a real path, re-apply FilePathToUri so the
            // cached key matches what our own EnsureDocumentOpen would produce.
            try
            {
                string path = UriToFilePath(uri);
                return FilePathToUri(path);
            }
            catch
            {
                return uri;
            }
        }

        private string ReadMessage(Stream stream)
        {
            // Read headers
            int contentLength = -1;
            var headerLine = new StringBuilder();

            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) return null;

                headerLine.Append((char)b);
                string h = headerLine.ToString();

                if (h.EndsWith("\r\n\r\n"))
                {
                    foreach (string line in h.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int val;
                            if (int.TryParse(line.Substring(15).Trim(), out val))
                                contentLength = val;
                        }
                    }
                    break;
                }
            }

            if (contentLength <= 0) return null;

            byte[] buffer = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(buffer, read, contentLength - read);
                if (n <= 0) return null;
                read += n;
            }

            return Encoding.UTF8.GetString(buffer);
        }

        #endregion

        #region Helpers

        private static string FilePathToUri(string filePath)
        {
            return "file:///" + filePath.Replace("\\", "/").Replace(" ", "%20");
        }

        public static string UriToFilePath(string uri)
        {
            if (uri.StartsWith("file:///"))
                uri = uri.Substring(8).Replace("/", "\\").Replace("%20", " ");
            return uri;
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }

        #region Diagnostic data types

        /// <summary>
        /// A single diagnostic entry from a textDocument/publishDiagnostics notification.
        /// Severity follows the LSP spec: 1=Error, 2=Warning, 3=Information, 4=Hint.
        /// </summary>
        public class DiagnosticEntry
        {
            public int Severity;
            public int Line;
            public int Character;
            public string Message;
            public string Source;
        }

        /// <summary>
        /// Per-URI diagnostics cache entry. Ready is set by HandlePublishDiagnostics
        /// whenever a new publish arrives, allowing WaitForDiagnostics callers to block
        /// on an event-driven signal instead of polling.
        /// </summary>
        private class DiagnosticSet
        {
            public List<DiagnosticEntry> Entries = new List<DiagnosticEntry>();
            public ManualResetEventSlim Ready = new ManualResetEventSlim(false);
            public long LastUpdateTicks = DateTime.UtcNow.Ticks;
            // True once a publishDiagnostics has ever arrived for this URI — distinguishes
            // an authoritative "clean file" (Entries=[]) from "we haven't heard anything yet".
            public bool WasPublished;
        }

        /// <summary>
        /// Result returned by WaitForDiagnostics. Pending=true means the wait timed out;
        /// the server may still be analysing and callers should report that to the user
        /// rather than treating empty Entries as "file is clean".
        /// </summary>
        public class DiagnosticWaitResult
        {
            public List<DiagnosticEntry> Entries;
            public bool Pending;
        }

        #endregion
    }
}
