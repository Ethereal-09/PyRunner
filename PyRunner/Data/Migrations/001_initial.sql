-- 001_initial.sql
-- PyRunner Manager 数据库初始化脚本（按补充文档 §四，含拍板修正）
-- 数据库路径: %LOCALAPPDATA%/PyRunner/pyrunner.db
-- 约定：所有时间戳字段由应用层写入 UTC ISO-8601（yyyy-MM-ddTHH:mm:ssZ），
--       DEFAULT datetime('now') 亦为 UTC，展示层负责转换本地时间。
-- 约定：RunRecord.Status 取值 running/success/failed/killed，建表默认 'running'。
-- 约定：路径类 UNIQUE 约束一律 COLLATE NOCASE（Windows 路径大小写不敏感，
--       拦截大小写变体重复登记；产品未发布，直接改初始迁移而非新增迁移）。
-- 注意：PRAGMA 语句由 SchemaMigrator 在事务外执行（连接工厂已逐连接设置
--       synchronous=NORMAL 与 foreign_keys=ON；journal_mode=WAL 持久化于库文件）。

PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS Script (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL,
    FilePath        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    Category        TEXT    NOT NULL DEFAULT '未分类',
    Tags            TEXT,
    Description     TEXT,
    InterpreterId   INTEGER REFERENCES Interpreter(Id),
    Arguments       TEXT,
    WorkingDirectory TEXT,
    IsFavorite      INTEGER NOT NULL DEFAULT 0,
    CreatedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UpdatedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS Interpreter (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL,
    ExecutablePath  TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    Version         TEXT,
    IsDefault       INTEGER NOT NULL DEFAULT 0,
    CreatedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS ScriptPath (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Path            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    Enabled         INTEGER NOT NULL DEFAULT 1,
    CreatedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS RunRecord (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ScriptId        INTEGER NOT NULL REFERENCES Script(Id) ON DELETE CASCADE,
    StartedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    FinishedAt      TEXT,
    ExitCode        INTEGER,
    Status          TEXT    NOT NULL DEFAULT 'running',
    Output          TEXT
);

-- 补充文档 §四 原有索引
CREATE INDEX IF NOT EXISTS idx_script_category ON Script(Category);
CREATE INDEX IF NOT EXISTS idx_script_favorite ON Script(IsFavorite);
CREATE INDEX IF NOT EXISTS idx_runrecord_scriptid ON RunRecord(ScriptId);
CREATE INDEX IF NOT EXISTS idx_runrecord_startedat ON RunRecord(StartedAt DESC);

-- Phase A 补充索引：按名搜索 / 脚本维度按时间倒序查最近运行
CREATE INDEX IF NOT EXISTS idx_script_name ON Script(Name);
CREATE INDEX IF NOT EXISTS idx_runrecord_script_status ON RunRecord(ScriptId, StartedAt DESC);

-- 数据库版本表（用于迁移）
CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version         INTEGER PRIMARY KEY,
    AppliedAt       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
INSERT OR IGNORE INTO SchemaVersion (Version) VALUES (1);
