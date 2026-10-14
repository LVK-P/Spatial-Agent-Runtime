using System.Collections.Generic;
using Newtonsoft.Json;

// ================================================================
// ProjectMemory.cs — 项目级持久记忆
//
// 存储在整个对话过程中用户确认的 5W2H 答案、操作纠正和偏好。
// 文件位于项目文件夹：<project>\.agent\memory.json
//
// 设计原则：
//   1. 只增不改 — 新增信息追加，旧信息保留（可追溯）
//   2. 自动注入 — 每次对话自动加载到上下文
//   3. 用户驱动 — 只记录用户明确确认或纠正的信息
// ================================================================
// English：================================================================ ProjectMemory.cs — Project-level persistent memory stores 5W2H answers, operation corrections, and preferences confirmed by users throughout the conversation. File located in the project folder: \.agent\memory.json Design principles: 1. Only add, not change — add new information, retain old information (retroactive). 2. Auto-injection — automatically loads into context every conversation 3. User-driven — only records information explicitly confirmed or corrected by the user ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    public class ProjectMemory
    {
        [JsonProperty("project_id")]
        public string ProjectId { get; set; } = "";

        [JsonProperty("project_path")]
        public string ProjectPath { get; set; } = "";

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; } = "";

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; } = "";

        /// <summary>5W2H 已确认的答案</summary>
        /// <remarks>English：5W2H has confirmed the answer</remarks>
        [JsonProperty("w5h2")]
        public W5H2Confirmed W5H2 { get; set; } = new();

        /// <summary>用户纠正记录（操作链修正）</summary>
        /// <remarks>English：User Correction Records (Operation Chain Correction)</remarks>
        [JsonProperty("corrections")]
        public List<CorrectionEntry> Corrections { get; set; } = new();

        /// <summary>用户偏好（长期有效）</summary>
        /// <remarks>English：User preference (long-term validity)</remarks>
        [JsonProperty("preferences")]
        public Dictionary<string, string> Preferences { get; set; } = new();

        /// <summary>学到的零散事实</summary>
        /// <remarks>English：Learned scattered facts</remarks>
        [JsonProperty("learned_facts")]
        public List<string> LearnedFacts { get; set; } = new();

        /// <summary>对话摘要（用于跨会话连续性）</summary>
        /// <remarks>English：Conversation Summary (for cross-session continuity)</remarks>
        [JsonProperty("session_summaries")]
        public List<SessionSummary> SessionSummaries { get; set; } = new();

        [JsonProperty("data_lineage")]
        public List<DataLineageRecord> DataLineage { get; set; } = new();
    }

    public class W5H2Confirmed
    {
        [JsonProperty("why")]
        public ConfirmedFact? Why { get; set; }

        [JsonProperty("what")]
        public ConfirmedFact? What { get; set; }

        [JsonProperty("who_admin_level")]
        public ConfirmedFact? WhoAdminLevel { get; set; }

        [JsonProperty("who_organization")]
        public string? WhoOrganization { get; set; }

        [JsonProperty("where_province")]
        public string? WhereProvince { get; set; }

        [JsonProperty("where_city")]
        public string? WhereCity { get; set; }

        [JsonProperty("when_base_year")]
        public ConfirmedFact? WhenBaseYear { get; set; }

        [JsonProperty("when_target_year")]
        public ConfirmedFact? WhenTargetYear { get; set; }

        [JsonProperty("how_much_population")]
        public ConfirmedFact? HowMuchPopulation { get; set; }

        [JsonProperty("how_much_indicators")]
        public Dictionary<string, double>? HowMuchIndicators { get; set; }
    }

    public class ConfirmedFact
    {
        [JsonProperty("value")]
        public string Value { get; set; } = "";

        [JsonProperty("confirmed_at")]
        public string ConfirmedAt { get; set; } = "";

        [JsonProperty("source")]
        public string Source { get; set; } = "user"; // 用户/推断/系统。 / User/inferred/system.
    }

    public class CorrectionEntry
    {
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = "";

        [JsonProperty("context")]
        public string Context { get; set; } = "";

        [JsonProperty("wrong")]
        public string Wrong { get; set; } = "";

        [JsonProperty("correct")]
        public string Correct { get; set; } = "";

        [JsonProperty("reason")]
        public string Reason { get; set; } = "";
    }

    public class SessionSummary
    {
        [JsonProperty("thread_id")]
        public string ThreadId { get; set; } = "";

        [JsonProperty("revision")]
        public int Revision { get; set; }

        [JsonProperty("is_terminal")]
        public bool IsTerminal { get; set; }

        [JsonProperty("terminal_status", NullValueHandling = NullValueHandling.Ignore)]
        public string TerminalStatus { get; set; } = "";

        [JsonProperty("termination_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string TerminationReason { get; set; } = "";

        [JsonProperty("session_lease_id", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionLeaseId { get; set; } = "";

        [JsonProperty("date")]
        public string Date { get; set; } = "";

        [JsonProperty("user")]
        public string User { get; set; } = "";

        [JsonProperty("what_was_done")]
        public string WhatWasDone { get; set; } = "";

        [JsonProperty("key_decisions")]
        public List<string> KeyDecisions { get; set; } = new();

        [JsonProperty("pending_issues")]
        public List<string> PendingIssues { get; set; } = new();
    }
}
