-- 1. Main transaction header
CREATE TABLE IF NOT EXISTS Transactions (
    id UUID PRIMARY KEY,
    reference_code VARCHAR(100) UNIQUE,
    status VARCHAR(50), -- 'PENDING', 'SUCCESS', 'FAILED'
    account_id VARCHAR(100), -- target account for the (initiated) payment
    amount DECIMAL(18, 2) DEFAULT 0, -- expected/booked amount in kobo
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 2. The Ledger (Double-Entry)
-- Every transaction MUST have a balanced debit/credit pair.
CREATE TABLE IF NOT EXISTS LedgerEntries (
    id SERIAL PRIMARY KEY,
    transaction_id UUID REFERENCES Transactions(id),
    account_id VARCHAR(100), -- Customer's virtual account or system account
    debit_amount DECIMAL(18, 2) DEFAULT 0,
    credit_amount DECIMAL(18, 2) DEFAULT 0,
    entry_type VARCHAR(50), -- 'PAYMENT', 'FEES', 'SETTLEMENT'
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Indexing for performance
CREATE INDEX IF NOT EXISTS idx_ledger_account ON LedgerEntries(account_id);