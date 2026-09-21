-- Local AI conversation history. It stores only messages the user explicitly sent,
-- provider replies, and context file paths/fingerprints; never credentials or hidden prompts.
CREATE TABLE IF NOT EXISTS AiConversation (
    Id          TEXT PRIMARY KEY,
    Title       TEXT NOT NULL,
    Mode        INTEGER NOT NULL,
    CreatedAt   TEXT NOT NULL,
    UpdatedAt   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS AiMessage (
    Id              TEXT PRIMARY KEY,
    ConversationId  TEXT NOT NULL REFERENCES AiConversation(Id) ON DELETE CASCADE,
    Sequence         INTEGER NOT NULL,
    Role             INTEGER NOT NULL,
    Kind             INTEGER NOT NULL,
    Content          TEXT NOT NULL,
    CreatedAt        TEXT NOT NULL,
    RequestId        TEXT
);

CREATE TABLE IF NOT EXISTS AiContextFile (
    ConversationId  TEXT NOT NULL REFERENCES AiConversation(Id) ON DELETE CASCADE,
    Sequence         INTEGER NOT NULL,
    FilePath         TEXT NOT NULL,
    PRIMARY KEY (ConversationId, FilePath COLLATE NOCASE)
);

CREATE INDEX IF NOT EXISTS idx_ai_conversation_updated ON AiConversation(UpdatedAt DESC);
CREATE INDEX IF NOT EXISTS idx_ai_message_conversation ON AiMessage(ConversationId, Sequence);
