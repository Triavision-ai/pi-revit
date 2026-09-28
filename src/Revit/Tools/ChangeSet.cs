namespace RevitBridge.Tools
{
    /// <summary>
    /// Net element changes of one tool call, from Revit's DocumentChanged operations. Pure, so it
    /// is tested offline. A rolled-back or undone group discards what was recorded before it, so a
    /// preview (commit inside a rolled-back group) reports no net change.
    /// </summary>
    internal sealed class ChangeSet
    {
        private readonly HashSet<long> _added = new(), _modified = new(), _deleted = new();
        public bool Observed { get; private set; }
        public bool RolledBack { get; private set; }
        public IReadOnlyCollection<long> Added => _added;
        public IReadOnlyCollection<long> Modified => _modified;
        public IReadOnlyCollection<long> Deleted => _deleted;

        public void Record(string operation, IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted)
        {
            Observed = true;
            if (operation is not ("TransactionCommitted" or "TransactionRedone"))
            {
                // Undo or rollback: everything recorded in this call is no longer a net change.
                _added.Clear(); _modified.Clear(); _deleted.Clear();
                RolledBack = true;
                return;
            }
            foreach (var id in added) { _added.Add(id); _deleted.Remove(id); }
            foreach (var id in modified) if (!_added.Contains(id)) _modified.Add(id);
            foreach (var id in deleted)
            {
                _modified.Remove(id);
                // Created and deleted within one call leaves nothing behind.
                if (!_added.Remove(id)) _deleted.Add(id);
            }
        }

        public bool IsEmpty => _added.Count == 0 && _modified.Count == 0 && _deleted.Count == 0;
    }
}
