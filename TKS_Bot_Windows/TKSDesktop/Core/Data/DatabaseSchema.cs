namespace TKSDesktop.Core.Data;

/// <summary>
/// 一版迁移（PRD FR-W-DB-3 / FR-W-DB-6）。
/// </summary>
/// <param name="Version">目标 <c>user_version</c>；必须从 1 起连续递增，不得复用已发布的版本号。</param>
/// <param name="Name">迁移名（写入日志与初始化报告，便于对账）。</param>
/// <param name="Scripts">按顺序执行的 SQL 脚本；每个元素作为一个 <c>SqliteCommand</c> 整体执行。</param>
public sealed record MigrationStep(int Version, string Name, IReadOnlyList<string> Scripts);

/// <summary>
/// SQLite schema 定义与迁移链（PRD §9.1–§9.6 / FR-W-DB-1..FR-W-DB-8）。
///
/// ⚠️ 硬约束：
/// <list type="bullet">
///   <item><c>user_version</c> 是唯一版本来源；**禁止**改列名、大小写与列序（另两端与后端按同一契约映射）。</item>
///   <item>DDL 逐字固定；已应用过的迁移**不得修改**，只能新增更高版本前滚（本地库无跨机共享，但用户机上已落地）。</item>
///   <item>全部迁移在事务内执行并逐版本递增；失败回滚后由上层走「备份 + 重建空库」（见 <see cref="LocalDb"/>）。</item>
///   <item>v3 的 <c>unicode61</c> 索引**不能**做中文子串匹配（实测）；v5 的 <c>trigram</c> 索引才是</item>
/// </list>
/// </summary>
public static class DatabaseSchema
{
    /// <summary>空库的初始版本。</summary>
    public const int BaselineVersion = 0;

    /// <summary>本端期望的 schema 版本（= 最后一条迁移的 <see cref="MigrationStep.Version"/>）。</summary>
    public const int TargetVersion = 7;

    private const string V6MemoryEvents = """
ALTER TABLE user_facts ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE user_facts ADD COLUMN revision_id TEXT;
ALTER TABLE user_facts ADD COLUMN permission_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE user_facts ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE user_facts ADD COLUMN stage TEXT NOT NULL DEFAULT 'candidate';
ALTER TABLE user_facts ADD COLUMN truth_status TEXT NOT NULL DEFAULT 'pending';
ALTER TABLE user_facts ADD COLUMN score REAL NOT NULL DEFAULT 0;
ALTER TABLE user_facts ADD COLUMN source_message_id TEXT;
ALTER TABLE user_facts ADD COLUMN temporal_validity TEXT;
CREATE TABLE memory_seen_events (event_id TEXT PRIMARY KEY, user_id TEXT NOT NULL);
CREATE TABLE memory_permission_queue (
  operation_id TEXT PRIMARY KEY, user_id TEXT NOT NULL, fact_id TEXT NOT NULL,
  enabled INTEGER NOT NULL, expected_revision INTEGER NOT NULL, state TEXT NOT NULL DEFAULT 'pending'
);
""";

    /* ------------------------------------------------------------------ */
    /* v1 baseline-room-v4-tables                                          */
    /* ------------------------------------------------------------------ */

    private const string V1ChatMessages = """
CREATE TABLE IF NOT EXISTS chat_messages (
  message_id     TEXT PRIMARY KEY,
  session_id     TEXT NOT NULL,
  role           TEXT NOT NULL,
  message_type   TEXT NOT NULL DEFAULT 'text',
  content_type   TEXT NOT NULL DEFAULT 'text',
  model_provider TEXT,
  content        TEXT NOT NULL DEFAULT '',
  status         TEXT NOT NULL DEFAULT 'received',
  timestamp      INTEGER NOT NULL,
  error_code     TEXT
);
CREATE INDEX IF NOT EXISTS idx_chat_messages_session_ts ON chat_messages (session_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_chat_messages_status ON chat_messages (status);
""";

