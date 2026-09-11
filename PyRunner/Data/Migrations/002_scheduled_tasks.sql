-- 应用内定时任务。ScriptId 使用 SET NULL，目录同步移除脚本记录时保留任务配置。
CREATE TABLE IF NOT EXISTS ScheduledTask (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL,
    ScriptId        INTEGER REFERENCES Script(Id) ON DELETE SET NULL,
    ScriptName      TEXT    NOT NULL,
    ScriptPath      TEXT    NOT NULL,
    ScheduleType    TEXT    NOT NULL,
    StartAtLocal    TEXT    NOT NULL,
    DaysOfWeek      INTEGER NOT NULL DEFAULT 0,
    IntervalMinutes INTEGER NOT NULL DEFAULT 0,
    NextRunAtUtc    TEXT,
    LastRunAtUtc    TEXT,
    LastResult      TEXT,
    Enabled         INTEGER NOT NULL DEFAULT 1,
    CreatedAt       TEXT    NOT NULL,
    UpdatedAt       TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_scheduledtask_next
ON ScheduledTask(Enabled, NextRunAtUtc);

CREATE INDEX IF NOT EXISTS idx_scheduledtask_script
ON ScheduledTask(ScriptId);
