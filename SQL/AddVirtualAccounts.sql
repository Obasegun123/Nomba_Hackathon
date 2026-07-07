-- Additive migration: virtual account registry for lifecycle management.
-- Applied automatically on startup if the virtual_accounts table is absent.
-- Safe to run repeatedly (all statements are idempotent).

CREATE TABLE IF NOT EXISTS virtual_accounts (
    account_ref  VARCHAR(100) PRIMARY KEY,
    account_name VARCHAR(255) NOT NULL DEFAULT '',
    -- ACTIVE | CLOSED | SUSPENDED
    status       VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE',
    -- 1 = ₦50k/day, 2 = ₦200k/day, 3 = unlimited
    kyc_tier     INT          NOT NULL DEFAULT 1,
    created_at   TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at   TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_virtual_accounts_status ON virtual_accounts(status);
