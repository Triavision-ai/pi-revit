using System.Diagnostics;

namespace RevitBridge
{
    /// <summary>
    /// Writes small files so that a reader sees either the previous content or the new content,
    /// never an empty, half-written or missing file: the text goes to a temporary file in the
    /// same folder, which is then renamed over the target in one step.
    ///
    /// (File.Replace is deliberately not used: it removes the old name before it puts the new
    /// file there, so a reader in that moment finds no file at all.)
    ///
    /// Windows refuses the rename while another process has the target open. Readers normally
    /// hold it for an instant, but under load (or with a virus scanner looking at the file) a
    /// single read was measured at 150 ms, so the rename is retried for DefaultPatience. Only
    /// if the target stays locked that long is the text written the plain way: a busy reader
    /// can delay an update but never prevent it.
    /// </summary>
    internal static class AtomicFile
    {
        internal static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

        /// <summary>Writes <paramref name="text"/> to <paramref name="path"/>. Returns true when the
        /// target was replaced in one step, false when it stayed locked for the whole
        /// <paramref name="patience"/> and was overwritten in place instead.</summary>
        internal static bool WriteAllText(string path, string text, TimeSpan? patience = null)
        {
            TimeSpan limit = patience ?? DefaultPatience;
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, text);
                var waited = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: true);
                        return true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        if (waited.Elapsed >= limit) break;
                        Thread.Sleep(RetryDelay);
                    }
                }
                // Still locked: fall back to the plain overwrite rather than not writing at all.
                File.WriteAllText(path, text);
                return false;
            }
            finally
            {
                // After a successful rename the temporary name no longer exists; after a
                // failure it must not be left behind next to the target.
                try { File.Delete(temp); } catch { }
            }
        }
    }
}