    private const string V1ChatAttachments = """
CREATE TABLE IF NOT EXISTS chat_attachments (
  attachment_id TEXT PRIMARY KEY,
  message_id    TEXT REFERENCES chat_messages (message_id) ON DELETE CASCADE,
  session_id    TEXT NOT NULL,
  mime_type     TEXT NOT NULL,
  local_path    TEXT NOT NULL,
  file_size     INTEGER NOT NULL DEFAULT 0,
  width         INTEGER,
  height        INTEGER,
  upload_state  TEXT NOT NULL DEFAULT 'local',
  timestamp     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_chat_attachments_message ON chat_attachments (message_id);
""";

    private const string V1BotNotifications = """
CREATE TABLE IF NOT EXISTS bot_notifications (
  notification_id TEXT PRIMARY KEY,
  error_code      TEXT NOT NULL,
  message         TEXT NOT NULL,
  is_read         INTEGER NOT NULL DEFAULT 0,
  timestamp       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_bot_notifications_ts ON bot_notifications (is_read, timestamp);
""";

    private const string V1UserFacts = """
CREATE TABLE IF NOT EXISTS user_facts (
  fact_id   TEXT PRIMARY KEY,
  user_id   TEXT NOT NULL,
  fact      TEXT NOT NULL,
  timestamp INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_user_facts_user_ts ON user_facts (user_id, timestamp);
""";

    private const string V1Reminders = """
CREATE TABLE IF NOT EXISTS reminders (
  reminder_id TEXT PRIMARY KEY,
  target_time TEXT NOT NULL,
  fire_at     INTEGER NOT NULL,
  text        TEXT NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending',
  created_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_reminders_status_fire ON reminders (status, fire_at);
""";

    private const string V1SyncCursors = """
CREATE TABLE IF NOT EXISTS sync_cursors (
  key        TEXT PRIMARY KEY,
  value      INTEGER NOT NULL
);
""";

    private const string V1DeliveredBotMessages = """
CREATE TABLE IF NOT EXISTS delivered_bot_messages (
  message_id TEXT PRIMARY KEY,
  timestamp  INTEGER NOT NULL
);
""";

    /* ------------------------------------------------------------------ */
    /* v2 gamification-cache-tables（仅缓存，非业务事实来源 FR-W-PROG-1）    */
    /* ------------------------------------------------------------------ */

    private const string V2UserProgressCache = """
CREATE TABLE IF NOT EXISTS user_progress_cache (
  user_id                  TEXT PRIMARY KEY,
  balance                  INTEGER NOT NULL DEFAULT 0,
  balance_updated_at       INTEGER NOT NULL DEFAULT 0,
  level_code               TEXT NOT NULL DEFAULT '',
  level_name               TEXT NOT NULL DEFAULT '',
  prev_level_code          TEXT,
  highest_level_code       TEXT,
  continuous_days          INTEGER NOT NULL DEFAULT 0,
  next_level_code          TEXT,
  next_level_name          TEXT,
  next_level_threshold_days INTEGER,
  days_to_next_level       INTEGER,
  last_valid_date          TEXT,
  gap_days                 INTEGER NOT NULL DEFAULT 0,
  break_deadline_date      TEXT,
  is_default_level         INTEGER NOT NULL DEFAULT 1,
  level_updated_at         INTEGER,
  available_makeup_cards   INTEGER NOT NULL DEFAULT 0,
  synced_at                INTEGER NOT NULL DEFAULT 0
);
""";

    private const string V2PointsLedgerCache = """
CREATE TABLE IF NOT EXISTS points_ledger_cache (
  id               INTEGER PRIMARY KEY,
  user_id          TEXT NOT NULL,
  change_amount    INTEGER NOT NULL,
  reason_code      TEXT NOT NULL,
  balance_after    INTEGER NOT NULL,
  related_item_id  TEXT,
  idempotency_key  TEXT,
  created_at       INTEGER NOT NULL,
  business_date    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_points_ledger_cache_created ON points_ledger_cache (user_id, created_at DESC);
""";

    private const string V2MakeupCardsCache = """
CREATE TABLE IF NOT EXISTS makeup_cards_cache (
  id             INTEGER PRIMARY KEY,
  user_id        TEXT NOT NULL,
  granted_month  TEXT NOT NULL,
  status         TEXT NOT NULL,
  used_for_date  TEXT,
  used_at        INTEGER,
  created_at     INTEGER NOT NULL
);
""";

