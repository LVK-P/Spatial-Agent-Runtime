namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Project-level persistent memory across sessions.
    /// Encapsulates both reading (summary injection) and writing (session recording).
    /// </summary>
    public interface IProjectMemoryService
    {
        /// <summary>Human-readable summary for injection into the system prompt.</summary>
        string GetSummary();

        /// <summary>Record a completed session summary into persistent storage.</summary>
        void RecordSessionSummary(SessionSummary summary);

        /// <summary>Whether any previous sessions have been recorded.</summary>
        bool HasHistory { get; }

        /// <summary>Update the 5W2H project cognition result.</summary>
        void SetW5H2Context(string w5h2);

        /// <summary>Get the 5W2H analysis text, or empty if not performed.</summary>
        string GetW5H2Context();
    }
}
