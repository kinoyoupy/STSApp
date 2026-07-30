-- VoiceLink Vector RAG用の手動マイグレーションです。
-- 実行前に、対象が開発用MySQLデータベースであることを確認してください。
-- MySQL 8.4系では今回必要なネイティブVector型を使わず、EmbeddingをJSONとして保存します。

ALTER TABLE conversation_turns
    ADD COLUMN answer_basis ENUM('knowledge_grounded', 'general_knowledge') NULL AFTER assistant_text,
    MODIFY COLUMN error_stage ENUM('upload', 'stt', 'rag', 'gemini', 'tts', 'database') NULL;

ALTER TABLE turn_events
    MODIFY COLUMN stage ENUM('upload', 'stt', 'rag', 'gemini', 'tts', 'database') NOT NULL;

CREATE TABLE knowledge_documents (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    source_path VARCHAR(1024) CHARACTER SET ascii NOT NULL,
    title VARCHAR(500) NOT NULL,
    source_hash CHAR(64) CHARACTER SET ascii NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    CONSTRAINT uq_knowledge_documents_source_path UNIQUE (source_path)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE knowledge_chunks (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    knowledge_document_id BIGINT UNSIGNED NOT NULL,
    parent_heading VARCHAR(500) NULL,
    heading VARCHAR(500) NOT NULL,
    content LONGTEXT NOT NULL,
    chunk_order INT NOT NULL,
    content_hash CHAR(64) CHARACTER SET ascii NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    CONSTRAINT uq_knowledge_chunks_document_order UNIQUE (knowledge_document_id, chunk_order),
    CONSTRAINT fk_knowledge_chunks_document
        FOREIGN KEY (knowledge_document_id) REFERENCES knowledge_documents(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE knowledge_chunk_embeddings (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    knowledge_chunk_id BIGINT UNSIGNED NOT NULL,
    model_name VARCHAR(100) CHARACTER SET ascii NOT NULL,
    dimensions INT NOT NULL,
    vector_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    CONSTRAINT uq_knowledge_chunk_embeddings_chunk UNIQUE (knowledge_chunk_id),
    CONSTRAINT fk_knowledge_chunk_embeddings_chunk
        FOREIGN KEY (knowledge_chunk_id) REFERENCES knowledge_chunks(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE turn_rag_references (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    -- conversation_turns.id は既存設計で ASCII のUUIDです。
    -- 外部キーでは文字セットまで一致している必要があるため、ここも明示します。
    conversation_turn_id CHAR(36) CHARACTER SET ascii NOT NULL,
    knowledge_chunk_id BIGINT UNSIGNED NULL,
    retrieval_rank INT NOT NULL,
    similarity_score DECIMAL(8, 6) NOT NULL,
    document_title_snapshot VARCHAR(500) NOT NULL,
    parent_heading_snapshot VARCHAR(500) NULL,
    heading_snapshot VARCHAR(500) NOT NULL,
    content_snapshot LONGTEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    CONSTRAINT uq_turn_rag_references_rank UNIQUE (conversation_turn_id, retrieval_rank),
    CONSTRAINT uq_turn_rag_references_chunk UNIQUE (conversation_turn_id, knowledge_chunk_id),
    CONSTRAINT fk_turn_rag_references_turn
        FOREIGN KEY (conversation_turn_id) REFERENCES conversation_turns(id) ON DELETE CASCADE,
    CONSTRAINT fk_turn_rag_references_chunk
        FOREIGN KEY (knowledge_chunk_id) REFERENCES knowledge_chunks(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