    private const string V2MakeupCandidatesCache = """
CREATE TABLE IF NOT EXISTS makeup_candidates_cache (
  date       TEXT PRIMARY KEY,
  days_ago   INTEGER NOT NULL,
  synced_at  INTEGER NOT NULL
);
""";

    private const string V2LevelConfigCache = """
CREATE TABLE IF NOT EXISTS level_config_cache (
  level_code      TEXT PRIMARY KEY,
  level_name      TEXT NOT NULL DEFAULT '',
  threshold_days  INTEGER NOT NULL DEFAULT 0,
  sort_order      INTEGER NOT NULL DEFAULT 0,
  synced_at       INTEGER NOT NULL
);
""";

    /* ------------------------------------------------------------------ */
    /* v3 chat-messages-fts5（unicode61）                                  */
    /* ------------------------------------------------------------------ */

    private const string V3FtsTable = """
CREATE VIRTUAL TABLE IF NOT EXISTS chat_messages_fts
  USING fts5(content, message_id UNINDEXED, tokenize='unicode61');
""";

    /// <summary>
    /// 存量回填。加 <c>NOT EXISTS</c> 守卫使脚本**幂等**：
    /// 重复执行不会产生重复行（否则 <c>DELETE ... WHERE message_id = old.message_id</c> 之外还会留下幽灵行）。
    /// </summary>
    private const string V3FtsBackfill = """
INSERT INTO chat_messages_fts (content, message_id)
SELECT m.content, m.message_id FROM chat_messages m
WHERE m.content <> ''
  AND NOT EXISTS (SELECT 1 FROM chat_messages_fts f WHERE f.message_id = m.message_id);
""";

    private const string V3FtsTriggers = """
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_ai
AFTER INSERT ON chat_messages BEGIN
  INSERT INTO chat_messages_fts (content, message_id) VALUES (new.content, new.message_id);
END;
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_au
AFTER UPDATE OF content ON chat_messages BEGIN
  DELETE FROM chat_messages_fts WHERE message_id = old.message_id;
  INSERT INTO chat_messages_fts (content, message_id) VALUES (new.content, new.message_id);
END;
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_ad
AFTER DELETE ON chat_messages BEGIN
  DELETE FROM chat_messages_fts WHERE message_id = old.message_id;
END;
""";

    /* ------------------------------------------------------------------ */
    /* v4 reminder-dedupe-and-attachment-index                             */
    /* ------------------------------------------------------------------ */

    private const string V4Indexes = """
CREATE INDEX IF NOT EXISTS idx_reminders_target_time ON reminders (target_time, status);
CREATE INDEX IF NOT EXISTS idx_chat_messages_role_ts ON chat_messages (role, timestamp);
""";

    /* ------------------------------------------------------------------ */
    /* v5 chat-messages-trigram-index-for-cjk                              */
    /* ------------------------------------------------------------------ */

    /*
     * FR-W-CHAT-17 修正：`unicode61` 分词器**不能对中文做子串匹配**。
     * Windows 端实测（SQLite 3.53.3 / Microsoft.Data.Sqlite 8.0.31）：
     *   内容「今天写了一个很长的递归函数」
     *     chat_messages_fts     MATCH '"递归函数"' → 0 命中
     *     chat_messages_fts_tri MATCH '"递归函数"' → 1 命中
     * 即中文子串检索在 unicode61 上会静默返回空。故额外建 trigram 索引。
     * 代价：查询词须 ≥3 个字符；1~2 字查询由 MessageRepository.SearchAsync 回退 LIKE。
     */

    private const string V5TrigramTable = """
CREATE VIRTUAL TABLE IF NOT EXISTS chat_messages_fts_tri
  USING fts5(content, message_id UNINDEXED, tokenize='trigram');
""";

    private const string V5TrigramBackfill = """
INSERT INTO chat_messages_fts_tri (content, message_id)
SELECT m.content, m.message_id FROM chat_messages m
WHERE m.content <> ''
  AND NOT EXISTS (SELECT 1 FROM chat_messages_fts_tri f WHERE f.message_id = m.message_id);
""";

