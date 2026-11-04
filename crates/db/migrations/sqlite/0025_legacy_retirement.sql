-- Immutable evidence survives source retirement for audit and replay protection.
ALTER TABLE legacy_repair_holds ADD COLUMN retired_at BIGINT;
ALTER TABLE legacy_repair_holds ADD COLUMN retirement_proof TEXT;
