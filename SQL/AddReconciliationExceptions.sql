-- Additive migration: reconciliation exception tracking with LLM analysis.
-- Applied automatically on startup if the reconciliation_exceptions table is absent.
-- Safe to run repeatedly (all statements are idempotent).

CREATE TABLE IF NOT EXISTS reconciliation_exceptions (
    id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    transaction_ref       VARCHAR(100) NOT NULL,
    -- Type of exception: webhook_loss, amount_mismatch, double_settlement, api_error, unexpected_status, unknown_error
    exception_type        VARCHAR(50) NOT NULL,
    error_message         VARCHAR(500),
    -- LLM-generated analysis (if LLM provider is configured)
    ai_diagnosis          TEXT,
    ai_recommendation     TEXT,
    -- Confidence score from LLM (0-1), stored as decimal(3,2)
    ai_confidence         DECIMAL(3,2),
    -- Status: PENDING, APPROVED, REJECTED, RESOLVED
    status                VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    approved_by           VARCHAR(255),
    resolution_action     TEXT,
    created_at            TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    resolved_at           TIMESTAMP WITH TIME ZONE,
    -- Raw context data (JSON) for debugging
    context_data          JSONB
);

CREATE INDEX IF NOT EXISTS idx_exceptions_ref ON reconciliation_exceptions(transaction_ref);
CREATE INDEX IF NOT EXISTS idx_exceptions_status ON reconciliation_exceptions(status);