    private const string V5TrigramTriggers = """
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_tri_ai
AFTER INSERT ON chat_messages BEGIN
  INSERT INTO chat_messages_fts_tri (content, message_id) VALUES (new.content, new.message_id);
END;
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_tri_au
AFTER UPDATE OF content ON chat_messages BEGIN
  DELETE FROM chat_messages_fts_tri WHERE message_id = old.message_id;
  INSERT INTO chat_messages_fts_tri (content, message_id) VALUES (new.content, new.message_id);
END;
CREATE TRIGGER IF NOT EXISTS chat_messages_fts_tri_ad
AFTER DELETE ON chat_messages BEGIN
  DELETE FROM chat_messages_fts_tri WHERE message_id = old.message_id;
END;
""";

    /* ------------------------------------------------------------------ */
    /* 迁移链                                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>按 <see cref="MigrationStep.Version"/> 升序排列的全部迁移。</summary>
    public static IReadOnlyList<MigrationStep> All { get; } =
    [
        new MigrationStep(
            1,
            "baseline-room-v4-tables",
            [
                V1ChatMessages,
                V1ChatAttachments,
                V1BotNotifications,
                V1UserFacts,
                V1Reminders,
                V1SyncCursors,
                V1DeliveredBotMessages,
            ]),
        new MigrationStep(
            2,
            "gamification-cache-tables",
            [
                V2UserProgressCache,
                V2PointsLedgerCache,
                V2MakeupCardsCache,
                V2MakeupCandidatesCache,
                V2LevelConfigCache,
            ]),
        new MigrationStep(
            3,
            "chat-messages-fts5",
            [
                V3FtsTable,
                V3FtsBackfill,
                V3FtsTriggers,
            ]),
        new MigrationStep(
            4,
            "reminder-dedupe-and-attachment-index",
            [
                V4Indexes,
            ]),
        new MigrationStep(
            5,
            "chat-messages-trigram-index-for-cjk",
            [
                V5TrigramTable,
                V5TrigramBackfill,
                V5TrigramTriggers,
            ]),
        new MigrationStep(6, "memory-event-cursor-and-permission-queue", [V6MemoryEvents]),
        new MigrationStep(7, "history-sync-checkpoints", ["CREATE TABLE history_sync_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);"]),
    ];

    /// <summary>普通表（不含 FTS5 的影子表），共 12 张。</summary>
    public static IReadOnlyList<string> TableNames { get; } =
    [
        "chat_messages",
        "chat_attachments",
        "bot_notifications",
        "user_facts",
        "reminders",
        "sync_cursors",
        "delivered_bot_messages",
        "user_progress_cache",
        "points_ledger_cache",
        "makeup_cards_cache",
        "makeup_candidates_cache",
        "level_config_cache",
        "memory_seen_events",
        "memory_permission_queue",
        "history_sync_state",
    ];

    /// <summary>FTS5 虚拟表，共 2 张。</summary>
    public static IReadOnlyList<string> VirtualTableNames { get; } =
    [
        "chat_messages_fts",
        "chat_messages_fts_tri",
    ];

    /// <summary>FTS 同步触发器，共 6 个（每张虚拟表 AFTER INSERT / UPDATE OF content / DELETE 各一个）。</summary>
    public static IReadOnlyList<string> TriggerNames { get; } =
    [
        "chat_messages_fts_ai",
        "chat_messages_fts_au",
        "chat_messages_fts_ad",
        "chat_messages_fts_tri_ai",
        "chat_messages_fts_tri_au",
        "chat_messages_fts_tri_ad",
    ];

    /// <summary>非隐式索引（PRD 要求显式建索引的全部条目）。</summary>
    public static IReadOnlyList<string> IndexNames { get; } =
    [
        "idx_chat_messages_session_ts",
        "idx_chat_messages_status",
        "idx_chat_attachments_message",
        "idx_bot_notifications_ts",
        "idx_user_facts_user_ts",
        "idx_reminders_status_fire",
        "idx_points_ledger_cache_created",
        "idx_reminders_target_time",
        "idx_chat_messages_role_ts",
    ];
}
