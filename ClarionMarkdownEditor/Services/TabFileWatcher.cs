using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace ClarionMarkdownEditor.Services
{
    /// <summary>
    /// Watches the on-disk files backing open editor tabs and raises UI-thread
    /// events when they change or disappear underneath the editor.
    ///
    /// One <see cref="FileSystemWatcher"/> is created per directory that holds at
    /// least one watched file; it is disposed once the last file in that directory
    /// stops being watched. Raw filesystem events (which fire on a thread-pool
    /// thread and often arrive in bursts) are marshalled onto the owning control's
    /// UI thread and debounced per-path before the public events fire. Reads use
    /// <see cref="FileShare.ReadWrite"/> and retry briefly, because the writer that
    /// triggered the event may still hold the file open.
    ///
    /// Threading: every method except the private On*/Marshal handlers runs on the
    /// UI thread, so the collections are single-threaded. The On* handlers only read
    /// <c>_disposed</c>/<c>_uiContext</c> and hop to the UI thread via BeginInvoke.
    /// </summary>
    internal sealed class TabFileWatcher : IDisposable
    {
        private const int DebounceMs = 250;
        private const int ReadRetries = 5;

        private readonly Control _uiContext;

        // key: directory path
        private readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dirRefCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // full file paths currently being watched
        private readonly HashSet<string> _watchedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // key: full file path -> debounce timer
        private readonly Dictionary<string, Timer> _debounceTimers =
            new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);

        private volatile bool _disposed;

        /// <summary>Raised on the UI thread when a watched file's content changed on disk.</summary>
        public event Action<string, string> FileChanged;   // (fullPath, diskContent)

        /// <summary>Raised on the UI thread when a watched file was deleted or renamed away.</summary>
        public event Action<string> FileRemoved;           // (fullPath)

        public TabFileWatcher(Control uiContext)
        {
            _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        }

        /// <summary>
        /// Begins watching the given file. No-op if already watched or the path is
        /// empty / its directory doesn't exist. Idempotent.
        /// </summary>
        public void Watch(string filePath)
        {
            if (_disposed || string.IsNullOrEmpty(filePath)) return;

            string full;
            try { full = Path.GetFullPath(filePath); }
            catch { return; }

            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            if (!_watchedFiles.Add(full)) return; // already watching this file

            if (_dirRefCounts.TryGetValue(dir, out var count))
            {
                _dirRefCounts[dir] = count + 1;
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                watcher.Changed += OnChangedOrCreated;
                watcher.Created += OnChangedOrCreated;
                watcher.Renamed += OnRenamed;
                watcher.Deleted += OnDeleted;
                watcher.EnableRaisingEvents = true;

                _watchers[dir] = watcher;
                _dirRefCounts[dir] = 1;
            }
            catch
            {
                // Couldn't watch this directory (permissions, unsupported path, ...).
                // Roll back the file registration so a later Watch can retry.
                _watchedFiles.Remove(full);
            }
        }

        /// <summary>Stops watching the given file, disposing its directory watcher if unused.</summary>
        public void Unwatch(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            string full;
            try { full = Path.GetFullPath(filePath); }
            catch { return; }

            if (!_watchedFiles.Remove(full)) return;

            DisposeTimer(full);

            var dir = Path.GetDirectoryName(full);
            if (dir == null || !_dirRefCounts.TryGetValue(dir, out var count)) return;

            if (count <= 1)
            {
                _dirRefCounts.Remove(dir);
                if (_watchers.TryGetValue(dir, out var watcher))
                {
                    DisposeWatcher(watcher);
                    _watchers.Remove(dir);
                }
            }
            else
            {
                _dirRefCounts[dir] = count - 1;
            }
        }

        // --- Raw filesystem event handlers (thread-pool thread) ---

        private void OnChangedOrCreated(object sender, FileSystemEventArgs e) => Marshal(e.FullPath);

        private void OnDeleted(object sender, FileSystemEventArgs e) => Marshal(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            // The old name may be a watched file renamed away; the new name may be a
            // watched file appearing (atomic save = write temp, rename over target).
            Marshal(e.OldFullPath);
            Marshal(e.FullPath);
        }

        private void Marshal(string fullPath)
        {
            if (_disposed) return;
            try
            {
                if (_uiContext.IsHandleCreated && !_uiContext.IsDisposed)
                    _uiContext.BeginInvoke(new Action(() => ScheduleDebounced(fullPath)));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // --- UI thread from here down ---

        private void ScheduleDebounced(string fullPath)
        {
            if (_disposed || !_watchedFiles.Contains(fullPath)) return;

            if (!_debounceTimers.TryGetValue(fullPath, out var timer))
            {
                timer = new Timer { Interval = DebounceMs };
                timer.Tag = new TimerState { Path = fullPath, Retries = 0 };
                timer.Tick += DebounceTick;
                _debounceTimers[fullPath] = timer;
            }

            var state = (TimerState)timer.Tag;
            state.Retries = 0;
            timer.Stop();
            timer.Start();
        }

        private void DebounceTick(object sender, EventArgs e)
        {
            var timer = (Timer)sender;
            var state = (TimerState)timer.Tag;
            timer.Stop();

            var path = state.Path;
            if (_disposed || !_watchedFiles.Contains(path))
            {
                DisposeTimer(path);
                return;
            }

            if (!File.Exists(path))
            {
                DisposeTimer(path);
                FileRemoved?.Invoke(path);
                return;
            }

            string content;
            try
            {
                content = ReadShared(path);
            }
            catch (IOException)
            {
                // Writer likely still holds the file — back off and retry shortly.
                if (state.Retries++ < ReadRetries) { timer.Start(); return; }
                DisposeTimer(path);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                if (state.Retries++ < ReadRetries) { timer.Start(); return; }
                DisposeTimer(path);
                return;
            }

            DisposeTimer(path);
            FileChanged?.Invoke(path, content);
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                return reader.ReadToEnd();
        }

        private void DisposeTimer(string path)
        {
            if (_debounceTimers.TryGetValue(path, out var timer))
            {
                timer.Stop();
                timer.Tick -= DebounceTick;
                timer.Dispose();
                _debounceTimers.Remove(path);
            }
        }

        private void DisposeWatcher(FileSystemWatcher watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChangedOrCreated;
            watcher.Created -= OnChangedOrCreated;
            watcher.Renamed -= OnRenamed;
            watcher.Deleted -= OnDeleted;
            watcher.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var watcher in _watchers.Values)
                DisposeWatcher(watcher);
            _watchers.Clear();
            _dirRefCounts.Clear();
            _watchedFiles.Clear();

            foreach (var timer in _debounceTimers.Values)
            {
                timer.Stop();
                timer.Tick -= DebounceTick;
                timer.Dispose();
            }
            _debounceTimers.Clear();
        }

        private sealed class TimerState
        {
            public string Path;
            public int Retries;
        }
    }
}
