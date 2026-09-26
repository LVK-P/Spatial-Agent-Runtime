namespace SpatialAgent.Services
{
    /// <summary>
    /// Process-local exactly-once fence for the ordered durable session close
    /// transaction. The persistent lease remains the cross-process commit
    /// marker and is deliberately released last.
    /// </summary>
    public sealed class DurableSessionFinalizer
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _inFlight =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completed =
            new(StringComparer.OrdinalIgnoreCase);

        public bool TryFinalize(
            string sessionId,
            Action persistTerminalCheckpoint,
            Action persistTerminalSummary,
            Action writeSessionEnd,
            Action releaseLease)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException(
                    "Session id is required.",
                    nameof(sessionId));
            ArgumentNullException.ThrowIfNull(persistTerminalCheckpoint);
            ArgumentNullException.ThrowIfNull(persistTerminalSummary);
            ArgumentNullException.ThrowIfNull(writeSessionEnd);
            ArgumentNullException.ThrowIfNull(releaseLease);

            lock (_gate)
            {
                if (_completed.Contains(sessionId)
                    || !_inFlight.Add(sessionId))
                    return false;
            }

            try
            {
                persistTerminalCheckpoint();
                persistTerminalSummary();
                writeSessionEnd();
                releaseLease();
                lock (_gate)
                {
                    _inFlight.Remove(sessionId);
                    _completed.Add(sessionId);
                }
                return true;
            }
            catch
            {
                lock (_gate)
                    _inFlight.Remove(sessionId);
                throw;
            }
        }

        public void Reopen(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;
            lock (_gate)
            {
                _inFlight.Remove(sessionId);
                _completed.Remove(sessionId);
            }
        }
    }
}
